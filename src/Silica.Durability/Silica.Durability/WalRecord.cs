using System;

namespace Silica.Durability
{
    public sealed class WalRecord
    {
        /// <summary>
        /// Monotonically increasing sequence number; used for ordering during recovery.
        /// Note: When appending via IWalManager, the writer assigns the actual persisted LSN.
        /// The value provided here by producers is not used by the writer and may differ.
        /// </summary>
        public long SequenceNumber { get; }

        /// <summary>
        /// The bytes to persist to the WAL. Immutable after construction.
        /// </summary>
        public ReadOnlyMemory<byte> Payload { get; }

        /// <summary>
        /// Timestamp (UTC) when this record was created. Helpful for diagnostics or metrics.
        /// </summary>
        public DateTimeOffset CreatedUtc { get; }

        /// <summary>
        /// Constructs a new WAL record.
        /// </summary>
        /// <param name="sequenceNumber">
        /// Must be non-negative; typically assigned by a single-producer allocator to ensure global ordering.
        /// </param>
        /// <param name="payload">
        /// The payload to log. Pass in a ReadOnlyMemory to avoid unnecessary copies.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// If <paramref name="sequenceNumber"/> is negative.
        /// </exception>
        public WalRecord(long sequenceNumber, ReadOnlyMemory<byte> payload)
        {
            if (sequenceNumber < 0)
                throw new ArgumentOutOfRangeException(nameof(sequenceNumber), "Sequence number must be non-negative.");

            SequenceNumber = sequenceNumber;
            Payload = payload;
            CreatedUtc = DateTimeOffset.UtcNow;
        }
    }
}