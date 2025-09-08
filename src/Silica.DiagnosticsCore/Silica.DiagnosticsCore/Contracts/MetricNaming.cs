using System;
using System.Text.RegularExpressions;

namespace Silica.DiagnosticsCore
{
    /// <summary>
    /// Provides central validation rules for metric naming and tagging conventions.
    /// </summary>
    public static class MetricNaming
    {
        private static readonly Regex SnakeCasePattern = new(@"^[a-z0-9_]+$", RegexOptions.Compiled);

        /// <summary>
        /// Validates that the metric descriptor follows naming and tagging conventions.
        /// Throws <see cref="ArgumentException"/> if invalid.
        /// </summary>
        public static void Validate(MetricDescriptor descriptor)
        {
            if (string.IsNullOrWhiteSpace(descriptor.Name))
                throw new ArgumentException("Metric name cannot be empty.", nameof(descriptor));

            if (!SnakeCasePattern.IsMatch(descriptor.Name))
                throw new ArgumentException($"Metric name '{descriptor.Name}' is not in snake_case.", nameof(descriptor));

            // Optional: validate tags, units, etc.
        }
    }
}
