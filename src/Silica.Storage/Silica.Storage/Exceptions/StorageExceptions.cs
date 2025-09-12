using System;
using Silica.Exceptions;

namespace Silica.Storage.Exceptions
{
    /// <summary>
    /// Canonical, low-cardinality Storage exception definitions for Silica.Exceptions.
    /// Contract-first: stable codes, categories, descriptions, and ExceptionIds.
    /// </summary>
    public static class StorageExceptions
    {
        // --------------------------
        // STABLE EXCEPTION IDS
        // --------------------------
        public static class Ids
        {
            public const int StorageDisposeTimeout = 2001;
            public const int InvalidOffset = 2002;
            public const int InvalidLength = 2003;
            public const int AlignmentRequired = 2004;
            public const int DeviceNotMounted = 2005;
            public const int DeviceDisposed = 2006;
            public const int ShortRead = 2007;
            public const int UnsupportedOperation = 2008;
            public const int FaultInjected = 2009;
            public const int InvalidGeometry = 2010;
            public const int DeviceAlreadyMounted = 2011;
            public const int FrameLockDisposed = 2012;
            public const int DeviceReadOutOfRange = 2014;
        }

        // --------------------------
        // RANGE DEFINITION
        // --------------------------
        /// <summary>
        /// Reserved ExceptionId range for Silica.Storage.
        /// </summary>
        public const int MinId = 2000;
        public const int MaxId = 2999;

        /// <summary>
        /// Validates that the given ExceptionId is within the Silica.Storage reserved range.
        /// </summary>
        public static void ValidateId(int id, string paramName)
        {
            if (id < MinId || id > MaxId)
            {
                throw new ArgumentOutOfRangeException(paramName,
                    $"ExceptionId must be between {MinId} and {MaxId} for Silica.Storage.");
            }
        }

        // --------------------------
        // DEFINITIONS
        // --------------------------

        /// <summary>
        /// Read request extends past the device's current length.
        /// </summary>
        public static readonly ExceptionDefinition DeviceReadOutOfRange =
            ExceptionDefinition.Create(
                code: "STORAGE.READ_OUT_OF_RANGE",
                exceptionTypeName: typeof(DeviceReadOutOfRangeException).FullName!,
                category: FailureCategory.IO,
                description: "Read request extends past the device's current length."
            );

        /// <summary>
        /// Storage device failed to shut down within the configured timeout.
        /// </summary>
        public static readonly ExceptionDefinition StorageDisposeTimeout =
            ExceptionDefinition.Create(
                code: "STORAGE.DISPOSE_TIMEOUT",
                exceptionTypeName: typeof(StorageDisposeTimeoutException).FullName!,
                category: FailureCategory.Timeout,
                description: "Storage device failed to shut down within the configured timeout."
            );

        /// <summary>
        /// Offset must be non-negative and within device bounds.
        /// </summary>
        public static readonly ExceptionDefinition InvalidOffset =
            ExceptionDefinition.Create(
                code: "STORAGE.INVALID_OFFSET",
                exceptionTypeName: typeof(InvalidOffsetException).FullName!,
                category: FailureCategory.Validation,
                description: "Offset must be non-negative and within device bounds."
            );

        /// <summary>
        /// Length must be positive and within device bounds.
        /// </summary>
        public static readonly ExceptionDefinition InvalidLength =
            ExceptionDefinition.Create(
                code: "STORAGE.INVALID_LENGTH",
                exceptionTypeName: typeof(InvalidLengthException).FullName!,
                category: FailureCategory.Validation,
                description: "Length must be positive and within device bounds."
            );

        /// <summary>
        /// Offset and length must be multiples of LogicalBlockSize.
        /// </summary>
        public static readonly ExceptionDefinition AlignmentRequired =
            ExceptionDefinition.Create(
                code: "STORAGE.ALIGNMENT_REQUIRED",
                exceptionTypeName: typeof(AlignmentRequiredException).FullName!,
                category: FailureCategory.Validation,
                description: "Offset and length must be multiples of LogicalBlockSize."
            );

