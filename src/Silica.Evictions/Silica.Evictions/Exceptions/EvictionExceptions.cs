using System;
using Silica.Exceptions;

namespace Silica.Evictions.Exceptions
{
    /// <summary>
    /// Canonical, low-cardinality Evictions exception definitions for Silica.Exceptions.
    /// Contract-first: stable codes, categories, descriptions, and ExceptionIds.
    /// </summary>
    public static class EvictionExceptions
    {
        // --------------------------
        // STABLE EXCEPTION IDS
        // --------------------------
        public static class Ids
        {
            public const int NullKey = 3001;
            public const int NullValueFactory = 3002;
            public const int NullCacheRegistration = 3003;
            public const int InvalidCapacity = 3004;
            public const int InvalidIdleTimeout = 3005;
            public const int CacheDisposed = 3006;
            public const int ManagerDisposed = 3007;
            public const int LfuInvariantViolation = 3008;
            public const int ClockPointerCorruption = 3009;
            public const int DuplicateCacheRegistration = 3010;
            public const int NullOnEvicted = 3011;
        }

        // --------------------------
        // RANGE DEFINITION
        // --------------------------
        /// <summary>
        /// Reserved ExceptionId range for Silica.Evictions.
        /// </summary>
        public const int MinId = 3000;
        public const int MaxId = 3999;

        /// <summary>
        /// Validates that the given ExceptionId is within the Silica.Evictions reserved range.
        /// </summary>
        public static void ValidateId(int id, string paramName)
        {
            if (id < MinId || id > MaxId)
            {
                throw new ArgumentOutOfRangeException(paramName,
                    $"ExceptionId must be between {MinId} and {MaxId} for Silica.Evictions.");
            }
        }

        // --------------------------
        // DEFINITIONS
        // --------------------------

        /// <summary>
        /// Cache key cannot be null.
        /// </summary>
        public static readonly ExceptionDefinition NullKey =
            ExceptionDefinition.Create(
                code: "EVICTIONS.NULL_KEY",
                exceptionTypeName: typeof(EvictionNullKeyException).FullName!,
                category: FailureCategory.Validation,
                description: "The cache key cannot be null."
            );

        /// <summary>
        /// Value factory delegate must be provided.
        /// </summary>
        public static readonly ExceptionDefinition NullValueFactory =
            ExceptionDefinition.Create(
                code: "EVICTIONS.NULL_VALUE_FACTORY",
                exceptionTypeName: typeof(EvictionNullValueFactoryException).FullName!,
                category: FailureCategory.Validation,
                description: "The value factory delegate cannot be null."
            );

        /// <summary>
        /// Eviction callback delegate must be provided.
        /// </summary>
        public static readonly ExceptionDefinition NullOnEvicted =
            ExceptionDefinition.Create(
                code: "EVICTIONS.NULL_ON_EVICTED",
                exceptionTypeName: typeof(EvictionNullOnEvictedException).FullName!,
                category: FailureCategory.Validation,
                description: "The eviction callback delegate cannot be null."
            );

        /// <summary>
        /// Null cache instance passed to the eviction manager.
        /// </summary>
        public static readonly ExceptionDefinition NullCacheRegistration =
            ExceptionDefinition.Create(
                code: "EVICTIONS.NULL_CACHE_REGISTRATION",
                exceptionTypeName: typeof(EvictionNullCacheRegistrationException).FullName!,
                category: FailureCategory.Validation,
                description: "The cache instance passed to the eviction manager cannot be null."
            );

        /// <summary>
        /// Cache capacity must be greater than zero.
        /// </summary>
        public static readonly ExceptionDefinition InvalidCapacity =
            ExceptionDefinition.Create(
                code: "EVICTIONS.INVALID_CAPACITY",
                exceptionTypeName: typeof(EvictionInvalidCapacityException).FullName!,
                category: FailureCategory.Validation,
                description: "The cache capacity must be greater than zero."
            );

        /// <summary>
        /// Idle timeout must be non-negative.
        /// </summary>
        public static readonly ExceptionDefinition InvalidIdleTimeout =
            ExceptionDefinition.Create(
                code: "EVICTIONS.INVALID_IDLE_TIMEOUT",
                exceptionTypeName: typeof(EvictionInvalidIdleTimeoutException).FullName!,
                category: FailureCategory.Validation,
                description: "The idle timeout must be non-negative."
            );

        /// <summary>
        /// Operation attempted on a disposed cache.
        /// </summary>
        public static readonly ExceptionDefinition CacheDisposed =
            ExceptionDefinition.Create(
                code: "EVICTIONS.CACHE_DISPOSED",
                exceptionTypeName: typeof(EvictionDisposedException).FullName!,
                category: FailureCategory.Internal,
                description: "Operation attempted on a disposed evictions cache."
            );

        /// <summary>
        /// Operation attempted on a disposed eviction manager.
        /// </summary>
        public static readonly ExceptionDefinition ManagerDisposed =
            ExceptionDefinition.Create(
                code: "EVICTIONS.MANAGER_DISPOSED",
                exceptionTypeName: typeof(EvictionManagerDisposedException).FullName!,
                category: FailureCategory.Internal,
                description: "Operation attempted on a disposed eviction manager."
            );

        /// <summary>
        /// LFU cache internal state is inconsistent.
        /// </summary>
        public static readonly ExceptionDefinition LfuInvariantViolation =
            ExceptionDefinition.Create(
                code: "EVICTIONS.LFU_INVARIANT_VIOLATION",
                exceptionTypeName: typeof(EvictionLfuInvariantViolationException).FullName!,
                category: FailureCategory.Internal,
                description: "LFU cache internal state is inconsistent."
            );

        /// <summary>
        /// CLOCK cache pointer/index state is invalid.
        /// </summary>
        public static readonly ExceptionDefinition ClockPointerCorruption =
            ExceptionDefinition.Create(
                code: "EVICTIONS.CLOCK_POINTER_CORRUPTION",
                exceptionTypeName: typeof(EvictionClockPointerCorruptionException).FullName!,
                category: FailureCategory.Internal,
                description: "CLOCK cache pointer state is invalid."
            );

        /// <summary>
        /// The cache is already registered with the eviction manager.
        /// </summary>
        public static readonly ExceptionDefinition DuplicateCacheRegistration =
            ExceptionDefinition.Create(
                code: "EVICTIONS.DUPLICATE_CACHE_REGISTRATION",
                exceptionTypeName: typeof(EvictionDuplicateCacheRegistrationException).FullName!,
                category: FailureCategory.Internal,
                description: "The cache is already registered with the eviction manager."
            );

        // --------------------------
        // REGISTRATION
        // --------------------------
        /// <summary>
        /// Registers all Evictions exception definitions with the global ExceptionRegistry.
        /// Call once at subsystem startup.
        /// </summary>
        public static void RegisterAll()
        {
            ExceptionRegistry.RegisterAll(new[]
            {
                NullKey,
                NullValueFactory,
                NullOnEvicted,
                NullCacheRegistration,
                InvalidCapacity,
                InvalidIdleTimeout,
                CacheDisposed,
                ManagerDisposed,
                LfuInvariantViolation,
                ClockPointerCorruption,
                DuplicateCacheRegistration
            });
        }
    }
}
