// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace LoRaWan.NetworkServer
{
    public class ResolvedLoRaDeviceForDevAddr : ILoRaDeviceRequestQueue
    {
        private readonly LoRaDevice loRaDevice;

        public ResolvedLoRaDeviceForDevAddr(LoRaDevice loRaDevice)
        {
            this.loRaDevice = loRaDevice;
        }

        public void Queue(LoRaRequestContext requestContext) => this.loRaDevice.QueueRequest(requestContext);
    }
}
