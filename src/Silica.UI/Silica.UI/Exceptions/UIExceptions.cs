using System;
using Silica.Exceptions;

namespace Silica.UI.Exceptions
{
    /// <summary>
    /// Canonical, low-cardinality UI exception definitions for Silica.Exceptions.
    /// Contract-first: stable codes, categories, descriptions, and ExceptionIds (validated in concrete types).
    /// </summary>
    public static class UIExceptions
    {
        // --------------------------
        // STABLE EXCEPTION IDS
        // --------------------------
        public static class Ids
        {
            // Reserve a dedicated range for UI: 2000–2999
            public const int ComponentRenderFailure = 2001;
            public const int InvalidThemeVariable = 2002;
            public const int LayoutOverflow = 2003;
            public const int SlotResolutionError = 2004;
            public const int UiDisposed = 2005;
        }

        // --------------------------
        // RANGE DEFINITION
        // --------------------------
        /// <summary>
        /// Reserved ExceptionId range for Silica.UI.
        /// </summary>
        public const int MinId = 2000;
        public const int MaxId = 2999;

        /// <summary>
        /// Validates that the given ExceptionId is within the Silica.UI reserved range.
        /// </summary>
        public static void ValidateId(int id, string paramName)
        {
            if (id < MinId || id > MaxId)
            {
                throw new ArgumentOutOfRangeException(paramName,
                    $"ExceptionId must be between {MinId} and {MaxId} for Silica.UI.");
            }
        }

        // --------------------------
        // DEFINITIONS
        // --------------------------

        public static readonly ExceptionDefinition ComponentRenderFailure =
            ExceptionDefinition.Create(
                code: "UI.COMPONENT.RENDER_FAILURE",
                exceptionTypeName: typeof(ComponentRenderFailureException).FullName!,
                category: FailureCategory.Internal,
                description: "A UI component failed during render lifecycle."
            );

        public static readonly ExceptionDefinition InvalidThemeVariable =
            ExceptionDefinition.Create(
                code: "UI.THEME.INVALID_VARIABLE",
                exceptionTypeName: typeof(InvalidThemeVariableException).FullName!,
                category: FailureCategory.Validation,
                description: "A CSS variable or theme token was invalid or missing."
            );

        public static readonly ExceptionDefinition LayoutOverflow =
            ExceptionDefinition.Create(
                code: "UI.LAYOUT.OVERFLOW",
                exceptionTypeName: typeof(LayoutOverflowException).FullName!,
                category: FailureCategory.Validation,
                description: "A layout container exceeded its bounds or caused overflow."
            );

        public static readonly ExceptionDefinition SlotResolutionError =
            ExceptionDefinition.Create(
                code: "UI.SLOT.RESOLUTION_ERROR",
                exceptionTypeName: typeof(SlotResolutionException).FullName!,
                category: FailureCategory.Internal,
                description: "A slot or template region could not be resolved."
            );

        public static readonly ExceptionDefinition UiDisposed =
            ExceptionDefinition.Create(
                code: "UI.DISPOSED",
                exceptionTypeName: typeof(UiDisposedException).FullName!,
                category: FailureCategory.Internal,
                description: "An operation was attempted on a disposed UI subsystem."
            );

        // --------------------------
        // REGISTRATION
        // --------------------------
        public static void RegisterAll()
        {
            ExceptionRegistry.RegisterAll(new[]
            {
                ComponentRenderFailure,
                InvalidThemeVariable,
                LayoutOverflow,
                SlotResolutionError,
                UiDisposed
            });
        }
    }
}
