// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace LoRaWan.NetworkServer.Test
{
    using System;
    using System.Collections.Generic;
    using System.Reactive.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using LoRaTools.LoRaMessage;
    using LoRaTools.LoRaPhysical;
    using LoRaTools.Regions;
    using LoRaTools.Utils;
    using LoRaWan.NetworkServer;
    using LoRaWan.Test.Shared;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Devices.Shared;
    using Microsoft.Extensions.Caching.Memory;
    using Moq;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using Xunit;

    // End to end tests without external dependencies (IoT Hub, Service Facade Function)
    // Parallel message processing
    public class MessageProcessor_End2End_NoDep_Parallel_Processing_Tests : MessageProcessorTestBase
    {
        private readonly TestPacketForwarder packetForwarder;

        public MessageProcessor_End2End_NoDep_Parallel_Processing_Tests()
        {
            this.packetForwarder = new TestPacketForwarder();
        }

        public static IEnumerable<object[]> Multiple_ABP_Messages()
        {
            yield return new object[]
            {
                    new ParallelTestConfiguration()
                    {
                        GatewayID = ServerGatewayID,
                        BetweenMessageDuration = 1000,
                        SearchByDevAddrDuration = 100,
                        SendEventDuration = 100,
                        ReceiveEventDuration = 100,
                        UpdateTwinDuration = 100,
                        LoadTwinDuration = 100,
                    }
            };

            // Slow first calls
            yield return new object[]
            {
                    new ParallelTestConfiguration()
                    {
                        GatewayID = ServerGatewayID,
                        BetweenMessageDuration = 1000,
                        SearchByDevAddrDuration = new int[] { 1000, 100 },
                        SendEventDuration = new int[] { 1000, 100 },
                        ReceiveEventDuration = 400,
                        UpdateTwinDuration = new int[] { 1000, 100 },
                        LoadTwinDuration = new int[] { 1000, 100 },
                    }
            };

            // Very slow first calls
            yield return new object[]
            {
                    new ParallelTestConfiguration()
                    {
                        GatewayID = ServerGatewayID,
                        BetweenMessageDuration = 1000,
                        SearchByDevAddrDuration = new int[] { 5000, 100 },
                        SendEventDuration = new int[] { 1000, 100 },
                        ReceiveEventDuration = 400,
                        UpdateTwinDuration = new int[] { 5000, 100 },
                        LoadTwinDuration = new int[] { 5000, 100 },
                    }
            };
        }

        [Theory]
        [MemberData(nameof(Multiple_ABP_Messages))]
        public async Task ABP_Load_And_Receiving_Multiple_Unconfirmed_Should_Send_All_ToHub(ParallelTestConfiguration parallelTestConfiguration)
        {
            Console.WriteLine("---");
            var simulatedDevice = new SimulatedDevice(TestDeviceInfo.CreateABPDevice(1, gatewayID: null));
            var loRaDeviceClient = new Mock<ILoRaDeviceClient>(MockBehavior.Strict);

            var devEUI = simulatedDevice.LoRaDevice.DeviceID;
            var devAddr = simulatedDevice.LoRaDevice.DevAddr;

            // message will be sent
            var sentTelemetry = new List<LoRaDeviceTelemetry>();
            loRaDeviceClient.Setup(x => x.SendEventAsync(It.IsNotNull<LoRaDeviceTelemetry>(), null))
                .Returns<LoRaDeviceTelemetry, Dictionary<string, string>>((t, _) =>
                {
                    sentTelemetry.Add(t);
                    var duration = parallelTestConfiguration.SendEventDuration.Next();
                    Console.WriteLine($"{nameof(loRaDeviceClient.Object.SendEventAsync)} sleeping for {duration}");
                    return Task.Delay(duration)
                        .ContinueWith((a) => true);
                });

            // C2D message will not be checked
            /*loRaDeviceClient.Setup(x => x.ReceiveAsync(It.IsNotNull<TimeSpan>()))
                .Returns<TimeSpan>((_) =>
                {
                    var duration = parallelTestConfiguration.ReceiveEventDuration.Next();
                    Console.WriteLine($"{nameof(loRaDeviceClient.Object.ReceiveAsync)} sleeping for {duration}");
                    return Task.Delay(duration)
                        .ContinueWith((a) => (Message)null);
                });*/

            // twin will be loaded
            var initialTwin = new Twin();
            initialTwin.Properties.Desired[TwinProperty.DevEUI] = devEUI;
            initialTwin.Properties.Desired[TwinProperty.AppEUI] = simulatedDevice.LoRaDevice.AppEUI;
            initialTwin.Properties.Desired[TwinProperty.AppKey] = simulatedDevice.LoRaDevice.AppKey;
            initialTwin.Properties.Desired[TwinProperty.NwkSKey] = simulatedDevice.LoRaDevice.NwkSKey;
            initialTwin.Properties.Desired[TwinProperty.AppSKey] = simulatedDevice.LoRaDevice.AppSKey;
            initialTwin.Properties.Desired[TwinProperty.DevAddr] = devAddr;
            if (parallelTestConfiguration.GatewayID != null)
                initialTwin.Properties.Desired[TwinProperty.GatewayID] = parallelTestConfiguration.GatewayID;
            initialTwin.Properties.Desired[TwinProperty.SensorDecoder] = simulatedDevice.LoRaDevice.SensorDecoder;
            if (parallelTestConfiguration.DeviceTwinFcntDown.HasValue)
                initialTwin.Properties.Reported[TwinProperty.FCntDown] = parallelTestConfiguration.DeviceTwinFcntDown.Value;
            if (parallelTestConfiguration.DeviceTwinFcntUp.HasValue)
                initialTwin.Properties.Reported[TwinProperty.FCntUp] = parallelTestConfiguration.DeviceTwinFcntUp.Value;

            loRaDeviceClient.Setup(x => x.GetTwinAsync())
                .Returns(() =>
                {
                    var duration = parallelTestConfiguration.LoadTwinDuration.Next();
                    Console.WriteLine($"{nameof(loRaDeviceClient.Object.GetTwinAsync)} sleeping for {duration}");
                    return Task.Delay(duration)
                        .ContinueWith(_ => initialTwin);
                });

            // twin will be updated with new fcnt
            int? fcntUpSavedInTwin = null;
            int? fcntDownSavedInTwin = null;

            var shouldSaveTwin = (parallelTestConfiguration.DeviceTwinFcntDown ?? 0) != 0 || (parallelTestConfiguration.DeviceTwinFcntUp ?? 0) != 0;
            if (shouldSaveTwin)
            {
                loRaDeviceClient.Setup(x => x.UpdateReportedPropertiesAsync(It.IsNotNull<TwinCollection>()))
                    .Returns<TwinCollection>((t) =>
                    {
                        fcntUpSavedInTwin = (int)t[TwinProperty.FCntUp];
                        fcntDownSavedInTwin = (int)t[TwinProperty.FCntDown];
                        var duration = parallelTestConfiguration.UpdateTwinDuration.Next();
                        Console.WriteLine($"{nameof(loRaDeviceClient.Object.UpdateReportedPropertiesAsync)} sleeping for {duration}");
                        return Task.Delay(duration)
                            .ContinueWith((a) => true);
                    });
            }

            // Lora device api
            var loRaDeviceApi = new Mock<LoRaDeviceAPIServiceBase>(MockBehavior.Strict);

            // multi gateway will reset the fcnt
            if (shouldSaveTwin)
            {
                loRaDeviceApi.Setup(x => x.ABPFcntCacheResetAsync(devEUI))
                    .Returns(() =>
                    {
                        var duration = parallelTestConfiguration.DeviceApiResetFcntDuration.Next();
                        Console.WriteLine($"{nameof(loRaDeviceApi.Object.ABPFcntCacheResetAsync)} sleeping for {duration}");
                        return Task.Delay(duration)
                            .ContinueWith((a) => true);
                    });
            }

            // device api will be searched for payload
            loRaDeviceApi.Setup(x => x.SearchByDevAddrAsync(devAddr))
                .Returns(() =>
                {
                    var duration = parallelTestConfiguration.SearchByDevAddrDuration.Next();
                    Console.WriteLine($"{nameof(loRaDeviceApi.Object.SearchByDevAddrAsync)} sleeping for {duration}");
                    return Task.Delay(duration)
                        .ContinueWith((a) => new SearchDevicesResult(new IoTHubDeviceInfo(devAddr, devEUI, "abc").AsList()));
                });

            // using factory to create mock of
            var loRaDeviceFactory = new TestLoRaDeviceFactory(loRaDeviceClient.Object);

            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var deviceRegistry = new LoRaDeviceRegistry(this.ServerConfiguration, memoryCache, loRaDeviceApi.Object, loRaDeviceFactory);

            var frameCounterUpdateStrategyFactory = new LoRaDeviceFrameCounterUpdateStrategyFactory(this.ServerConfiguration.GatewayID, loRaDeviceApi.Object);

            // Send to message processor
            var messageProcessor = new MessageProcessor(
                this.ServerConfiguration,
                deviceRegistry,
                frameCounterUpdateStrategyFactory,
                new LoRaPayloadDecoder());

            // sends unconfirmed message
            var unconfirmedMessage1 = simulatedDevice.CreateUnconfirmedMessageUplink("1", fcnt: 1).Rxpk[0];
            var unconfirmedMessage2 = simulatedDevice.CreateUnconfirmedMessageUplink("2", fcnt: 2).Rxpk[0];
            var unconfirmedMessage3 = simulatedDevice.CreateUnconfirmedMessageUplink("3", fcnt: 3).Rxpk[0];

            var packetForwarder = new TestPacketForwarder();

            var req1 = new WaitableLoRaRequest(unconfirmedMessage1, this.packetForwarder);
            messageProcessor.DispatchRequest(req1);
            await Task.Delay(parallelTestConfiguration.BetweenMessageDuration.Next());

            var req2 = new WaitableLoRaRequest(unconfirmedMessage2, this.packetForwarder);
            messageProcessor.DispatchRequest(req2);
            await Task.Delay(parallelTestConfiguration.BetweenMessageDuration.Next());

            var req3 = new WaitableLoRaRequest(unconfirmedMessage3, this.packetForwarder);
            messageProcessor.DispatchRequest(req3);
            await Task.Delay(parallelTestConfiguration.BetweenMessageDuration.Next());

            await Task.WhenAll(req1.WaitCompleteAsync(), req2.WaitCompleteAsync(), req3.WaitCompleteAsync());

            Assert.Null(req1.ResponseDownlink);
            Assert.Null(req2.ResponseDownlink);
            Assert.Null(req3.ResponseDownlink);

            loRaDeviceClient.Verify(x => x.GetTwinAsync(), Times.Exactly(1));
            loRaDeviceClient.Verify(x => x.UpdateReportedPropertiesAsync(It.IsAny<TwinCollection>()), Times.Exactly(1));
            // loRaDeviceClient.Verify(x => x.ReceiveAsync(It.IsAny<TimeSpan>()), Times.Exactly(1));
            loRaDeviceApi.Verify(x => x.SearchByDevAddrAsync(devAddr), Times.Once);

            // Ensure that all telemetry was sent
            Assert.Equal(3, sentTelemetry.Count);

            // Ensure that the device twins were saved
            if (shouldSaveTwin)
            {
                Assert.NotNull(fcntDownSavedInTwin);
                Assert.NotNull(fcntUpSavedInTwin);
                Assert.Equal(0, fcntDownSavedInTwin.Value);
                Assert.Equal(0, fcntUpSavedInTwin.Value);
            }

            // verify that the device in device registry has correct properties and frame counters
            var devicesForDevAddr = deviceRegistry.InternalGetCachedDevicesForDevAddr(devAddr);
            Assert.Single(devicesForDevAddr);
            Assert.True(devicesForDevAddr.TryGetValue(devEUI, out var loRaDevice));
            Assert.Equal(devAddr, loRaDevice.DevAddr);
            Assert.Equal(devEUI, loRaDevice.DevEUI);
            Assert.True(loRaDevice.IsABP);
            Assert.Equal(3, loRaDevice.FCntUp);
            Assert.Equal(0, loRaDevice.FCntDown);
            Assert.True(loRaDevice.HasFrameCountChanges); // should have changes!

            loRaDeviceClient.VerifyAll();
            loRaDeviceApi.VerifyAll();
        }
    }
}
