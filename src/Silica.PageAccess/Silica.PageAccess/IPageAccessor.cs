using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Silica.BufferPool;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.PageAccess
{
    /// <summary>
    /// Contract for safe, validated access to physical pages.
    /// Responsibility boundaries:
    /// - Validates physical invariants (magic, version, type). Optionally verifies checksum.
    /// - Acquires and returns read/write leases (pins + latches) from the buffer pool.
    /// - Exposes parsed/writable headers and raw page/slice to higher layers (heap/index/PFS managers).
    /// - Provides header-only mutation convenience.
    /// It does NOT interpret payload semantics (slot directory, rows, keys) — that belongs to higher-level managers.
    /// Callers performing non-header writes must ensure header semantic fields (e.g., FreeSpaceOffset upper bound) remain valid for the actual page length.
    /// No LINQ, no reflection, no JSON, no third-party libs.
    /// </summary>
    public interface IPageAccessor
    {
        /// <summary>
        /// Acquire a read lease and return a handle with the parsed header and page content.
        /// Validates magic/type/version before returning; optional checksum verification is policy-driven (options).
        /// </summary>
        ValueTask<PageReadHandle> ReadAsync(PageId id, PageVersion expectedVersion, PageType expectedType, CancellationToken ct = default);

        /// <summary>
        /// Acquire a write lease over the specified byte range within the page after validating the header.
        /// This method first performs a short read-validate to avoid nested latch deadlocks, then acquires the write lease.
        /// </summary>
        ValueTask<PageWriteHandle> WriteAsync(PageId id, int offset, int length, PageVersion expectedVersion, PageType expectedType, CancellationToken ct = default);

        /// <summary>
        /// Acquire a write lease for the header region [0..HeaderSize), provide the parsed header to the caller-provided mutator,
        /// write the updated header back, and return the updated header value. Checksum update may be deferred by policy.
        /// </summary>
        ValueTask<PageHeader> WriteHeaderAsync(PageId id, PageVersion expectedVersion, PageType expectedType, Func<PageHeader, PageHeader> mutate, CancellationToken ct = default);

        /// <summary>
        /// Flush a single page to backing storage if dirty. Pass-through to buffer pool.
        /// </summary>
        Task FlushPageAsync(PageId id, CancellationToken ct = default);

        /// <summary>
        /// Flush all dirty pages. Pass-through to buffer pool.
        /// </summary>
        Task FlushAllAsync(CancellationToken ct = default);
    }


    /// <summary>
    /// Policy knobs for PageAccess.
    /// - Magic: mandatory invariant check.
    /// - VerifyChecksumOnRead: detect torn/corrupt reads early.
    /// - SkipChecksumVerifyIfZero: if true and header checksum is 0, verification is skipped (treated as deferred recompute).
    /// - UpdateChecksumOnWrite: if true, header writes will zero the checksum field (full-page recompute is left to higher layers).
    /// - AllowMinorUpgrade: permit reading older minor version pages (higher layers may upgrade).
    /// - AllowFutureMinor: if false, reject pages with a higher minor version than expected.
    /// - FlushOnDispose: if true, DisposeAsync triggers a best-effort FlushAll.
    /// - RequireHeaderRevalidationOnWrite: if true, WriteAsync must include [0..HeaderSize) and revalidate under exclusive lease.
    /// </summary>
    public readonly struct PageAccessorOptions
    {
        public readonly uint Magic;
        public readonly bool VerifyChecksumOnRead;
        public readonly bool UpdateChecksumOnWrite;
        public readonly bool AllowMinorUpgrade;
        public readonly bool AllowFutureMinor;
        public readonly bool SkipChecksumVerifyIfZero;
        public readonly bool FlushOnDispose;
        /// <summary>
        /// If true, WriteAsync requires the header region to be included and revalidated under the exclusive lease,
        /// closing the TOCTOU window for type/version/magic changes during writes.
        /// </summary>
        public readonly bool RequireHeaderRevalidationOnWrite;

        public PageAccessorOptions(
            uint magic,
            bool verifyChecksumOnRead,
            bool updateChecksumOnWrite,
            bool allowMinorUpgrade,
            bool skipChecksumVerifyIfZero,
            bool flushOnDispose = false,
            bool allowFutureMinor = true,
            bool requireHeaderRevalidationOnWrite = false)
        {
            if (magic == 0) throw new ArgumentOutOfRangeException(nameof(magic), "Magic must be non-zero.");
            Magic = magic;
            VerifyChecksumOnRead = verifyChecksumOnRead;
            UpdateChecksumOnWrite = updateChecksumOnWrite;
            AllowMinorUpgrade = allowMinorUpgrade;
            SkipChecksumVerifyIfZero = skipChecksumVerifyIfZero;
            FlushOnDispose = flushOnDispose;
            AllowFutureMinor = allowFutureMinor;
            RequireHeaderRevalidationOnWrite = requireHeaderRevalidationOnWrite;
        }

        public static PageAccessorOptions Default(uint magic)
            // Defaults: don’t zero checksum on header-only writes unless caller opts in,
            // and if checksum is zero, skip verification (treat as deferred).
            => new PageAccessorOptions(
                magic,
                verifyChecksumOnRead: true,
                updateChecksumOnWrite: false,
                allowMinorUpgrade: true,
                skipChecksumVerifyIfZero: true,
                flushOnDispose: false,
                allowFutureMinor: true,
                requireHeaderRevalidationOnWrite: false);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Supporting physical types — immutable, BCL-only, allocation-free.
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Semantic version for page layout. Major must match; minor may be upgradeable per policy.
    /// </summary>
    public readonly struct PageVersion : IEquatable<PageVersion>
    {
        public readonly ushort Major;
        public readonly ushort Minor;

        public PageVersion(ushort major, ushort minor)
        {
            Major = major;
            Minor = minor;
        }

        public bool Equals(PageVersion other) => Major == other.Major && Minor == other.Minor;
        public override bool Equals(object obj) => obj is PageVersion pv && Equals(pv);
        public override int GetHashCode() => (Major << 16) ^ Minor;
        public override string ToString() => Major + "." + Minor;
    }

    /// <summary>
    /// Stable page type identifiers. Low-cardinality, engine-wide constants.
    /// </summary>
    public enum PageType : byte
    {
        Unknown = 0,
        Header = 1,
        Catalog = 2,
        PFS = 3,
        GAM = 4,
        SGAM = 5,
        DCM = 6,
        IAM = 7,
        Data = 10,
        Index = 11,
        Lob = 12,
        Free = 254
    }

    /// <summary>
    /// Fixed 64-byte physical header, with explicit little-endian offsets.
    /// PageAccess enforces and writes these fields; higher layers decide values for semantic fields.
    /// </summary>
    public readonly struct PageHeader
    {
        // Offsets (bytes)
        public const int MagicOffset = 0;           // u32
        public const int ChecksumOffset = 4;        // u32
        public const int VersionMajorOffset = 8;    // u16
        public const int VersionMinorOffset = 10;   // u16
        public const int TypeOffset = 12;           // u8
        public const int FlagsOffset = 13;          // u8
        public const int FreeSpaceOffsetOffset = 14;// u16 (semantic; higher-level decides)
        public const int RowCountOffset = 16;       // u16 (semantic; higher-level decides)
        public const int LsnOffset = 18;            // u64 (critical for recovery; stamped prior to write)
        public const int NextFileIdOffset = 26;     // i64 (semantic; used for chaining)
        public const int NextPageIndexOffset = 34;  // i64 (semantic; used for chaining)
        public const int ReservedOffset = 42;       // 22 bytes reserved
        public const int HeaderSize = 64;

        // Flag bits
        public const byte FlagRowVersioning = 0x01;

        // Parsed fields
        public readonly uint Magic;
        public readonly uint Checksum;
        public readonly PageVersion Version;
        public readonly PageType Type;
        // Preserve all flag bits for forward-compat; expose convenience boolean.
        public readonly byte Flags;
        public bool HasRowVersioning => (Flags & FlagRowVersioning) != 0;
        public readonly ushort FreeSpaceOffset;
        public readonly ushort RowCount;
        public readonly ulong Lsn;
        public readonly long NextFileId;
        public readonly long NextPageIndex;


        public PageHeader(
            uint magic,
            uint checksum,
            PageVersion version,
            PageType type,
            bool hasRowVersioning,
            ushort freeSpaceOffset,
            ushort rowCount,
            ulong lsn,
            long nextFileId,
            long nextPageIndex)
        {
            Magic = magic;
            Checksum = checksum;
            Version = version;
            Type = type;
            Flags = hasRowVersioning ? FlagRowVersioning : (byte)0;
            FreeSpaceOffset = freeSpaceOffset;
            RowCount = rowCount;
            Lsn = lsn;
            NextFileId = nextFileId;
            NextPageIndex = nextPageIndex;
        }

        /// <summary>
        /// Constructor that accepts the raw flags byte for full forward-compatibility preservation.
        /// </summary>
        public PageHeader(
            uint magic,
            uint checksum,
            PageVersion version,
            PageType type,
            byte flagsRaw,
            ushort freeSpaceOffset,
            ushort rowCount,
            ulong lsn,
            long nextFileId,
            long nextPageIndex,
            bool useRawFlags)
        {
            // useRawFlags is a dummy parameter to disambiguate the overload.
            Magic = magic;
            Checksum = checksum;
            Version = version;
            Type = type;
            Flags = flagsRaw;
            FreeSpaceOffset = freeSpaceOffset;
            RowCount = rowCount;
            Lsn = lsn;
            NextFileId = nextFileId;
            NextPageIndex = nextPageIndex;
        }


        /// <summary>
        /// Parse header from the start of the given page span. Returns false if buffer too small.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryRead(ReadOnlySpan<byte> page, out PageHeader header)
        {
            if (page.Length < HeaderSize)
            {
                header = default;
                return false;
            }

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(page.Slice(MagicOffset, 4));
            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(page.Slice(ChecksumOffset, 4));
            var major = BinaryPrimitives.ReadUInt16LittleEndian(page.Slice(VersionMajorOffset, 2));
            var minor = BinaryPrimitives.ReadUInt16LittleEndian(page.Slice(VersionMinorOffset, 2));
            var type = (PageType)page[TypeOffset];
            var flags = page[FlagsOffset];
            var freeOff = BinaryPrimitives.ReadUInt16LittleEndian(page.Slice(FreeSpaceOffsetOffset, 2));
            var rowCnt = BinaryPrimitives.ReadUInt16LittleEndian(page.Slice(RowCountOffset, 2));
            var lsn = BinaryPrimitives.ReadUInt64LittleEndian(page.Slice(LsnOffset, 8));
            var nextFile = BinaryPrimitives.ReadInt64LittleEndian(page.Slice(NextFileIdOffset, 8));
            var nextIdx = BinaryPrimitives.ReadInt64LittleEndian(page.Slice(NextPageIndexOffset, 8));

            // Construct directly with raw flags to preserve unknown bits without post-processing.
            header = new PageHeader(
                magic,
                checksum,
                new PageVersion(major, minor),
                type,
                flags,
                freeOff,
                rowCnt,
                lsn,
                nextFile,
                nextIdx,
                useRawFlags: true);

            return true;
        }

        /// <summary>
        /// Write header fields to the given page span. Caller must ensure correct semantics and checksum policy.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write(Span<byte> page, in PageHeader header)
        {
            if (page.Length < HeaderSize)
                // Align with domain-specific physical invariants.
                throw new PageHeaderInvalidException(
                    $"Page too small for header write: len={page.Length}, required={HeaderSize}."
                );

            BinaryPrimitives.WriteUInt32LittleEndian(page.Slice(MagicOffset, 4), header.Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(page.Slice(ChecksumOffset, 4), header.Checksum);
            BinaryPrimitives.WriteUInt16LittleEndian(page.Slice(VersionMajorOffset, 2), header.Version.Major);
            BinaryPrimitives.WriteUInt16LittleEndian(page.Slice(VersionMinorOffset, 2), header.Version.Minor);
            page[TypeOffset] = (byte)header.Type;
            // Preserve all flags bits; HasRowVersioning is represented in Flags.
            page[FlagsOffset] = header.Flags;
            BinaryPrimitives.WriteUInt16LittleEndian(page.Slice(FreeSpaceOffsetOffset, 2), header.FreeSpaceOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(page.Slice(RowCountOffset, 2), header.RowCount);
            BinaryPrimitives.WriteUInt64LittleEndian(page.Slice(LsnOffset, 8), header.Lsn);
            BinaryPrimitives.WriteInt64LittleEndian(page.Slice(NextFileIdOffset, 8), header.NextFileId);
            BinaryPrimitives.WriteInt64LittleEndian(page.Slice(NextPageIndexOffset, 8), header.NextPageIndex);
        }

        /// <summary>
        /// Non-alloc With-style updater for immutable header.
        /// Only the provided parameters change; others preserved.
        /// </summary>
        public PageHeader With(
            uint? checksum = null,
            PageVersion? version = null,
            PageType? type = null,
            bool? hasRowVersioning = null,
            byte? flagsRaw = null,
            ushort? freeSpaceOffset = null,
            ushort? rowCount = null,
            ulong? lsn = null,
            long? nextFileId = null,
            long? nextPageIndex = null)
        {
            // Start from existing flags; apply raw override, then boolean overlay if specified.
            byte newFlags = flagsRaw ?? Flags;
            if (hasRowVersioning.HasValue)
            {
                if (hasRowVersioning.Value) newFlags = (byte)(newFlags | FlagRowVersioning);
                else newFlags = (byte)(newFlags & ~FlagRowVersioning);
            }
            // Construct once using the flagsRaw-aware constructor. No recursion.
            return new PageHeader(
                Magic,
                checksum ?? Checksum,
                version ?? Version,
                type ?? Type,
                newFlags,
                freeSpaceOffset ?? FreeSpaceOffset,
                rowCount ?? RowCount,
                lsn ?? Lsn,
                nextFileId ?? NextFileId,
                nextPageIndex ?? NextPageIndex,
                useRawFlags: true);
        }
    }

    /// <summary>
    /// Handle for a read lease. Holds the pin+shared latch via the underlying buffer pool lease until disposed.
    /// Carry the parsed header and a ReadOnlyMemory<byte> view.
    /// </summary>
    public sealed class PageReadHandle : IAsyncDisposable
    {
        private readonly PageReadLease _lease;

        public PageReadHandle(PageReadLease lease, PageHeader header)
        {
            _lease = lease;
            Header = header;
            Page = lease.Page;
        }

        public PageHeader Header { get; }
        public ReadOnlyMemory<byte> Page { get; }

        public ValueTask DisposeAsync() => _lease.DisposeAsync();
    }

    /// <summary>
    /// Handle for a write lease. Holds the pin+exclusive range latch until disposed.
    /// Slice is the requested writable region. Mutated flag is a hint for higher layers, not required by PageAccess.
    /// </summary>
    public sealed class PageWriteHandle : IAsyncDisposable
    {
        private readonly PageWriteLease _lease;

        public PageWriteHandle(PageWriteLease lease, PageHeader header, bool headerRevalidated)
        {
            _lease = lease;
            Header = header;
            Slice = lease.Slice;
            HeaderRevalidated = headerRevalidated;
        }

        public PageHeader Header { get; }
        public Memory<byte> Slice { get; }
        /// <summary>
        /// True if Header was parsed under the exclusive write lease (i.e., header region was locked).
        /// False if Header was parsed during the pre-check read lease.
        /// </summary>
        public bool HeaderRevalidated { get; }

        public ValueTask DisposeAsync() => _lease.DisposeAsync();
    }

    /// <summary>
    /// Lightweight checksum helper. Computes a 32-bit rolling checksum over the entire page
    /// with the checksum field treated as zero. BCL-only, no CRC dependency here.
    /// </summary>
    internal static class PageChecksum
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Compute(ReadOnlySpan<byte> page, int checksumOffset)
        {
            uint c = 2166136261u; // FNV-like seed
            int len = page.Length;
            for (int i = 0; i < len; i++)
            {
                byte b = page[i];
                if ((uint)(i - checksumOffset) < 4u) b = 0;
                c ^= b;
                c = (c << 5) | (c >> 27);
            }
            return c;
        }
    }

    // Removed legacy IEventLogger in favor of DiagnosticsCore traces.

}
