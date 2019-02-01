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
    using LoRaTools;
    using LoRaTools.LoRaMessage;
    using LoRaTools.LoRaPhysical;
    using LoRaTools.Utils;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Devices.Shared;
    using Microsoft.Extensions.Logging;
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

        readonly Queue<LoRaRequestContext> queuedRequests = new Queue<LoRaRequestContext>();
        volatile LoRaRequestContext runningRequest;

        public void QueueRequest(LoRaRequestContext requestContext)
        {
            // Access to runningRequest and queuedRequests must be
            // thread safe
            lock (this)
            {
                if (this.runningRequest == null)
                {
                    this.StartNextRequest(requestContext);
                }
                else
                {
                    this.queuedRequests.Enqueue(requestContext);
                }
            }
        }

        private void OnRequestCompleted(Task<LoRaDeviceRequestProcessResult> completedTask)
        {
            LoRaRequestContext processedRequest = null;
            DownlinkPktFwdMessage downlink = null;
            Exception processingError = null;

            // Access to runningRequest and queuedRequests must be
            // thread safe
            lock (this)
            {
                processedRequest = this.runningRequest;
                if (completedTask.IsCompletedSuccessfully)
                {
                    downlink = completedTask.Result.DownlinkMessage;
                }
                else
                {
                    processingError = completedTask.Exception;
                }

                this.runningRequest = null;
                if (this.queuedRequests.TryDequeue(out var nextRequest))
                {
                    this.StartNextRequest(nextRequest);
                }
            }

            if (processingError != null)
            {
                processedRequest.NotifyFailed(this, processingError);
            }
            else
            {
                processedRequest.NotifySucceeded(this, downlink);
            }
        }

        void StartNextRequest(LoRaRequestContext requestContext)
        {
            this.runningRequest = requestContext;

            this.ProcessRequest(requestContext)
                .ContinueWith(this.OnRequestCompleted)
                .ConfigureAwait(false);
        }

        async Task<LoRaDeviceRequestProcessResult> ProcessRequest(LoRaRequestContext requestContext)
        {
            var timer = new LoRaOperationTimeWatcher(requestContext.LoRaRegion, requestContext.Request.StartTime);
            var loraPayload = (LoRaPayloadData)requestContext.Request.Payload;
            var isMultiGateway = !string.Equals(this.GatewayID, requestContext.Configuration.GatewayID, StringComparison.InvariantCultureIgnoreCase);
            var frameCounterStrategy = isMultiGateway ?
                requestContext.FrameCounterUpdateStrategyFactory.GetMultiGatewayStrategy() :
                requestContext.FrameCounterUpdateStrategyFactory.GetSingleGatewayStrategy();

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
                    // if it is confirmed most probably we did not ack in time before or device lost the ack packet so we should continue but not send the msg to iothub
                    if (requiresConfirmation && payloadFcnt == this.FCntUp)
                    {
                        isConfirmedResubmit = true;
                        Logger.Log(this.DevEUI, $"resubmit from confirmed message detected, msg: {payloadFcnt} server: {this.FCntUp}", LogLevel.Information);
                    }
                    else
                    {
                        Logger.Log(this.DevEUI, $"invalid frame counter, message ignored, msg: {payloadFcnt} server: {this.FCntUp}", LogLevel.Information);
                        return new LoRaDeviceRequestProcessResult(this, requestContext.Request);
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
                        // loRaDevice.SetFcntUp(payloadFcnt);
                        Logger.Log(this.DevEUI, "another gateway has already sent ack or downlink msg", LogLevel.Information);

                        return new LoRaDeviceRequestProcessResult(this, requestContext.Request);
                    }

                    Logger.Log(this.DevEUI, $"down frame counter: {this.FCntDown}", LogLevel.Information);
                }

                if (!isConfirmedResubmit)
                {
                    var validFcntUp = isFrameCounterFromNewlyStartedDevice || (payloadFcnt > this.FCntUp);
                    if (validFcntUp)
                    {
                        Logger.Log(this.DevEUI, $"valid frame counter, msg: {payloadFcnt} server: {this.FCntUp}", LogLevel.Information);

                        object payloadData = null;

                        // if it is an upward acknowledgement from the device it does not have a payload
                        // This is confirmation from leaf device that he received a C2D confirmed
                        // if a message payload is null we don't try to decrypt it.
                        if (loraPayload.Frmpayload.Length != 0)
                        {
                            byte[] decryptedPayloadData = null;
                            try
                            {
                                decryptedPayloadData = loraPayload.GetDecryptedPayload(this.AppSKey);
                            }
                            catch (Exception ex)
                            {
                                Logger.Log(this.DevEUI, $"failed to decrypt message: {ex.Message}", LogLevel.Error);
                            }

                            var fportUp = loraPayload.GetFPort();

                            if (string.IsNullOrEmpty(this.SensorDecoder))
                            {
                                Logger.Log(this.DevEUI, $"no decoder set in device twin. port: {fportUp}", LogLevel.Debug);
                                payloadData = Convert.ToBase64String(decryptedPayloadData);
                            }
                            else
                            {
                                Logger.Log(this.DevEUI, $"decoding with: {this.SensorDecoder} port: {fportUp}", LogLevel.Debug);
                                payloadData = await requestContext.PayloadDecoder.DecodeMessageAsync(decryptedPayloadData, fportUp, this.SensorDecoder);
                            }
                        }

                        // What do we need to send an UpAck to IoT Hub?
                        // What is the content of the message
                        // TODO Future: Don't wait if it is an unconfirmed message
                        await this.SendDeviceEventAsync(requestContext, timer, payloadData);

                        this.SetFcntUp(payloadFcnt);
                    }
                    else
                    {
                        Logger.Log(this.DevEUI, $"invalid frame counter, msg: {payloadFcnt} server: {this.FCntUp}", LogLevel.Information);
                    }
                }

                // We check if we have time to futher progress or not
                // C2D checks are quite expensive so if we are really late we just stop here
                var timeToSecondWindow = timer.GetRemainingTimeToReceiveSecondWindow(this);
                if (timeToSecondWindow < LoRaOperationTimeWatcher.ExpectedTimeToPackageAndSendMessage)
                {
                    if (requiresConfirmation)
                    {
                        Logger.Log(this.DevEUI, $"too late for down message ({timer.GetElapsedTime()}), sending only ACK to gateway", LogLevel.Information);
                    }

                    return new LoRaDeviceRequestProcessResult(this, requestContext.Request);
                }

                // If it is confirmed and we don't have time to check c2d and send to device we return now
                if (requiresConfirmation && timeToSecondWindow <= LoRaOperationTimeWatcher.ExpectedTimeToCheckCloudToDeviceMessagePackageAndSendMessage)
                {
                    var downlinkMessage = this.CreateDownlinkMessage(
                        null,
                        requestContext,
                        timer,
                        false, // fpending
                        (ushort)fcntDown);

                    if (downlinkMessage != null)
                    {
                        _ = requestContext.Request.PacketForwarder.SendDownstreamAsync(downlinkMessage);
                    }

                    return new LoRaDeviceRequestProcessResult(this, requestContext.Request, downlinkMessage);
                }

                // ReceiveAsync has a longer timeout
                // But we wait less that the timeout (available time before 2nd window)
                // if message is received after timeout, keep it in loraDeviceInfo and return the next call
                var cloudToDeviceReceiveTimeout = timeToSecondWindow - LoRaOperationTimeWatcher.ExpectedTimeToCheckCloudToDeviceMessagePackageAndSendMessage;
                // Flag indicating if there is another C2D message waiting
                var fpending = false;
                // Contains the Cloud to message we need to send
                Message cloudToDeviceMessage = null;
                if (cloudToDeviceReceiveTimeout > TimeSpan.Zero)
                {
                    cloudToDeviceMessage = await this.ReceiveCloudToDeviceAsync(cloudToDeviceReceiveTimeout);
                    if (cloudToDeviceMessage != null && !this.ValidateCloudToDeviceMessage(this, cloudToDeviceMessage))
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

                                Logger.Log(this.DevEUI, $"down frame counter: {this.FCntDown}", LogLevel.Information);
                            }
                        }

                        // Checking again because the fcntdown resolution could have failed, causing us to drop the message
                        if (cloudToDeviceMessage != null)
                        {
                            timeToSecondWindow = timer.GetRemainingTimeToReceiveSecondWindow(this);
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
                    // await frameCounterStrategy.SaveChangesAsync(loRaDevice);
                    return new LoRaDeviceRequestProcessResult(this, requestContext.Request);
                }

                // We did it in the LoRaPayloadData constructor
                // we got here:
                // a) was a confirm request
                // b) we have a c2d message
                var confirmDownstream = this.CreateDownlinkMessage(
                    cloudToDeviceMessage,
                    requestContext,
                    timer,
                    fpending,
                    (ushort)fcntDown);

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
                    _ = requestContext.Request.PacketForwarder.SendDownstreamAsync(confirmDownstream);
                }

                return new LoRaDeviceRequestProcessResult(this, requestContext.Request, confirmDownstream);
            }
        }

        private bool ValidateCloudToDeviceMessage(LoRaDevice loRaDevice, Message cloudToDeviceMsg)
        {
            // ensure fport property has been set
            if (!cloudToDeviceMsg.Properties.TryGetValueCaseInsensitive(Constants.FPORT_MSG_PROPERTY_KEY, out var fportValue))
            {
                Logger.Log(loRaDevice.DevEUI, $"missing {Constants.FPORT_MSG_PROPERTY_KEY} property in C2D message '{cloudToDeviceMsg.MessageId}'", LogLevel.Error);
                return false;
            }

            if (byte.TryParse(fportValue, out var fport))
            {
                // ensure fport follows LoRa specification
                // 0    => reserved for mac commands
                // 224+ => reserved for future applications
                if (fport != Constants.LORA_FPORT_RESERVED_MAC_MSG && fport < Constants.LORA_FPORT_RESERVED_FUTURE_START)
                    return true;
            }

            Logger.Log(loRaDevice.DevEUI, $"invalid fport '{fportValue}' in C2D message '{cloudToDeviceMsg.MessageId}'", LogLevel.Error);
            return false;
        }

        /// <summary>
        /// Creates downlink message with ack for confirmation or cloud to device message
        /// </summary>
        private DownlinkPktFwdMessage CreateDownlinkMessage(
            Message cloudToDeviceMessage,
            LoRaRequestContext requestContext,
            LoRaOperationTimeWatcher timer,
            bool fpending,
            ushort fcntDown)
        {
            // default fport
            byte fctrl = 0;
            var loRaPayloadData = (LoRaPayloadData)requestContext.Request.Payload;
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
                    Logger.Log(this.DevEUI, $"Cloud to device MAC command received", LogLevel.Information);
                    var macCommandHolder = new MacCommandHolder(Convert.ToByte(cidTypeValue));
                    macbytes = macCommandHolder.MacCommand[0].ToBytes();
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

                Logger.Log(this.DevEUI, $"Sending a downstream message with ID {ConversionHelper.ByteArrayToString(rndToken)}", LogLevel.Debug);

                frmPayload = cloudToDeviceMessage?.GetBytes();

                Logger.Log(this.DevEUI, $"C2D message: {Encoding.UTF8.GetString(frmPayload)}, id: {cloudToDeviceMessage.MessageId ?? "undefined"}, fport: {fport}, confirmed: {requiresDeviceAcknowlegement}, cidType: {cidTypeValue}", LogLevel.Information);

                // cut to the max payload of lora for any EU datarate
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
            var payloadDevAddr = requestContext.Request.Payload.DevAddr.Span;
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

            var receiveWindow = timer.ResolveReceiveWindowToUse(this);
            if (receiveWindow == 0)
                return null;

            string datr = null;
            double freq;
            long tmst;
            if (receiveWindow == 2)
            {
                tmst = requestContext.Request.Rxpk.Tmst + requestContext.LoRaRegion.Receive_delay2 * 1000000;

                if (string.IsNullOrEmpty(requestContext.Configuration.Rx2DataRate))
                {
                    Logger.Log(this.DevEUI, $"using standard second receive windows", LogLevel.Information);
                    freq = requestContext.LoRaRegion.RX2DefaultReceiveWindows.frequency;
                    datr = requestContext.LoRaRegion.DRtoConfiguration[requestContext.LoRaRegion.RX2DefaultReceiveWindows.dr].configuration;
                }

                // if specific twins are set, specify second channel to be as specified
                else
                {
                    freq = requestContext.Configuration.Rx2DataFrequency;
                    datr = requestContext.Configuration.Rx2DataRate;
                    Logger.Log(this.DevEUI, $"using custom DR second receive windows freq : {freq}, datr:{datr}", LogLevel.Information);
                }
            }
            else
            {
                datr = requestContext.LoRaRegion.GetDownstreamDR(requestContext.Request.Rxpk);
                freq = requestContext.LoRaRegion.GetDownstreamChannel(requestContext.Request.Rxpk);
                tmst = requestContext.Request.Rxpk.Tmst + requestContext.LoRaRegion.Receive_delay1 * 1000000;
            }

            return ackLoRaMessage.Serialize(this.AppSKey, this.NwkSKey, datr, freq, tmst, this.DevEUI);
        }

        private async Task SendDeviceEventAsync(LoRaRequestContext requestContext, LoRaOperationTimeWatcher timer, object decodedValue)
        {
            var loRaPayloadData = (LoRaPayloadData)requestContext.Request.Payload;
            var deviceTelemetry = new LoRaDeviceTelemetry(requestContext.Request.Rxpk, loRaPayloadData, decodedValue);
            deviceTelemetry.DeviceEUI = this.DevEUI;
            deviceTelemetry.GatewayID = requestContext.Configuration.GatewayID;
            deviceTelemetry.Edgets = (long)(timer.Start - DateTime.UnixEpoch).TotalMilliseconds;

            Dictionary<string, string> eventProperties = null;
            if (loRaPayloadData.IsUpwardAck())
            {
                eventProperties = new Dictionary<string, string>();
                Logger.Log(this.DevEUI, $"Message ack received for C2D message id {this.LastConfirmedC2DMessageID ?? "undefined"}", LogLevel.Information);
                eventProperties.Add(Constants.C2D_MSG_PROPERTY_VALUE_NAME, this.LastConfirmedC2DMessageID ?? Constants.C2D_MSG_ID_PLACEHOLDER);
                this.LastConfirmedC2DMessageID = null;
            }

            var macCommand = loRaPayloadData.GetMacCommands();
            if (macCommand.MacCommand.Count > 0)
            {
                eventProperties = eventProperties ?? new Dictionary<string, string>();

                for (int i = 0; i < macCommand.MacCommand.Count; i++)
                {
                    eventProperties[macCommand.MacCommand[i].Cid.ToString()] = JsonConvert.SerializeObject(macCommand.MacCommand[i], Formatting.None);

                    // in case it is a link check mac, we need to send it downstream.
                    if (macCommand.MacCommand[i].Cid == CidEnum.LinkCheckCmd)
                    {
                        // linkCheckCmdResponse = new LinkCheckCmd(rxPk.GetModulationMargin(), 1).ToBytes();
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

                Logger.Log(this.DevEUI, $"message '{payloadAsRaw}' sent to hub", LogLevel.Information);
            }
        }
    }
}
