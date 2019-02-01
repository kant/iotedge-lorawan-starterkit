﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace LoRaWan.NetworkServer.Test
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using LoRaTools.LoRaMessage;
    using LoRaTools.LoRaPhysical;
    using LoRaTools.Regions;
    using LoRaWan.NetworkServer;
    using LoRaWan.Test.Shared;
    using Microsoft.Azure.Devices.Shared;
    using Microsoft.Extensions.Caching.Memory;
    using Moq;
    using Xunit;

    public class WaitableLoRaRequest : LoRaRequest
    {
        SemaphoreSlim complete;

        public bool Failed { get; private set; }

        public LoRaDevice LoRaDevice { get; private set; }

        public LoRaDeviceRequestQueueFailedReason FailedReason { get; private set; }

        public DownlinkPktFwdMessage Downlink { get; private set; }

        public bool Succeeded { get; private set; }

        public WaitableLoRaRequest(LoRaPayloadData payload)
            : base(payload)
        {
            this.complete = new SemaphoreSlim(0);
            this.SetFailedHandler(this.OnFailed);
            this.SetSucceededHandler(this.OnSucceeded);
        }

        public WaitableLoRaRequest(Rxpk rxpk, IPacketForwarder packetForwarder)
            : base(rxpk, packetForwarder, DateTime.UtcNow)
        {
            this.complete = new SemaphoreSlim(0);
            this.SetFailedHandler(this.OnFailed);
            this.SetSucceededHandler(this.OnSucceeded);
        }

        private void OnSucceeded(LoRaRequest request, LoRaDevice loRaDevice, DownlinkPktFwdMessage downlink)
        {
            this.LoRaDevice = loRaDevice;
            this.Downlink = downlink;
            this.Succeeded = true;
            this.complete.Release();
        }

        private void OnFailed(LoRaRequest request, LoRaDevice loRaDevice, LoRaDeviceRequestQueueFailedReason reason)
        {
            this.Failed = true;
            this.LoRaDevice = loRaDevice;
            this.FailedReason = reason;
            this.complete.Release();
        }

        internal Task<bool> WaitCompleteAsync(int timeout = 10000) => this.complete.WaitAsync(timeout);
    }
}
