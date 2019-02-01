// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
namespace LoRaWan.NetworkServer.Test
{
    using System;
    using System.Collections.Generic;

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
            if (this.Sequence != null && this.Sequence.Count > 0)
            {
                lock (this.Sequence)
                {
                    if (this.sequenceIndex < (this.Sequence.Count - 1))
                    {
                        return TimeSpan.FromMilliseconds(this.Sequence[this.sequenceIndex++]);
                    }

                    // returns the last one
                    return TimeSpan.FromMilliseconds(this.Sequence[this.Sequence.Count - 1]);
                }
            }

            return TimeSpan.FromMilliseconds(this.Duration);
        }

        public static implicit operator RecordedDuration(int value) => new RecordedDuration(value);

        public static implicit operator RecordedDuration(int[] value) => new RecordedDuration(value);
    }
}
