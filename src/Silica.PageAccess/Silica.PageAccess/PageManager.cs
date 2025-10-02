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
using Silica.PageAccess.Diagnostics;
using Silica.Common.Primitives;
using Silica.Storage.Allocation;

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
        // Static bootstrap: register PageAccess exception definitions once.
        static PageManager()
        {
            try { PageAccessExceptions.RegisterAll(); } catch { /* idempotent, never throw */ }
        }


        private readonly IBufferPoolManager _pool;
        private readonly PageAccessorOptions _options;
        private readonly IMetricsManager _metrics;
        private readonly KeyValuePair<string, object> _componentTag;
        private readonly IPageWalHook? _walHook;
        private readonly IStorageAllocator _allocator;
        private int _disposed;

        public PageManager(
            IBufferPoolManager pool,
            PageAccessorOptions options,
            IMetricsManager metrics,
            IStorageAllocator allocator,
            string componentName = "PageAccess",
            IPageWalHook? walHook = null)

        {
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
            _options = options;
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));

            var comp = string.IsNullOrWhiteSpace(componentName) ? "PageAccess" : componentName;
            _componentTag = new KeyValuePair<string, object>(TagKeys.Component, comp);
            _walHook = walHook;
            // Register PageAccess metrics once (DiagnosticsCore is tolerant of duplicates).
            PageAccessMetrics.RegisterAll(_metrics, comp);
        }

        public async Task<PageId> CreatePageAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            var pageId = await _allocator.AllocatePageAsync(ct).ConfigureAwait(false);
            PageAccessDiagnostics.Emit(
                component: "Silica.PageAccess",
                operation: "CreatePageAsync",
                level: "info",
                message: $"Allocated page: file={pageId.FileId}, index={pageId.PageIndex}",
                ex: null,
                more: new Dictionary<string, string>
                {
                { "file_id", pageId.FileId.ToString() },
                { "page_index", pageId.PageIndex.ToString() }
                });
            return pageId;
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


        // Centralized tracing is handled via PageAccessDiagnostics.Emit (which checks IsStarted internally).

        /// <summary>
        /// ARIES-compliant write: mutate a range under exclusive latch, append WAL via the hook,
        /// obtain LSN, and stamp the page header LSN before returning. Enforces WAL-before-write
        /// by delegating to the hook. The mutate callback can modify the provided writable slice.
        /// </summary>
        public async ValueTask<PageHeader> WriteAndStampAsync(
            PageId id,
            int offset,
            int length,
            PageVersion expectedVersion,
            PageType expectedType,
            Func<Memory<byte>, PageHeader, PageHeader> mutate,
            CancellationToken ct = default)
        {
            if (mutate is null) throw new ArgumentNullException(nameof(mutate));
            ThrowIfDisposed();
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
            if (offset > int.MaxValue - length) throw new ArgumentOutOfRangeException(nameof(length));

            // 1) Short read to validate header & invariants
            var readLease = await _pool.AcquireReadAsync(id, ct).ConfigureAwait(false);
            PageHeader current;
            try
            {
                if (!TryReadHeader(readLease.Page, out current))
                {
                    RecordError("invalid_header");
                    PageAccessDiagnostics.Emit(
                        component: "Silica.PageAccess",
                        operation: "WriteAndStampAsync",
                        level: "error",
                        message: "invalid_page_header",
                        ex: null,
                        more: new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                        {
                            { "status", "error" },
                            { "code", "invalid_header" },
                            { "file_id", id.FileId.ToString() },
                            { "page_index", id.PageIndex.ToString() },
                        });
                    throw new PageHeaderInvalidException(id, "invalid_header_prewrite");
                }
                ValidateHeader(id, current, expectedType, expectedVersion);
                ValidateHeaderPhysical(id, current, readLease.Page.Length);
            }
            finally
            {
                await readLease.DisposeAsync().ConfigureAwait(false);
            }

            // 2) Exclusive latch on target range
            var w = await _pool.AcquireWriteAsync(id, offset, length, ct).ConfigureAwait(false);
            try
            {
                if (w.Slice.Length != length)
                    throw new ArgumentOutOfRangeException(nameof(length), "Write lease size mismatch.");

                // 2a) Re-parse header under exclusive lease if header is included
                PageHeader headerUnderWrite = current;
                bool headerLocked = (offset == 0 && length >= PageHeader.HeaderSize);
                if (headerLocked)
                {
                    if (!TryReadHeader(w.Slice, out headerUnderWrite))
                    {
                        RecordError("invalid_header");
                        PageAccessDiagnostics.Emit(
                            "Silica.PageAccess", "WriteAndStampAsync", "error", "invalid_page_header_under_write", null,
                            new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                            {
                                { "status", "error" },
                                { "code", "invalid_header" },
                                { "file_id", id.FileId.ToString() },
                                { "page_index", id.PageIndex.ToString() },
                            });
                        throw new PageHeaderInvalidException(id, "invalid_header_under_write");
                    }
                    ValidateHeader(id, headerUnderWrite, expectedType, expectedVersion);
                }
                else if (_options.RequireHeaderRevalidationOnWrite)
                {
                    RecordError("header_not_revalidated");
                    PageAccessDiagnostics.Emit(
                        "Silica.PageAccess", "WriteAndStampAsync", "error", "header_not_revalidated_requires_header_lock", null,
                        new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                        {
                            { "status", "error" },
                            { "code", "header_not_revalidated" },
                            { "file_id", id.FileId.ToString() },
                            { "page_index", id.PageIndex.ToString() },
                        });
                    throw new PageHeaderInvalidException("Header revalidation required; include header region in the write lease.");
                }
                else
                {
                    RecordWarn("header_not_revalidated");
                    PageAccessDiagnostics.Emit(
                        "Silica.PageAccess", "WriteAndStampAsync", "warn", "header_not_revalidated", null,
                        new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                        {
                            { "status", "warn" },
                            { "code", "header_not_revalidated" },
                            { "file_id", id.FileId.ToString() },
                            { "page_index", id.PageIndex.ToString() },
                        });
                }

                // 3) Mutate in-place under the exclusive latch
                var mutatedHeader = mutate(w.Slice, headerUnderWrite);

                // 4) Capture after-image of the range (copy once, bounded by 'length')
                var afterImage = new byte[length];
                w.Slice.Span.CopyTo(afterImage.AsSpan());

                // 5) Append WAL / get LSN via hook (enforce WAL-before-write)
                ulong lsn = 0;
                if (_walHook != null)
                {
                    lsn = await _walHook.OnBeforePageWriteAsync(id, offset, length, afterImage, ct).ConfigureAwait(false);
                }
                else
                {
                    // No hook installed: still proceed, but signal via warn metric/trace.
                    RecordWarn("wal_hook_missing");
                    PageAccessDiagnostics.Emit(
                        "Silica.PageAccess", "WriteAndStampAsync", "warn", "wal_hook_missing", null,
                        new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                        {
                            { "status", "warn" },
                            { "code", "wal_hook_missing" },
                            { "file_id", id.FileId.ToString() },
                            { "page_index", id.PageIndex.ToString() },
                        });
                }

                // 6) Stamp header LSN and re-write header under the same exclusive latch if headerLocked
                //    If header wasn't part of the range, acquire a short exclusive header lease to stamp LSN.
                if (lsn != 0)
                {
                    mutatedHeader = mutatedHeader.With(lsn: lsn);
                }

                if (headerLocked)
                {
                    WriteHeaderToMemory(w.Slice, _options.UpdateChecksumOnWrite ? mutatedHeader.With(checksum: 0) : mutatedHeader);
                    if (lsn != 0)
                    {
                        // We modified the header via the main write lease 'w'
                        w.MarkDirty((long)lsn);
                    }
                }
                else
                {
                    // Acquire a short exclusive lease on [0..HeaderSize) to stamp LSN safely.
                    var hdrLease = await _pool.AcquireWriteAsync(id, 0, PageHeader.HeaderSize, ct).ConfigureAwait(false);
                    try
                    {
                        if (!TryReadHeader(hdrLease.Slice, out var hdrNow))
                            throw new PageHeaderInvalidException(id, "invalid_header_lsn_stamp");
                        ValidateHeader(id, hdrNow, expectedType, expectedVersion);
                        var hdrStamped = mutatedHeader;
                        if (lsn != 0) hdrStamped = hdrStamped.With(lsn: lsn);
                        WriteHeaderToMemory(hdrLease.Slice, _options.UpdateChecksumOnWrite ? hdrStamped.With(checksum: 0) : hdrStamped);
                        if (lsn != 0)
                        {
                            // We modified the header via the short header lease
                            hdrLease.MarkDirty((long)lsn);
                        }
                    }
                    finally
                    {
                        await hdrLease.DisposeAsync().ConfigureAwait(false);
                    }
                }

                // The write lease remains held until PageWriteHandle is disposed by the caller.
                RecordWrite(mutatedHeader.Type, Stopwatch.GetTimestamp());
                return mutatedHeader;
            }
            finally
            {
                await w.DisposeAsync().ConfigureAwait(false);
            }
        }

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
                PageAccessDiagnostics.Emit(
                    "Silica.PageAccess", "ReadAsync", "warn",
                    $"operation_canceled: {id.FileId}/{id.PageIndex}", null,
                    new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                    {
                        { "status", "warn" },
                        { "code", "canceled" },
                        { "file_id", id.FileId.ToString() },
                        { "page_index", id.PageIndex.ToString() },
                    });
                throw;
            }

            try
            {
                if (!TryReadHeader(lease.Page, out var hdr))
                {
                    RecordError("invalid_header");
                    PageAccessDiagnostics.Emit(
                        "Silica.PageAccess", "ReadAsync", "error",
                        $"invalid_page_header: {id.FileId}/{id.PageIndex}", null,
                        new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                        {
                            { "status", "error" },
                            { "code", "invalid_header" },
                            { "file_id", id.FileId.ToString() },
                            { "page_index", id.PageIndex.ToString() },
                        });
                    throw new PageHeaderInvalidException(id, $"invalid_header(len={lease.Page.Length})");
                }

                ValidateHeader(id, hdr, expectedType, expectedVersion);
                ValidateHeaderPhysical(id, hdr, lease.Page.Length);

                if (_options.VerifyChecksumOnRead &&
                    !(hdr.Checksum == 0 && _options.SkipChecksumVerifyIfZero))
                {
                    if (!VerifyChecksum(lease.Page.Span, out var expected, out var actual))
                    {
                        RecordError("checksum_mismatch");
                        PageAccessDiagnostics.Emit(
                            "Silica.PageAccess", "ReadAsync", "error",
                            $"checksum_mismatch: {expected:X8}!={actual:X8} id={id.FileId}/{id.PageIndex}", null,
                            new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                            {
                                { "status", "error" },
                                { "code", "checksum_mismatch" },
                                { "file_id", id.FileId.ToString() },
                                { "page_index", id.PageIndex.ToString() },
                            });
                        throw new PageChecksumMismatchException(id, expected, actual);
                    }
                }
                else if (_options.VerifyChecksumOnRead && hdr.Checksum == 0 && _options.SkipChecksumVerifyIfZero)
                {
                    // Operational visibility: we deliberately skipped verification due to policy and zero checksum.
                    RecordWarn("checksum_skipped_zero");
                    PageAccessDiagnostics.Emit(
                        "Silica.PageAccess", "ReadAsync", "info",
                        $"checksum_verification_skipped_zero: {id.FileId}/{id.PageIndex}", null,
                        new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                        {
                            { "status", "info" },
                            { "code", "checksum_skipped_zero" },
                            { "file_id", id.FileId.ToString() },
                            { "page_index", id.PageIndex.ToString() },
                        });
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
                    PageAccessDiagnostics.Emit(
                        "Silica.PageAccess", "WriteAsync", "error",
                        $"invalid_page_header: {id.FileId}/{id.PageIndex}", null,
                        new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                        {
                            { "status", "error" },
                            { "code", "invalid_header" },
                            { "file_id", id.FileId.ToString() },
                            { "page_index", id.PageIndex.ToString() },
                        });
                    throw new PageHeaderInvalidException(id, $"invalid_header(len={readLease.Page.Length})");
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
                            PageAccessDiagnostics.Emit(
                                "Silica.PageAccess", "WriteAsync", "error",
                                $"invalid_page_header_under_write: {id.FileId}/{id.PageIndex}", null,
                                new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                                {
                                    { "status", "error" },
                                    { "code", "invalid_header" },
                                    { "file_id", id.FileId.ToString() },
                                    { "page_index", id.PageIndex.ToString() },
                                });
                            throw new PageHeaderInvalidException(id, "invalid_header_under_write");
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
                            PageAccessDiagnostics.Emit(
                                "Silica.PageAccess", "WriteAsync", "error",
                                $"header_not_revalidated_requires_header_lock: {id.FileId}/{id.PageIndex}", null,
                                new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                                {
                                    { "status", "error" },
                                    { "code", "header_not_revalidated" },
                                    { "file_id", id.FileId.ToString() },
                                    { "page_index", id.PageIndex.ToString() },
                                });
                            throw new PageHeaderInvalidException("Header revalidation required on write. Include header region [0..HeaderSize) in the write lease.");
                        }
                        else
                        {
                            RecordWarn("header_not_revalidated");
                            PageAccessDiagnostics.Emit(
                                "Silica.PageAccess", "WriteAsync", "warn",
                                $"header_not_revalidated: {id.FileId}/{id.PageIndex}", null,
                                new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                                {
                                    { "status", "warn" },
                                    { "code", "header_not_revalidated" },
                                    { "file_id", id.FileId.ToString() },
                                    { "page_index", id.PageIndex.ToString() },
                                });
                            // Optional: highlight that no LSN will be stamped unless caller uses WriteAndStampAsync.
                            if (_walHook != null)
                            {
                                RecordWarn("wal_stamp_unavailable_in_plain_write");
                                PageAccessDiagnostics.Emit(
                                    "Silica.PageAccess", "WriteAsync", "info",
                                    $"use WriteAndStampAsync for ARIES-compliant LSN stamping: {id.FileId}/{id.PageIndex}", null,
                                    new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                                    {
                                        { "status", "info" },
                                        { "code", "advise_use_write_and_stamp" },
                                        { "file_id", id.FileId.ToString() },
                                        { "page_index", id.PageIndex.ToString() },
                                    });
                            }
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
                PageAccessDiagnostics.Emit(
                    "Silica.PageAccess", "WriteAsync", "warn",
                    $"operation_canceled: {id.FileId}/{id.PageIndex}", null,
                    new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                    {
                        { "status", "warn" },
                        { "code", "canceled" },
                        { "file_id", id.FileId.ToString() },
                        { "page_index", id.PageIndex.ToString() },
                    });
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
                    PageAccessDiagnostics.Emit(
                        "Silica.PageAccess", "WriteHeaderAsync", "error",
                        $"invalid_page_header: {id.FileId}/{id.PageIndex}", null,
                        new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                        {
                            { "status", "error" },
                            { "code", "invalid_header" },
                            { "file_id", id.FileId.ToString() },
                            { "page_index", id.PageIndex.ToString() },
                        });
                    throw new PageHeaderInvalidException(id, "invalid_header_header_write");
                }

                ValidateHeader(id, hdr, expectedType, expectedVersion);
                // For header-only mutations, we cannot validate FreeSpaceOffset upper bound (page length unknown here),
                // but we can ensure the lower bound is respected.
                ValidateHeaderPhysicalLowerBoundOnly(id, hdr);

                var updated = mutate(hdr);
                // WAL-before-write: append header after-image (HeaderSize) and stamp LSN
                ulong lsn = 0;
                if (_walHook != null)
                {
                    var afterHdr = new byte[PageHeader.HeaderSize];
                    // Write the would-be header (checksum policy applied later) into a temp buffer to log as after-image
                    var tmp = new Memory<byte>(afterHdr, 0, PageHeader.HeaderSize);
                    PageHeader.Write(tmp.Span, _options.UpdateChecksumOnWrite ? updated.With(checksum: 0) : updated);
                    lsn = await _walHook.OnBeforePageWriteAsync(id, 0, PageHeader.HeaderSize, afterHdr, ct).ConfigureAwait(false);
                    updated = updated.With(lsn: lsn);
                }
                else
                {
                    RecordWarn("wal_hook_missing");
                    PageAccessDiagnostics.Emit(
                        "Silica.PageAccess", "WriteHeaderAsync", "warn",
                        $"wal_hook_missing: {id.FileId}/{id.PageIndex}", null,
                        new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                        {
                            { "status", "warn" },
                            { "code", "wal_hook_missing" },
                            { "file_id", id.FileId.ToString() },
                            { "page_index", id.PageIndex.ToString() },
                        });
                }

                // Ensure the mutated header still respects physical invariants.
                ValidateHeader(id, updated, expectedType, expectedVersion);
                ValidateHeaderPhysicalLowerBoundOnly(id, updated);

                // If policy requests checksum update on header writes, zero-out checksum field so a later full-page recompute is correct.
                var writeBack = _options.UpdateChecksumOnWrite ? updated.With(checksum: 0) : updated;
                if (_options.UpdateChecksumOnWrite)
                {
                    PageAccessDiagnostics.Emit(
                        "Silica.PageAccess", "WriteHeaderAsync", "info",
                        $"checksum_zeroed: {id.FileId}/{id.PageIndex}", null,
                        new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                        {
                            { "status", "info" },
                            { "code", "checksum_zeroed" },
                            { "file_id", id.FileId.ToString() },
                            { "page_index", id.PageIndex.ToString() },
                        });
                }
                WriteHeaderToMemory(lease.Slice, writeBack);
                if (lsn != 0)
                {
                    // Header-only mutation; mark this page dirty with the stamped pageLSN
                    lease.MarkDirty((long)lsn);
                }

                RecordHeaderWrite(updated.Type, start);
                return updated;
            }
            catch (OperationCanceledException)
            {
                RecordWarn("canceled");
                PageAccessDiagnostics.Emit(
                    "Silica.PageAccess", "WriteHeaderAsync", "warn",
                    $"operation_canceled: {id.FileId}/{id.PageIndex}", null,
                    new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                    {
                        { "status", "warn" },
                        { "code", "canceled" },
                        { "file_id", id.FileId.ToString() },
                        { "page_index", id.PageIndex.ToString() },
                    });
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
                PageAccessDiagnostics.Emit(
                    "Silica.PageAccess", "ValidateHeader", "warn",
                    $"lsn_zero: {id.FileId}/{id.PageIndex}", null,
                    new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                    {
                        { "status", "warn" },
                        { "code", "lsn_zero" },
                        { "file_id", id.FileId.ToString() },
                        { "page_index", id.PageIndex.ToString() },
                    });
                // optionally throw or warn
            }

            if (hdr.Magic != _options.Magic)
            {
                RecordError("bad_magic");
                PageAccessDiagnostics.Emit(
                    "Silica.PageAccess", "ValidateHeader", "error",
                    $"magic_mismatch: {id.FileId}/{id.PageIndex}", null,
                    new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                    {
                        { "status", "error" },
                        { "code", "bad_magic" },
                        { "file_id", id.FileId.ToString() },
                        { "page_index", id.PageIndex.ToString() },
                    });
                throw new MagicMismatchException(id, _options.Magic, hdr.Magic);
            }

            if (expectedType != PageType.Unknown && hdr.Type != expectedType)
            {
                RecordError("type_mismatch");
                PageAccessDiagnostics.Emit(
                    "Silica.PageAccess", "ValidateHeader", "error",
                    $"type_mismatch: {id.FileId}/{id.PageIndex}", null,
                    new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                    {
                        { "status", "error" },
                        { "code", "type_mismatch" },
                        { "file_id", id.FileId.ToString() },
                        { "page_index", id.PageIndex.ToString() },
                    });
                throw new PageTypeMismatchException(id, expectedType, hdr.Type);
            }

            // PageType handling: Unknown is allowed only if explicitly expected, otherwise reject.
            if (hdr.Type == PageType.Unknown)
            {
                if (expectedType == PageType.Unknown)
                {
                    RecordWarn("type_unknown");
                    PageAccessDiagnostics.Emit(
                        "Silica.PageAccess", "ValidateHeader", "warn",
                        $"page_type_unknown: {id.FileId}/{id.PageIndex}", null,
                        new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                        {
                            { "status", "warn" },
                            { "code", "type_unknown" },
                            { "file_id", id.FileId.ToString() },
                            { "page_index", id.PageIndex.ToString() },
                        });
                }
                else
                {
                    RecordError("type_unexpected_unknown");
                    PageAccessDiagnostics.Emit(
                        "Silica.PageAccess", "ValidateHeader", "error",
                        $"unexpected_unknown_type: {id.FileId}/{id.PageIndex}", null,
                        new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                        {
                            { "status", "error" },
                            { "code", "type_unexpected_unknown" },
                            { "file_id", id.FileId.ToString() },
                            { "page_index", id.PageIndex.ToString() },
                        });
                    throw new PageHeaderInvalidException(id, "unexpected_unknown_type");
                }
            }
            else if (expectedType == PageType.Unknown && !IsKnownPageType(hdr.Type))
            {
                RecordError("type_invalid");
                PageAccessDiagnostics.Emit(
                    "Silica.PageAccess", "ValidateHeader", "error",
                    $"invalid_page_type: {id.FileId}/{id.PageIndex}", null,
                    new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                    {
                        { "status", "error" },
                        { "code", "type_invalid" },
                        { "file_id", id.FileId.ToString() },
                        { "page_index", id.PageIndex.ToString() },
                    });
                throw new PageHeaderInvalidException(id, $"invalid_type:{hdr.Type}");
            }

            if (hdr.Version.Major != expectedVersion.Major)
            {
                RecordError("major_version");
                PageAccessDiagnostics.Emit(
                    "Silica.PageAccess", "ValidateHeader", "error",
                    $"major_version_mismatch: {id.FileId}/{id.PageIndex}", null,
                    new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                    {
                        { "status", "error" },
                        { "code", "major_version" },
                        { "file_id", id.FileId.ToString() },
                        { "page_index", id.PageIndex.ToString() },
                    });
                throw new PageVersionMismatchException(id, expectedVersion, hdr.Version, "major_mismatch");
            }

            if (hdr.Version.Minor < expectedVersion.Minor && !_options.AllowMinorUpgrade)
            {
                RecordError("minor_version");
                PageAccessDiagnostics.Emit(
                    "Silica.PageAccess", "ValidateHeader", "error",
                    $"minor_version_too_old: {id.FileId}/{id.PageIndex}", null,
                    new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                    {
                        { "status", "error" },
                        { "code", "minor_version" },
                        { "file_id", id.FileId.ToString() },
                        { "page_index", id.PageIndex.ToString() },
                    });
                throw new PageVersionMismatchException(id, expectedVersion, hdr.Version, "minor_too_old");
            }

            if (!_options.AllowFutureMinor && hdr.Version.Minor > expectedVersion.Minor)
            {
                RecordError("minor_future");
                PageAccessDiagnostics.Emit(
                    "Silica.PageAccess", "ValidateHeader", "error",
                    $"minor_version_too_new: {id.FileId}/{id.PageIndex}", null,
                    new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                    {
                        { "status", "error" },
                        { "code", "minor_future" },
                        { "file_id", id.FileId.ToString() },
                        { "page_index", id.PageIndex.ToString() },
                    });
                throw new PageVersionMismatchException(id, expectedVersion, hdr.Version, "minor_too_new");
            }

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ValidateHeaderPhysical(PageId id, in PageHeader hdr, int pageLength)
        {
            // Basic page length sanity
            if (pageLength < PageHeader.HeaderSize)
            {
                RecordError("page_too_small");
                PageAccessDiagnostics.Emit(
                    "Silica.PageAccess", "ValidateHeaderPhysical", "error",
                    $"page_too_small: {id.FileId}/{id.PageIndex}", null,
                    new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                    {
                        { "status", "error" },
                        { "code", "page_too_small" },
                        { "file_id", id.FileId.ToString() },
                        { "page_index", id.PageIndex.ToString() },
                    });
                throw new PageHeaderInvalidException(id, $"page_too_small(len={pageLength}, required={PageHeader.HeaderSize})");
            }
            // FreeSpaceOffset must be within [HeaderSize..pageLength]
            if (hdr.FreeSpaceOffset < PageHeader.HeaderSize || hdr.FreeSpaceOffset > pageLength)
            {
                RecordError("free_offset_range");
                PageAccessDiagnostics.Emit(
                    "Silica.PageAccess", "ValidateHeaderPhysical", "error",
                    $"free_offset_out_of_range: {id.FileId}/{id.PageIndex}", null,
                    new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                    {
                        { "status", "error" },
                        { "code", "free_offset_range" },
                        { "file_id", id.FileId.ToString() },
                        { "page_index", id.PageIndex.ToString() },
                    });
                throw new PageHeaderInvalidException(id, $"free_offset_out_of_range(offset={hdr.FreeSpaceOffset}, pageLen={pageLength})");
            }

            // PageType validity handled in ValidateHeader via IsKnownPageType when unconstrained
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ValidateHeaderPhysicalLowerBoundOnly(PageId id, in PageHeader hdr)
        {
            if (hdr.FreeSpaceOffset < PageHeader.HeaderSize)
            {
                RecordError("free_offset_lt_header");
                PageAccessDiagnostics.Emit(
                    "Silica.PageAccess", "ValidateHeaderPhysicalLowerBoundOnly", "error",
                    $"free_offset_lt_header: {id.FileId}/{id.PageIndex}", null,
                    new Dictionary<string, string>(4, StringComparer.OrdinalIgnoreCase)
                    {
                        { "status", "error" },
                        { "code", "free_offset_lt_header" },
                        { "file_id", id.FileId.ToString() },
                        { "page_index", id.PageIndex.ToString() },
                    });
                throw new PageHeaderInvalidException(id, $"free_offset_lt_header(offset={hdr.FreeSpaceOffset}, required>={PageHeader.HeaderSize})");
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
                PageAccessDiagnostics.Emit(
                    "Silica.PageAccess", "TryReadHeader", "error",
                    "Failed to parse page header", null,
                    new Dictionary<string, string>(2, StringComparer.OrdinalIgnoreCase)
                    {
                        { "status", "error" },
                        { "code", "header_parse_failed" },
                    });
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
                PageAccessDiagnostics.Emit(
                    "Silica.PageAccess", "DisposeAsync", "warn",
                    $"flush_all_failed: {ex.Message}", ex,
                    new Dictionary<string, string>(2, StringComparer.OrdinalIgnoreCase)
                    {
                        { "status", "warn" },
                        { "code", "flush_failed" },
                    });
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

        // Inline TryEmitTrace removed; all trace emissions now go through PageAccessDiagnostics.Emit.

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
