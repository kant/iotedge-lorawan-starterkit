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

    public class LoRaDeviceRequestProcessResult
    {
        public LoRaDeviceRequestProcessResult(LoRaDevice loRaDevice, LoRaRequest request, DownlinkPktFwdMessage downlinkMessage = null)
        {
            this.LoRaDevice = loRaDevice;
            this.Request = request;
            this.DownlinkMessage = downlinkMessage;
        }

        public DownlinkPktFwdMessage DownlinkMessage { get; }

        public LoRaRequest Request { get; }

        public LoRaDevice LoRaDevice { get; }
    }
}
