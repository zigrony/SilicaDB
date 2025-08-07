// File: CheckpointData.cs
using System;

namespace SilicaDB.Durability
{
    public sealed class CheckpointData
    {
        public long LastSequenceNumber { get; init; }
        public DateTimeOffset CreatedUtc { get; init; }

        // In future you can add more fields: file offsets, snapshot metadata, etc.
    }
}
