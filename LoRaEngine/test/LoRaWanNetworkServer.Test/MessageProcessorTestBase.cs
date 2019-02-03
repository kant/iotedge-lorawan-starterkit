// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace LoRaWan.NetworkServer.Test
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using LoRaTools.LoRaMessage;
    using LoRaTools.LoRaPhysical;
    using LoRaTools.Regions;
    using LoRaWan.NetworkServer;
    using LoRaWan.Test.Shared;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.Logging;
    using Moq;

    public class MessageProcessorTestBase
    {
        protected const string ServerGatewayID = "test-gateway";

        private readonly byte[] macAddress;
        private long startTime;

        private LoRaDataRequestHandlerImplementation requestHandlerImplementation;

        public TestPacketForwarder PacketForwarder { get; }

        protected Mock<LoRaDeviceAPIServiceBase> LoRaDeviceApi { get; }

        protected ILoRaDeviceFrameCounterUpdateStrategyFactory FrameCounterUpdateStrategyFactory { get; }

        protected NetworkServerConfiguration ServerConfiguration { get; }

        internal TestLoRaDeviceFactory LoRaDeviceFactory { get; }

        protected Mock<ILoRaDeviceClient> LoRaDeviceClient { get; }

        public MessageProcessorTestBase()
        {
            this.startTime = DateTimeOffset.UtcNow.Ticks;

            this.macAddress = Utility.GetMacAddress();
            this.ServerConfiguration = new NetworkServerConfiguration
            {
                GatewayID = ServerGatewayID,
                LogToConsole = true,
                LogLevel = ((int)LogLevel.Debug).ToString(),
            };

            this.PacketForwarder = new TestPacketForwarder();
            this.LoRaDeviceApi = new Mock<LoRaDeviceAPIServiceBase>(MockBehavior.Strict);
            this.FrameCounterUpdateStrategyFactory = new LoRaDeviceFrameCounterUpdateStrategyFactory(ServerGatewayID, this.LoRaDeviceApi.Object);
            this.requestHandlerImplementation = new LoRaDataRequestHandlerImplementation(this.ServerConfiguration, this.FrameCounterUpdateStrategyFactory, new LoRaPayloadDecoder());
            this.LoRaDeviceClient = new Mock<ILoRaDeviceClient>(MockBehavior.Strict);
            this.LoRaDeviceFactory = new TestLoRaDeviceFactory(this.ServerConfiguration, this.FrameCounterUpdateStrategyFactory, this.LoRaDeviceClient.Object);
        }

        public MemoryCache NewMemoryCache() => new MemoryCache(new MemoryCacheOptions());

        public LoRaDevice CreateLoRaDevice(SimulatedDevice simulatedDevice) => TestUtils.CreateFromSimulatedDevice(simulatedDevice, this.LoRaDeviceClient.Object, this.requestHandlerImplementation);
    }
}
