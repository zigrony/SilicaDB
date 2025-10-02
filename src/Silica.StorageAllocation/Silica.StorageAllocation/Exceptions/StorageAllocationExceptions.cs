using System;
using Silica.Exceptions;

namespace Silica.Storage.Allocation.Exceptions
{
    /// <summary>
    /// Canonical, low-cardinality StorageAllocation exception definitions for Silica.Exceptions.
    /// Contract-first: stable codes, categories, descriptions, and ExceptionIds.
    /// </summary>
    public static class StorageAllocationExceptions
    {
        // --------------------------
        // STABLE EXCEPTION IDS
        // --------------------------
        public static class Ids
        {
            public const int StrategyNotFound = 3001;
            public const int AllocationFailed = 3002;
            public const int ExtentExhausted = 3003;
            public const int PageFreeInvalid = 3004;
        }

        // --------------------------
        // RANGE DEFINITION
        // --------------------------
        /// <summary>
        /// Reserved ExceptionId range for Silica.Storage.Allocation.
        /// </summary>
        public const int MinId = 3000;
        public const int MaxId = 3999;

        /// <summary>
        /// Validates that the given ExceptionId is within the Silica.Storage.Allocation reserved range.
        /// </summary>
        public static void ValidateId(int id, string paramName)
        {
            if (id < MinId || id > MaxId)
            {
                throw new ArgumentOutOfRangeException(paramName,
                    $"ExceptionId must be between {MinId} and {MaxId} for Silica.Storage.Allocation.");
            }
        }

        // --------------------------
        // DEFINITIONS
        // --------------------------

        public static readonly ExceptionDefinition StrategyNotFound =
            ExceptionDefinition.Create(
                code: "ALLOC.STRATEGY_NOT_FOUND",
                exceptionTypeName: typeof(AllocationStrategyNotFoundException).FullName!,
                category: FailureCategory.Configuration,
                description: "The requested allocation strategy was not registered."
            );

        public static readonly ExceptionDefinition AllocationFailed =
            ExceptionDefinition.Create(
                code: "ALLOC.ALLOCATION_FAILED",
                exceptionTypeName: typeof(AllocationFailedException).FullName!,
                category: FailureCategory.Internal,
                description: "The allocation request could not be satisfied."
            );

        public static readonly ExceptionDefinition ExtentExhausted =
            ExceptionDefinition.Create(
                code: "ALLOC.EXTENT_EXHAUSTED",
                exceptionTypeName: typeof(ExtentExhaustedException).FullName!,
                category: FailureCategory.Internal,
                description: "The extent has no free pages remaining."
            );


        public static readonly ExceptionDefinition PageFreeInvalid =
            ExceptionDefinition.Create(
                code: "ALLOC.PAGE_FREE_INVALID",
                exceptionTypeName: typeof(PageFreeException).FullName!,
                category: FailureCategory.Validation,
                description: "The page is already free or cannot be freed."
            );

        // --------------------------
        // REGISTRATION
        // --------------------------

        /// <summary>
        /// Registers all StorageAllocation exception definitions with the global ExceptionRegistry.
        /// Call once at subsystem startup.
        /// </summary>
        public static void RegisterAll()
        {
            ExceptionRegistry.RegisterAll(new[]
            {
                StrategyNotFound,
                AllocationFailed,
                ExtentExhausted,
                PageFreeInvalid
            });
        }
    }
}
