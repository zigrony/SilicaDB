// File: Silica.Sessions\Exceptions\InvalidSnapshotLsnException.cs

using System;
using Silica.Exceptions;

namespace Silica.Sessions
{
    public sealed class InvalidSnapshotLsnException : SilicaException
    {
        public long SnapshotLsn { get; }

        public InvalidSnapshotLsnException(long snapshotLsn)
            : base(
                code: SessionExceptions.InvalidSnapshotLsn.Code,
                message: $"Snapshot LSN must be > 0. Provided: {snapshotLsn}.",
                category: SessionExceptions.InvalidSnapshotLsn.Category,
                exceptionId: SessionExceptions.Ids.InvalidSnapshotLsn)
        {
            SessionExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            SnapshotLsn = snapshotLsn;
        }
    }
}
