// Filename: Silica.Exceptions/Silica.Exceptions/ExceptionRegistry.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.CompilerServices;

namespace Silica.Exceptions
{
    /// <summary>
    /// Thread-safe registry of exception definitions (code -> metadata).
    /// Subsystems call RegisterAll() once at startup to register their catalog.
    /// </summary>
    public static class ExceptionRegistry
    {
        private static readonly ConcurrentDictionary<string, ExceptionDefinition> _byCode =
            // All codes are canonicalized to uppercase via ExceptionPolicy.CanonicalizeCode before mutation.
            // Dictionary uses Ordinal since keys are already canonicalized (effective case-insensitivity via canonicalization).
            new(StringComparer.Ordinal);
        // Secondary index to enforce and query one-to-one binding by CLR type name.
        // Accessed only under _gate to keep state coherent with _byCode.
        private static readonly Dictionary<string, Binding> _byTypeName =
            new Dictionary<string, Binding>(StringComparer.Ordinal);
        private static readonly object _gate = new();
        private static volatile bool _frozen;
        private static volatile bool _enforceRegisteredCodes;
        // Monotonic registry version for cache invalidation.
        // Incremented on effective schema mutation (adds/description updates) and registry freeze transitions.
        private static long _version;

        // Validate that a given ExceptionTypeName is not already bound to a different code/category in the registry.
        // O(1) via secondary index; must be called under _gate.
        private static void ValidateTypeBindingAgainstRegistry(ExceptionDefinition def)
        {
            if (_byTypeName.Count == 0) return;
            if (_byTypeName.TryGetValue(def.ExceptionTypeName, out var binding))
            {
                if (!string.Equals(binding.Code, def.Code, StringComparison.Ordinal) || binding.Category != def.Category)
                {
                    throw new ArgumentException(
                        "Exception CLR type already bound with a different schema. " +
                        "Type=" + def.ExceptionTypeName +
                        ", ExistingCode=" + binding.Code +
                        ", ExistingCategory=" + binding.Category.ToString() +
                        ", NewCode=" + def.Code +
                        ", NewCategory=" + def.Category.ToString() + ".");
                }
            }
        }

        private static ExceptionDefinition Canonicalize(ExceptionDefinition def)
        {
            // Caller has validated def. Only normalize the code casing.
            var canonical = ExceptionPolicy.CanonicalizeCode(def.Code);
            if (string.Equals(canonical, def.Code, StringComparison.Ordinal)) return def;
            return ExceptionDefinition.CreateTrusted(canonical, def.ExceptionTypeName, def.Category, def.Description);
        }

        internal static void Register(ExceptionDefinition def)
        {
            if (def is null) throw new ArgumentNullException(nameof(def));
            def.EnsureValid();
            def = Canonicalize(def);
            lock (_gate)
            {
                if (_frozen) throw new InvalidOperationException("ExceptionRegistry is frozen and cannot be modified.");

                // Enforce one-to-one binding between ExceptionTypeName and (Code, Category) against current registry.
                ValidateTypeBindingAgainstRegistry(def);

                // Use TryAdd/TryGetValue to avoid updater allocation and keep behavior explicit.
                if (_byCode.TryGetValue(def.Code, out var existing))
                {
                    if (!SchemaEquals(existing, def))
                        throw new ArgumentException($"Exception code '{def.Code}' already registered with different schema.");
                    // Allow description update pre-freeze without schema drift.
                    if (!string.Equals(existing.Description, def.Description, StringComparison.Ordinal))
                    {
                        // Replace with updated description.
                        _byCode[def.Code] = ExceptionDefinition.CreateTrusted(existing.Code, existing.ExceptionTypeName, existing.Category, def.Description);
                        Interlocked.Increment(ref _version);
                    }
                    // Ensure type binding index reflects current schema mapping.
                    EnsureTypeBindingIndex(existing.ExceptionTypeName, existing.Code, existing.Category);
                    return;
                }

                if (!_byCode.TryAdd(def.Code, def))
                {
                    // Raced with another writer before we took the lock (extremely unlikely here),
                    // re-check and enforce schema consistency.
                    if (_byCode.TryGetValue(def.Code, out existing))
                    {
                        if (!SchemaEquals(existing, def))
                            throw new ArgumentException($"Exception code '{def.Code}' already registered with different schema.");
                        // If schema matches and only description differs, update pre-freeze.
                        if (!string.Equals(existing.Description, def.Description, StringComparison.Ordinal))
                        {
                            _byCode[def.Code] = ExceptionDefinition.CreateTrusted(existing.Code, existing.ExceptionTypeName, existing.Category, def.Description);
                            Interlocked.Increment(ref _version);
                        }
                        EnsureTypeBindingIndex(existing.ExceptionTypeName, existing.Code, existing.Category);
                    }
                    else
                    {
                        // Defensive: if TryAdd failed but no existing found, treat as no-op (no version bump).
                        return;
                    }
                }
                else
                {
                    // Maintain secondary index for new add.
                    EnsureTypeBindingIndex(def.ExceptionTypeName, def.Code, def.Category);
                    // Effective add: bump registry version.
                    Interlocked.Increment(ref _version);
                }
            }
        }

        // Prefer explicit creation of ExceptionDefinition to avoid runtime typeof<TException>.
        // If you still want a helper, pass the exception CLR type name explicitly.
        public static void Register(string code, string exceptionTypeName, FailureCategory category, string description)
        {
            var def = ExceptionDefinition.Create(code, exceptionTypeName, category, description);
            Register(def);
        }

