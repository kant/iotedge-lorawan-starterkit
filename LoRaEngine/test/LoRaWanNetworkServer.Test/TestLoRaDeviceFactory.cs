// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace LoRaWan.NetworkServer.Test
{
    using LoRaWan.NetworkServer;

    internal class TestLoRaDeviceFactory : ILoRaDeviceFactory
    {
        private readonly ILoRaDeviceClient loRaDeviceClient;
        private readonly NetworkServerConfiguration configuration;
        private readonly ILoRaDeviceFrameCounterUpdateStrategyFactory frameCounterUpdateStrategyFactory;

        public TestLoRaDeviceFactory(ILoRaDeviceClient loRaDeviceClient)
        {
            this.loRaDeviceClient = loRaDeviceClient;
        }

        public TestLoRaDeviceFactory(
            NetworkServerConfiguration configuration,
            ILoRaDeviceFrameCounterUpdateStrategyFactory frameCounterUpdateStrategyFactory,
            ILoRaDeviceClient loRaDeviceClient)
        {
            this.configuration = configuration;
            this.frameCounterUpdateStrategyFactory = frameCounterUpdateStrategyFactory;
            this.loRaDeviceClient = loRaDeviceClient;
        }

        public LoRaDevice Create(IoTHubDeviceInfo deviceInfo)
        {
            var loRaDevice = new LoRaDevice(
                deviceInfo.DevAddr,
                deviceInfo.DevEUI,
                this.loRaDeviceClient);

            loRaDevice.SetRequestHandler(new LoRaDataRequestHandlerImplementation(this.configuration, this.frameCounterUpdateStrategyFactory, new LoRaPayloadDecoder()));
            return loRaDevice;
        }
    }
}