// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace LoRaWan.NetworkServer
{
    using System;
    using System.Threading.Tasks;
    using LoRaTools.LoRaMessage;
    using LoRaTools.LoRaPhysical;
    using LoRaTools.Regions;

    public class LoRaRequestContext
    {
        public LoRaTools.Regions.Region LoRaRegion { get; }

        public LoRaRequest Request { get; }

        public NetworkServerConfiguration Configuration { get; }

        public ILoRaDeviceFrameCounterUpdateStrategyFactory FrameCounterUpdateStrategyFactory { get; }

        public ILoRaPayloadDecoder PayloadDecoder { get; }

        private FailedLoRaDeviceRequestHandler failedHandler;
        private SucceededLoRaDeviceRequestHandler succeededHandler;

        internal LoRaRequestContext(LoRaRequest request)
        {
            this.Request = request;
        }

        public LoRaRequestContext(
            LoRaRequest request,
            Region loRaRegion,
            NetworkServerConfiguration configuration,
            ILoRaDeviceFrameCounterUpdateStrategyFactory frameCounterUpdateStrategyFactory,
            ILoRaPayloadDecoder payloadDecoder,
            FailedLoRaDeviceRequestHandler failedHandler,
            SucceededLoRaDeviceRequestHandler succeededHandler = null)
        {
            this.LoRaRegion = loRaRegion;
            this.Request = request;
            this.Configuration = configuration;
            this.PayloadDecoder = payloadDecoder;
            this.FrameCounterUpdateStrategyFactory = frameCounterUpdateStrategyFactory;
            this.failedHandler = failedHandler;
            this.succeededHandler = succeededHandler;
        }

        public void NotifyFailed(LoRaDevice loRaDevice, Exception error) => this.NotifyFailed(loRaDevice, LoRaDeviceRequestQueueFailedReason.ApplicationError);

        public void NotifyFailed(LoRaDevice loRaDevice, LoRaDeviceRequestQueueFailedReason reason)
        {
            this.failedHandler?.Invoke(this.Request, loRaDevice, reason);
            this.Request.NotifyFailed(loRaDevice, reason);
        }

        public void NotifySucceeded(LoRaDevice loRaDevice, DownlinkPktFwdMessage downlink)
        {
            this.succeededHandler?.Invoke(this.Request, loRaDevice, downlink);
            this.Request.NotifySucceeded(loRaDevice, downlink);
        }
    }
}