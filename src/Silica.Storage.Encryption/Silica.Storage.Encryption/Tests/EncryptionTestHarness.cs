using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Silica.Storage;
using Silica.Storage.Devices;
using Silica.Storage.Interfaces;
using Silica.Storage.Exceptions;

using Silica.Storage.Encryption;
using Silica.Storage.Encryption.Exceptions;
using Silica.Storage.Encryption.Metrics;

namespace Silica.Storage.Encryption.Testing
{
    public static class EncryptionTestHarness
    {
        public static async Task RunAsync(CancellationToken token = default)
        {
            Console.WriteLine("=== Encryption Test Harness (InMemory, Physical) ===");

            await TestInMemoryAsync(token);
            await TestPhysicalAsync(token);

            Console.WriteLine("=== Harness Complete ===");
        }

        // -------------------------------------------------------
        // InMemoryBlockDevice
        // -------------------------------------------------------

        private static async Task TestInMemoryAsync(CancellationToken token)
        {
            Console.WriteLine("\n--- InMemoryBlockDevice ---");

            var baseDevice = new InMemoryBlockDevice();
            var lbs = baseDevice.Geometry.LogicalBlockSize;

            // Key/salt (deterministic for tests)
            var key = MakeSeq(32, 0x00);
            var salt = MakeSeq(16, 0xA0);

            var provider = new FixedKeyProvider(key, salt);

            // Derivation config (epoch-based to allow future rotation)
            var cfg = new CounterDerivationConfig(
                CounterDerivationKind.SaltXorFrameId_Epoch_V1,
                version: 1,
                flags: 0,
                epoch: 1);

            // Persist config in manifest blob
            var encBlob = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                { CounterDerivationConfig.BlobKey, CounterDerivationConfig.Serialize(cfg) }
            };

            // Program base manifest (format-affecting)
            SetExpectedManifestWithEncryption(baseDevice, lbs, cfg, encBlob);

            // Wrap with encryption
            await using var enc = new EncryptionDevice(baseDevice, provider, cfg);

            try
            {
                await enc.MountAsync(token);

                await RunCoreFrameTests(enc, baseDevice, token);
                await RunExtendedBasic(enc, baseDevice, token);

                await enc.UnmountAsync(token);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[InMemory] FAILED: " + ex.Message);
                throw;
            }
        }

        // -------------------------------------------------------
        // PhysicalBlockDevice
        // -------------------------------------------------------

