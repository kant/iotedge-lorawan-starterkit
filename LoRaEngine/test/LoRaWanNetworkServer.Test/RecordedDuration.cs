//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//
using LoRaWan.NetworkServer;
using LoRaWan.Test.Shared;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Shared;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace LoRaWan.NetworkServer.Test
{    
    public class RecordedDuration
    {
        int Duration { get; set; }
        IReadOnlyList<int> Sequence { get; set; }

        int sequenceIndex;

        public RecordedDuration(int duration)
        {
            this.Duration = duration;
        }

        public RecordedDuration(IReadOnlyList<int> sequence)
        {
            this.Sequence = sequence;
        }

        public TimeSpan Next()
        {
            if (Sequence != null && Sequence.Count > 0)
            {
                lock (Sequence)
                {
                    if (sequenceIndex < (Sequence.Count - 1))
                    {
                        return TimeSpan.FromMilliseconds(Sequence[sequenceIndex++]);
                    }

                    // returns the last one
                    return TimeSpan.FromMilliseconds(Sequence[Sequence.Count - 1]);
                }
            }

            return TimeSpan.FromMilliseconds(Duration);
        }

        public static implicit operator RecordedDuration(int value) => new RecordedDuration(value);

        public static implicit operator RecordedDuration(int[] value) => new RecordedDuration(value);

    }
}
