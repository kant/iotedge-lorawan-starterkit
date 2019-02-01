// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace LoRaWan.NetworkServer
{
    using System;
    using LoRaTools.LoRaPhysical;

    /// <summary>
    /// Defines delegate that handles a request not being processed
    /// </summary>
    public delegate void FailedLoRaDeviceRequestHandler(LoRaRequest request, LoRaDevice loRaDevice, LoRaDeviceRequestQueueFailedReason reason);

    /// <summary>
    /// Defines delegate that handles a request being processed
    /// </summary>
    public delegate void SucceededLoRaDeviceRequestHandler(LoRaRequest request, LoRaDevice loRaDevice, DownlinkPktFwdMessage downlink);

    /// <summary>
    /// Defines a loRa device request queue
    /// </summary>
    public interface ILoRaDeviceRequestQueue
    {
        /// <summary>
        /// Queues a request
        /// </summary>
        void Queue(LoRaRequestContext requestContext);
    }
}
