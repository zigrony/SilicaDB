using System;
using System.IO;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Silica.BufferPool;
using Silica.PageAccess.Metrics;
using Silica.DiagnosticsCore.Metrics;
using Silica.DiagnosticsCore;
using System.Collections.Generic;

namespace Silica.PageAccess
{
    /// <summary>
    /// Enterprise-ready implementation of IPageAccessor:
    /// - Acquires read/write leases from BufferPool with correct pinning/latching semantics.
    /// - Validates physical header invariants (magic, type, version); optional checksum verification on reads.
    /// - Provides header-only mutation flow with policy-driven checksum handling.
    /// - Emits low-overhead metrics and simple structured trace events.
    /// Design constraints: BCL-only, no LINQ/reflection/JSON/3rd-party libs.
    /// </summary>
    public sealed class PageManager : IPageAccessor, IAsyncDisposable
    {
        private readonly IBufferPoolManager _pool;
        private readonly PageAccessorOptions _options;
        private readonly IMetricsManager _metrics;
        private readonly KeyValuePair<string, object> _componentTag;
        private int _disposed;
        [ThreadStatic]
        private static int _traceReentrancy;

        public PageManager(
            IBufferPoolManager pool,
            PageAccessorOptions options,
            IMetricsManager metrics,
            string componentName = "PageAccess")
        {
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
            _options = options;
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));

            var comp = string.IsNullOrWhiteSpace(componentName) ? "PageAccess" : componentName;
            _componentTag = new KeyValuePair<string, object>(TagKeys.Component, comp);

