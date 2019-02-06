﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace LoRaWan.NetworkServer
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Represents a running task loading devices by devAddr
    /// - Prevents querying the registry and loading twins multiple times
    /// - Ensure that requests are queued by <see cref="LoRaDevice"/> in the order they come
    /// </summary>
    class DeviceLoaderSynchronizer : ILoRaDeviceRequestQueue
    {
        private readonly LoRaDeviceAPIServiceBase loRaDeviceAPIService;
        private readonly ILoRaDeviceFactory deviceFactory;
        private readonly string devAddr;
        private readonly DevEUIToLoRaDeviceDictionary destinationDictionary;
        private readonly HashSet<ILoRaDeviceInitializer> initializers;
        private readonly NetworkServerConfiguration configuration;
        private readonly Task loading;
        private volatile bool isLoadingDevices;
        private volatile bool loadingDevicesFailed;
        private object queueLock;
        private volatile List<LoRaRequest> queuedRequests;

        internal DeviceLoaderSynchronizer(
            string devAddr,
            LoRaDeviceAPIServiceBase loRaDeviceAPIService,
            ILoRaDeviceFactory deviceFactory,
            DevEUIToLoRaDeviceDictionary destinationDictionary,
            HashSet<ILoRaDeviceInitializer> initializers,
            NetworkServerConfiguration configuration,
            Action<Task> continuationAction)
        {
            this.loRaDeviceAPIService = loRaDeviceAPIService;
            this.deviceFactory = deviceFactory;
            this.devAddr = devAddr;
            this.destinationDictionary = destinationDictionary;
            this.initializers = initializers;
            this.configuration = configuration;
            this.isLoadingDevices = true;
            this.loadingDevicesFailed = false;
            this.queueLock = new object();
            this.queuedRequests = new List<LoRaRequest>();
            this.loading = this.Load().ContinueWith(continuationAction, TaskContinuationOptions.ExecuteSynchronously);
        }

        async Task Load()
        {
            try
            {
                SearchDevicesResult searchDeviceResult = null;
                try
                {
                    searchDeviceResult = await this.loRaDeviceAPIService.SearchByDevAddrAsync(this.devAddr);
                }
                catch (Exception ex)
                {
                    Logger.Log(this.devAddr, $"Error searching device for payload. {ex.Message}", LogLevel.Error);
                    throw;
                }

                var initTasks = new List<Task<LoRaDevice>>();
                if (searchDeviceResult.Devices?.Count > 0)
                {
                    foreach (var foundDevice in searchDeviceResult.Devices)
                    {
                        var loRaDevice = this.deviceFactory.Create(foundDevice);
                        initTasks.Add(this.InitializeDeviceAsync(loRaDevice));
                    }

                    try
                    {
                        await Task.WhenAll(initTasks);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(this.devAddr, $"One or more device initialization failed. {ex.Message}", LogLevel.Error);
                    }
                }

                var createdDevices = new List<LoRaDevice>();
                if (initTasks.Count > 0)
                {
                    foreach (var deviceTask in initTasks)
                    {
                        if (deviceTask.IsCompletedSuccessfully)
                        {
                            var device = deviceTask.Result;
                            createdDevices.Add(device);
                        }
                    }
                }

                // Dispatch queued requests to created devices
                // those without a matching device will receive "failed" notification
                lock (this.queueLock)
                {
                    this.DispatchQueuedItems(createdDevices);

                    foreach (var device in createdDevices)
                    {
                        this.destinationDictionary.AddOrUpdate(device.DevEUI, device, (_, existing) =>
                        {
                            return existing;
                        });
                    }

                    this.isLoadingDevices = false;
                }
            }
            catch (Exception ex)
            {
                Logger.Log(this.devAddr, $"One or more device creation from devAddr failed. {ex.Message}", LogLevel.Error);
                this.NotifyQueueItemsDueToError();
                throw;
            }
        }

        private void NotifyQueueItemsDueToError()
        {
            List<LoRaRequest> failedRequests;
            lock (this.queueLock)
            {
                failedRequests = this.queuedRequests;
                this.queuedRequests = new List<LoRaRequest>();
                this.loadingDevicesFailed = true;
                this.isLoadingDevices = false;
            }

            failedRequests.ForEach(x => x.NotifyFailed(null, LoRaDeviceRequestFailedReason.ApplicationError));
        }

        private void DispatchQueuedItems(List<LoRaDevice> devices)
        {
            foreach (var queuedItem in this.queuedRequests)
            {
                var requestHandled = false;
                if (devices.Count > 0)
                {
                    foreach (var device in devices)
                    {
                        if (queuedItem.Payload.CheckMic(device.NwkSKey))
                        {
                            device.Queue(queuedItem);
                            requestHandled = true;
                            break;
                        }
                    }
                }

                if (!requestHandled)
                {
                    var failedReason = devices.Count > 0 ? LoRaDeviceRequestFailedReason.NotMatchingDeviceByMicCheck : LoRaDeviceRequestFailedReason.NotMatchingDeviceByDevAddr;
                    queuedItem.NotifyFailed(null, failedReason);
                }
            }

            this.queuedRequests.Clear();
        }

        public void Queue(LoRaRequest request)
        {
            var localIsLoadingDevices = this.isLoadingDevices;
            if (localIsLoadingDevices)
            {
                lock (this.queueLock)
                {
                    if (!this.isLoadingDevices)
                    {
                        localIsLoadingDevices = false;
                    }
                    else
                    {
                        this.queuedRequests.Add(request);
                    }
                }
            }

            if (!localIsLoadingDevices)
            {
                foreach (var device in this.destinationDictionary.Values)
                {
                    if (request.Payload.CheckMic(device.NwkSKey))
                    {
                        device.Queue(request);
                        return;
                    }
                }

                // not handled, raised failed event
                var failedReason =
                    this.loadingDevicesFailed ? LoRaDeviceRequestFailedReason.ApplicationError :
                    this.destinationDictionary.Count > 0 ? LoRaDeviceRequestFailedReason.NotMatchingDeviceByMicCheck : LoRaDeviceRequestFailedReason.NotMatchingDeviceByDevAddr;
                request.NotifyFailed(null, failedReason);
            }
        }

        private async Task<LoRaDevice> InitializeDeviceAsync(LoRaDevice loRaDevice)
        {
            try
            {
                // Calling initialize async here to avoid making async calls in the concurrent dictionary
                // Since only one device will be added, we guarantee that initialization only happens once
                if (await loRaDevice.InitializeAsync())
                {
                    loRaDevice.IsOurDevice = string.IsNullOrEmpty(loRaDevice.GatewayID) || string.Equals(loRaDevice.GatewayID, this.configuration.GatewayID, StringComparison.InvariantCultureIgnoreCase);

                    // once added, call initializers
                    foreach (var initializer in this.initializers)
                        initializer.Initialize(loRaDevice);

                    return loRaDevice;
                }
            }
            catch (Exception ex)
            {
                // device does not have the required properties
                Logger.Log(loRaDevice.DevEUI ?? this.devAddr, $"Error initializing device {loRaDevice.DevEUI}. {ex.Message}", LogLevel.Error);
            }

            // instance not used, dispose the connection
            loRaDevice.Dispose();
            return null;
        }
    }
}