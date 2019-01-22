using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LoRaTools.LoRaMessage;

namespace LoRaWan.NetworkServer
{
    /// <summary>
    /// Represents a running task to load devices by devAddr
    /// Prevents querying the registry and loading twins multiple times
    /// </summary>
    class DeviceForPayloadLoader
    {
        private readonly LoRaDeviceAPIServiceBase loRaDeviceAPIService;
        private readonly ILoRaDeviceFactory deviceFactory;
        private readonly string devAddr;
        private readonly DevEUIToLoRaDeviceDictionary destinationDictionary;
        private readonly HashSet<ILoRaDeviceInitializer> initializers;
        private readonly NetworkServerConfiguration configuration;
        private readonly Task loading;

        internal DeviceForPayloadLoader(
            string devAddr,
            LoRaDeviceAPIServiceBase loRaDeviceAPIService,
            ILoRaDeviceFactory deviceFactory,
            DevEUIToLoRaDeviceDictionary destinationDictionary,
            HashSet<ILoRaDeviceInitializer> initializers,
            NetworkServerConfiguration configuration
        )
        {
            this.loRaDeviceAPIService = loRaDeviceAPIService;
            this.deviceFactory = deviceFactory;
            this.devAddr = devAddr;
            this.destinationDictionary = destinationDictionary;
            this.initializers = initializers;
            this.configuration = configuration;
            this.loading = Load();
        }

        public Task WaitComplete() => loading;

        async Task Load()
        {
            SearchDevicesResult searchDeviceResult = null;
            try
            {
                searchDeviceResult = await this.loRaDeviceAPIService.SearchByDevAddrAsync(devAddr);
            }
            catch (Exception ex)
            {
                Logger.Log(devAddr, $"Error searching device for payload. {ex.Message}", Logger.LoggingLevel.Error);
                return;
            }

            if (searchDeviceResult?.Devices != null)
            {
                var initTasks = new List<Task<LoRaDevice>>();
                foreach (var foundDevice in searchDeviceResult.Devices)
                {
                    var loRaDevice = this.deviceFactory.Create(foundDevice);
                    initTasks.Add(InitializeDeviceAsync(loRaDevice));
                }

                try
                {
                    await Task.WhenAll(initTasks);
                }
                catch (Exception ex)
                {
                    Logger.Log(devAddr, $"One or more device initialization failed. {ex.Message}", Logger.LoggingLevel.Error);
                }

                // loop through devices and get them, queueing any particular
                // Block adding any requests to queue!
                var createdDevice = new List<LoRaDevice>();
                
                foreach (var deviceTask in initTasks)
                {
                    if (deviceTask.IsCompletedSuccessfully)
                    {
                        var device = deviceTask.Result;
                        createdDevice.Add(device);

                        QueueMatchingRequests(device);

                        destinationDictionary.AddOrUpdate(device.DevEUI, device, (_, existing) =>
                        {
                            return existing;
                        });
                    }
                }

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
                Logger.Log(loRaDevice.DevEUI ?? devAddr, $"Error initializing device {loRaDevice.DevEUI}. {ex.Message}", Logger.LoggingLevel.Error);
            }

            // instance not used, dispose the connection
            loRaDevice.Dispose();
            return null;
        }


        private void QueueMatchingRequests(LoRaDevice device)
        {
            for (var i=0; i < queuedRequests.Count; ++i)
            {
                var request = queuedRequests[i];
                if (request.LoRaPayloadData.CheckMic(device.NwkSKey))
                {
                    device.QueueRequest(request);
                    queuedRequests.RemoveAt(i);
                    i--;
                }
            }
        }


        List<LoRaPayloadRequest> queuedRequests = new List<LoRaPayloadRequest>();

        public Task<bool> TryQueuePayload(LoRaPayloadData loraPayload)
        {
            queuedRequests.Add(new LoRaPayloadRequest());
            return Task.FromResult(true);
        }
    }
}