            // Register PageAccess metrics once (DiagnosticsCore is tolerant of duplicates).
            PageAccessMetrics.RegisterAll(_metrics, comp);
        }

        private static class Stopwatch
        {
            public static long GetTimestamp()
                => System.Diagnostics.Stopwatch.GetTimestamp();

            public static double GetElapsedMilliseconds(long startTicks)
            {
                return (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            }
        }


        private static bool IsTracing => DiagnosticsCoreBootstrap.IsStarted;

        /// <summary>
        /// Read path:
        /// - Acquire read lease (pins page, shared latch).
        /// - Parse and validate header (magic/type/version).
        /// - Optionally verify checksum over entire page.
        /// - Return a handle containing header + ReadOnlyMemory page view. Caller disposes when done.
        /// </summary>
        public async ValueTask<PageReadHandle> ReadAsync(PageId id, PageVersion expectedVersion, PageType expectedType, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            var start = Stopwatch.GetTimestamp();
            PageReadLease lease;
            try
            {
                lease = await _pool.AcquireReadAsync(id, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                RecordWarn("canceled");
                if (IsTracing) TryEmitTrace("ReadAsync", "warn", $"operation_canceled: {id.FileId}/{id.PageIndex}", "canceled", id);
                throw;
            }

            try
            {
                if (!TryReadHeader(lease.Page, out var hdr))
                {
                    RecordError("invalid_header");
                    if (IsTracing) TryEmitTrace("ReadAsync", "error", $"invalid_page_header: {id.FileId}/{id.PageIndex}", "invalid_header", id);
                    throw new PageHeaderInvalidException($"Invalid page header at {id.FileId}/{id.PageIndex} (len={lease.Page.Length}).");
                }

                ValidateHeader(id, hdr, expectedType, expectedVersion);
                ValidateHeaderPhysical(id, hdr, lease.Page.Length);

                if (_options.VerifyChecksumOnRead &&
                    !(hdr.Checksum == 0 && _options.SkipChecksumVerifyIfZero))
                {
                    if (!VerifyChecksum(lease.Page.Span, out var expected, out var actual))
                    {
                        RecordError("checksum_mismatch");
                        if (IsTracing) TryEmitTrace("ReadAsync", "error", $"checksum_mismatch: {expected:X8}!={actual:X8} id={id.FileId}/{id.PageIndex}", "checksum_mismatch", id);
                        throw new PageChecksumMismatchException($"Checksum mismatch at {id.FileId}/{id.PageIndex}: header={expected:X8}, computed={actual:X8}.");
                    }
                }
                else if (_options.VerifyChecksumOnRead && hdr.Checksum == 0 && _options.SkipChecksumVerifyIfZero)
                {
                    // Operational visibility: we deliberately skipped verification due to policy and zero checksum.
                    RecordWarn("checksum_skipped_zero");
                    if (IsTracing) TryEmitTrace("ReadAsync", "info", $"checksum_verification_skipped_zero: {id.FileId}/{id.PageIndex}", "checksum_skipped_zero", id);
                }

                RecordRead(hdr.Type, start);
                return new PageReadHandle(lease, hdr);
            }
            catch
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Write path:
        /// - Perform a short read-validate (header) to avoid nested latch pitfalls.
        /// - Acquire write lease over the requested range (exclusive range latch, pin).
        /// - Return a handle with parsed header and writable slice. Caller mutates then disposes.
        /// Header checksum/LSN: full-page recompute and WAL stamping occur in higher-level flows and BufferPool flush; this layer ensures header integrity.
        /// </summary>
        public async ValueTask<PageWriteHandle> WriteAsync(PageId id, int offset, int length, PageVersion expectedVersion, PageType expectedType, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
            // Prevent integer overflow on offset + length
            if (offset > int.MaxValue - length) throw new ArgumentOutOfRangeException(nameof(length), "Offset + length overflow.");
            // Disallow partial header overlap to protect invariants
            if (offset < PageHeader.HeaderSize && (offset != 0 || length < PageHeader.HeaderSize))
                throw new ArgumentOutOfRangeException(nameof(offset), "Writes overlapping the header must start at 0 and cover the full header.");

            var start = Stopwatch.GetTimestamp();

            // Step 1: header validation under a short read lease (no writer held yet).
            PageHeader hdr;
            var readLease = await _pool.AcquireReadAsync(id, ct).ConfigureAwait(false);
            try
            {
                if (!TryReadHeader(readLease.Page, out hdr))
                {
                    RecordError("invalid_header");
                    if (IsTracing) TryEmitTrace("WriteAsync", "error", $"invalid_page_header: {id.FileId}/{id.PageIndex}", "invalid_header", id);
                    throw new PageHeaderInvalidException($"Invalid page header at {id.FileId}/{id.PageIndex} (len={readLease.Page.Length}).");
                }
                ValidateHeader(id, hdr, expectedType, expectedVersion);
                ValidateHeaderPhysical(id, hdr, readLease.Page.Length);
            }
            finally
            {
                await readLease.DisposeAsync().ConfigureAwait(false);
            }

            // Step 2: acquire write lease on requested range.
            try
            {
                var writeLease = await _pool.AcquireWriteAsync(id, offset, length, ct).ConfigureAwait(false);
                var success = false;
                try
                {
                    if (writeLease.Slice.Length != length)
                    {
                        throw new ArgumentOutOfRangeException(nameof(length), "Write lease size mismatch.");
                    }
                    // If we are writing the header region, re-read and re-validate under the exclusive lease to close the TOCTOU window.
                    if (offset == 0 && length >= PageHeader.HeaderSize)
                    {
                        if (!TryReadHeader(writeLease.Slice, out var hdr2))
                        {
                            RecordError("invalid_header");
                            if (IsTracing) TryEmitTrace("WriteAsync", "error", $"invalid_page_header_under_write: {id.FileId}/{id.PageIndex}", "invalid_header", id);
                            throw new PageHeaderInvalidException($"Invalid page header at {id.FileId}/{id.PageIndex} during write.");
                        }
                        ValidateHeader(id, hdr2, expectedType, expectedVersion);
                        hdr = hdr2;
                        // Note: physical free space upper-bound cannot be rechecked here without full page, but header region is locked.
                    }
                    else
                    {
                        // Header was not locked; caller accepts a TOCTOU risk unless policy forbids it.
                        if (_options.RequireHeaderRevalidationOnWrite)
                        {
                            RecordError("header_not_revalidated");
                            if (IsTracing) TryEmitTrace("WriteAsync", "error", $"header_not_revalidated_requires_header_lock: {id.FileId}/{id.PageIndex}", "header_not_revalidated", id);
                            throw new PageHeaderInvalidException("Header revalidation required on write. Include header region [0..HeaderSize) in the write lease.");
                        }
                        else
                        {
                            RecordWarn("header_not_revalidated");
                            if (IsTracing) TryEmitTrace("WriteAsync", "warn", $"header_not_revalidated: {id.FileId}/{id.PageIndex}", "header_not_revalidated", id);
                        }
                    }
                    // Reaching here means the write lease is valid and will be returned to the caller.
                    success = true;
                }
                finally
                {
                    if (!success)
                    {
                        await writeLease.DisposeAsync().ConfigureAwait(false);
                    }
                }

                RecordWrite(hdr.Type, start);
                // Header was revalidated under the exclusive lease only if header region was locked.
                return new PageWriteHandle(writeLease, hdr, headerRevalidated: (offset == 0 && length >= PageHeader.HeaderSize));
            }
            catch (OperationCanceledException)
            {
                RecordWarn("canceled");
                if (IsTracing) TryEmitTrace("WriteAsync", "warn", $"operation_canceled: {id.FileId}/{id.PageIndex}", "canceled", id);
                throw;
            }
        }

        /// <summary>
        /// Header-only mutation:
        /// - Acquire write lease over header region.
        /// - Validate header invariants.
        /// - Apply caller-provided mutation (pure function of PageHeader).
        /// - Write updated header (optionally zero checksum per policy; full-page checksum usually handled by higher layer on full writes).
        /// </summary>
        public async ValueTask<PageHeader> WriteHeaderAsync(PageId id, PageVersion expectedVersion, PageType expectedType, Func<PageHeader, PageHeader> mutate, CancellationToken ct = default)
        {
            if (mutate is null) throw new ArgumentNullException(nameof(mutate));
            ThrowIfDisposed();

            var start = Stopwatch.GetTimestamp();
            var lease = await _pool.AcquireWriteAsync(id, 0, PageHeader.HeaderSize, ct).ConfigureAwait(false);
            try
            {
                if (!TryReadHeader(lease.Slice, out var hdr))
                {
                    RecordError("invalid_header");
                    if (IsTracing) TryEmitTrace("WriteHeaderAsync", "error", $"invalid_page_header: {id.FileId}/{id.PageIndex}", "invalid_header", id);
                    throw new PageHeaderInvalidException($"Invalid page header at {id.FileId}/{id.PageIndex} during header write.");
                }

                ValidateHeader(id, hdr, expectedType, expectedVersion);
                // For header-only mutations, we cannot validate FreeSpaceOffset upper bound (page length unknown here),
                // but we can ensure the lower bound is respected.
                ValidateHeaderPhysicalLowerBoundOnly(id, hdr);

                var updated = mutate(hdr);
                if (updated.Lsn == hdr.Lsn)
                {
                    RecordWarn("lsn_not_updated");
                    if (IsTracing) TryEmitTrace("WriteHeaderAsync", "warn", $"lsn_not_updated: {id.FileId}/{id.PageIndex}", "lsn_static", id);
                }

                // Ensure the mutated header still respects physical invariants.
                ValidateHeader(id, updated, expectedType, expectedVersion);
                ValidateHeaderPhysicalLowerBoundOnly(id, updated);

                // If policy requests checksum update on header writes, zero-out checksum field so a later full-page recompute is correct.
                var writeBack = _options.UpdateChecksumOnWrite ? updated.With(checksum: 0) : updated;
                if (_options.UpdateChecksumOnWrite)
                {
                    if (IsTracing) TryEmitTrace("WriteHeaderAsync", "info", $"checksum_zeroed: {id.FileId}/{id.PageIndex}", "checksum_zeroed", id);
                }
                WriteHeaderToMemory(lease.Slice, writeBack);

                RecordHeaderWrite(updated.Type, start);
                return updated;
            }
            catch (OperationCanceledException)
            {
                RecordWarn("canceled");
                if (IsTracing) TryEmitTrace("WriteHeaderAsync", "warn", $"operation_canceled: {id.FileId}/{id.PageIndex}", "canceled", id);
                throw;
            }
            finally
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Pass-through to buffer pool FlushPageAsync.
        /// </summary>
        public Task FlushPageAsync(PageId id, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            return _pool.FlushPageAsync(id, ct);
        }

        /// <summary>
        /// Pass-through to buffer pool FlushAllAsync.
        /// </summary>
        public Task FlushAllAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            return _pool.FlushAllAsync(ct);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                // Optional: flush dirty pages before disposal (policy-driven; can be expensive)
                if (_options.FlushOnDispose)
                {
                    return new ValueTask(FlushAllSafeAsync());
                }
                return ValueTask.CompletedTask;
            }
            return ValueTask.CompletedTask;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Internal invariants and helpers
        // ─────────────────────────────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(PageManager));
        }

        /// <summary>
        /// Enforces physical invariants: magic, type, version policy. Checksum verification is performed at call sites.
        /// </summary>
        private void ValidateHeader(PageId id, in PageHeader hdr, PageType expectedType, PageVersion expectedVersion)
        {
            // Optional: enforce LSN monotonicity or range
            if (hdr.Lsn == 0 && IsLsnExpected(hdr.Type))
            {
                RecordWarn("lsn_zero");
                TryEmitTrace("ValidateHeader", "warn", $"lsn_zero: {id.FileId}/{id.PageIndex}", "lsn_zero", id);
                // optionally throw or warn
            }

            if (hdr.Magic != _options.Magic)
            {
                RecordError("bad_magic");
                TryEmitTrace("ValidateHeader", "error", $"magic_mismatch: {id.FileId}/{id.PageIndex}", "bad_magic", id);
                throw new MagicMismatchException($"Magic mismatch at {id.FileId}/{id.PageIndex}: expected={_options.Magic:X8}, actual={hdr.Magic:X8}.");
            }

            if (expectedType != PageType.Unknown && hdr.Type != expectedType)
            {
                RecordError("type_mismatch");
                TryEmitTrace("ValidateHeader", "error", $"type_mismatch: {id.FileId}/{id.PageIndex}", "type_mismatch", id);
                throw new PageTypeMismatchException($"Page type mismatch at {id.FileId}/{id.PageIndex}: expected={expectedType}, actual={hdr.Type}.");
            }

            // PageType handling: Unknown is allowed only if explicitly expected, otherwise reject.
            if (hdr.Type == PageType.Unknown)
            {
                if (expectedType == PageType.Unknown)
                {
                    RecordWarn("type_unknown");
                    TryEmitTrace("ValidateHeader", "warn", $"page_type_unknown: {id.FileId}/{id.PageIndex}", "type_unknown", id);
                }
                else
                {
                    RecordError("type_unexpected_unknown");
                    TryEmitTrace("ValidateHeader", "error", $"unexpected_unknown_type: {id.FileId}/{id.PageIndex}", "type_unexpected_unknown", id);
                    throw new PageHeaderInvalidException($"Unexpected Unknown page type at {id.FileId}/{id.PageIndex}.");
                }
            }
            else if (expectedType == PageType.Unknown && !IsKnownPageType(hdr.Type))
            {
                RecordError("type_invalid");
                TryEmitTrace("ValidateHeader", "error", $"invalid_page_type: {id.FileId}/{id.PageIndex}", "type_invalid", id);
                throw new PageHeaderInvalidException($"Invalid page type at {id.FileId}/{id.PageIndex}: {hdr.Type}.");
            }

            if (hdr.Version.Major != expectedVersion.Major)
            {
                RecordError("major_version");
                TryEmitTrace("ValidateHeader", "error", $"major_version_mismatch: {id.FileId}/{id.PageIndex}", "major_version", id);
                throw new PageVersionMismatchException($"Major version mismatch at {id.FileId}/{id.PageIndex}: expected={expectedVersion.Major}, actual={hdr.Version.Major}.");
            }

            if (hdr.Version.Minor < expectedVersion.Minor && !_options.AllowMinorUpgrade)
            {
                RecordError("minor_version");
                TryEmitTrace("ValidateHeader", "error", $"minor_version_too_old: {id.FileId}/{id.PageIndex}", "minor_version", id);
                throw new PageVersionMismatchException($"Minor version too old at {id.FileId}/{id.PageIndex}: expected≥{expectedVersion.Minor}, actual={hdr.Version.Minor}.");
            }

            if (!_options.AllowFutureMinor && hdr.Version.Minor > expectedVersion.Minor)
            {
                RecordError("minor_future");
                TryEmitTrace("ValidateHeader", "error", $"minor_version_too_new: {id.FileId}/{id.PageIndex}", "minor_future", id);
                throw new PageVersionMismatchException($"Minor version too new at {id.FileId}/{id.PageIndex}: expected≤{expectedVersion.Minor}, actual={hdr.Version.Minor}.");
            }

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ValidateHeaderPhysical(PageId id, in PageHeader hdr, int pageLength)
        {
            // Basic page length sanity
            if (pageLength < PageHeader.HeaderSize)
            {
                RecordError("page_too_small");
                TryEmitTrace("ValidateHeaderPhysical", "error", $"page_too_small: {id.FileId}/{id.PageIndex}", "page_too_small", id);
                throw new PageHeaderInvalidException($"Page too small at {id.FileId}/{id.PageIndex}: len={pageLength}, required={PageHeader.HeaderSize}.");
            }
            // FreeSpaceOffset must be within [HeaderSize..pageLength]
            if (hdr.FreeSpaceOffset < PageHeader.HeaderSize || hdr.FreeSpaceOffset > pageLength)
            {
                RecordError("free_offset_range");
                TryEmitTrace("ValidateHeaderPhysical", "error", $"free_offset_out_of_range: {id.FileId}/{id.PageIndex}", "free_offset_range", id);
                throw new PageHeaderInvalidException($"FreeSpaceOffset out of bounds at {id.FileId}/{id.PageIndex}: offset={hdr.FreeSpaceOffset}, pageLen={pageLength}.");
            }

            // PageType validity handled in ValidateHeader via IsKnownPageType when unconstrained
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ValidateHeaderPhysicalLowerBoundOnly(PageId id, in PageHeader hdr)
        {
            if (hdr.FreeSpaceOffset < PageHeader.HeaderSize)
            {
                RecordError("free_offset_lt_header");
                TryEmitTrace("ValidateHeaderPhysicalLowerBoundOnly", "error", $"free_offset_lt_header: {id.FileId}/{id.PageIndex}", "free_offset_lt_header", id);
                throw new PageHeaderInvalidException($"FreeSpaceOffset below header size: offset={hdr.FreeSpaceOffset}, required≥{PageHeader.HeaderSize} at {id.FileId}/{id.PageIndex}.");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsKnownPageType(PageType t)
        {
            return t == PageType.Header
                || t == PageType.Catalog
                || t == PageType.PFS
                || t == PageType.GAM
                || t == PageType.SGAM
                || t == PageType.DCM
                || t == PageType.IAM
                || t == PageType.Data
                || t == PageType.Index
                || t == PageType.Lob
                || t == PageType.Free;
        }


        /// <summary>
        /// Full-page checksum update helper. Intended for higher layers performing full-page writes.
        /// Computes checksum with the checksum field treated as zero, then writes it to the header.
        /// </summary>
        public static void UpdateChecksumInPlace(Span<byte> fullPage)
        {
            if (fullPage.Length < PageHeader.HeaderSize)
                throw new PageHeaderInvalidException($"Page too small for checksum update: len={fullPage.Length}, required={PageHeader.HeaderSize}.");
            uint sum = PageChecksum.Compute(fullPage, PageHeader.ChecksumOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(fullPage.Slice(PageHeader.ChecksumOffset, 4), sum);
        }

        /// <summary>
        /// Verify checksum by recomputing over the full page (checksum field zeroed) and comparing header value.
        /// </summary>
        public static bool VerifyChecksum(ReadOnlySpan<byte> fullPage, out uint expected, out uint actual)
        {
            if (fullPage.Length < PageHeader.HeaderSize)
                throw new PageHeaderInvalidException($"Page too small for checksum verification: len={fullPage.Length}, required={PageHeader.HeaderSize}.");
            expected = BinaryPrimitives.ReadUInt32LittleEndian(fullPage.Slice(PageHeader.ChecksumOffset, 4));
            actual = PageChecksum.Compute(fullPage, PageHeader.ChecksumOffset);
            return expected == actual;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Metrics: low-overhead, BCL-only. Metrics must never throw.
        // ─────────────────────────────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RecordRead(PageType type, long startTicks)
        {
            try
            {
                _metrics.Increment(PageAccessMetrics.ReadCount.Name, 1,
                    _componentTag,
                    new KeyValuePair<string, object>(TagKeys.Field, (byte)type));
                var ms = Stopwatch.GetElapsedMilliseconds(startTicks);
                _metrics.Record(PageAccessMetrics.ReadLatencyMs.Name, ms, _componentTag);
            }
            catch (Exception ex)
            {
                _ = ex;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RecordWrite(PageType type, long startTicks)
        {
            try
            {
                _metrics.Increment(PageAccessMetrics.WriteCount.Name, 1,
                    _componentTag,
                    new KeyValuePair<string, object>(TagKeys.Field, (byte)type));
                var ms = Stopwatch.GetElapsedMilliseconds(startTicks);
                _metrics.Record(PageAccessMetrics.WriteLatencyMs.Name, ms, _componentTag);
            }
            catch (Exception ex)
            {
                // Swallow to guarantee metrics never cause faults or secondary effects.
                _ = ex;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RecordHeaderWrite(PageType type, long startTicks)
        {
            try
            {
                _metrics.Increment(PageAccessMetrics.HeaderWriteCount.Name, 1,
                    _componentTag,
                    new KeyValuePair<string, object>(TagKeys.Field, (byte)type));
                var ms = Stopwatch.GetElapsedMilliseconds(startTicks);
                _metrics.Record(PageAccessMetrics.HeaderWriteLatencyMs.Name, ms, _componentTag);
            }
            catch (Exception ex)
            {
                _ = ex;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RecordError(string reason)
        {
            try
            {
                _metrics.Increment(PageAccessMetrics.ErrorCount.Name, 1,
                    _componentTag,
                    // Keep type under TagKeys.Field elsewhere; use a dedicated key for error/warn reasons.
                    new KeyValuePair<string, object>("reason", reason));
            }
            catch (Exception ex)
            {
                _ = ex;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryReadHeader(ReadOnlyMemory<byte> mem, out PageHeader hdr)
        {
            var success = PageHeader.TryRead(mem.Span, out hdr);
            if (!success)
            {
                // Use the guarded trace path to avoid any recursion on this thread.
                TryEmitTrace("TryReadHeader", "error", "Failed to parse page header", "header_parse_failed");
            }
            return success;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteHeaderToMemory(Memory<byte> mem, in PageHeader hdr)
        {
            PageHeader.Write(mem.Span, hdr);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async Task FlushAllSafeAsync()
        {
            try
            {
                await _pool.FlushAllAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (IsTracing) TryEmitTrace("DisposeAsync", "warn", $"flush_all_failed: {ex.Message}", "flush_failed");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RecordWarn(string reason)
        {
            try
            {
                _metrics.Increment(PageAccessMetrics.WarnCount.Name, 1,
                    _componentTag,
                    new KeyValuePair<string, object>("reason", reason));
            }
            catch (Exception ex)
            {
                _ = ex;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void TryEmitTrace(string operation, string status, string message, string code, in PageId? id = null)
        {
            if (!DiagnosticsCoreBootstrap.IsStarted) return;
            if (_traceReentrancy != 0) return; // prevent recursion on this thread if trace pipeline faults
            try
            {
                _traceReentrancy = 1;
                string component = (_componentTag.Value is string s) ? s : "PageAccess";
                var tags = new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase);
                // Keep trace classification under a dedicated, low-cardinality key.
                tags["code"] = code;
                if (id.HasValue)
                {
                    // Add structured correlation tags for querying.
                    tags["file_id"] = id.Value.FileId.ToString();
                    tags["page_index"] = id.Value.PageIndex.ToString();
                }
                DiagnosticsCoreBootstrap.Instance.Traces.Emit(
                    component: component,
                    operation: operation,
                    status: status,
                    tags: tags,
                    message: message
                );
            }
            catch
            {
                // Swallow: never bounce back into metrics or tracing on trace failures.
            }
            finally
            {
                _traceReentrancy = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsLsnExpected(PageType t)
        {
            // Restrict zero-LSN warnings to data-bearing pages to avoid noise on system/free pages.
            return t == PageType.Data
                || t == PageType.Index
                || t == PageType.Lob;
        }


    }
}
