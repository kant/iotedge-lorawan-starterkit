﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace LoRaWan.NetworkServer
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using LoRaTools;
    using LoRaTools.LoRaMessage;
    using LoRaTools.Utils;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Primitives;

    /// <summary>
    /// LoRa device registry
    /// </summary>
    public class LoRaDeviceRegistry : ILoRaDeviceRegistry
    {
        // Caches a device making join for 2 minute
        const int INTERVAL_TO_CACHE_DEVICE_IN_JOIN_PROCESS_IN_MINUTES = 2;

        private readonly LoRaDeviceAPIServiceBase loRaDeviceAPIService;
        private readonly ILoRaDeviceFactory deviceFactory;
        private readonly HashSet<ILoRaDeviceInitializer> initializers;
        private readonly NetworkServerConfiguration configuration;

        private volatile IMemoryCache cache;
        private CancellationTokenSource resetCacheToken;

        public LoRaDeviceRegistry(
            NetworkServerConfiguration configuration,
            IMemoryCache cache,
            LoRaDeviceAPIServiceBase loRaDeviceAPIService,
            ILoRaDeviceFactory deviceFactory)
        {
            this.configuration = configuration;
            this.resetCacheToken = new CancellationTokenSource();
            this.cache = cache;
            this.loRaDeviceAPIService = loRaDeviceAPIService;
            this.deviceFactory = deviceFactory;
            this.initializers = new HashSet<ILoRaDeviceInitializer>();
        }

        /// <summary>
        /// Registers a <see cref="ILoRaDeviceInitializer"/>
        /// </summary>
        public void RegisterDeviceInitializer(ILoRaDeviceInitializer initializer) => this.initializers.Add(initializer);

        /// <summary>
        /// Gets a <see cref="DevEUIToLoRaDeviceDictionary"/> containing a list of devices given a <paramref name="devAddr"/>
        /// </summary>
        /// <remarks>
        /// This method is internal in order to allow test assembly to used it!
        /// </remarks>
        internal DevEUIToLoRaDeviceDictionary InternalGetCachedDevicesForDevAddr(string devAddr)
        {
            return this.cache.GetOrCreate<DevEUIToLoRaDeviceDictionary>(devAddr, (cacheEntry) =>
            {
                cacheEntry.SlidingExpiration = TimeSpan.FromDays(1);
                cacheEntry.ExpirationTokens.Add(new CancellationChangeToken(this.resetCacheToken.Token));
                return new DevEUIToLoRaDeviceDictionary();
            });
        }

        DeviceLoaderSynchronizer GetOrCreateLoadingDevicesRequestQueue(string devAddr)
        {
            return this.cache.GetOrCreate<DeviceLoaderSynchronizer>(
                $"devaddrloader:{devAddr}",
                (ce) =>
                {
                    var cts = new CancellationTokenSource();
                    ce.ExpirationTokens.Add(new CancellationChangeToken(cts.Token));

                    var loader = new DeviceLoaderSynchronizer(
                        devAddr,
                        this.loRaDeviceAPIService,
                        this.deviceFactory,
                        this.InternalGetCachedDevicesForDevAddr(devAddr),
                        this.initializers,
                        this.configuration,
                        (t) =>
                        {
                            if (t.IsCompletedSuccessfully)
                            {
                                // remove from cache after 30 seconds
                                cts.CancelAfter(TimeSpan.FromSeconds(30));
                            }
                            else
                            {
                                // remove from cache now
                                cts.Cancel();
                            }
                        });

                    return loader;
                });
        }

        public void QueueRequest(LoRaRequestContext requestContext)
        {
            var devAddr = ConversionHelper.ByteArrayToString(requestContext.Request.Payload.DevAddr);
            var devicesMatchingDevAddr = this.InternalGetCachedDevicesForDevAddr(devAddr);

            // If already in cache, return quickly
            if (devicesMatchingDevAddr.Count > 0)
            {
                var cachedMatchingDevice = devicesMatchingDevAddr.Values.FirstOrDefault(x => this.IsValidDeviceForPayload(x, (LoRaPayloadData)requestContext.Request.Payload, logError: false) && x.IsOurDevice);
                if (cachedMatchingDevice != null)
                {
                    Logger.Log(cachedMatchingDevice.DevEUI, "device in cache", LogLevel.Debug);
                    cachedMatchingDevice.QueueRequest(requestContext);
                }
            }

            // not in cache, need to make a single search by dev addr
            this.GetOrCreateLoadingDevicesRequestQueue(devAddr).Queue(requestContext);
        }

        /// <summary>
        /// Checks whether a <see cref="LoRaDevice"/> is valid for a <see cref="LoRaPayloadData"/>
        /// It validates that the device has a <see cref="LoRaDevice.NwkSKey"/> and mic check
        /// </summary>
        /// <param name="logError">Indicates if error should be log if mic check fails</param>
        private bool IsValidDeviceForPayload(LoRaDevice loRaDevice, LoRaPayloadData loraPayload, bool logError)
        {
            if (string.IsNullOrEmpty(loRaDevice.NwkSKey))
                return false;

            var checkMicResult = loraPayload.CheckMic(loRaDevice.NwkSKey);
            if (!checkMicResult && logError)
            {
                Logger.Log(loRaDevice.DevEUI, $"with devAddr {loRaDevice.DevAddr} check MIC failed", LogLevel.Debug);
            }

            return checkMicResult;
        }

        // Creates cache key for join device loader: "joinloader:{devEUI}"
        string GetJoinDeviceLoaderCacheKey(string devEUI) => string.Concat("joinloader:", devEUI);

        // Removes join device loader from cache
        void RemoveJoinDeviceLoader(string devEUI) => this.cache.Remove(this.GetJoinDeviceLoaderCacheKey(devEUI));

        // Gets or adds a join device loader to the memory cache
        JoinDeviceLoader GetOrAddJoinDeviceLoader(IoTHubDeviceInfo ioTHubDeviceInfo)
        {
            return this.cache.GetOrCreate(this.GetJoinDeviceLoaderCacheKey(ioTHubDeviceInfo.DevEUI), (entry) =>
            {
                entry.SlidingExpiration = TimeSpan.FromMinutes(INTERVAL_TO_CACHE_DEVICE_IN_JOIN_PROCESS_IN_MINUTES);
                return new JoinDeviceLoader(ioTHubDeviceInfo, this.deviceFactory);
            });
        }

        /// <summary>
        /// Searchs for devices that match the join request
        /// </summary>
        public async Task<LoRaDevice> GetDeviceForJoinRequestAsync(string devEUI, string appEUI, string devNonce)
        {
            if (string.IsNullOrEmpty(devEUI) || string.IsNullOrEmpty(appEUI) || string.IsNullOrEmpty(devNonce))
            {
                Logger.Log(devEUI, "join refused: missing devEUI/AppEUI/DevNonce in request", LogLevel.Error);
                return null;
            }

            Logger.Log(devEUI, "querying the registry for OTTA device", LogLevel.Information);

            try
            {
                var searchDeviceResult = await this.loRaDeviceAPIService.SearchAndLockForJoinAsync(
                    gatewayID: this.configuration.GatewayID,
                    devEUI: devEUI,
                    appEUI: appEUI,
                    devNonce: devNonce);

                if (searchDeviceResult.IsDevNonceAlreadyUsed)
                {
                    Logger.Log(devEUI, $"join refused: DevNonce already used by this device", LogLevel.Information);
                    return null;
                }

                if (searchDeviceResult.Devices?.Count == 0)
                {
                    Logger.Log(devEUI, "join refused: no devices found matching join request", LogLevel.Information);
                    return null;
                }

                var matchingDeviceInfo = searchDeviceResult.Devices[0];
                var loader = this.GetOrAddJoinDeviceLoader(matchingDeviceInfo);
                var loRaDevice = await loader.WaitCompleteAsync();
                if (!loader.CanCache)
                    this.RemoveJoinDeviceLoader(devEUI);

                return loRaDevice;
            }
            catch (Exception ex)
            {
                Logger.Log(devEUI, $"failed to get join devices from api. {ex.Message}", LogLevel.Error);
                return null;
            }
        }

        /// <summary>
        /// Updates a device after a successful login
        /// </summary>
        public void UpdateDeviceAfterJoin(LoRaDevice loRaDevice)
        {
            var devicesMatchingDevAddr = this.cache.GetOrCreate<DevEUIToLoRaDeviceDictionary>(loRaDevice.DevAddr, (cacheEntry) =>
            {
                cacheEntry.SlidingExpiration = TimeSpan.FromDays(1);
                cacheEntry.ExpirationTokens.Add(new CancellationChangeToken(this.resetCacheToken.Token));
                return new DevEUIToLoRaDeviceDictionary();
            });

            // if there is an instance, overwrite it
            devicesMatchingDevAddr.AddOrUpdate(loRaDevice.DevEUI, loRaDevice, (key, existing) => loRaDevice);

            // don't remove from pending joins because the device can try again if there was
            // a problem in the transmission
            // TryRemoveJoinDeviceLoader(loRaDevice.DevEUI);

            // once added, call initializers
            foreach (var initializer in this.initializers)
                initializer.Initialize(loRaDevice);
        }

        /// <summary>
        /// <inheritdoc />
        /// </summary>
        public void ResetDeviceCache()
        {
            var oldResetCacheToken = this.resetCacheToken;
            this.resetCacheToken = new CancellationTokenSource();

            if (oldResetCacheToken != null && !oldResetCacheToken.IsCancellationRequested && oldResetCacheToken.Token.CanBeCanceled)
            {
                oldResetCacheToken.Cancel();
                oldResetCacheToken.Dispose();

                Logger.Log("device cache cleared", LogLevel.Information);
            }
        }
    }
}