        /// <summary>
        /// Operation attempted on a device that is not mounted.
        /// </summary>
        public static readonly ExceptionDefinition DeviceNotMounted =
            ExceptionDefinition.Create(
                code: "STORAGE.DEVICE_NOT_MOUNTED",
                exceptionTypeName: typeof(DeviceNotMountedException).FullName!,
                category: FailureCategory.Internal,
                description: "Operation attempted on a device that is not mounted."
            );

        /// <summary>
        /// Operation attempted on a disposed device.
        /// </summary>
        public static readonly ExceptionDefinition DeviceDisposed =
            ExceptionDefinition.Create(
                code: "STORAGE.DEVICE_DISPOSED",
                exceptionTypeName: typeof(DeviceDisposedException).FullName!,
                category: FailureCategory.Internal,
                description: "Operation attempted on a disposed device."
            );

        /// <summary>
        /// Underlying stream returned fewer bytes than requested.
        /// </summary>
        public static readonly ExceptionDefinition ShortRead =
            ExceptionDefinition.Create(
                code: "STORAGE.SHORT_READ",
                exceptionTypeName: typeof(ShortReadException).FullName!,
                category: FailureCategory.IO,
                description: "Underlying stream returned fewer bytes than requested."
            );

        /// <summary>
        /// Operation not supported by this device or stream configuration.
        /// </summary>
        public static readonly ExceptionDefinition UnsupportedOperation =
            ExceptionDefinition.Create(
                code: "STORAGE.UNSUPPORTED_OPERATION",
                exceptionTypeName: typeof(UnsupportedOperationException).FullName!,
                category: FailureCategory.Configuration,
                description: "Operation not supported by this device or stream configuration."
            );

        /// <summary>
        /// Fault injected for testing purposes.
        /// </summary>
        public static readonly ExceptionDefinition FaultInjected =
            ExceptionDefinition.Create(
                code: "STORAGE.FAULT_INJECTED",
                exceptionTypeName: typeof(FaultInjectedException).FullName!,
                category: FailureCategory.Internal,
                description: "Fault injected for testing purposes."
            );

        /// <summary>
        /// Device geometry is invalid or inconsistent.
        /// </summary>
        public static readonly ExceptionDefinition InvalidGeometry =
            ExceptionDefinition.Create(
                code: "STORAGE.INVALID_GEOMETRY",
                exceptionTypeName: typeof(InvalidGeometryException).FullName!,
                category: FailureCategory.Configuration,
                description: "Device geometry is invalid or inconsistent."
            );

        /// <summary>
        /// Mount attempted when device is already mounted.
        /// </summary>
        public static readonly ExceptionDefinition DeviceAlreadyMounted =
            ExceptionDefinition.Create(
                code: "STORAGE.DEVICE_ALREADY_MOUNTED",
                exceptionTypeName: typeof(DeviceAlreadyMountedException).FullName!,
                category: FailureCategory.Internal,
                description: "Operation attempted on a device that is already mounted."
            );

        /// <summary>
        /// Per-frame lock was disposed/evicted while being acquired.
        /// </summary>
        public static readonly ExceptionDefinition FrameLockDisposed =
            ExceptionDefinition.Create(
                code: "STORAGE.FRAMELOCK_DISPOSED",
                exceptionTypeName: typeof(FrameLockDisposedException).FullName!,
                category: FailureCategory.Internal,
                description: "Frame lock was disposed/evicted during acquisition."
            );

        // --------------------------
        // REGISTRATION
        // --------------------------

        /// <summary>
        /// Registers all Storage exception definitions with the global ExceptionRegistry.
        /// Call once at subsystem startup.
        /// </summary>
        public static void RegisterAll()
        {
            ExceptionRegistry.RegisterAll(new[]
            {
                DeviceReadOutOfRange,
                StorageDisposeTimeout,
                InvalidOffset,
                InvalidLength,
                AlignmentRequired,
                DeviceNotMounted,
                DeviceDisposed,
                ShortRead,
                UnsupportedOperation,
                FaultInjected,
                InvalidGeometry,
                DeviceAlreadyMounted,
                FrameLockDisposed
            });
        }
    }
}
