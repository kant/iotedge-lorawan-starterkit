// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace LoRaWan.NetworkServer
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Threading.Tasks.Sources;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Devices.Shared;
    using Newtonsoft.Json;

    public sealed class LoRaDevice : IDisposable
    {
        public string DevAddr { get; set; }

        // Gets if a device is activated by personalization
        public bool IsABP => string.IsNullOrEmpty(this.AppKey);

        public string DevEUI { get; set; }

        public string AppKey { get; set; }

        public string AppEUI { get; set; }

        public string NwkSKey { get; set; }

        public string AppSKey { get; set; }

        public string AppNonce { get; set; }

        public string DevNonce { get; set; }

        public string NetID { get; set; }

        public bool IsOurDevice { get; set; }

        public string LastConfirmedC2DMessageID { get; set; }

        int fcntUp;

        public int FCntUp => this.fcntUp;

        int fcntDown;

        public int FCntDown => this.fcntDown;

        private readonly ILoRaDeviceClient loRaDeviceClient;

        public string GatewayID { get; set; }

        public string SensorDecoder { get; set; }

        public int? ReceiveDelay1 { get; set; }

        public int? ReceiveDelay2 { get; set; }

        public bool IsABPRelaxedFrameCounter { get; set; } = true;

        public bool AlwaysUseSecondWindow { get; set; } = false;

        int hasFrameCountChanges;

        public LoRaDevice(string devAddr, string devEUI, ILoRaDeviceClient loRaDeviceClient)
        {
            this.DevAddr = devAddr;
            this.DevEUI = devEUI;
            this.loRaDeviceClient = loRaDeviceClient;
            this.hasFrameCountChanges = 0;
        }

        /// <summary>
        /// Initializes the device from twin properties
        /// Throws InvalidLoRaDeviceException if the device does contain require properties
        /// </summary>
        public async Task<bool> InitializeAsync()
        {
            var twin = await this.loRaDeviceClient.GetTwinAsync();

            if (twin != null)
            {
                // ABP requires the property AppSKey, AppNwkSKey, DevAddr to be present
                if (twin.Properties.Desired.Contains(TwinProperty.AppSKey))
                {
                    // ABP Case
                    this.AppSKey = twin.Properties.Desired[TwinProperty.AppSKey];

                    if (!twin.Properties.Desired.Contains(TwinProperty.NwkSKey))
                        throw new InvalidLoRaDeviceException("Missing NwkSKey for ABP device");

                    if (!twin.Properties.Desired.Contains(TwinProperty.DevAddr))
                        throw new InvalidLoRaDeviceException("Missing DevAddr for ABP device");

                    this.NwkSKey = twin.Properties.Desired[TwinProperty.NwkSKey];
                    this.DevAddr = twin.Properties.Desired[TwinProperty.DevAddr];
                    this.IsOurDevice = true;
                }
                else
                {
                    // OTAA
                    if (!twin.Properties.Desired.Contains(TwinProperty.AppKey))
                    {
                        throw new InvalidLoRaDeviceException("Missing AppKey for OTAA device");
                    }

                    this.AppKey = twin.Properties.Desired[TwinProperty.AppKey];

                    if (!twin.Properties.Desired.Contains(TwinProperty.AppEUI))
                    {
                        throw new InvalidLoRaDeviceException("Missing AppEUI for OTAA device");
                    }

                    this.AppEUI = twin.Properties.Desired[TwinProperty.AppEUI];

                    // Check for already joined OTAA device properties
                    if (twin.Properties.Reported.Contains(TwinProperty.DevAddr))
                        this.DevAddr = twin.Properties.Reported[TwinProperty.DevAddr];

                    if (twin.Properties.Reported.Contains(TwinProperty.AppSKey))
                        this.AppSKey = twin.Properties.Reported[TwinProperty.AppSKey];

                    if (twin.Properties.Reported.Contains(TwinProperty.NwkSKey))
                        this.NwkSKey = twin.Properties.Reported[TwinProperty.NwkSKey];

                    if (twin.Properties.Reported.Contains(TwinProperty.NetID))
                        this.NetID = twin.Properties.Reported[TwinProperty.NetID];

                    if (twin.Properties.Reported.Contains(TwinProperty.DevNonce))
                        this.DevNonce = twin.Properties.Reported[TwinProperty.DevNonce];
                }

                if (twin.Properties.Desired.Contains(TwinProperty.GatewayID))
                    this.GatewayID = twin.Properties.Desired[TwinProperty.GatewayID];
                if (twin.Properties.Desired.Contains(TwinProperty.SensorDecoder))
                    this.SensorDecoder = twin.Properties.Desired[TwinProperty.SensorDecoder];
                if (twin.Properties.Reported.Contains(TwinProperty.FCntUp))
                    this.fcntUp = twin.Properties.Reported[TwinProperty.FCntUp];
                if (twin.Properties.Reported.Contains(TwinProperty.FCntDown))
                    this.fcntDown = twin.Properties.Reported[TwinProperty.FCntDown];

                return true;
            }

            return false;
        }

        /// <summary>
        /// Saves the frame count changes
        /// </summary>
        /// <remarks>
        /// Changes will be saved only if there are actually changes to be saved
        /// </remarks>
        public async Task<bool> SaveFrameCountChangesAsync()
        {
            if (this.hasFrameCountChanges == 1)
            {
                var reportedProperties = new TwinCollection();
                var savedFcntDown = this.FCntDown;
                var savedFcntUp = this.FCntUp;
                reportedProperties[TwinProperty.FCntDown] = savedFcntDown;
                reportedProperties[TwinProperty.FCntUp] = savedFcntUp;
                var result = await this.loRaDeviceClient.UpdateReportedPropertiesAsync(reportedProperties);
                if (result)
                {
                    if (savedFcntUp == this.FCntUp && savedFcntDown == this.FCntDown)
                    {
                        this.AcceptFrameCountChanges();
                    }
                }

                return result;
            }

            return true;
        }

        /// <summary>
        /// Gets a value indicating whether there are pending frame count changes
        /// </summary>
        public bool HasFrameCountChanges => this.hasFrameCountChanges == 1;

        /// <summary>
        /// Accept changes to the frame count
        /// </summary>
        public void AcceptFrameCountChanges() => Interlocked.Exchange(ref this.hasFrameCountChanges, 0);

        /// <summary>
        /// Increments <see cref="FCntDown"/>
        /// </summary>
        public int IncrementFcntDown(int value)
        {
            var result = Interlocked.Add(ref this.fcntDown, value);
            Interlocked.Exchange(ref this.hasFrameCountChanges, 1);
            return result;
        }

        /// <summary>
        /// Sets a new value for <see cref="FCntDown"/>
        /// </summary>
        public void SetFcntDown(int newValue)
        {
            var oldValue = Interlocked.Exchange(ref this.fcntDown, newValue);
            if (newValue != oldValue)
                Interlocked.Exchange(ref this.hasFrameCountChanges, 1);
        }

        public void SetFcntUp(int newValue)
        {
            var oldValue = Interlocked.Exchange(ref this.fcntUp, newValue);
            if (newValue != oldValue)
                Interlocked.Exchange(ref this.hasFrameCountChanges, 1);
        }

        public Task<bool> SendEventAsync(LoRaDeviceTelemetry telemetry, Dictionary<string, string> properties = null) => this.loRaDeviceClient.SendEventAsync(telemetry, properties);

        public Task<Message> ReceiveCloudToDeviceAsync(TimeSpan timeout) => this.loRaDeviceClient.ReceiveAsync(timeout);

        public Task<bool> CompleteCloudToDeviceMessageAsync(Message cloudToDeviceMessage) => this.loRaDeviceClient.CompleteAsync(cloudToDeviceMessage);

        public Task<bool> AbandonCloudToDeviceMessageAsync(Message cloudToDeviceMessage) => this.loRaDeviceClient.AbandonAsync(cloudToDeviceMessage);

        /// <summary>
        /// Updates device on the server after a join succeeded
        /// </summary>
        internal async Task<bool> UpdateAfterJoinAsync(string devAddr, string nwkSKey, string appSKey, string appNonce, string devNonce, string netID)
        {
            var reportedProperties = new TwinCollection();
            reportedProperties[TwinProperty.AppSKey] = appSKey;
            reportedProperties[TwinProperty.NwkSKey] = nwkSKey;
            reportedProperties[TwinProperty.DevAddr] = devAddr;
            reportedProperties[TwinProperty.FCntDown] = 0;
            reportedProperties[TwinProperty.FCntUp] = 0;
            reportedProperties[TwinProperty.DevEUI] = this.DevEUI;
            reportedProperties[TwinProperty.NetID] = netID;
            reportedProperties[TwinProperty.DevNonce] = devNonce;

            var succeeded = await this.loRaDeviceClient.UpdateReportedPropertiesAsync(reportedProperties);
            if (succeeded)
            {
                this.DevAddr = devAddr;
                this.NwkSKey = nwkSKey;
                this.AppSKey = appSKey;
                this.AppNonce = appNonce;
                this.DevNonce = devNonce;
                this.NetID = netID;
                this.SetFcntDown(0);
                this.SetFcntUp(0);
                this.AcceptFrameCountChanges();
            }

            return succeeded;
        }

        

        public void Dispose()
        {
            this.loRaDeviceClient?.Dispose();
            GC.SuppressFinalize(this);
        }

        volatile LoRaDeviceRequest runningRequest;
        readonly Queue<LoRaDeviceRequest> queuedRequests = new Queue<LoRaDeviceRequest>();


        public void QueueRequest(LoRaDeviceRequest request)
        {
            // Access to runningRequest and queuedRequests must be 
            // thread safe
            lock (this)
            {
                if (this.runningRequest == null)
                {
                    StartNextRequest(request);
                }
                else
                {
                    queuedRequests.Enqueue(request);
                }
            }
        }

        private void OnRequestCompleted(Task task)
        {
            // Access to runningRequest and queuedRequests must be 
            // thread safe
            lock (this)
            {
                this.runningRequest = null;
                if (this.queuedRequests.TryDequeue(out var nextRequest))
                {
                    StartNextRequest(nextRequest);
                }
            }
        }

        void StartNextRequest(LoRaDeviceRequest msg)
        {
            this.runningRequest = msg;

            this.ProcessRequest(msg)
                .ContinueWith(OnRequestCompleted, TaskContinuationOptions.ExecuteSynchronously) // TODO: verify if it is better
                .ConfigureAwait(false);
        }

        
        
        async Task ProcessRequest(LoRaDeviceRequest request)
        {
            var loraPayload = (LoRaPayloadData)request.Payload;
            var isMultiGateway = !string.Equals(this.GatewayID, request.Configuration.GatewayID, StringComparison.InvariantCultureIgnoreCase);
            var frameCounterStrategy = isMultiGateway ?
                request.FrameCounterUpdateStrategyFactory.GetMultiGatewayStrategy() :
                request.FrameCounterUpdateStrategyFactory.GetSingleGatewayStrategy();

                var payloadFcnt = loraPayload.GetFcnt();
                var requiresConfirmation = loraPayload.IsConfirmed();

                using (new LoRaDeviceFrameCounterSession(this, frameCounterStrategy))
                {
                    // Leaf devices that restart lose the counter. In relax mode we accept the incoming frame counter
                    // ABP device does not reset the Fcnt so in relax mode we should reset for 0 (LMIC based) or 1
                    var isFrameCounterFromNewlyStartedDevice = false;
                    if (payloadFcnt <= 1)
                    {
                        if (this.IsABP)
                        {
                            if (this.IsABPRelaxedFrameCounter && this.FCntUp >= 0 && payloadFcnt <= 1)
                            {
                                // known problem when device restarts, starts fcnt from zero
                                _ = frameCounterStrategy.ResetAsync(this);
                                isFrameCounterFromNewlyStartedDevice = true;
                            }
                        }
                        else if (this.FCntUp == payloadFcnt && payloadFcnt == 0)
                        {
                            // Some devices start with frame count 0
                            isFrameCounterFromNewlyStartedDevice = true;
                        }
                    }

                    // Reply attack or confirmed reply
                    // Confirmed resubmit: A confirmed message that was received previously but we did not answer in time
                    // Device will send it again and we just need to return an ack (but also check for C2D to send it over)
                    var isConfirmedResubmit = false;
                    if (!isFrameCounterFromNewlyStartedDevice && payloadFcnt <= this.FCntUp)
                    {
                        // TODO: have a maximum retry (3)
                        // Future: Keep track of how many times we acked the confirmed message (4+ times we skip)
                        //if it is confirmed most probably we did not ack in time before or device lost the ack packet so we should continue but not send the msg to iothub 
                        if (requiresConfirmation && payloadFcnt == this.FCntUp)
                        {
                            isConfirmedResubmit = true;
                            Logger.Log(this.DevEUI, $"resubmit from confirmed message detected, msg: {payloadFcnt} server: {this.FCntUp}", Logger.LoggingLevel.Info);
                        }
                        else
                        {
                            Logger.Log(this.DevEUI, $"invalid frame counter, message ignored, msg: {payloadFcnt} server: {this.FCntUp}", Logger.LoggingLevel.Info);
                            return;
                        }
                    }

                    var fcntDown = 0;
                    // If it is confirmed it require us to update the frame counter down
                    // Multiple gateways: in redis, otherwise in device twin
                    if (requiresConfirmation)
                    {
                        fcntDown = await frameCounterStrategy.NextFcntDown(this, payloadFcnt);

                        // Failed to update the fcnt down
                        // In multi gateway scenarios it means the another gateway was faster than using, can stop now
                        if (fcntDown <= 0)
                        {
                            // update our fcntup anyway?
                            //loRaDevice.SetFcntUp(payloadFcnt);

                            Logger.Log(this.DevEUI, "another gateway has already sent ack or downlink msg", Logger.LoggingLevel.Info);

                            return;
                        }

                        Logger.Log(this.DevEUI, $"down frame counter: {this.FCntDown}", Logger.LoggingLevel.Info);
                    }


                    if (!isConfirmedResubmit)
                    {
                        var validFcntUp = isFrameCounterFromNewlyStartedDevice || (payloadFcnt > this.FCntUp);
                        if (validFcntUp)
                        {
                            Logger.Log(this.DevEUI, $"valid frame counter, msg: {payloadFcnt} server: {this.FCntUp}", Logger.LoggingLevel.Info);

                            object payloadData = null;


                            // if it is an upward acknowledgement from the device it does not have a payload
                            // This is confirmation from leaf device that he received a C2D confirmed
                            //if a message payload is null we don't try to decrypt it.
                            if (loraPayload.Frmpayload.Length != 0)
                            {
                                byte[] decryptedPayloadData = null;
                                try
                                {
                                    decryptedPayloadData = loraPayload.GetDecryptedPayload(this.AppSKey);
                                }
                                catch (Exception ex)
                                {
                                    Logger.Log(this.DevEUI, $"failed to decrypt message: {ex.Message}", Logger.LoggingLevel.Error);
                                }


                                var fportUp = loraPayload.GetFPort();

                                if (string.IsNullOrEmpty(this.SensorDecoder))
                                {
                                    Logger.Log(this.DevEUI, $"no decoder set in device twin. port: {fportUp}", Logger.LoggingLevel.Full);
                                    payloadData = Convert.ToBase64String(decryptedPayloadData);
                                }
                                else
                                {
                                    Logger.Log(this.DevEUI, $"decoding with: {this.SensorDecoder} port: {fportUp}", Logger.LoggingLevel.Full);
                                    payloadData = await request.PayloadDecoder.DecodeMessageAsync(decryptedPayloadData, fportUp, this.SensorDecoder);
                                }
                            }



                            // What do we need to send an UpAck to IoT Hub?
                            // What is the content of the message
                            // TODO Future: Don't wait if it is an unconfirmed message
                            await SendDeviceEventAsync(request, payloadData);

                            this.SetFcntUp(payloadFcnt);
                        }
                        else
                        {
                            Logger.Log(this.DevEUI, $"invalid frame counter, msg: {payloadFcnt} server: {this.FCntUp}", Logger.LoggingLevel.Info);
                        }
                    }

                    // We check if we have time to futher progress or not
                    // C2D checks are quite expensive so if we are really late we just stop here
                    var timeToSecondWindow = request.OperationTimer.GetRemainingTimeToReceiveSecondWindow(this);
                    if (timeToSecondWindow < LoRaOperationTimeWatcher.ExpectedTimeToPackageAndSendMessage)
                    {
                        if (requiresConfirmation)
                        {
                            Logger.Log(this.DevEUI, $"too late for down message ({request.OperationTimer.GetElapsedTime()}), sending only ACK to gateway", Logger.LoggingLevel.Info);
                        }

                        return;
                    }

                    // If it is confirmed and we don't have time to check c2d and send to device we return now
                    if (requiresConfirmation && timeToSecondWindow <= (LoRaOperationTimeWatcher.ExpectedTimeToCheckCloudToDeviceMessagePackageAndSendMessage))
                    {
                        return CreateDownlinkMessage(
                            null,
                            loRaDevice,
                            rxpk,
                            loraPayload,
                            request.OperationTimer,
                            devAddr,
                            false, // fpending
                            (ushort)fcntDown
                        );
                    }

                    // ReceiveAsync has a longer timeout
                    // But we wait less that the timeout (available time before 2nd window)
                    // if message is received after timeout, keep it in loraDeviceInfo and return the next call
                    var cloudToDeviceReceiveTimeout = timeToSecondWindow - (LoRaOperationTimeWatcher.ExpectedTimeToCheckCloudToDeviceMessagePackageAndSendMessage);
                    // Flag indicating if there is another C2D message waiting
                    var fpending = false;
                    // Contains the Cloud to message we need to send
                    Message cloudToDeviceMessage = null;
                    if (cloudToDeviceReceiveTimeout > TimeSpan.Zero)
                    {
                        cloudToDeviceMessage = await this.ReceiveCloudToDeviceAsync(cloudToDeviceReceiveTimeout);
                        if (cloudToDeviceMessage != null && !ValidateCloudToDeviceMessage(this, cloudToDeviceMessage))
                        {
                            _ = this.CompleteCloudToDeviceMessageAsync(cloudToDeviceMessage);
                            cloudToDeviceMessage = null;
                        }

                        if (cloudToDeviceMessage != null)
                        {
                            if (!requiresConfirmation)
                            {
                                // The message coming from the device was not confirmed, therefore we did not computed the frame count down
                                // Now we need to increment because there is a C2D message to be sent
                                fcntDown = await frameCounterStrategy.NextFcntDown(this, payloadFcnt);

                                if (fcntDown == 0)
                                {
                                    // We did not get a valid frame count down, therefore we should not process the message
                                    _ = this.AbandonCloudToDeviceMessageAsync(cloudToDeviceMessage);

                                    cloudToDeviceMessage = null;
                                }
                                else
                                {
                                    requiresConfirmation = true;

                                    Logger.Log(this.DevEUI, $"down frame counter: {this.FCntDown}", Logger.LoggingLevel.Info);

                                }
                            }

                            // Checking again because the fcntdown resolution could have failed, causing us to drop the message
                            if (cloudToDeviceMessage != null)
                            {
                                timeToSecondWindow = request.OperationTimer.GetRemainingTimeToReceiveSecondWindow(this);
                                if (timeToSecondWindow > LoRaOperationTimeWatcher.ExpectedTimeToCheckCloudToDeviceMessagePackageAndSendMessage)
                                {
                                    var additionalMsg = await this.ReceiveCloudToDeviceAsync(timeToSecondWindow - LoRaOperationTimeWatcher.ExpectedTimeToCheckCloudToDeviceMessagePackageAndSendMessage);
                                    if (additionalMsg != null)
                                    {
                                        fpending = true;
                                        _ = this.AbandonCloudToDeviceMessageAsync(additionalMsg);
                                    }
                                }
                            }
                        }
                    }

                    // No C2D message and request was not confirmed, return nothing
                    if (!requiresConfirmation)
                    {
                        // TODO: can we let the session save it?
                        //await frameCounterStrategy.SaveChangesAsync(loRaDevice);                    
                        return;
                    }

                    // We did it in the LoRaPayloadData constructor
                    // we got here:
                    // a) was a confirm request
                    // b) we have a c2d message
                    var confirmDownstream = CreateDownlinkMessage(
                        cloudToDeviceMessage,
                        request,
                        fpending,
                        (ushort)fcntDown
                    );

                    if (cloudToDeviceMessage != null)
                    {
                        if (confirmDownstream == null)
                        {
                            _ = this.AbandonCloudToDeviceMessageAsync(cloudToDeviceMessage);
                        }
                        else
                        {
                            _ = this.CompleteCloudToDeviceMessageAsync(cloudToDeviceMessage);
                        }
                    }

                    if (confirmDownstream != null)
                    {
                        _ = request.PacketForwarder.SendDownstreamAsync(confirmDownstream);
                    }
                }
            }

        /// <summary>
        /// Creates downlink message with ack for confirmation or cloud to device message
        /// </summary>
        private DownlinkPktFwdMessage CreateDownlinkMessage(
            Message cloudToDeviceMessage,
            LoRaDeviceRequest request,
            bool fpending,
            ushort fcntDown)
        {
            var loRaPayloadData = (LoRaPayloadData)request.Payload;
            //default fport
            byte fctrl = 0;
            if (loRaPayloadData.LoRaMessageType == LoRaMessageType.ConfirmedDataUp)
            {
                // Confirm receiving message to device                            
                fctrl = (byte)FctrlEnum.Ack;
            }

            byte? fport = null;
            var requiresDeviceAcknowlegement = false;
            byte[] macbytes = null;

            byte[] rndToken = new byte[2];
            Random rnd = new Random();
            rnd.NextBytes(rndToken);

            byte[] frmPayload = null;

            if (cloudToDeviceMessage != null)
            {
                if (cloudToDeviceMessage.Properties.TryGetValueCaseInsensitive("cidtype", out var cidTypeValue))
                {
                    Logger.Log(this.DevEUI, $"Cloud to device MAC command received", Logger.LoggingLevel.Info);
                    MacCommandHolder macCommandHolder = new MacCommandHolder(Convert.ToByte(cidTypeValue));
                    macbytes = macCommandHolder.macCommand[0].ToBytes();
                }

                if (cloudToDeviceMessage.Properties.TryGetValueCaseInsensitive("confirmed", out var confirmedValue) && confirmedValue.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    requiresDeviceAcknowlegement = true;
                    this.LastConfirmedC2DMessageID = cloudToDeviceMessage.MessageId ?? Constants.C2D_MSG_ID_PLACEHOLDER;

                }
                if (cloudToDeviceMessage.Properties.TryGetValueCaseInsensitive("fport", out var fPortValue))
                {
                    fport = byte.Parse(fPortValue);
                }

                Logger.Log(this.DevEUI, string.Format("Sending a downstream message with ID {0}",
                    ConversionHelper.ByteArrayToString(rndToken)),
                    Logger.LoggingLevel.Full);


                frmPayload = cloudToDeviceMessage?.GetBytes();

                Logger.Log(this.DevEUI, $"C2D message: {Encoding.UTF8.GetString(frmPayload)}, id: {cloudToDeviceMessage.MessageId ?? "undefined"}, fport: {fport}, confirmed: {requiresDeviceAcknowlegement}, cidType: {cidTypeValue}", Logger.LoggingLevel.Info);

                //cut to the max payload of lora for any EU datarate
                if (frmPayload.Length > 51)
                    Array.Resize(ref frmPayload, 51);

                Array.Reverse(frmPayload);
            }

            if (fpending)
            {
                fctrl |= (int)FctrlEnum.FpendingOrClassB;
            }

            // if (macbytes != null && linkCheckCmdResponse != null)
            //     macbytes = macbytes.Concat(linkCheckCmdResponse).ToArray();

            var payloadDevAddr = loRaPayloadData.DevAddr.Span;
            var reversedDevAddr = new byte[payloadDevAddr.Length];
            for (int i = reversedDevAddr.Length - 1; i >= 0; --i)
            {
                reversedDevAddr[i] = payloadDevAddr[payloadDevAddr.Length - (1 + i)];
            }

            var msgType = requiresDeviceAcknowlegement ? LoRaMessageType.ConfirmedDataDown : LoRaMessageType.UnconfirmedDataDown;
            var ackLoRaMessage = new LoRaPayloadData(
                msgType,
                reversedDevAddr,
                new byte[] { fctrl },
                BitConverter.GetBytes(fcntDown),
                macbytes,
                fport.HasValue ? new byte[] { fport.Value } : null,
                frmPayload,
                1);

            var receiveWindow = request.OperationTimer.ResolveReceiveWindowToUse(this);
            if (receiveWindow == 0)
                return null;

            string datr = null;
            double freq;
            long tmst;
            if (receiveWindow == 2)
            {
                tmst = request.Rxpk.Tmst + request.LoRaRegion.Receive_delay2 * 1000000;

                if (string.IsNullOrEmpty(request.Configuration.Rx2DataRate))
                {
                    Logger.Log(this.DevEUI, $"using standard second receive windows", Logger.LoggingLevel.Info);
                    freq = request.LoRaRegion.RX2DefaultReceiveWindows.frequency;
                    datr = request.LoRaRegion.DRtoConfiguration[request.LoRaRegion.RX2DefaultReceiveWindows.dr].configuration;
                }
                //if specific twins are set, specify second channel to be as specified
                else
                {
                    freq = request.Configuration.Rx2DataFrequency;
                    datr = request.Configuration.Rx2DataRate;
                    Logger.Log(this.DevEUI, $"using custom DR second receive windows freq : {freq}, datr:{datr}", Logger.LoggingLevel.Info);
                }
            }
            else
            {
                datr = request.LoRaRegion.GetDownstreamDR(request.Rxpk);
                freq = request.LoRaRegion.GetDownstreamChannel(request.Rxpk);
                tmst = request.Rxpk.Tmst + request.LoRaRegion.Receive_delay1 * 1000000;
            }


            //todo: check the device twin preference if using confirmed or unconfirmed down    
            var loRaMessageType = (msgType == LoRaMessageType.ConfirmedDataDown) ? LoRaMessageType.ConfirmedDataDown : LoRaMessageType.UnconfirmedDataDown;
            return ackLoRaMessage.Serialize(loraDeviceInfo.AppSKey, this.NwkSKey, datr, freq, tmst, this.DevEUI);
        }

        private async Task SendDeviceEventAsync(LoRaDeviceRequest request, object decodedValue)
        {
            var loRaPayloadData = (LoRaPayloadData)request.Payload;
            var deviceTelemetry = new LoRaDeviceTelemetry(request.Rxpk, loRaPayloadData, decodedValue);
            deviceTelemetry.DeviceEUI = this.DevEUI;
            deviceTelemetry.GatewayID = request.Configuration.GatewayID;
            deviceTelemetry.Edgets = (long)((request.OperationTimer.Start - DateTime.UnixEpoch).TotalMilliseconds);

            Dictionary<string, string> eventProperties = null;
            if (loRaPayloadData.IsUpwardAck())
            {
                eventProperties = new Dictionary<string, string>();
                Logger.Log(this.DevEUI, String.Concat($"Message ack received",
                                 this.LastConfirmedC2DMessageID != null ?
                                 $" for C2D message id {this.LastConfirmedC2DMessageID}" : ""),
                                 Logger.LoggingLevel.Info);
                eventProperties.Add(Constants.C2D_MSG_PROPERTY_VALUE_NAME,
                    this.LastConfirmedC2DMessageID ??
                    Constants.C2D_MSG_ID_PLACEHOLDER);
                this.LastConfirmedC2DMessageID = null;
            }
            var macCommand = loRaPayloadData.GetMacCommands();
            if (macCommand.macCommand.Count > 0)
            {
                eventProperties = eventProperties ?? new Dictionary<string, string>();

                for (int i = 0; i < macCommand.macCommand.Count; i++)
                {
                    eventProperties[macCommand.macCommand[i].Cid.ToString()] = JsonConvert.SerializeObject(macCommand.macCommand[i], Formatting.None);

                    //in case it is a link check mac, we need to send it downstream.
                    if (macCommand.macCommand[i].Cid == CidEnum.LinkCheckCmd)
                    {
                        //linkCheckCmdResponse = new LinkCheckCmd(rxPk.GetModulationMargin(), 1).ToBytes();
                    }
                }
            }

            if (await this.SendEventAsync(deviceTelemetry, eventProperties))
            {
                var payloadAsRaw = deviceTelemetry.Data as string;
                if (payloadAsRaw == null && deviceTelemetry.Data != null)
                {
                    payloadAsRaw = JsonConvert.SerializeObject(deviceTelemetry.Data, Formatting.None);
                }

                Logger.Log(this.DevEUI, $"message '{payloadAsRaw}' sent to hub", Logger.LoggingLevel.Info);
            }
        }
    }
}
