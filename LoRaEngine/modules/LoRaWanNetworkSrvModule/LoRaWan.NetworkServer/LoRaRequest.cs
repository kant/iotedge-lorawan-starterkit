// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace LoRaWan.NetworkServer
{
    using System;
    using LoRaTools.LoRaMessage;
    using LoRaTools.LoRaPhysical;

    public class LoRaRequest
    {
        private FailedLoRaDeviceRequestHandler failedHandler;
        private SucceededLoRaDeviceRequestHandler succeededHandler;

        public Rxpk Rxpk { get; }

        public LoRaPayload Payload { get; private set; }

        public IPacketForwarder PacketForwarder { get; }

        public DateTime StartTime { get; }

        protected LoRaRequest(LoRaPayload payload)
        {
            this.Payload = payload;
        }

        public LoRaRequest(
            Rxpk rxpk,
            IPacketForwarder packetForwarder,
            DateTime startTime,
            FailedLoRaDeviceRequestHandler failedHandler = null,
            SucceededLoRaDeviceRequestHandler succeededHandler = null)
        {
            this.Rxpk = rxpk;
            // this.Payload = payload;
            this.PacketForwarder = packetForwarder;
            this.StartTime = startTime;
            this.failedHandler = failedHandler;
            this.succeededHandler = succeededHandler;
        }

        protected void SetFailedHandler(FailedLoRaDeviceRequestHandler handler) => this.failedHandler = handler;

        protected void SetSucceededHandler(SucceededLoRaDeviceRequestHandler handler) => this.succeededHandler = handler;

        public void NotifyFailed(LoRaDevice loRaDevice, Exception error) => this.NotifyFailed(loRaDevice, LoRaDeviceRequestQueueFailedReason.ApplicationError);

        public void NotifyFailed(LoRaDevice loRaDevice, LoRaDeviceRequestQueueFailedReason reason)
        {
            this.failedHandler?.Invoke(this, loRaDevice, reason);
        }

        public void NotifySucceeded(LoRaDevice loRaDevice, DownlinkPktFwdMessage downlink)
        {
            this.succeededHandler?.Invoke(this, loRaDevice, downlink);
        }

        internal void SetPayload(LoRaPayload loRaPayload) => this.Payload = loRaPayload;
    }
}