        private static async Task TestPhysicalAsync(CancellationToken token)
        {
            Console.WriteLine("\n--- PhysicalBlockDevice ---");

            var path = Path.Combine(@"c:\temp\", "silica_enc_phys.dat");
            try { if (File.Exists(path)) File.Delete(path); } catch { }

            var baseDevice = new PhysicalBlockDevice(path);
            var lbs = baseDevice.Geometry.LogicalBlockSize;

            // Key/salt (deterministic)
            var key = MakeSeq(32, 0x01);
            var salt = MakeSeq(16, 0xB0);

            var provider = new FixedKeyProvider(key, salt);

            var cfg = new CounterDerivationConfig(
                CounterDerivationKind.SaltXorFrameId_Epoch_V1,
                version: 1,
                flags: 0,
                epoch: 7);

            var encBlob = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                { CounterDerivationConfig.BlobKey, CounterDerivationConfig.Serialize(cfg) }
            };

            SetExpectedManifestWithEncryption(baseDevice, lbs, cfg, encBlob);

            await using var enc = new EncryptionDevice(baseDevice, provider, cfg);

            try
            {
                await enc.MountAsync(token);

                await RunCoreFrameTests(enc, baseDevice, token);
                await RunExtendedWithPersistence(path, provider, enc, baseDevice, token);

                await enc.UnmountAsync(token);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Physical] FAILED: " + ex.Message);
                throw;
            }
        }

        // -------------------------------------------------------
        // Tests
        // -------------------------------------------------------

        private static async Task RunCoreFrameTests(IStorageDevice enc, IStorageDevice baseDev, CancellationToken token)
        {
            int blockSize = baseDev.Geometry.LogicalBlockSize;
            Console.WriteLine($"[Core] LBS={blockSize}");

            // Prepare two patterns
            var f0 = MakePattern(blockSize, b => (byte)(b));
            var f1 = MakePattern(blockSize, b => (byte)(255 - b));

            // Write 2 frames
            await enc.WriteFrameAsync(0, f0, token);
            await enc.WriteFrameAsync(1, f1, token);

            // Round-trip verification
            var buf = new byte[blockSize];
            await enc.ReadFrameAsync(0, buf, token);
            bool ok0 = SpanEqual(f0, buf);
            Console.WriteLine($"[Core] Frame 0 round-trip: {ok0}");

            await enc.ReadFrameAsync(1, buf, token);
            bool ok1 = SpanEqual(f1, buf);
            Console.WriteLine($"[Core] Frame 1 round-trip: {ok1}");

            // Ciphertext differs from plaintext at base layer
            var raw = new byte[blockSize];
            try
            {
                // Read ciphertext directly from base (will throw if not written yet)
                await baseDev.ReadFrameAsync(0, raw, token);
                bool differs0 = !SpanEqual(f0, raw);
                Console.WriteLine($"[Core] Base ciphertext differs (frame 0): {differs0}");
            }
            catch (DeviceReadOutOfRangeException) { Console.WriteLine("[Core] Base frame 0 unreadable (EOF)"); }

            await enc.FlushAsync(token);
        }

        private static async Task RunExtendedBasic(IStorageDevice enc, IStorageDevice baseDev, CancellationToken token)
        {
            int blockSize = baseDev.Geometry.LogicalBlockSize;

            // Cold read beyond EOF
            var coldBuf = new byte[blockSize];
            bool gotEof = false;
            try
            {
                await enc.ReadFrameAsync(99, coldBuf, token);
            }
            catch (DeviceReadOutOfRangeException)
            {
                gotEof = true;
            }
            Console.WriteLine($"[Ext] Cold read beyond EOF => exception: {gotEof}");

            // Invalid length write
            bool invalidLengthRejected = false;
            try
            {
                var wrong = new byte[17];
                await enc.WriteFrameAsync(5, wrong, token);
            }
            catch (InvalidFrameLengthException)
            {
                invalidLengthRejected = true;
            }
            Console.WriteLine($"[Ext] Invalid frame length rejected: {invalidLengthRejected}");

            // Multi-frame sequence
            var f0 = MakePattern(blockSize, b => (byte)(b + 0));
            var f1 = MakePattern(blockSize, b => (byte)(b + 1));
            var f2 = MakePattern(blockSize, b => (byte)(b + 2));
            var f3 = MakePattern(blockSize, b => (byte)(b + 3));

            await enc.WriteFrameAsync(10, f0, token);
            await enc.WriteFrameAsync(11, f1, token);
            await enc.WriteFrameAsync(12, f2, token);
            await enc.WriteFrameAsync(13, f3, token);

            var buf = new byte[blockSize];
            bool seqOk = true;

            await enc.ReadFrameAsync(10, buf, token);
            if (!SpanEqual(f0, buf)) seqOk = false;
            await enc.ReadFrameAsync(11, buf, token);
            if (!SpanEqual(f1, buf)) seqOk = false;
            await enc.ReadFrameAsync(12, buf, token);
            if (!SpanEqual(f2, buf)) seqOk = false;
            await enc.ReadFrameAsync(13, buf, token);
            if (!SpanEqual(f3, buf)) seqOk = false;

            Console.WriteLine($"[Ext] Sequence 10..13 round-trip: {seqOk}");

            await enc.FlushAsync(token);
        }

        private static async Task RunExtendedWithPersistence(
            string path,
            IEncryptionKeyProvider provider,
            IStorageDevice encLive,
            IStorageDevice baseLive,
            CancellationToken token)
        {
            int blockSize = baseLive.Geometry.LogicalBlockSize;

            // Write via live device
            var pattern = MakePattern(blockSize, b => (byte)(0x5A ^ b));
            await encLive.WriteFrameAsync(20, pattern, token);
            await encLive.FlushAsync(token);

            // Open a fresh base on same file
            var base2 = new PhysicalBlockDevice(path);
            var cfg = new CounterDerivationConfig(CounterDerivationKind.SaltXorFrameId_Epoch_V1, 1, 0, 7);
            var encBlob = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                { CounterDerivationConfig.BlobKey, CounterDerivationConfig.Serialize(cfg) }
            };
            SetExpectedManifestWithEncryption(base2, blockSize, cfg, encBlob);

            await using var enc2 = new EncryptionDevice(base2, provider, cfg);
            await enc2.MountAsync(token);

            var buf = new byte[blockSize];
            await enc2.ReadFrameAsync(20, buf, token);
            bool ok = SpanEqual(pattern, buf);
            Console.WriteLine($"[Persist] Same-key remount round-trip: {ok}");

            await enc2.UnmountAsync(token);

            // Wrong key test
            var wrongKey = new byte[32];
            provider.GetKey().CopyTo(wrongKey);
            wrongKey[0] ^= 0xFF;

            var wrongSalt = new byte[16];
            provider.GetDeviceSalt().CopyTo(wrongSalt);
            wrongSalt[0] ^= 0x01;

            var wrongProvider = new FixedKeyProvider(wrongKey, wrongSalt);

            var baseWrong = new PhysicalBlockDevice(path);
            SetExpectedManifestWithEncryption(baseWrong, blockSize, cfg, encBlob);

            await using var wrongEnc = new EncryptionDevice(baseWrong, wrongProvider, cfg);
            await wrongEnc.MountAsync(token);

            var wrong = new byte[blockSize];
            await wrongEnc.ReadFrameAsync(20, wrong, token);
            bool equalsPlaintext = SpanEqual(pattern, wrong);
            Console.WriteLine($"[WrongKey] Wrong key yields plaintext match: {equalsPlaintext} (expected False)");

            await wrongEnc.UnmountAsync(token);
        }

        // -------------------------------------------------------
        // Manifest programming (format-affecting entry + blob)
        // -------------------------------------------------------

        private static void SetExpectedManifestWithEncryption(
            IStorageDevice baseDevice,
            int logicalBlockSize,
            in CounterDerivationConfig cfg,
            Dictionary<string, byte[]> blobs)
        {
            if (baseDevice is not IStackManifestHost host) return;

            const ulong uuidLo = 0x1122334455667788UL;
            const ulong uuidHi = 0x99AABBCCDDEEFF00UL;

            var entry = new DeviceManifest.Entry(
                kind: MiniDriverKind.Encryption,
                flags: 0,
                p1: 1,                       // algo id placeholder
                p2: (uint)cfg.Kind,
                p3: cfg.Version,
                p4: cfg.Epoch,
                blobs: blobs);

            var manifest = new DeviceManifest(
                logicalBlockSize: logicalBlockSize,
                uuidLo: uuidLo,
                uuidHi: uuidHi,
                entries: new[] { entry });

            host.SetExpectedManifest(manifest);
        }

        // -------------------------------------------------------
        // Helpers
        // -------------------------------------------------------

        private static byte[] MakeSeq(int len, byte start)
        {
            var b = new byte[len];
            for (int i = 0; i < len; i++) b[i] = (byte)(start + i);
            return b;
        }

        private static byte[] MakePattern(int size, Func<int, byte> f)
        {
            var b = new byte[size];
            for (int i = 0; i < size; i++) b[i] = f(i);
            return b;
        }

        private static bool SpanEqual(byte[] a, byte[] b)
        {
            if (a is null || b is null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
