// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace LoRaWan.NetworkServer
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using LoRaTools;
    using LoRaTools.LoRaMessage;
    using LoRaTools.LoRaPhysical;
    using LoRaTools.Regions;
    using LoRaTools.Utils;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Extensions.Logging;
    using Newtonsoft.Json;

    /// <summary>
    /// Message processor
    /// </summary>
    public class MessageProcessor
    {
        // Defines Cloud to device message property containing fport value
        internal const string FPORT_MSG_PROPERTY_KEY = "fport";

        // Fport value reserved for mac commands
        const byte LORA_FPORT_RESERVED_MAC_MSG = 0;

        // Starting Fport value reserved for future applications
        const byte LORA_FPORT_RESERVED_FUTURE_START = 224;

        // Default value of a C2D message id if missing from the message
        internal const string C2D_MSG_ID_PLACEHOLDER = "ConfirmationC2DMessageWithNoId";

        // Name of the upstream message property reporint a confirmed message
        internal const string C2D_MSG_PROPERTY_VALUE_NAME = "C2DMsgConfirmed";

        private readonly NetworkServerConfiguration configuration;
        private readonly ILoRaDeviceRegistry deviceRegistry;
        private readonly ILoRaDeviceFrameCounterUpdateStrategyFactory frameCounterUpdateStrategyFactory;
        private readonly ILoRaPayloadDecoder payloadDecoder;
        private volatile Region loraRegion;

        public MessageProcessor(
            NetworkServerConfiguration configuration,
            ILoRaDeviceRegistry deviceRegistry,
            ILoRaDeviceFrameCounterUpdateStrategyFactory frameCounterUpdateStrategyFactory,
            ILoRaPayloadDecoder payloadDecoder)
        {
            this.configuration = configuration;
            this.deviceRegistry = deviceRegistry;
            this.frameCounterUpdateStrategyFactory = frameCounterUpdateStrategyFactory;
            this.payloadDecoder = payloadDecoder;

            // Register frame counter initializer
            // It will take care of seeding ABP devices created here for single gateway scenarios
            this.deviceRegistry.RegisterDeviceInitializer(new FrameCounterLoRaDeviceInitializer(configuration.GatewayID, frameCounterUpdateStrategyFactory));
        }

        /// <summary>
        /// Process a raw message
        /// </summary>
        /// <returns>A <see cref="DownlinkPktFwdMessage"/> if a message has to be sent back to device</returns>
        public async Task<DownlinkPktFwdMessage> ProcessRequestAsync(LoRaRequest request)
        {
            if (!LoRaPayload.TryCreateLoRaPayload(request.Rxpk, out LoRaPayload loRaPayload))
            {
                Logger.Log("There was a problem in decoding the Rxpk", LogLevel.Error);
                return null;
            }

            if (this.loraRegion == null)
            {
                if (!RegionFactory.TryResolveRegion(request.Rxpk))
                {
                    // log is generated in Region factory
                    // move here once V2 goes GA
                    return null;
                }

                this.loraRegion = RegionFactory.CurrentRegion;
            }

            request.SetPayload(loRaPayload);

            if (loRaPayload.LoRaMessageType == LoRaMessageType.JoinRequest)
            {
                return await this.ProcessJoinRequestAsync(request.Rxpk, (LoRaPayloadJoinRequest)loRaPayload, request.StartTime);
            }
            else if (loRaPayload.LoRaMessageType == LoRaMessageType.UnconfirmedDataUp || loRaPayload.LoRaMessageType == LoRaMessageType.ConfirmedDataUp)
            {
                this.DispatchLoRaDataMessage(request);
            }

            return null;
        }

        private void DispatchLoRaDataMessage(LoRaRequest request)
        {
            var loRaPayload = (LoRaPayloadData)request.Payload;
            if (!this.IsValidNetId(loRaPayload.GetDevAddrNetID(), this.configuration.NetId))
            {
                Logger.Log(ConversionHelper.ByteArrayToString(loRaPayload.DevAddr), "device is using another network id, ignoring this message", LogLevel.Information);
                // Todo: log processing time
                return;
            }

            var reqContext = new LoRaRequestContext(
                    request,
                    this.loraRegion,
                    this.configuration,
                    this.frameCounterUpdateStrategyFactory,
                    this.payloadDecoder,
                    this.OnRequestedFailed);

            this.deviceRegistry.QueueRequest(reqContext);
        }

        private void OnRequestedFailed(LoRaRequest request, LoRaDevice loRaDevice, LoRaDeviceRequestQueueFailedReason reason)
        {
            // failed
            // log that the message was not processed because it does not belong to our network
            // mic failed: if there are items in the collection
            // unkown devAddr: if there are no items in the collection
        }

        bool IsValidNetId(byte devAddrNwkid, uint netId)
        {
            var netIdBytes = BitConverter.GetBytes(netId);
            devAddrNwkid = (byte)(devAddrNwkid >> 1);
            return devAddrNwkid == (netIdBytes[0] & 0b01111111);
        }

        /// <summary>
        /// Process OTAA join request
        /// </summary>
        async Task<DownlinkPktFwdMessage> ProcessJoinRequestAsync(Rxpk rxpk, LoRaPayloadJoinRequest joinReq, DateTime startTimeProcessing)
        {
            var timeWatcher = new LoRaOperationTimeWatcher(this.loraRegion, startTimeProcessing);
            using (var processLogger = new ProcessLogger(timeWatcher))
            {
                byte[] udpMsgForPktForwarder = new byte[0];

                var devEUI = joinReq.GetDevEUIAsString();
                var appEUI = joinReq.GetAppEUIAsString();

                // set context to logger
                processLogger.SetDevEUI(devEUI);

                var devNonce = joinReq.GetDevNonceAsString();
                Logger.Log(devEUI, $"join request received", LogLevel.Information);

                var loRaDevice = await this.deviceRegistry.GetDeviceForJoinRequestAsync(devEUI, appEUI, devNonce);
                if (loRaDevice == null)
                    return null;

                if (string.IsNullOrEmpty(loRaDevice.AppKey))
                {
                    Logger.Log(loRaDevice.DevEUI, "join refused: missing AppKey for OTAA device", LogLevel.Error);
                    return null;
                }

                if (loRaDevice.AppEUI != appEUI)
                {
                    Logger.Log(devEUI, "join refused: AppEUI for OTAA does not match device", LogLevel.Error);
                    return null;
                }

                if (!joinReq.CheckMic(loRaDevice.AppKey))
                {
                    Logger.Log(devEUI, "join refused: invalid MIC", LogLevel.Error);
                    return null;
                }

                // Make sure that is a new request and not a replay
                if (!string.IsNullOrEmpty(loRaDevice.DevNonce) && loRaDevice.DevNonce == devNonce)
                {
                    Logger.Log(devEUI, "join refused: DevNonce already used by this device", LogLevel.Information);
                    loRaDevice.IsOurDevice = false;
                    return null;
                }

                // Check that the device is joining through the linked gateway and not another
                if (!string.IsNullOrEmpty(loRaDevice.GatewayID) && !string.Equals(loRaDevice.GatewayID, this.configuration.GatewayID, StringComparison.InvariantCultureIgnoreCase))
                {
                    Logger.Log(devEUI, $"join refused: trying to join not through its linked gateway, ignoring join request", LogLevel.Information);
                    loRaDevice.IsOurDevice = false;
                    return null;
                }

                var netIdBytes = BitConverter.GetBytes(this.configuration.NetId);
                var netId = new byte[3]
                {
                    netIdBytes[0],
                    netIdBytes[1],
                    netIdBytes[2]
                };
                var appNonce = OTAAKeysGenerator.GetAppNonce();
                var appNonceBytes = LoRaTools.Utils.ConversionHelper.StringToByteArray(appNonce);
                var appKeyBytes = LoRaTools.Utils.ConversionHelper.StringToByteArray(loRaDevice.AppKey);
                var appSKey = OTAAKeysGenerator.CalculateKey(new byte[1] { 0x02 }, appNonceBytes, netId, joinReq.DevNonce, appKeyBytes);
                var nwkSKey = OTAAKeysGenerator.CalculateKey(new byte[1] { 0x01 }, appNonceBytes, netId, joinReq.DevNonce, appKeyBytes);
                var devAddr = OTAAKeysGenerator.GetNwkId(netId);

                if (!timeWatcher.InTimeForJoinAccept())
                {
                    // in this case it's too late, we need to break and avoid saving twins
                    Logger.Log(devEUI, $"join refused: processing of the join request took too long, sending no message", LogLevel.Information);
                    return null;
                }

                Logger.Log(loRaDevice.DevEUI, $"saving join properties twins", LogLevel.Debug);
                var deviceUpdateSucceeded = await loRaDevice.UpdateAfterJoinAsync(devAddr, nwkSKey, appSKey, appNonce, devNonce, LoRaTools.Utils.ConversionHelper.ByteArrayToString(netId));
                Logger.Log(loRaDevice.DevEUI, $"done saving join properties twins", LogLevel.Debug);

                if (!deviceUpdateSucceeded)
                {
                    Logger.Log(devEUI, $"join refused: join request could not save twins", LogLevel.Error);
                    return null;
                }

                var windowToUse = timeWatcher.ResolveJoinAcceptWindowToUse(loRaDevice);
                if (windowToUse == 0)
                {
                    Logger.Log(devEUI, $"join refused: processing of the join request took too long, sending no message", LogLevel.Information);
                    return null;
                }

                double freq = 0;
                string datr = null;
                uint tmst = 0;
                if (windowToUse == 1)
                {
                    datr = this.loraRegion.GetDownstreamDR(rxpk);
                    freq = this.loraRegion.GetDownstreamChannel(rxpk);

                    // set tmst for the normal case
                    tmst = rxpk.Tmst + this.loraRegion.Join_accept_delay1 * 1000000;
                }
                else
                {
                    Logger.Log(devEUI, $"processing of the join request took too long, using second join accept receive window", LogLevel.Information);
                    tmst = rxpk.Tmst + this.loraRegion.Join_accept_delay2 * 1000000;
                    if (string.IsNullOrEmpty(this.configuration.Rx2DataRate))
                    {
                        Logger.Log(devEUI, $"using standard second receive windows for join request", LogLevel.Information);
                        // using EU fix DR for RX2
                        freq = this.loraRegion.RX2DefaultReceiveWindows.frequency;
                        datr = this.loraRegion.DRtoConfiguration[RegionFactory.CurrentRegion.RX2DefaultReceiveWindows.dr].configuration;
                    }

                    // if specific twins are set, specify second channel to be as specified
                    else
                    {
                        Logger.Log(devEUI, $"using custom  second receive windows for join request", LogLevel.Information);
                        freq = this.configuration.Rx2DataFrequency;
                        datr = this.configuration.Rx2DataRate;
                    }
                }

                loRaDevice.IsOurDevice = true;
                this.deviceRegistry.UpdateDeviceAfterJoin(loRaDevice);

                // Build join accept downlink message
                Array.Reverse(netId);
                Array.Reverse(appNonceBytes);

                return this.CreateJoinAcceptDownlinkMessage(
                    netId,
                    loRaDevice.AppKey,
                    devAddr,
                    appNonceBytes,
                    datr,
                    freq,
                    tmst,
                    rxpk,
                    devEUI);
            }
        }

        /// <summary>
        /// Creates downlink message for join accept
        /// </summary>
        DownlinkPktFwdMessage CreateJoinAcceptDownlinkMessage(
            ReadOnlyMemory<byte> netId,
            string appKey,
            string devAddr,
            ReadOnlyMemory<byte> appNonce,
            string datr,
            double freq,
            long tmst,
            Rxpk rxpk,
            string devEUI)
        {
            var loRaPayloadJoinAccept = new LoRaTools.LoRaMessage.LoRaPayloadJoinAccept(
                LoRaTools.Utils.ConversionHelper.ByteArrayToString(netId), // NETID 0 / 1 is default test
                ConversionHelper.StringToByteArray(devAddr), // todo add device address management
                appNonce.ToArray(),
                new byte[] { 0 },
                new byte[] { 0 },
                null);

            return loRaPayloadJoinAccept.Serialize(appKey, datr, freq, tmst, devEUI);
        }
    }
}