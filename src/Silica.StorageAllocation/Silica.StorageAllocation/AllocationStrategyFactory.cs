// Path: Silica.Storage.Allocation/AllocationStrategyFactory.cs
using System;
using System.Collections.Generic;

namespace Silica.Storage.Allocation
{
    /// <summary>
    /// Registry/factory for pluggable allocation strategies. 
    /// Supports 0..N registrations and late-binding selection by name.
    /// </summary>
    public sealed class AllocationStrategyFactory
    {
        private readonly Dictionary<string, IAllocationStrategy> _strategies =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Register a strategy. Last writer wins for duplicate names.
        /// </summary>
        public void Register(IAllocationStrategy strategy)
        {
            if (strategy is null) throw new ArgumentNullException(nameof(strategy));
            var name = strategy.Name?.Trim();
            if (string.IsNullOrEmpty(name))
                throw new ArgumentOutOfRangeException(nameof(strategy), "Strategy.Name must be non-empty.");
            _strategies[name] = strategy;
        }

        /// <summary>
        /// Resolve a strategy by name. Throws if not found.
        /// </summary>
        public IAllocationStrategy Get(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentOutOfRangeException(nameof(name));
            if (!_strategies.TryGetValue(name, out var s))
                throw new InvalidOperationException($"No allocation strategy registered with name '{name}'.");
            return s;
        }

        /// <summary>
        /// Enumerate registered strategy names (for diagnostics or config validation).
        /// </summary>
        public IReadOnlyCollection<string> ListStrategyNames()
            => _strategies.Keys as IReadOnlyCollection<string> ?? new List<string>(_strategies.Keys);
    }
}