        /// <summary>
        /// Bulk registration helper. Stops at first failure.
        /// </summary>
        public static void RegisterAll(ExceptionDefinition[] definitions)
        {
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));
            // Two-phase: (1) validate and check conflicts against current and within batch; (2) apply.
            // Keeps registry unchanged on failure.
            lock (_gate)
            {
                if (_frozen) throw new InvalidOperationException("ExceptionRegistry is frozen and cannot be modified.");

                // Phase 1: build a canonicalized local copy and check internal duplicates and external schema conflicts.
                // Keys are canonicalized to uppercase; use Ordinal to match canonical storage semantics.
                var local = new Dictionary<string, ExceptionDefinition>(
                    capacity: definitions.Length,
                    comparer: StringComparer.Ordinal);
                // Track per-type binding in O(1) within this batch (ExceptionTypeName -> first observed (Code, Category)).
                var localTypeFirst = new Dictionary<string, ExceptionDefinition>(
                    capacity: definitions.Length,
                    comparer: StringComparer.Ordinal);
                int i = 0;
                while (i < definitions.Length)
                {
                    var raw = definitions[i];
                    if (raw is null) throw new ArgumentException("Definitions array contains null entry.", nameof(definitions));
                    raw.EnsureValid();
                    var def = Canonicalize(raw);

                    // Enforce per-batch type binding consistency (ExceptionTypeName -> (Code, Category)).
                    if (localTypeFirst.TryGetValue(def.ExceptionTypeName, out var priorType))
                    {
                        if (!string.Equals(priorType.Code, def.Code, StringComparison.Ordinal) || priorType.Category != def.Category)
                        {
                            throw new ArgumentException(
                                "Exception CLR type bound inconsistently within batch. " +
                                "Type=" + def.ExceptionTypeName +
                                ", PriorCode=" + priorType.Code +
                                ", PriorCategory=" + priorType.Category.ToString() +
                                ", NewCode=" + def.Code +
                                ", NewCategory=" + def.Category.ToString() + ".");
                        }
                    }
                    else
                    {
                        localTypeFirst.Add(def.ExceptionTypeName, def);
                    }

                    // Check duplicates within the batch.
                    if (local.TryGetValue(def.Code, out var priorInBatch))
                    {
                        if (!SchemaEquals(priorInBatch, def))
                            throw new ArgumentException($"Exception code '{def.Code}' appears multiple times in batch with different schema.");
                        // If schema matches and only description differs, prefer the last occurrence's description deterministically.
                        if (!string.Equals(priorInBatch.Description, def.Description, StringComparison.Ordinal))
                        {
                            local[def.Code] = ExceptionDefinition.CreateTrusted(def.Code, def.ExceptionTypeName, def.Category, def.Description);
                        }
                    }
                    else
                    {
                        local.Add(def.Code, def);
                    }

                    // Check existing registry compatibility.
                    if (_byCode.TryGetValue(def.Code, out var existing))
                    {
                        if (!SchemaEquals(existing, def))
                            throw new ArgumentException($"Exception code '{def.Code}' already registered with different schema.");
                    }
                    // Check type binding against registry state.
                    ValidateTypeBindingAgainstRegistry(def);
                    i++;
                }

                // Phase 2: apply additions for codes not already present.
                // Use TryAdd to cooperate with any concurrent adds outside the gate (defensive).
                // Because we're under _gate, our own mutations are serialized vs. Freeze and other RegisterAll calls.
                bool anyAdded = false;
                bool anyUpdatedDesc = false;
                foreach (var kvp in local)
                {
                    // Defensive: enforce type binding again right before mutate (registry could have changed due to concurrency).
                    ValidateTypeBindingAgainstRegistry(kvp.Value);

                    if (_byCode.TryGetValue(kvp.Key, out var existing))
                    {
                        // Schema match guaranteed by phase 1. Update description if it differs.
                        if (!string.Equals(existing.Description, kvp.Value.Description, StringComparison.Ordinal))
                        {
                            _byCode[kvp.Key] = ExceptionDefinition.CreateTrusted(existing.Code, existing.ExceptionTypeName, existing.Category, kvp.Value.Description);
                            anyUpdatedDesc = true;
                        }
                        // Keep type binding index aligned (no change in code/category, but ensure presence).
                        EnsureTypeBindingIndex(existing.ExceptionTypeName, existing.Code, existing.Category);
                        continue;
                    }
                    if (!_byCode.TryAdd(kvp.Key, kvp.Value))
                    {
                        // If a concurrent add happened, ensure schema identical.
                        if (_byCode.TryGetValue(kvp.Key, out var concurrentExisting))
                        {
                            if (!SchemaEquals(concurrentExisting, kvp.Value))
                                throw new ArgumentException($"Exception code '{kvp.Key}' already registered concurrently with different schema.");
                            // If schema matches and only description differs, update.
                            if (!string.Equals(concurrentExisting.Description, kvp.Value.Description, StringComparison.Ordinal))
                            {
                                _byCode[kvp.Key] = ExceptionDefinition.CreateTrusted(concurrentExisting.Code, concurrentExisting.ExceptionTypeName, concurrentExisting.Category, kvp.Value.Description);
                                anyUpdatedDesc = true;
                            }
                            EnsureTypeBindingIndex(concurrentExisting.ExceptionTypeName, concurrentExisting.Code, concurrentExisting.Category);
                        }
                        // No version bump if concurrently added with identical schema.
                    }
                    else
                    {
                        EnsureTypeBindingIndex(kvp.Value.ExceptionTypeName, kvp.Value.Code, kvp.Value.Category);
                        anyAdded = true;
                    }
                }
                if (anyAdded || anyUpdatedDesc)
                {
                    Interlocked.Increment(ref _version);
                }
            }
        }

        public static bool TryGetByCode(string code, out ExceptionDefinition? def)
        {
            if (code == null) { def = null; return false; }

            // Non-throwing validation to preserve API contract and avoid violating CanonicalizeCode’s assumption.
            if (!ExceptionPolicy.IsValidCodeNonThrowing(code))
            {
                def = null;
                return false;
            }

            // Canonicalize to reduce upstream drift; dictionary stores canonical uppercase keys.
            var canonical = ExceptionPolicy.CanonicalizeCode(code);
            return _byCode.TryGetValue(canonical, out def);
        }

        // By-type resolution removed to avoid reflection in runtime paths.

        public static IReadOnlyDictionary<string, ExceptionDefinition> AllByCodeSnapshot()
        {
            // Provide a stable snapshot with canonicalized uppercase keys.
            // Use Ordinal to reflect actual storage semantics (keys are already canonicalized).
            var copy = new Dictionary<string, ExceptionDefinition>(_byCode.Count, StringComparer.Ordinal);
            foreach (var kvp in _byCode)
            {
                copy[kvp.Key] = kvp.Value;
            }
            return new System.Collections.ObjectModel.ReadOnlyDictionary<string, ExceptionDefinition>(copy);
        }

        /// <summary>
        /// Snapshot plus version captured in one call to simplify cache invalidation for callers.
        /// </summary>
        public static IReadOnlyDictionary<string, ExceptionDefinition> AllByCodeSnapshot(out long version)
        {
            // Capture version before copying; version increments only on effective adds and freeze transitions.
            // A concurrent add could occur between reading version and copying dictionary, so we capture again after
            // and, if changed, we still return the snapshot and the later version to prompt callers to retry if they need exact alignment.
            var v1 = System.Threading.Volatile.Read(ref _version);
            var copy = new Dictionary<string, ExceptionDefinition>(_byCode.Count, StringComparer.Ordinal);
            foreach (var kvp in _byCode)
            {
                copy[kvp.Key] = kvp.Value;
            }
            var v2 = System.Threading.Volatile.Read(ref _version);
            version = (v2 >= v1) ? v2 : v1;
            return new System.Collections.ObjectModel.ReadOnlyDictionary<string, ExceptionDefinition>(copy);
        }

        public static void Freeze()
        {
            lock (_gate)
            {
                if (_frozen) return;
                _frozen = true;
                Interlocked.Increment(ref _version);
            }
        }

        public static bool IsFrozen
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _frozen;
        }

        /// <summary>
        /// Idempotent attempt to freeze. Returns true if state changed from unfrozen to frozen.
        /// </summary>
        public static bool TryFreeze()
        {
            lock (_gate)
            {
                if (_frozen) return false;
                _frozen = true;
                Interlocked.Increment(ref _version);
                return true;
            }
        }

        public static void FreezeObservers()
        {
            // Avoid holding the registry gate while taking the notifier gate to prevent cross-lock coupling.
            if (ExceptionNotifier.AreFrozen) return;
            ExceptionNotifier.FreezeObservers();
        }

        /// <summary>
        /// Idempotent attempt to freeze observers. Returns true if state changed.
        /// </summary>
        public static bool TryFreezeObservers()
        {
            if (ExceptionNotifier.AreFrozen) return false;
            return ExceptionNotifier.TryFreezeObservers();
        }

        public static bool AreObserversFrozen
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ExceptionNotifier.AreFrozen;
        }

        public static void FreezeAll()
        {
            bool needFreezeObservers = false;
            lock (_gate)
            {
                bool bumpedRegistry = false;

                if (!_frozen)
                {
                    _frozen = true;
                    bumpedRegistry = true;
                }
                // Defer observer freeze until after releasing the registry gate.
                if (!ExceptionNotifier.AreFrozen) { needFreezeObservers = true; }

                if (bumpedRegistry)
                {
                    Interlocked.Increment(ref _version);
                }
            }
            if (needFreezeObservers) { ExceptionNotifier.FreezeObservers(); }
        }

        /// <summary>
        /// Atomically enable EnforceRegisteredCodes and freeze registry and observers.
        /// If the registry is already frozen, throws to avoid partial enforcement drift.
        /// </summary>
        public static void FreezeAndSeal()
        {
            lock (_gate)
            {
                if (_frozen)
                    throw new InvalidOperationException("Registry already frozen; cannot enable EnforceRegisteredCodes via FreezeAndSeal.");
                // Enable enforcement before freezing, then freeze registry state.
                _enforceRegisteredCodes = true;
                _frozen = true;
                Interlocked.Increment(ref _version);
            }
            // Freeze observers after releasing registry gate to avoid nested lock holds longer than needed.
            ExceptionNotifier.FreezeObservers();
        }

        /// <summary>
        /// Try to enable EnforceRegisteredCodes and freeze registry and observers.
        /// Returns true if transitioned; false if already frozen (state unchanged).
        /// </summary>
        public static bool TryFreezeAndSeal()
        {
            lock (_gate)
            {
                if (_frozen) return false;
                _enforceRegisteredCodes = true;
                _frozen = true;
                Interlocked.Increment(ref _version);
            }
            ExceptionNotifier.FreezeObservers();
            return true;
        }

        /// <summary>
        /// Add an exception observer if not already present. No-op when observers are frozen.
        /// </summary>
        public static void AddObserver(IExceptionObserver observer)
        {
            // Delegate to internal notifier; it handles freeze state and duplicate prevention.
            ExceptionNotifier.AddObserver(observer);
        }

        /// <summary>
        /// Try to add an exception observer. Returns false if already present or observers are frozen.
        /// </summary>
        public static bool TryAddObserver(IExceptionObserver observer)
        {
            return ExceptionNotifier.TryAddObserver(observer);
        }

        /// <summary>
        /// Remove an exception observer if present. No-op when observers are frozen.
        /// </summary>
        public static void RemoveObserver(IExceptionObserver observer)
        {
            ExceptionNotifier.RemoveObserver(observer);
        }

        /// <summary>
        /// Try to remove an exception observer. Returns false if not present or observers are frozen.
        /// </summary>
        public static bool TryRemoveObserver(IExceptionObserver observer)
        {
            return ExceptionNotifier.TryRemoveObserver(observer);
        }

        /// <summary>
        /// When enabled, SilicaException constructors will enforce that codes are registered.
        /// Default: false.
        /// </summary>
        public static bool EnforceRegisteredCodes
        {
            get => System.Threading.Volatile.Read(ref _enforceRegisteredCodes);
            set
            {
                lock (_gate)
                {
                    if (_frozen)
                        throw new InvalidOperationException("Cannot change EnforceRegisteredCodes after registry is frozen.");
                    _enforceRegisteredCodes = value;
                    // This is a behavior toggle, not a schema change; do not bump version.
                }
            }
        }
        /// <summary>
        /// Attempt to set EnforceRegisteredCodes without throwing when frozen. Returns true if applied.
        /// </summary>
        public static bool TrySetEnforceRegisteredCodes(bool enabled)
        {
            lock (_gate)
            {
                if (_frozen) return false;
                _enforceRegisteredCodes = enabled;
                return true;
            }
        }


        /// <summary>
        /// Fast check for presence. Case-insensitive.
        /// </summary>
        public static bool IsKnownCode(string code)
        {
            if (code == null) return false;
            // Use non-throwing validator to avoid violating CanonicalizeCode’s contract.
            if (!ExceptionPolicy.IsValidCodeNonThrowing(code)) return false;
            // Safe canonicalization now that the input is known valid.
            var canonical = ExceptionPolicy.CanonicalizeCode(code);
            return _byCode.TryGetValue(canonical, out _);
        }

        /// <summary>
        /// Count snapshot for diagnostics/health endpoints.
        /// </summary>
        public static int Count => _byCode.Count;

        /// <summary>
        /// Snapshot of registered codes (canonicalized). Order not guaranteed.
        /// </summary>
        public static string[] CodesSnapshot()
        {
            // ConcurrentDictionary enumeration is safe and reflects a moment-in-time view.
            // Copy to a compact array for cheap diagnostics/health.
            var count = _byCode.Count;
            var result = new string[count];
            int i = 0;
            foreach (var kvp in _byCode)
            {
                // Codes are stored canonicalized by Register paths.
                if (i < result.Length) { result[i] = kvp.Key; i++; }
                else
                {
                    // In rare concurrent growth, expand once.
                    var expanded = new string[result.Length + 8];
                    Array.Copy(result, expanded, result.Length);
                    expanded[i] = kvp.Key;
                    result = expanded;
                    i++;
                }
            }
            if (i == result.Length) return result;
            var trimmed = new string[i];
            Array.Copy(result, trimmed, i);
            return trimmed;
        }

        /// <summary>
        /// Snapshot of registered codes (canonicalized), sorted Ordinal for deterministic docs and diffing.
        /// </summary>
        public static string[] CodesSnapshotSorted()
        {
            // Copy into a compact array then sort. No LINQ; stable ordinal semantics.
            var count = _byCode.Count;
            var result = new string[count];
            int i = 0;
            foreach (var kvp in _byCode)
            {
                if (i < result.Length) { result[i] = kvp.Key; i++; }
                else
                {
                    var expanded = new string[result.Length + 8];
                    Array.Copy(result, expanded, result.Length);
                    expanded[i] = kvp.Key;
                    result = expanded;
                    i++;
                }
            }
            if (i != result.Length)
            {
                var trimmed = new string[i];
                Array.Copy(result, trimmed, i);
                result = trimmed;
            }
            Array.Sort(result, StringComparer.Ordinal);
            return result;
        }

        /// <summary>
        /// Try to get a definition by code and the registry version observed for this lookup.
        /// - Non-throwing on invalid code (mirrors TryGetByCode).
        /// - Returns false if code invalid or not present. When true, both def and version are set.
        /// </summary>
        public static bool TryGetByCodeWithVersion(string code, out ExceptionDefinition? def, out long version)
        {
            def = null;
            var v1 = System.Threading.Volatile.Read(ref _version);
            if (code == null)
            {
                version = v1;
                return false;
            }
            if (!ExceptionPolicy.IsValidCodeNonThrowing(code))
            {
                version = v1;
                return false;
            }
            var canonical = ExceptionPolicy.CanonicalizeCode(code);
            // ConcurrentDictionary.TryGetValue is safe without additional locking.
            var foundIt = _byCode.TryGetValue(canonical, out var found);
            if (foundIt) { def = found; }
            var v2 = System.Threading.Volatile.Read(ref _version);
            version = (v2 >= v1) ? v2 : v1;
            return foundIt;
        }

        /// <summary>
        /// Snapshot of codes only and the version in a single call, for ultra-fast presence checks.
        /// Useful for cache warmups or health endpoints.
        /// </summary>
        public static string[] CodesSnapshot(out long version)
        {
            var v1 = System.Threading.Volatile.Read(ref _version);
            var count = _byCode.Count;
            var result = new string[count];
            int i = 0;
            foreach (var kvp in _byCode)
            {
                if (i < result.Length)
                {
                    result[i] = kvp.Key;
                    i++;
                }
                else
                {
                    var expanded = new string[result.Length + 8];
                    Array.Copy(result, expanded, result.Length);
                    expanded[i] = kvp.Key;
                    result = expanded;
                    i++;
                }
            }
            var v2 = System.Threading.Volatile.Read(ref _version);
            version = (v2 >= v1) ? v2 : v1;
            if (i == result.Length) return result;
            var trimmed = new string[i];
            Array.Copy(result, trimmed, i);
            return trimmed;
        }


        // Internal for tests / controlled re-init in hosts that rebuild containers.
        internal static void ClearForTests()
        {
            lock (_gate)
            {
                _byCode.Clear();
                _byTypeName.Clear();
                _frozen = false;
                _enforceRegisteredCodes = false;
                // Ensure observer state is also reset for full test isolation.
                ExceptionNotifier.ClearForTests();
                _version = 0;

            }
        }

        // Internal fast-path: assumes canonicalized, validated code.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryGetByCanonicalCode(string canonicalCode, out ExceptionDefinition? def)
        {
            return _byCode.TryGetValue(canonicalCode, out def);
        }

        /// <summary>
        /// Try to get a definition by CLR exception type name (as registered), exact Ordinal match.
        /// </summary>
        public static bool TryGetByTypeName(string exceptionTypeName, out ExceptionDefinition? def)
        {
            def = null;
            if (exceptionTypeName == null) return false;
            // Preserve Try* non-throwing semantics and avoid lock if obviously invalid.
            if (!ExceptionPolicy.IsValidTypeNameNonThrowing(exceptionTypeName)) return false;
            lock (_gate)
            {
                if (_byTypeName.TryGetValue(exceptionTypeName, out var binding))
                {
                    // _byCode contains the authoritative definitions.
                    if (_byCode.TryGetValue(binding.Code, out var found))
                    {
                        def = found;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Snapshot of registered definitions keyed by CLR exception type name.
        /// Order not guaranteed. Uses Ordinal semantics.
        /// </summary>
        public static IReadOnlyDictionary<string, ExceptionDefinition> AllByTypeSnapshot()
        {
            lock (_gate)
            {
                // Build from _byTypeName to ensure one-to-one mapping is reflected in keys.
                var copy = new Dictionary<string, ExceptionDefinition>(_byTypeName.Count, StringComparer.Ordinal);
                foreach (var kvp in _byTypeName)
                {
                    // Defensive: map through _byCode to return the current definition object.
                    if (_byCode.TryGetValue(kvp.Value.Code, out var def))
                    {
                        copy[kvp.Key] = def;
                    }
                }
                return new System.Collections.ObjectModel.ReadOnlyDictionary<string, ExceptionDefinition>(copy);
            }
        }

        /// <summary>
        /// Snapshot of registered definitions keyed by CLR exception type name, sorted Ordinal by key.
        /// Useful for documentation and deterministic outputs. Uses Ordinal semantics.
        /// </summary>
        public static IReadOnlyDictionary<string, ExceptionDefinition> AllByTypeSnapshotSorted()
        {
            lock (_gate)
            {
                // Extract keys, sort, then build a new map in sorted order for predictable enumeration.
                var keyCount = _byTypeName.Count;
                var keys = new string[keyCount];
                int i = 0;
                foreach (var kvp in _byTypeName)
                {
                    keys[i] = kvp.Key;
                    i++;
                }
                Array.Sort(keys, StringComparer.Ordinal);
                var copy = new Dictionary<string, ExceptionDefinition>(keyCount, StringComparer.Ordinal);
                int k = 0;
                while (k < keys.Length)
                {
                    var key = keys[k];
                    if (_byTypeName.TryGetValue(key, out var binding))
                    {
                        if (_byCode.TryGetValue(binding.Code, out var def))
                        {
                            copy[key] = def;
                        }
                    }
                    k++;
                }
                return new System.Collections.ObjectModel.ReadOnlyDictionary<string, ExceptionDefinition>(copy);
            }
        }

        /// <summary>
        /// Try to get a definition by CLR exception type name with observed registry version.
        /// Returns false on invalid type name or when not found.
        /// </summary>
        public static bool TryGetByTypeNameWithVersion(string exceptionTypeName, out ExceptionDefinition? def, out long version)
        {
            def = null;
            var v1 = System.Threading.Volatile.Read(ref _version);
            if (exceptionTypeName == null || !ExceptionPolicy.IsValidTypeNameNonThrowing(exceptionTypeName))
            {
                version = v1;
                return false;
            }
            bool found = false;
            lock (_gate)
            {
                if (_byTypeName.TryGetValue(exceptionTypeName, out var binding))
                {
                    if (_byCode.TryGetValue(binding.Code, out var d))
                    {
                        def = d;
                        found = true;
                    }
                }
            }
            var v2 = System.Threading.Volatile.Read(ref _version);
            version = (v2 >= v1) ? v2 : v1;
            return found;
        }

        /// <summary>
        /// Strict lookup by CLR exception type name. Throws on invalid input or when not registered.
        /// </summary>
        public static ExceptionDefinition GetRequiredByTypeName(string exceptionTypeName)
        {
            if (exceptionTypeName == null) throw new ArgumentNullException(nameof(exceptionTypeName));
            // Be strict and provide precise error messages, mirroring GetRequiredByCode strictness.
            ExceptionPolicy.ValidateTypeName(exceptionTypeName, nameof(exceptionTypeName));
            lock (_gate)
            {
                if (_byTypeName.TryGetValue(exceptionTypeName, out var binding))
                {
                    if (_byCode.TryGetValue(binding.Code, out var d)) return d;
                }
            }
            throw new KeyNotFoundException("Exception type '" + exceptionTypeName + "' is not registered.");
        }

        public static ExceptionDefinition GetRequiredByCode(string code)
        {
            if (code == null) throw new ArgumentNullException(nameof(code));
            // Be strict for required lookups: invalid codes are a caller bug, not “not registered”.
            if (!ExceptionPolicy.IsValidCodeNonThrowing(code))
            {
                throw new ArgumentException("Invalid exception code.", nameof(code));
            }
            // Safe canonicalization (input validated above).
            var canonical = ExceptionPolicy.CanonicalizeCode(code);
            if (_byCode.TryGetValue(canonical, out var def)) return def;
            // Surface canonicalized code to avoid confusion in logs where case may differ.
            throw new KeyNotFoundException("Exception code '" + canonical + "' is not registered.");
        }
        /// <summary>
        /// Idempotent registration. Returns true if added or already present with identical schema.
        /// Returns false if the registry is frozen. Throws if a different schema is already registered.
        /// </summary>
        public static bool TryRegister(ExceptionDefinition def)
        {
            if (def is null) throw new ArgumentNullException(nameof(def));
            def.EnsureValid();
            def = Canonicalize(def);
            lock (_gate)
            {
                if (_frozen) return false;
                // Enforce type binding consistency before any mutation.
                ValidateTypeBindingAgainstRegistry(def);

                if (_byCode.TryGetValue(def.Code, out var existing))
                {
                    if (!SchemaEquals(existing, def))
                        throw new ArgumentException($"Exception code '{def.Code}' already registered with different schema.");
                    if (!string.Equals(existing.Description, def.Description, StringComparison.Ordinal))
                    {
                        _byCode[def.Code] = ExceptionDefinition.CreateTrusted(existing.Code, existing.ExceptionTypeName, existing.Category, def.Description);
                        Interlocked.Increment(ref _version);
                    }
                    EnsureTypeBindingIndex(existing.ExceptionTypeName, existing.Code, existing.Category);
                    return true;
                }
                if (_byCode.TryAdd(def.Code, def))
                {
                    EnsureTypeBindingIndex(def.ExceptionTypeName, def.Code, def.Category);
                    Interlocked.Increment(ref _version);
                    return true;
                }
                // Re-check consistency if concurrent add occurred
                if (_byCode.TryGetValue(def.Code, out existing))
                {
                    if (!SchemaEquals(existing, def))
                        throw new ArgumentException($"Exception code '{def.Code}' already registered with different schema.");
                    if (!string.Equals(existing.Description, def.Description, StringComparison.Ordinal))
                    {
                        _byCode[def.Code] = ExceptionDefinition.CreateTrusted(existing.Code, existing.ExceptionTypeName, existing.Category, def.Description);
                        Interlocked.Increment(ref _version);
                    }
                    EnsureTypeBindingIndex(existing.ExceptionTypeName, existing.Code, existing.Category);
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Idempotent registration helper by fields.
        /// </summary>
        public static bool TryRegister(string code, string exceptionTypeName, FailureCategory category, string description)
        {
            var def = ExceptionDefinition.Create(code, exceptionTypeName, category, description);
            return TryRegister(def);
        }

        // Treat Code case-insensitively to align with the registry key equality.
        private static bool SchemaEquals(ExceptionDefinition a, ExceptionDefinition b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null) return false;
            // All codes are canonicalized before reaching here; use Ordinal for stricter equality.
            if (!string.Equals(a.Code, b.Code, StringComparison.Ordinal)) return false;
            if (!string.Equals(a.ExceptionTypeName, b.ExceptionTypeName, StringComparison.Ordinal)) return false;
            if (a.Category != b.Category) return false;
            return true;
        }

        /// <summary>
        /// Observer count snapshot for diagnostics/health endpoints.
        /// </summary>
        public static int ObserverCount
        {
            get
            {
                // Cheap O(1) passthrough; relies on ExceptionNotifier's volatile array semantics.
                return ExceptionNotifier.ObserverCount;
            }
        }

        /// <summary>
        /// Observer error count snapshot for diagnostics/health endpoints.
        /// </summary>
        public static long ObserverErrorCount
        {
            get { return ExceptionNotifier.ObserverErrorCount; }
        }

        /// <summary>
        /// Monotonic registry version for cache invalidation. Increments on effective adds and freeze transitions.
        /// </summary>
        public static long VersionInline
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => System.Threading.Volatile.Read(ref _version);
        }

        /// <summary>
        /// Observer lifecycle version. Increments on observer freeze and add/remove transitions.
        /// Read semantics are eventually monotonic across reads (not strictly linearizable across the two counters).
        /// </summary>
        public static long ObserverVersion
        {
            get
            {
                // Single source of truth in ExceptionNotifier to simplify monotonicity guarantees.
                return ExceptionNotifier.ObserverVersion;
            }
        }
        /// <summary>
        /// Configure an optional safety fuse that auto-freezes observers after the given
        /// cumulative error count (>= 1). Set to 0 to disable auto-freeze (default).
        /// </summary>
        public static int ObserverErrorAutoFreezeThreshold
        {
            get { return ExceptionNotifier.ObserverErrorAutoFreezeThreshold; }
            set { ExceptionNotifier.ObserverErrorAutoFreezeThreshold = value; }
        }

        /// <summary>
        /// Update description for an existing code (pre-freeze). Throws if unknown or frozen.
        /// </summary>
        public static void UpdateDescription(string code, string newDescription)
        {
            if (code == null) throw new ArgumentNullException(nameof(code));
            ExceptionPolicy.ValidateDescription(newDescription, nameof(newDescription));
            // Enforce canonicalizer precondition: validate before canonicalization.
            ExceptionPolicy.ValidateCode(code, nameof(code));
            var canonical = ExceptionPolicy.CanonicalizeCode(code);
            lock (_gate)
            {
                if (_frozen) throw new InvalidOperationException("ExceptionRegistry is frozen and cannot be modified.");
                if (!_byCode.TryGetValue(canonical, out var existing))
                    throw new KeyNotFoundException("Exception code '" + canonical + "' is not registered.");
                if (string.Equals(existing.Description, newDescription, StringComparison.Ordinal)) return;
                _byCode[canonical] = ExceptionDefinition.CreateTrusted(existing.Code, existing.ExceptionTypeName, existing.Category, newDescription);
                Interlocked.Increment(ref _version);
            }
        }

        /// <summary>
        /// Return a fully consistent snapshot and its version captured atomically under the registry gate.
        /// Use when a precise mapping must correspond to the exact version value returned.
        /// </summary>
        public static IReadOnlyDictionary<string, ExceptionDefinition> AllByCodeSnapshotConsistent(out long version)
        {
            lock (_gate)
            {
                version = System.Threading.Volatile.Read(ref _version);
                var copy = new Dictionary<string, ExceptionDefinition>(_byCode.Count, StringComparer.Ordinal);
                foreach (var kvp in _byCode)
                {
                    copy[kvp.Key] = kvp.Value;
                }
                return new System.Collections.ObjectModel.ReadOnlyDictionary<string, ExceptionDefinition>(copy);
            }
        }

        /// <summary>
        /// Try to update description (pre-freeze). Returns false if frozen or unknown.
        /// </summary>
        public static bool TryUpdateDescription(string code, string newDescription)
        {
            if (code == null) return false;
            ExceptionPolicy.ValidateDescription(newDescription, nameof(newDescription));
            // Non-throwing code validation to preserve Try* semantics and canonicalizer precondition.
            if (!ExceptionPolicy.IsValidCodeNonThrowing(code)) return false;
            var canonical = ExceptionPolicy.CanonicalizeCode(code);
            lock (_gate)
            {
                if (_frozen) return false;
                if (!_byCode.TryGetValue(canonical, out var existing)) return false;
                if (string.Equals(existing.Description, newDescription, StringComparison.Ordinal)) return true;
                _byCode[canonical] = ExceptionDefinition.CreateTrusted(existing.Code, existing.ExceptionTypeName, existing.Category, newDescription);
                Interlocked.Increment(ref _version);
                return true;
            }
        }

        /// <summary>
        /// Bulk registration that reports outcome without partial application on conflict.
        /// Returns false and sets result.Conflicts if any schema conflict is detected.
        /// </summary>
        public static bool TryRegisterAll(ExceptionDefinition[] definitions, out BulkRegistrationResult result)
        {
            result = default;
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));
            lock (_gate)
            {
                if (_frozen)
                {
                    result = new BulkRegistrationResult(0, 0, 0, new Conflict(-1, string.Empty, "Registry is frozen."));
                    return false;
                }

                var local = new Dictionary<string, ExceptionDefinition>(definitions.Length, StringComparer.Ordinal);
                // Track per-type binding within this batch to prevent intra-batch conflicts.
                // Keyed by ExceptionTypeName to the first observed (Code, Category). Later mismatches are rejected.
                var localTypeFirst = new Dictionary<string, ExceptionDefinition>(definitions.Length, StringComparer.Ordinal);
                int i = 0;
                while (i < definitions.Length)
                {
                    var raw = definitions[i];
                    if (raw is null)
                    {
                        result = new BulkRegistrationResult(0, 0, 0, new Conflict(i, string.Empty, "Null entry in batch."));
                        return false;
                    }
                    raw.EnsureValid();
                    var def = Canonicalize(raw);

                    // Enforce per-batch type binding consistency (ExceptionTypeName -> (Code, Category)).
                    if (localTypeFirst.TryGetValue(def.ExceptionTypeName, out var priorType))
                    {
                        if (!string.Equals(priorType.Code, def.Code, StringComparison.Ordinal) || priorType.Category != def.Category)
                        {
                            result = new BulkRegistrationResult(0, 0, 0, new Conflict(i, def.Code, "Type bound inconsistently in batch (code/category drift for the same CLR type)."));
                            return false;
                        }
                    }
                    else
                    {
                        localTypeFirst.Add(def.ExceptionTypeName, def);
                    }

                    if (local.TryGetValue(def.Code, out var prior))
                    {
                        if (!SchemaEquals(prior, def))
                        {
                            result = new BulkRegistrationResult(0, 0, 0, new Conflict(i, def.Code, "Duplicate code in batch with different schema."));
                            return false;
                        }
                        // Prefer last description deterministically.
                        if (!string.Equals(prior.Description, def.Description, StringComparison.Ordinal))
                            local[def.Code] = ExceptionDefinition.CreateTrusted(def.Code, def.ExceptionTypeName, def.Category, def.Description);
                    }
                    else
                    {
                        local.Add(def.Code, def);
                    }

                    if (_byCode.TryGetValue(def.Code, out var existing) && !SchemaEquals(existing, def))
                    {
                        result = new BulkRegistrationResult(0, 0, 0, new Conflict(i, def.Code, "Schema conflict with existing registry."));
                        return false;
                    }
                    // Enforce type binding against the registry via the O(1) secondary index.
                    // Keeps behavior consistent with Register/RegisterAll and avoids O(n) scans.
                    ValidateTypeBindingAgainstRegistry(def);
                    i++;
                }

                int added = 0, updatedDesc = 0, already = 0;
                foreach (var kvp in local)
                {
                    // Defensive: recheck type binding just before mutation.
                    ValidateTypeBindingAgainstRegistry(kvp.Value);
                    if (_byCode.TryGetValue(kvp.Key, out var existing))
                    {
                        if (!string.Equals(existing.Description, kvp.Value.Description, StringComparison.Ordinal))
                        {
                            _byCode[kvp.Key] = ExceptionDefinition.CreateTrusted(existing.Code, existing.ExceptionTypeName, existing.Category, kvp.Value.Description);
                            updatedDesc++;
                        }
                        else
                        {
                            already++;
                        }
                        // Keep type binding index aligned (ensure presence).
                        EnsureTypeBindingIndex(existing.ExceptionTypeName, existing.Code, existing.Category);
                        continue;
                    }
                    if (_byCode.TryAdd(kvp.Key, kvp.Value))
                    {
                        // Maintain secondary index for new add.
                        EnsureTypeBindingIndex(kvp.Value.ExceptionTypeName, kvp.Value.Code, kvp.Value.Category);
                        added++;
                    }
                    else if (_byCode.TryGetValue(kvp.Key, out existing))
                    {
                        // Concurrent identical or description-diff add.
                        if (!SchemaEquals(existing, kvp.Value))
                        {
                            result = new BulkRegistrationResult(0, 0, 0, new Conflict(-1, kvp.Key, "Concurrent schema conflict."));
                            return false;
                        }
                        if (!string.Equals(existing.Description, kvp.Value.Description, StringComparison.Ordinal))
                        {
                            _byCode[kvp.Key] = ExceptionDefinition.CreateTrusted(existing.Code, existing.ExceptionTypeName, existing.Category, kvp.Value.Description);
                            updatedDesc++;
                        }
                        else
                        {
                            already++;
                        }
                        // Ensure type binding index presence in the concurrent-existing case as well.
                        EnsureTypeBindingIndex(existing.ExceptionTypeName, existing.Code, existing.Category);
                    }
                    else
                    {
                        // Lost race but no item found; treat as no-op.
                        already++;
                    }
                }
                if ((added + updatedDesc) > 0) Interlocked.Increment(ref _version);
                result = new BulkRegistrationResult(added, updatedDesc, already, new Conflict(-1, string.Empty, string.Empty));
                return true;
            }
        }

        public readonly struct BulkRegistrationResult
        {
            public readonly int Added;
            public readonly int UpdatedDescriptions;
            public readonly int AlreadyPresent;
            public readonly Conflict Conflict;
            public BulkRegistrationResult(int added, int updated, int already, Conflict conflict)
            {
                Added = added; UpdatedDescriptions = updated; AlreadyPresent = already; Conflict = conflict;
            }
            // A conflict is present when a non-empty message (or code) is populated by the producer.
            public bool HasConflict => (Conflict.Message != null && Conflict.Message.Length != 0) || (Conflict.Code != null && Conflict.Code.Length != 0);
        }

        public readonly struct Conflict
        {
            public readonly int Index; // index in input array, -1 if unknown
            public readonly string Code;
            public readonly string Message;
            public Conflict(int index, string code, string message)
            {
                Index = index; Code = code; Message = message;
            }
        }

        // Compact binding value used by the secondary index.
        private readonly struct Binding
        {
            public readonly string Code;
            public readonly FailureCategory Category;
            public Binding(string code, FailureCategory category) { Code = code; Category = category; }
        }

        // Maintain _byTypeName to reflect the authoritative (Code, Category) binding for a CLR type name.
        // Must be called under _gate.
        private static void EnsureTypeBindingIndex(string exceptionTypeName, string code, FailureCategory category)
        {
            if (_byTypeName.TryGetValue(exceptionTypeName, out var existing))
            {
                // Enforce consistency (defensive): if an entry exists, it must match the schema binding.
                if (!string.Equals(existing.Code, code, StringComparison.Ordinal) || existing.Category != category)
                {
                    // This should be unreachable due to pre-validation; failing here preserves invariant.
                    throw new ArgumentException(
                        "Type binding index drift detected. " +
                        "Type=" + exceptionTypeName +
                        ", ExistingCode=" + existing.Code +
                        ", ExistingCategory=" + existing.Category.ToString() +
                        ", NewCode=" + code +
                        ", NewCategory=" + category.ToString() + ".");
                }
                return;
            }
            _byTypeName.Add(exceptionTypeName, new Binding(code, category));
        }

        /// <summary>
        /// Return a fully consistent snapshot keyed by CLR exception type name
        /// with the registry version captured atomically under the registry gate.
        /// Mirrors AllByCodeSnapshotConsistent for callers that index by type name.
        /// </summary>
        public static IReadOnlyDictionary<string, ExceptionDefinition> AllByTypeSnapshotConsistent(out long version)
        {
            lock (_gate)
            {
                version = System.Threading.Volatile.Read(ref _version);
                var copy = new Dictionary<string, ExceptionDefinition>(_byTypeName.Count, StringComparer.Ordinal);
                foreach (System.Collections.Generic.KeyValuePair<string, Binding> kvp in _byTypeName)
                {
                    ExceptionDefinition def;
                    if (!_byCode.TryGetValue(kvp.Value.Code, out def) || def == null)
                    {
                        // Under registry gate this should be impossible; fail fast to surface drift.
                        throw new InvalidOperationException(
                            "Inconsistent registry snapshot: missing definition by code for type binding. " +
                            "Type=" + kvp.Key + ", Code=" + kvp.Value.Code + ".");
                    }
                    copy[kvp.Key] = def;
                }
                return new System.Collections.ObjectModel.ReadOnlyDictionary<string, ExceptionDefinition>(copy);
            }
        }
    }
}
