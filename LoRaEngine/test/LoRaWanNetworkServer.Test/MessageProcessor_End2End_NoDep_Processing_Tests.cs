// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace LoRaWan.NetworkServer.Test
{
    using System;
    using System.Collections.Generic;
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
    // General message processor tests (Join tests are handled in other class)
    public class MessageProcessor_End2End_NoDep_Processing_Tests : MessageProcessorTestBase
    {
        private readonly IPacketForwarder packetForwarder;

        public MessageProcessor_End2End_NoDep_Processing_Tests()
        {
            this.packetForwarder = new TestPacketForwarder();
        }

        [Theory]
        [InlineData(ServerGatewayID, 0, 0, 0)]
        [InlineData(ServerGatewayID, 0, 1, 1)]
        [InlineData(ServerGatewayID, 0, 100, 20)]
        [InlineData(ServerGatewayID, 1, 0, 0)]
        [InlineData(ServerGatewayID, 1, 1, 1)]
        [InlineData(ServerGatewayID, 1, 100, 20)]
        [InlineData(null, 0, 0, 0)]
        [InlineData(null, 0, 1, 1)]
        [InlineData(null, 0, 100, 20)]
        [InlineData(null, 1, 0, 0)]
        [InlineData(null, 1, 1, 1)]
        [InlineData(null, 1, 100, 20)]
        public async Task ABP_Cached_Device_With_Fcnt_1_Or_0_Should_Reset_Fcnt_And_Send_To_IotHub(
            string deviceGatewayID,
            int payloadFcntUp,
            int deviceInitialFcntUp,
            int deviceInitialFcntDown)
        {
            var simulatedDevice = new SimulatedDevice(TestDeviceInfo.CreateABPDevice(1, gatewayID: deviceGatewayID));
            simulatedDevice.FrmCntDown = deviceInitialFcntDown;
            simulatedDevice.FrmCntUp = deviceInitialFcntUp;

            var devEUI = simulatedDevice.LoRaDevice.DeviceID;
            var devAddr = simulatedDevice.LoRaDevice.DevAddr;

            // message will be sent
            LoRaDeviceTelemetry loRaDeviceTelemetry = null;
            this.LoRaDeviceClient.Setup(x => x.SendEventAsync(It.IsNotNull<LoRaDeviceTelemetry>(), null))
                .Callback<LoRaDeviceTelemetry, Dictionary<string, string>>((t, _) => loRaDeviceTelemetry = t)
                .ReturnsAsync(true);

            // C2D message will be checked
            this.LoRaDeviceClient.Setup(x => x.ReceiveAsync(It.IsNotNull<TimeSpan>()))
                .ReturnsAsync((Message)null);

            // twin will be updated with new fcnt
            int? fcntUpSavedInTwin = null;
            int? fcntDownSavedInTwin = null;

            // twin should be saved only if not starting at 0, 0
            var shouldSaveTwin = deviceInitialFcntDown != 0 || deviceInitialFcntUp != 0;
            if (shouldSaveTwin)
            {
                // Twin will be saved
                this.LoRaDeviceClient.Setup(x => x.UpdateReportedPropertiesAsync(It.IsNotNull<TwinCollection>()))
                    .Callback<TwinCollection>((t) =>
                    {
                        fcntUpSavedInTwin = (int)t[TwinProperty.FCntUp];
                        fcntDownSavedInTwin = (int)t[TwinProperty.FCntDown];
                    })
                    .ReturnsAsync(true);
            }

            var cachedDevice = this.CreateLoRaDevice(simulatedDevice);
            var devEUIDeviceDict = new DevEUIToLoRaDeviceDictionary();
            devEUIDeviceDict.TryAdd(devEUI, cachedDevice);
            var memoryCache = this.NewMemoryCache();
            memoryCache.Set(devAddr, devEUIDeviceDict);

            var deviceRegistry = new LoRaDeviceRegistry(this.ServerConfiguration, memoryCache, this.LoRaDeviceApi.Object, this.LoRaDeviceFactory);

            // Send to message processor
            var messageProcessor = new MessageProcessor(
                this.ServerConfiguration,
                deviceRegistry,
                this.FrameCounterUpdateStrategyFactory);

            // sends unconfirmed message
            var unconfirmedMessagePayload = simulatedDevice.CreateUnconfirmedDataUpMessage("hello", fcnt: payloadFcntUp);
            var rxpk = unconfirmedMessagePayload.SerializeUplink(simulatedDevice.AppSKey, simulatedDevice.NwkSKey).Rxpk[0];
            var request = new WaitableLoRaRequest(rxpk, this.packetForwarder);
            messageProcessor.DispatchRequest(request);
            Assert.True(await request.WaitCompleteAsync());
            Assert.Null(request.ResponseDownlink);

            // Ensure that a telemetry was sent
            Assert.NotNull(loRaDeviceTelemetry);
            // Assert.Equal(msgPayload, loRaDeviceTelemetry.data);

            // Ensure that the device twins were saved
            if (shouldSaveTwin)
            {
                Assert.NotNull(fcntDownSavedInTwin);
                Assert.NotNull(fcntUpSavedInTwin);
                Assert.Equal(0, fcntDownSavedInTwin.Value); // fcntDown will be set to zero
                Assert.Equal(0, fcntUpSavedInTwin.Value); // fcntUp will be set to zero
            }

            // verify that the device in device registry has correct properties and frame counters
            var devicesForDevAddr = deviceRegistry.InternalGetCachedDevicesForDevAddr(devAddr);
            Assert.Single(devicesForDevAddr);
            Assert.True(devicesForDevAddr.TryGetValue(devEUI, out var loRaDevice));
            Assert.Equal(devAddr, loRaDevice.DevAddr);
            Assert.Equal(devEUI, loRaDevice.DevEUI);
            Assert.True(loRaDevice.IsABP);
            Assert.Equal(payloadFcntUp, loRaDevice.FCntUp);
            Assert.Equal(0, loRaDevice.FCntDown); // fctn down will always be set to zero
            if (payloadFcntUp == 0)
                Assert.False(loRaDevice.HasFrameCountChanges); // no changes
            else
                Assert.True(loRaDevice.HasFrameCountChanges); // there are pending changes (fcntUp 0 => 1)

            // will update api in multi gateway scenario
            if (string.IsNullOrEmpty(deviceGatewayID))
            {
                this.LoRaDeviceApi.Verify(x => x.ABPFcntCacheResetAsync(devEUI), Times.Exactly(1));
            }

            this.LoRaDeviceClient.VerifyAll();
            this.LoRaDeviceApi.VerifyAll();
        }

        [Theory]
        [InlineData(0, null, null)]
        [InlineData(0, 1, 1)]
        [InlineData(0, 100, 20)]
        [InlineData(1, null, null)]
        [InlineData(1, 1, 1)]
        [InlineData(1, 100, 20)]
        public async Task SingleGateway_ABP_New_Loaded_Device_With_Fcnt_1_Or_0_Should_Reset_Fcnt_And_Send_To_IotHub(
            int payloadFcntUp,
            int? deviceTwinFcntUp,
            int? deviceTwinFcntDown)
        {
            var simulatedDevice = new SimulatedDevice(TestDeviceInfo.CreateABPDevice(1));

            var devEUI = simulatedDevice.LoRaDevice.DeviceID;
            var devAddr = simulatedDevice.LoRaDevice.DevAddr;

            // message will be sent
            LoRaDeviceTelemetry loRaDeviceTelemetry = null;
            this.LoRaDeviceClient.Setup(x => x.SendEventAsync(It.IsNotNull<LoRaDeviceTelemetry>(), null))
                .Callback<LoRaDeviceTelemetry, Dictionary<string, string>>((t, _) => loRaDeviceTelemetry = t)
                .ReturnsAsync(true);

            // C2D message will be checked
            this.LoRaDeviceClient.Setup(x => x.ReceiveAsync(It.IsNotNull<TimeSpan>()))
                .ReturnsAsync((Message)null);

            // twin will be loaded
            var initialTwin = new Twin();
            initialTwin.Properties.Desired[TwinProperty.DevEUI] = devEUI;
            initialTwin.Properties.Desired[TwinProperty.AppEUI] = simulatedDevice.LoRaDevice.AppEUI;
            initialTwin.Properties.Desired[TwinProperty.AppKey] = simulatedDevice.LoRaDevice.AppKey;
            initialTwin.Properties.Desired[TwinProperty.NwkSKey] = simulatedDevice.LoRaDevice.NwkSKey;
            initialTwin.Properties.Desired[TwinProperty.AppSKey] = simulatedDevice.LoRaDevice.AppSKey;
            initialTwin.Properties.Desired[TwinProperty.DevAddr] = devAddr;
            initialTwin.Properties.Desired[TwinProperty.GatewayID] = this.ServerConfiguration.GatewayID;
            initialTwin.Properties.Desired[TwinProperty.SensorDecoder] = simulatedDevice.LoRaDevice.SensorDecoder;
            if (deviceTwinFcntDown.HasValue)
                initialTwin.Properties.Reported[TwinProperty.FCntDown] = deviceTwinFcntDown.Value;
            if (deviceTwinFcntUp.HasValue)
                initialTwin.Properties.Reported[TwinProperty.FCntUp] = deviceTwinFcntUp.Value;

            this.LoRaDeviceClient.Setup(x => x.GetTwinAsync()).ReturnsAsync(initialTwin);

            // twin will be updated with new fcnt
            int? fcntUpSavedInTwin = null;
            int? fcntDownSavedInTwin = null;

            // Twin will be save (0, 0) only if it was not 0, 0
            this.LoRaDeviceClient.Setup(x => x.UpdateReportedPropertiesAsync(It.IsNotNull<TwinCollection>()))
                .Callback<TwinCollection>((t) =>
                {
                    fcntUpSavedInTwin = (int)t[TwinProperty.FCntUp];
                    fcntDownSavedInTwin = (int)t[TwinProperty.FCntDown];
                })
                .ReturnsAsync(true);

            // device api will be searched for payload
            this.LoRaDeviceApi.Setup(x => x.SearchByDevAddrAsync(devAddr))
                .ReturnsAsync(new SearchDevicesResult(new IoTHubDeviceInfo(devAddr, devEUI, "abc").AsList()));

            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var deviceRegistry = new LoRaDeviceRegistry(this.ServerConfiguration, memoryCache, this.LoRaDeviceApi.Object, this.LoRaDeviceFactory);

            // Send to message processor
            var messageProcessor = new MessageProcessor(
                this.ServerConfiguration,
                deviceRegistry,
                this.FrameCounterUpdateStrategyFactory);

            // sends unconfirmed message
            var unconfirmedMessagePayload = simulatedDevice.CreateUnconfirmedDataUpMessage("hello", fcnt: payloadFcntUp);
            var rxpk = unconfirmedMessagePayload.SerializeUplink(simulatedDevice.AppSKey, simulatedDevice.NwkSKey).Rxpk[0];
            var request = new WaitableLoRaRequest(rxpk, this.packetForwarder);
            messageProcessor.DispatchRequest(request);
            Assert.True(await request.WaitCompleteAsync());
            Assert.Null(request.ResponseDownlink);

            // Ensure that a telemetry was sent
            Assert.NotNull(loRaDeviceTelemetry);
            // Assert.Equal(msgPayload, loRaDeviceTelemetry.data);

            // Ensure that the device twins were saved
            Assert.NotNull(fcntDownSavedInTwin);
            Assert.NotNull(fcntUpSavedInTwin);
            Assert.Equal(0, fcntDownSavedInTwin.Value);
            Assert.Equal(0, fcntUpSavedInTwin.Value);

            // verify that the device in device registry has correct properties and frame counters
            var devicesForDevAddr = deviceRegistry.InternalGetCachedDevicesForDevAddr(devAddr);
            Assert.Single(devicesForDevAddr);
            Assert.True(devicesForDevAddr.TryGetValue(devEUI, out var loRaDevice));
            Assert.Equal(devAddr, loRaDevice.DevAddr);
            Assert.Equal(devEUI, loRaDevice.DevEUI);
            Assert.True(loRaDevice.IsABP);
            Assert.Equal(payloadFcntUp, loRaDevice.FCntUp);
            Assert.Equal(0, loRaDevice.FCntDown);
            if (payloadFcntUp == 0)
                Assert.False(loRaDevice.HasFrameCountChanges); // no changes
            else
                Assert.True(loRaDevice.HasFrameCountChanges); // there are pending changes (fcntUp 0 => 1)

            this.LoRaDeviceClient.VerifyAll();
            this.LoRaDeviceApi.VerifyAll();
        }

        [Theory]
        [InlineData(ServerGatewayID, "1234", "")]
        [InlineData(ServerGatewayID, "hello world", null)]
        [InlineData(null, "hello world", null)]
        [InlineData(null, "1234", "")]
        public async Task ABP_Unconfirmed_With_No_Decoder_Sends_Raw_Payload(string deviceGatewayID, string msgPayload, string sensorDecoder)
        {
            var simulatedDevice = new SimulatedDevice(TestDeviceInfo.CreateABPDevice(1, gatewayID: deviceGatewayID));

            var loRaDevice = this.CreateLoRaDevice(simulatedDevice);
            loRaDevice.SensorDecoder = sensorDecoder;

            // message will be sent
            LoRaDeviceTelemetry loRaDeviceTelemetry = null;
            this.LoRaDeviceClient.Setup(x => x.SendEventAsync(It.IsNotNull<LoRaDeviceTelemetry>(), null))
                .Callback<LoRaDeviceTelemetry, Dictionary<string, string>>((t, _) => loRaDeviceTelemetry = t)
                .ReturnsAsync(true);

            // C2D message will be checked
            this.LoRaDeviceClient.Setup(x => x.ReceiveAsync(It.IsNotNull<TimeSpan>()))
                .ReturnsAsync((Message)null);

            // add device to cache already
            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var dictionary = new DevEUIToLoRaDeviceDictionary();
            dictionary[loRaDevice.DevEUI] = loRaDevice;
            memoryCache.Set<DevEUIToLoRaDeviceDictionary>(loRaDevice.DevAddr, dictionary);

            var deviceRegistry = new LoRaDeviceRegistry(this.ServerConfiguration, memoryCache, this.LoRaDeviceApi.Object, this.LoRaDeviceFactory);

            // Send to message processor
            var messageProcessor = new MessageProcessor(
                this.ServerConfiguration,
                deviceRegistry,
                this.FrameCounterUpdateStrategyFactory);

            // sends unconfirmed message
            var unconfirmedMessagePayload = simulatedDevice.CreateUnconfirmedDataUpMessage(msgPayload, fcnt: 1);
            var rxpk = unconfirmedMessagePayload.SerializeUplink(simulatedDevice.AppSKey, simulatedDevice.NwkSKey).Rxpk[0];
            var request = new WaitableLoRaRequest(rxpk, this.packetForwarder);
            messageProcessor.DispatchRequest(request);
            Assert.True(await request.WaitCompleteAsync());
            Assert.Null(request.ResponseDownlink);

            Assert.NotNull(loRaDeviceTelemetry);
            Assert.IsType<string>(loRaDeviceTelemetry.Data);
            var expectedPayloadContent = Convert.ToBase64String(Encoding.UTF8.GetBytes(msgPayload));
            Assert.Equal(expectedPayloadContent, loRaDeviceTelemetry.Data);

            this.LoRaDeviceClient.VerifyAll();
            this.LoRaDeviceApi.VerifyAll();
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(0, 1)]
        [InlineData(255, 1)]
        [InlineData(16777215, 16777215)]
        [InlineData(127, 127)]
        [InlineData(255, 255)]
        public async Task ABP_Device_NetId_Should_Match_Server(uint deviceNetId, uint serverNetId)
        {
            string msgPayload = "1234";
            var simulatedDevice = new SimulatedDevice(TestDeviceInfo.CreateABPDevice(1, netId: deviceNetId));

            var loRaDevice = this.CreateLoRaDevice(simulatedDevice);
            loRaDevice.SensorDecoder = null;

            // message will be sent if there is a match
            bool netIdMatches = deviceNetId == serverNetId;
            LoRaDeviceTelemetry loRaDeviceTelemetry = null;
            if (netIdMatches)
            {
                this.LoRaDeviceClient.Setup(x => x.SendEventAsync(It.IsNotNull<LoRaDeviceTelemetry>(), null))
                    .Callback<LoRaDeviceTelemetry, Dictionary<string, string>>((t, _) => loRaDeviceTelemetry = t)
                    .ReturnsAsync(true);

                // C2D message will be checked
                this.LoRaDeviceClient.Setup(x => x.ReceiveAsync(It.IsNotNull<TimeSpan>()))
                    .ReturnsAsync((Message)null);
            }

            // Lora device api

            // add device to cache already
            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var dictionary = new DevEUIToLoRaDeviceDictionary();
            dictionary[loRaDevice.DevEUI] = loRaDevice;
            memoryCache.Set<DevEUIToLoRaDeviceDictionary>(loRaDevice.DevAddr, dictionary);
            this.ServerConfiguration.NetId = serverNetId;
            var deviceRegistry = new LoRaDeviceRegistry(this.ServerConfiguration, memoryCache, this.LoRaDeviceApi.Object, this.LoRaDeviceFactory);

            // Send to message processor
            var messageProcessor = new LoRaWan.NetworkServer.MessageProcessor(
                this.ServerConfiguration,
                deviceRegistry,
                this.FrameCounterUpdateStrategyFactory);

            // sends unconfirmed message
            var unconfirmedMessagePayload = simulatedDevice.CreateUnconfirmedDataUpMessage(msgPayload, fcnt: 1);
            var rxpk = unconfirmedMessagePayload.SerializeUplink(simulatedDevice.AppSKey, simulatedDevice.NwkSKey).Rxpk[0];
            var request = new WaitableLoRaRequest(rxpk, this.packetForwarder);
            messageProcessor.DispatchRequest(request);
            Assert.True(await request.WaitCompleteAsync());
            Assert.Null(request.ResponseDownlink);

            if (netIdMatches)
            {
                Assert.NotNull(loRaDeviceTelemetry);
                Assert.IsType<string>(loRaDeviceTelemetry.Data);
                var expectedPayloadContent = Convert.ToBase64String(Encoding.UTF8.GetBytes(msgPayload));
                Assert.Equal(expectedPayloadContent, loRaDeviceTelemetry.Data);
            }
            else
            {
                Assert.Null(loRaDeviceTelemetry);
                Assert.Equal(0, loRaDevice.FCntUp);
            }

            this.LoRaDeviceClient.VerifyAll();
            this.LoRaDeviceApi.VerifyAll();
        }

        [Theory]
        [InlineData(ServerGatewayID, "1234")]
        [InlineData(null, "1234")]
        public async Task ABP_Unconfirmed_With_Value_Decoder_Sends_Decoded_Numeric_Payload(string deviceGatewayID, string msgPayload)
        {
            var simulatedDevice = new SimulatedDevice(TestDeviceInfo.CreateABPDevice(1, gatewayID: deviceGatewayID));

            var loRaDevice = this.CreateLoRaDevice(simulatedDevice);
            loRaDevice.SensorDecoder = "DecoderValueSensor";

            // message will be sent
            LoRaDeviceTelemetry loRaDeviceTelemetry = null;
            this.LoRaDeviceClient.Setup(x => x.SendEventAsync(It.IsNotNull<LoRaDeviceTelemetry>(), null))
                .Callback<LoRaDeviceTelemetry, Dictionary<string, string>>((t, _) => loRaDeviceTelemetry = t)
                .ReturnsAsync(true);

            // C2D message will be checked
            this.LoRaDeviceClient.Setup(x => x.ReceiveAsync(It.IsNotNull<TimeSpan>()))
                .ReturnsAsync((Message)null);

            // Lora device api

            // add device to cache already
            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var dictionary = new DevEUIToLoRaDeviceDictionary();
            dictionary[loRaDevice.DevEUI] = loRaDevice;
            memoryCache.Set<DevEUIToLoRaDeviceDictionary>(loRaDevice.DevAddr, dictionary);

            var deviceRegistry = new LoRaDeviceRegistry(this.ServerConfiguration, memoryCache, this.LoRaDeviceApi.Object, this.LoRaDeviceFactory);

            // Send to message processor
            var messageProcessor = new MessageProcessor(
                this.ServerConfiguration,
                deviceRegistry,
                this.FrameCounterUpdateStrategyFactory);

            // sends unconfirmed message
            var unconfirmedMessagePayload = simulatedDevice.CreateUnconfirmedDataUpMessage(msgPayload, fcnt: 1);
            var rxpk = unconfirmedMessagePayload.SerializeUplink(simulatedDevice.AppSKey, simulatedDevice.NwkSKey).Rxpk[0];
            var request = new WaitableLoRaRequest(rxpk, this.packetForwarder);
            messageProcessor.DispatchRequest(request);
            Assert.True(await request.WaitCompleteAsync());
            Assert.Null(request.ResponseDownlink);

            Assert.NotNull(loRaDeviceTelemetry);
            Assert.IsType<JObject>(loRaDeviceTelemetry.Data);
            var telemetryData = (JObject)loRaDeviceTelemetry.Data;
            Assert.Equal(msgPayload, telemetryData["value"].ToString());

            this.LoRaDeviceApi.VerifyAll();
            this.LoRaDeviceClient.VerifyAll();
        }

        [Theory]
        [InlineData(ServerGatewayID, null)]
        [InlineData(ServerGatewayID, "test")]
        [InlineData(ServerGatewayID, "test", "idtest")]
        public async Task When_Ack_Message_Received_Should_Be_In_Msg_Properties(string deviceGatewayID, string data, string msgId = null)
        {
            const int initialFcntUp = 100;
            const int payloadFcnt = 102;
            var simulatedDevice = new SimulatedDevice(TestDeviceInfo.CreateABPDevice(1, gatewayID: deviceGatewayID));
            simulatedDevice.FrmCntUp = initialFcntUp;

            var loRaDevice = this.CreateLoRaDevice(simulatedDevice);
            if (msgId != null)
                loRaDevice.LastConfirmedC2DMessageID = msgId;

            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var dictionary = new DevEUIToLoRaDeviceDictionary();
            dictionary[loRaDevice.DevEUI] = loRaDevice;
            memoryCache.Set<DevEUIToLoRaDeviceDictionary>(loRaDevice.DevAddr, dictionary);

            // using factory to create mock of
            var deviceRegistry = new LoRaDeviceRegistry(this.ServerConfiguration, memoryCache, this.LoRaDeviceApi.Object, this.LoRaDeviceFactory);
            this.LoRaDeviceClient.Setup(x => x.SendEventAsync(It.IsAny<LoRaDeviceTelemetry>(), It.IsAny<Dictionary<string, string>>()))
                .Callback<LoRaDeviceTelemetry, Dictionary<string, string>>((t, d) =>
                {
                    Assert.NotNull(d);
                    Assert.True(d.ContainsKey(MessageProcessor.C2D_MSG_PROPERTY_VALUE_NAME));

                    if (msgId == null)
                        Assert.True(d.ContainsValue(MessageProcessor.C2D_MSG_ID_PLACEHOLDER));
                    else
                        Assert.True(d.ContainsValue(msgId));
                })
                .Returns(Task.FromResult(true));

            // C2D message will be checked
            this.LoRaDeviceClient.Setup(x => x.ReceiveAsync(It.IsNotNull<TimeSpan>()))
                .ReturnsAsync((Message)null);

            var messageProcessor = new MessageProcessor(
               this.ServerConfiguration,
               deviceRegistry,
               this.FrameCounterUpdateStrategyFactory);

            var ackMessage = simulatedDevice.CreateUnconfirmedDataUpMessage(data, fcnt: payloadFcnt, fctrl: (byte)FctrlEnum.Ack);
            var ackRxpk = ackMessage.SerializeUplink(simulatedDevice.AppSKey, simulatedDevice.NwkSKey).Rxpk[0];
            var ackRequest = new WaitableLoRaRequest(ackRxpk, this.packetForwarder);
            messageProcessor.DispatchRequest(ackRequest);
            Assert.True(await ackRequest.WaitCompleteAsync());
            Assert.Null(ackRequest.ResponseDownlink);
            Assert.True(deviceRegistry.InternalGetCachedDevicesForDevAddr(loRaDevice.DevAddr).TryGetValue(loRaDevice.DevEUI, out var loRaDeviceInfo));

            Assert.Equal(payloadFcnt, loRaDeviceInfo.FCntUp);
        }

        [Theory]
        [InlineData(ServerGatewayID, 21)]
        [InlineData(null, 21)]
        [InlineData(null, 30)]
        public async Task When_ConfirmedUp_Message_With_Same_Fcnt_Should_Return_Ack_And_Not_Send_To_Hub(string deviceGatewayID, int expectedFcntDown)
        {
            const int initialFcntUp = 100;
            const int initialFcntDown = 20;

            var simulatedDevice = new SimulatedDevice(TestDeviceInfo.CreateABPDevice(1, gatewayID: deviceGatewayID));
            simulatedDevice.FrmCntUp = initialFcntUp;
            simulatedDevice.FrmCntDown = initialFcntDown;

            var loRaDevice = this.CreateLoRaDevice(simulatedDevice);

            var devEUI = simulatedDevice.LoRaDevice.DeviceID;

            // C2D message will be checked
            this.LoRaDeviceClient.Setup(x => x.ReceiveAsync(It.IsNotNull<TimeSpan>()))
                .ReturnsAsync((Message)null);

            // Lora device api

            // in multigateway scenario the device api will be called to resolve fcntDown
            if (string.IsNullOrEmpty(deviceGatewayID))
            {
                this.LoRaDeviceApi.Setup(x => x.NextFCntDownAsync(devEUI, 20, 100, this.ServerConfiguration.GatewayID))
                    .ReturnsAsync((ushort)expectedFcntDown);
            }

            // add device to cache already
            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var dictionary = new DevEUIToLoRaDeviceDictionary();
            dictionary[loRaDevice.DevEUI] = loRaDevice;
            memoryCache.Set<DevEUIToLoRaDeviceDictionary>(loRaDevice.DevAddr, dictionary);

            var deviceRegistry = new LoRaDeviceRegistry(this.ServerConfiguration, memoryCache, this.LoRaDeviceApi.Object, this.LoRaDeviceFactory);

            // Send to message processor
            var messageProcessor = new MessageProcessor(
                this.ServerConfiguration,
                deviceRegistry,
                this.FrameCounterUpdateStrategyFactory);

            // sends confirmed message
            var confirmedMessagePayload = simulatedDevice.CreateConfirmedDataUpMessage("repeat", fcnt: 100);
            var rxpk = confirmedMessagePayload.SerializeUplink(simulatedDevice.AppSKey, simulatedDevice.NwkSKey).Rxpk[0];
            var request = new WaitableLoRaRequest(rxpk, this.packetForwarder);
            messageProcessor.DispatchRequest(request);
            Assert.True(await request.WaitCompleteAsync());

            // ack should be received
            Assert.NotNull(request.ResponseDownlink);
            var txpk = request.ResponseDownlink.Txpk;
            Assert.NotNull(txpk);

            // validates txpk according to eu region
            Assert.Equal(RegionFactory.CreateEU868Region().GetDownstreamChannel(rxpk), txpk.Freq);
            Assert.Equal("4/5", txpk.Codr);
            Assert.False(txpk.Imme);
            Assert.True(txpk.Ipol);
            Assert.Equal("LORA", txpk.Modu);

            // Expected changes to fcnt:
            // FcntDown => expectedFcntDown
            Assert.Equal(initialFcntUp, loRaDevice.FCntUp);
            Assert.Equal(expectedFcntDown, loRaDevice.FCntDown);
            Assert.True(loRaDevice.HasFrameCountChanges);

            // message should not be sent to iot hub
            this.LoRaDeviceClient.Verify(x => x.SendEventAsync(It.IsAny<LoRaDeviceTelemetry>(), It.IsAny<Dictionary<string, string>>()), Times.Never);
            this.LoRaDeviceClient.VerifyAll();
            this.LoRaDeviceApi.VerifyAll();
        }

        /// <summary>
        /// This tests the multi gateway scenario where a 2nd gateway cannot find the joined device because IoT Hub twin has not yet been updated
        /// It device api will not find it, only once the device registry finds it the message will be sent to IoT Hub
        /// </summary>
        [Fact]
        public async Task When_Second_Gateway_Does_Not_Find_Device_Should_Keep_Trying_On_Subsequent_Requests()
        {
            var simulatedDevice = new SimulatedDevice(TestDeviceInfo.CreateOTAADevice(1));
            const string devAddr = "02AABBCC";
            const string nwkSKey = "00000000000000000000000000000002";
            const string appSKey = "00000000000000000000000000000001";
            var devEUI = simulatedDevice.DevEUI;

            simulatedDevice.SetupJoin(appSKey, nwkSKey, devAddr);

            var updatedTwin = TestUtils.CreateTwin(
                desired: new Dictionary<string, object>
                {
                    { TwinProperty.AppEUI, simulatedDevice.AppEUI },
                    { TwinProperty.AppKey, simulatedDevice.AppKey },
                    { TwinProperty.SensorDecoder, nameof(LoRaPayloadDecoder.DecoderValueSensor) },
                },
                reported: new Dictionary<string, object>
                {
                    { TwinProperty.AppSKey, appSKey },
                    { TwinProperty.NwkSKey, nwkSKey },
                    { TwinProperty.DevAddr, devAddr },
                    { TwinProperty.DevNonce, "ABCD" },
                });

            var deviceClientMock = new Mock<ILoRaDeviceClient>(MockBehavior.Strict);

            // Twin will be loaded once
            deviceClientMock.Setup(x => x.GetTwinAsync())
                .ReturnsAsync(updatedTwin);

            // Will check received messages once
            deviceClientMock.Setup(x => x.ReceiveAsync(It.IsNotNull<TimeSpan>())).ReturnsAsync((Message)null);

            // Will send the 3 unconfirmed message
            deviceClientMock.Setup(x => x.SendEventAsync(It.IsAny<LoRaDeviceTelemetry>(), It.IsAny<Dictionary<string, string>>()))
                .Callback<LoRaDeviceTelemetry, Dictionary<string, string>>((t, _) =>
                {
                    Assert.NotNull(t.Data);
                    Assert.IsType<JObject>(t.Data);
                    Assert.Equal("3", ((JObject)t.Data)["value"].ToString());
                })
                .ReturnsAsync(true);

            // Will try to find the iot device based on dev addr
            this.LoRaDeviceApi.SetupSequence(x => x.SearchByDevAddrAsync(devAddr))
                .ReturnsAsync(new SearchDevicesResult())
                .ReturnsAsync(new SearchDevicesResult())
                .ReturnsAsync(new SearchDevicesResult(new IoTHubDeviceInfo(devAddr, devEUI, "abc").AsList()));

            var deviceFactory = new TestLoRaDeviceFactory(this.ServerConfiguration, this.FrameCounterUpdateStrategyFactory, deviceClientMock.Object);

            var deviceRegistry = new LoRaDeviceRegistry(
                this.ServerConfiguration,
                new MemoryCache(new MemoryCacheOptions()),
                this.LoRaDeviceApi.Object,
                deviceFactory);

            var messageProcessor = new MessageProcessor(
               this.ServerConfiguration,
               deviceRegistry,
               this.FrameCounterUpdateStrategyFactory);

            // Unconfirmed message #1 should fail
            var unconfirmedRxpk1 = simulatedDevice.CreateUnconfirmedDataUpMessage("1", fcnt: 1)
                .SerializeUplink(simulatedDevice.AppSKey, simulatedDevice.NwkSKey).Rxpk[0];
            var unconfirmedRequest1 = new WaitableLoRaRequest(unconfirmedRxpk1, this.packetForwarder);
            messageProcessor.DispatchRequest(unconfirmedRequest1);
            Assert.True(await unconfirmedRequest1.WaitCompleteAsync());
            Assert.Null(unconfirmedRequest1.ResponseDownlink);
            Assert.True(unconfirmedRequest1.ProcessingFailed);
            Assert.Equal(LoRaDeviceRequestFailedReason.NotMatchingDeviceByDevAddr, unconfirmedRequest1.ProcessingFailedReason);

            // Unconfirmed message #2 should fail
            var unconfirmedRxpk2 = simulatedDevice.CreateUnconfirmedDataUpMessage("2", fcnt: 2)
                .SerializeUplink(simulatedDevice.AppSKey, simulatedDevice.NwkSKey).Rxpk[0];
            var unconfirmedRequest2 = new WaitableLoRaRequest(unconfirmedRxpk2, this.packetForwarder);
            messageProcessor.DispatchRequest(unconfirmedRequest2);
            Assert.True(await unconfirmedRequest2.WaitCompleteAsync());
            Assert.Null(unconfirmedRequest2.ResponseDownlink);
            Assert.True(unconfirmedRequest2.ProcessingFailed);
            Assert.Equal(LoRaDeviceRequestFailedReason.NotMatchingDeviceByDevAddr, unconfirmedRequest2.ProcessingFailedReason);

            // Unconfirmed message #3 should succeed
            var unconfirmedRxpk3 = simulatedDevice.CreateUnconfirmedDataUpMessage("3", fcnt: 3)
                .SerializeUplink(simulatedDevice.AppSKey, simulatedDevice.NwkSKey).Rxpk[0];
            var unconfirmedRequest3 = new WaitableLoRaRequest(unconfirmedRxpk3, this.packetForwarder);
            messageProcessor.DispatchRequest(unconfirmedRequest3);
            Assert.True(await unconfirmedRequest3.WaitCompleteAsync());
            Assert.True(unconfirmedRequest3.ProcessingSucceeded);
            Assert.Null(unconfirmedRequest3.ResponseDownlink);

            deviceClientMock.VerifyAll();
            this.LoRaDeviceApi.VerifyAll();
        }

        /// <summary>
        /// Downlink should use same rfch than uplink message
        /// RFCH stands for Concentrator "RF chain" used for RX
        /// </summary>
        [Theory]
        [InlineData(ServerGatewayID, 1)]
        [InlineData(ServerGatewayID, 0)]
        public async Task ABP_Confirmed_Message_Should_Use_Rchf_0(string deviceGatewayID, uint rfch)
        {
            var simulatedDevice = new SimulatedDevice(TestDeviceInfo.CreateABPDevice(1, gatewayID: deviceGatewayID));
            simulatedDevice.FrmCntDown = 20;
            simulatedDevice.FrmCntUp = 100;

            var devEUI = simulatedDevice.LoRaDevice.DeviceID;
            var devAddr = simulatedDevice.LoRaDevice.DevAddr;

            // message will be sent
            LoRaDeviceTelemetry loRaDeviceTelemetry = null;

            this.LoRaDeviceClient.Setup(x => x.SendEventAsync(It.IsNotNull<LoRaDeviceTelemetry>(), null))
                .Callback<LoRaDeviceTelemetry, Dictionary<string, string>>((t, _) => loRaDeviceTelemetry = t)
                .ReturnsAsync(true);

            // C2D message will be checked
            this.LoRaDeviceClient.Setup(x => x.ReceiveAsync(It.IsNotNull<TimeSpan>()))
                .ReturnsAsync((Message)null);

            // Lora device api
            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var cachedDevice = this.CreateLoRaDevice(simulatedDevice);

            var devEUIDeviceDict = new DevEUIToLoRaDeviceDictionary();
            devEUIDeviceDict.TryAdd(devEUI, cachedDevice);
            memoryCache.Set(devAddr, devEUIDeviceDict);

            var deviceRegistry = new LoRaDeviceRegistry(this.ServerConfiguration, memoryCache, this.LoRaDeviceApi.Object, this.LoRaDeviceFactory);

            // Send to message processor
            var messageProcessor = new MessageProcessor(
                this.ServerConfiguration,
                deviceRegistry,
                this.FrameCounterUpdateStrategyFactory);

            // sends unconfirmed message
            var unconfirmedMessagePayload = simulatedDevice.CreateConfirmedDataUpMessage("1234");
            var rxpk = unconfirmedMessagePayload.SerializeUplink(simulatedDevice.AppSKey, simulatedDevice.NwkSKey).Rxpk[0];
            rxpk.Rfch = rfch;
            var request = new WaitableLoRaRequest(rxpk, this.packetForwarder);
            messageProcessor.DispatchRequest(request);
            Assert.True(await request.WaitCompleteAsync());
            Assert.NotNull(request.ResponseDownlink);
            var txpk = request.ResponseDownlink.Txpk;
            Assert.Equal(0U, txpk.Rfch);
            Assert.Equal(RegionFactory.CreateEU868Region().GetDownstreamChannel(rxpk), txpk.Freq);
            Assert.Equal("4/5", txpk.Codr);
            Assert.False(txpk.Imme);
            Assert.True(txpk.Ipol);
            Assert.Equal("LORA", txpk.Modu);

            this.LoRaDeviceClient.VerifyAll();
            this.LoRaDeviceApi.VerifyAll();
        }

        [Theory]
        [InlineData(ServerGatewayID, 1600)]
        [InlineData(ServerGatewayID, 2000)]
        [InlineData(ServerGatewayID, 5000)]
        [InlineData(null, 1600)]
        [InlineData(null, 2000)]
        [InlineData(null, 5000)]
        public async Task When_Sending_Unconfirmed_Message_To_IoT_Hub_Takes_Too_Long_Should_Not_Check_For_C2D(
            string deviceGatewayID,
            int sendMessageDelayInMs)
        {
            var simulatedDevice = new SimulatedDevice(TestDeviceInfo.CreateABPDevice(1, gatewayID: deviceGatewayID));

            var devEUI = simulatedDevice.LoRaDevice.DeviceID;
            var devAddr = simulatedDevice.LoRaDevice.DevAddr;

            // message will be sent
            this.LoRaDeviceClient.Setup(x => x.SendEventAsync(It.IsNotNull<LoRaDeviceTelemetry>(), null))
                .Callback(() => Thread.Sleep(TimeSpan.FromMilliseconds(sendMessageDelayInMs)))
                .ReturnsAsync(true);

            // Lora device api
            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var cachedDevice = this.CreateLoRaDevice(simulatedDevice);

            var devEUIDeviceDict = new DevEUIToLoRaDeviceDictionary();
            devEUIDeviceDict.TryAdd(devEUI, cachedDevice);
            memoryCache.Set(devAddr, devEUIDeviceDict);

            var deviceRegistry = new LoRaDeviceRegistry(this.ServerConfiguration, memoryCache, this.LoRaDeviceApi.Object, this.LoRaDeviceFactory);

            // Send to message processor
            var messageProcessor = new MessageProcessor(
                this.ServerConfiguration,
                deviceRegistry,
                this.FrameCounterUpdateStrategyFactory);

            // sends unconfirmed message
            var unconfirmedMessagePayload = simulatedDevice.CreateUnconfirmedDataUpMessage("hello");
            var rxpk = unconfirmedMessagePayload.SerializeUplink(simulatedDevice.AppSKey, simulatedDevice.NwkSKey).Rxpk[0];
            var request = new WaitableLoRaRequest(rxpk, this.packetForwarder);
            messageProcessor.DispatchRequest(request);
            Assert.True(await request.WaitCompleteAsync());
            Assert.Null(request.ResponseDownlink);

            this.LoRaDeviceClient.Verify(x => x.ReceiveAsync(It.IsAny<TimeSpan>()), Times.Never());

            this.LoRaDeviceClient.VerifyAll();
            this.LoRaDeviceApi.VerifyAll();
        }

        /// <summary>
        /// Verifies that if the update twin takes too long that no join accepts are sent
        /// </summary>
        [Theory]
        [InlineData(ServerGatewayID)]
        [InlineData(null)]
        public async Task ABP_When_Getting_Twin_Fails_Should_Work_On_Retry(string deviceGatewayID)
        {
            var simulatedDevice = new SimulatedDevice(TestDeviceInfo.CreateABPDevice(1, gatewayID: deviceGatewayID));
            var devEUI = simulatedDevice.LoRaDevice.DeviceID;
            var appEUI = simulatedDevice.LoRaDevice.AppEUI;
            var devAddr = simulatedDevice.DevAddr;

            // Device twin will be queried
            var twin = simulatedDevice.CreateABPTwin();
            this.LoRaDeviceClient.SetupSequence(x => x.GetTwinAsync())
                .ReturnsAsync((Twin)null)
                .ReturnsAsync(twin);

            // 1 message will be sent
            this.LoRaDeviceClient.Setup(x => x.SendEventAsync(It.IsNotNull<LoRaDeviceTelemetry>(), It.IsAny<Dictionary<string, string>>()))
                .Callback<LoRaDeviceTelemetry, Dictionary<string, string>>((t, d) =>
                 {
                    Assert.Equal(2, t.Fcnt);
                    Assert.Equal("2", ((JObject)t.Data)["value"].ToString());
                 })
                 .ReturnsAsync(true);

            // will check for c2d msg
            this.LoRaDeviceClient.Setup(x => x.ReceiveAsync(It.IsAny<TimeSpan>()))
                .ReturnsAsync((Message)null);

            // Lora device api will be search by devices with matching deveui
            this.LoRaDeviceApi.Setup(x => x.SearchByDevAddrAsync(devAddr))
                .ReturnsAsync(new SearchDevicesResult(new IoTHubDeviceInfo(devAddr, devEUI, "aabb").AsList()));

            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var deviceRegistry = new LoRaDeviceRegistry(this.ServerConfiguration, memoryCache, this.LoRaDeviceApi.Object, this.LoRaDeviceFactory);

            var messageProcessor = new MessageProcessor(
                this.ServerConfiguration,
                deviceRegistry,
                this.FrameCounterUpdateStrategyFactory);

            // send 1st unconfirmed message, get twin will fail
            var unconfirmedMessage1 = simulatedDevice.CreateUnconfirmedDataUpMessage("1", fcnt: 1);
            var unconfirmedMessage1Rxpk = unconfirmedMessage1.SerializeUplink(simulatedDevice.AppSKey, simulatedDevice.NwkSKey).Rxpk[0];
            var unconfirmedRequest1 = new WaitableLoRaRequest(unconfirmedMessage1Rxpk, this.packetForwarder);
            messageProcessor.DispatchRequest(unconfirmedRequest1);
            Assert.True(await unconfirmedRequest1.WaitCompleteAsync());
            Assert.Null(unconfirmedRequest1.ResponseDownlink);

            var devicesInCache = deviceRegistry.InternalGetCachedDevicesForDevAddr(devAddr);
            Assert.Empty(devicesInCache);

            // sends 2nd unconfirmed message, now get twin will work
            var unconfirmedMessage2 = simulatedDevice.CreateUnconfirmedDataUpMessage("2", fcnt: 2);
            var unconfirmedMessage2Rxpk = unconfirmedMessage2.SerializeUplink(simulatedDevice.AppSKey, simulatedDevice.NwkSKey).Rxpk[0];
            var unconfirmedRequest2 = new WaitableLoRaRequest(unconfirmedMessage2Rxpk, this.packetForwarder);
            messageProcessor.DispatchRequest(unconfirmedRequest2);
            Assert.True(await unconfirmedRequest2.WaitCompleteAsync());
            Assert.Null(unconfirmedRequest2.ResponseDownlink);

            devicesInCache = deviceRegistry.InternalGetCachedDevicesForDevAddr(devAddr);
            Assert.Single(devicesInCache);
            Assert.True(devicesInCache.TryGetValue(devEUI, out var loRaDevice));
            Assert.Equal(simulatedDevice.NwkSKey, loRaDevice.NwkSKey);
            Assert.Equal(simulatedDevice.AppSKey, loRaDevice.AppSKey);
            Assert.Equal(devAddr, loRaDevice.DevAddr);
            Assert.Equal(2, loRaDevice.FCntUp);

            this.LoRaDeviceClient.VerifyAll();
            this.LoRaDeviceApi.VerifyAll();
        }

        /// <summary>
        /// Tests that a ABP device (already in cached or not), receives 1st message with invalid mic, 2nd with valid
        /// should send message 2 to iot hub
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ABP_When_First_Message_Has_Invalid_Mic_Second_Should_Send_To_Hub(bool isAlreadyInDeviceRegistryCache)
        {
            var simulatedDevice = new SimulatedDevice(TestDeviceInfo.CreateABPDevice(1, gatewayID: ServerGatewayID));

            var loRaDevice = this.CreateLoRaDevice(simulatedDevice);
            loRaDevice.SensorDecoder = "DecoderValueSensor";

            const int firstMessageFcnt = 3;
            const int secondMessageFcnt = 4;
            const string wrongNwkSKey = "00000000000000000000000000001234";
            var unconfirmedMessageWithWrongMic = simulatedDevice.CreateUnconfirmedDataUpMessage("123", fcnt: firstMessageFcnt).SerializeUplink(simulatedDevice.AppSKey, wrongNwkSKey).Rxpk[0];
            var unconfirmedMessageWithCorrectMic = simulatedDevice.CreateUnconfirmedDataUpMessage("456", fcnt: secondMessageFcnt).SerializeUplink(simulatedDevice.AppSKey, simulatedDevice.NwkSKey).Rxpk[0];

            // message will be sent
            LoRaDeviceTelemetry loRaDeviceTelemetry = null;
            this.LoRaDeviceClient.Setup(x => x.SendEventAsync(It.IsNotNull<LoRaDeviceTelemetry>(), null))
                .Callback<LoRaDeviceTelemetry, Dictionary<string, string>>((t, _) => loRaDeviceTelemetry = t)
                .ReturnsAsync(true);

            if (!isAlreadyInDeviceRegistryCache)
            {
                this.LoRaDeviceClient.Setup(x => x.GetTwinAsync())
                    .ReturnsAsync(simulatedDevice.CreateABPTwin());
            }

            // C2D message will be checked
            this.LoRaDeviceClient.Setup(x => x.ReceiveAsync(It.IsNotNull<TimeSpan>()))
                .ReturnsAsync((Message)null);

            // Lora device api

            // will search for the device twice
            this.LoRaDeviceApi.Setup(x => x.SearchByDevAddrAsync(loRaDevice.DevAddr))
                .ReturnsAsync(new SearchDevicesResult(new IoTHubDeviceInfo(loRaDevice.DevAddr, loRaDevice.DevEUI, "aaa").AsList()));

            // add device to cache already
            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            if (isAlreadyInDeviceRegistryCache)
            {
                var dictionary = new DevEUIToLoRaDeviceDictionary();
                dictionary[loRaDevice.DevEUI] = loRaDevice;
                memoryCache.Set<DevEUIToLoRaDeviceDictionary>(loRaDevice.DevAddr, dictionary);
            }

            var deviceRegistry = new LoRaDeviceRegistry(this.ServerConfiguration, memoryCache, this.LoRaDeviceApi.Object, this.LoRaDeviceFactory);

            // Send to message processor
            var messageProcessor = new MessageProcessor(
                this.ServerConfiguration,
                deviceRegistry,
                this.FrameCounterUpdateStrategyFactory);

            // first message should fail
            var requestWithWrongMic = new WaitableLoRaRequest(unconfirmedMessageWithWrongMic, this.packetForwarder);
            messageProcessor.DispatchRequest(requestWithWrongMic);
            Assert.True(await requestWithWrongMic.WaitCompleteAsync());
            Assert.Null(requestWithWrongMic.ResponseDownlink);

            // second message should succeed
            var requestWithCorrectMic = new WaitableLoRaRequest(unconfirmedMessageWithCorrectMic, this.packetForwarder);
            messageProcessor.DispatchRequest(requestWithCorrectMic);
            Assert.True(await requestWithCorrectMic.WaitCompleteAsync());
            Assert.Null(requestWithCorrectMic.ResponseDownlink);

            Assert.NotNull(loRaDeviceTelemetry);
            Assert.IsType<JObject>(loRaDeviceTelemetry.Data);
            var telemetryData = (JObject)loRaDeviceTelemetry.Data;
            Assert.Equal("456", telemetryData["value"].ToString());

            var devicesByDevAddr = deviceRegistry.InternalGetCachedDevicesForDevAddr(simulatedDevice.DevAddr);
            Assert.NotEmpty(devicesByDevAddr);
            Assert.True(devicesByDevAddr.TryGetValue(simulatedDevice.DevEUI, out var loRaDeviceFromRegistry));
            Assert.Equal(secondMessageFcnt, loRaDeviceFromRegistry.FCntUp);
            Assert.True(loRaDeviceFromRegistry.IsOurDevice);

            this.LoRaDeviceApi.VerifyAll();
            this.LoRaDeviceClient.VerifyAll();
        }
    }
}
