using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Silica.Storage;
using Silica.Storage.Encryption;
using Silica.Storage.Devices;
using Silica.Storage.Exceptions;
using Silica.Storage.Encryption.Exceptions;
using Silica.Storage.Interfaces;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;
using Silica.DiagnosticsCore.Tracing;
using Silica.Storage.Encryption.Metrics;

namespace Silica.Storage.Encryption.Testing
{
    public static class EncryptionTestHarness
    {
        private const string Component = "EncryptionHarness";

        private static void Emit(string op, string level, string message, Exception? ex = null, IReadOnlyDictionary<string, string>? more = null)
        {
            if (!DiagnosticsCoreBootstrap.IsStarted) return;
            TraceManager traces;
            try { traces = DiagnosticsCoreBootstrap.Instance.Traces; }
            catch { return; }

            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                tags[TagKeys.Operation] = op;
                if (more is not null)
                {
                    foreach (var kv in more) tags[kv.Key] = kv.Value;
                }
            }
            catch { }
            try { traces.Emit(Component, op, level, tags, message, ex); } catch { }
        }

        public static async Task RunAsync(CancellationToken token = default)
        {
            Console.WriteLine("=== Encryption Test Harness ===");
            Emit("RunAsync", "info", "harness_start");

            try
            {
                // Fixed key/salt for deterministic tests
                var key = new byte[32];
                for (int i = 0; i < key.Length; i++) key[i] = (byte)i;

                var salt = new byte[16];
                for (int i = 0; i < salt.Length; i++) salt[i] = (byte)(0xA0 + i);

                var provider = new FixedKeyProvider(key, salt);
                Emit("Setup", "debug", "fixed_key_and_salt_ready");

                await TestInMemoryAsync(provider, token);
                await TestLoopbackAsync(provider, token);
                await TestPhysicalBlockAsync(provider, token);
                await TestStreamAsync(provider, token);

                Console.WriteLine("=== Encryption Test Harness Complete ===");
                Emit("RunAsync", "info", "harness_complete");
            }
            catch (Exception ex)
            {
                Emit("RunAsync", "error", "harness_failed", ex);
                throw;
            }
        }

        private static async Task TestInMemoryAsync(IEncryptionKeyProvider provider, CancellationToken token)
        {
            Console.WriteLine("\n--- Testing InMemoryDevice ---");
            Emit("InMemoryDevice", "debug", "test_begin");

            var baseDevice = new InMemoryDevice();
            SetExpectedManifest(baseDevice, baseDevice.Geometry);

            int blockSize = baseDevice.Geometry.LogicalBlockSize;

            await using var encDevice = new EncryptionDevice(baseDevice, provider);
            try
            {
                await encDevice.MountAsync(token);
                Emit("InMemoryDevice", "debug", "mounted");

                await RunCoreFrameTests(encDevice, baseDevice, blockSize, token);
                await RunExtendedTests(nameof(InMemoryDevice), encDevice, baseDevice, provider, blockSize, token, skipPersistence: true);

                Emit("InMemoryDevice", "info", "test_complete");
            }
            catch (Exception ex)
            {
                Emit("InMemoryDevice", "error", "test_failed", ex);
                throw;
            }
            finally
            {
                try { await encDevice.UnmountAsync(token); Emit("InMemoryDevice", "debug", "unmounted"); } catch (Exception ex) { Emit("InMemoryDevice", "error", "unmount_failed", ex); }
            }
        }

        private static async Task TestLoopbackAsync(IEncryptionKeyProvider provider, CancellationToken token)
        {
            Console.WriteLine("\n--- Testing LoopbackDevice ---");
            Emit("LoopbackDevice", "debug", "test_begin");

            var baseDevice = new LoopbackDevice(frameSize: 4096);
            int blockSize = baseDevice.Geometry.LogicalBlockSize;

            await using var encDevice = new EncryptionDevice(baseDevice, provider);
            try
            {
                await encDevice.MountAsync(token);
                Emit("LoopbackDevice", "debug", "mounted");

                await RunCoreFrameTests(encDevice, baseDevice, blockSize, token);
                await RunExtendedTests(nameof(LoopbackDevice), encDevice, baseDevice, provider, blockSize, token, skipPersistence: true);

                Emit("LoopbackDevice", "info", "test_complete");
            }
            catch (Exception ex)
            {
                Emit("LoopbackDevice", "error", "test_failed", ex);
                throw;
            }
            finally
            {
                try { await encDevice.UnmountAsync(token); Emit("LoopbackDevice", "debug", "unmounted"); } catch (Exception ex) { Emit("LoopbackDevice", "error", "unmount_failed", ex); }
            }
        }

        private static async Task TestPhysicalBlockAsync(IEncryptionKeyProvider provider, CancellationToken token)
        {
            Console.WriteLine("\n--- Testing PhysicalBlockDevice ---");
            Emit("PhysicalBlockDevice", "debug", "test_begin");

            string dir = @"c:\temp\";
            try { System.IO.Directory.CreateDirectory(dir); } catch { }
            string path = Path.Combine(dir, "enc_phys_dev.dat");

            if (File.Exists(path)) File.Delete(path);

            var baseDevice = new PhysicalBlockDevice(path);
            SetExpectedManifest(baseDevice, baseDevice.Geometry);

            int blockSize = baseDevice.Geometry.LogicalBlockSize;

            await using var encDevice = new EncryptionDevice(baseDevice, provider);
            try
            {
                await encDevice.MountAsync(token);
                Emit("PhysicalBlockDevice", "debug", "mounted", null, new Dictionary<string, string> { { TagKeys.Field, "path" }, { "path", path } });

                await RunCoreFrameTests(encDevice, baseDevice, blockSize, token);
                await RunExtendedTests(nameof(PhysicalBlockDevice), encDevice, baseDevice, provider, blockSize, token, persistencePath: path);

                Emit("PhysicalBlockDevice", "info", "test_complete");
            }
            catch (Exception ex)
            {
                Emit("PhysicalBlockDevice", "error", "test_failed", ex, new Dictionary<string, string> { { "path", path } });
                throw;
            }
            finally
            {
                try { await encDevice.UnmountAsync(token); Emit("PhysicalBlockDevice", "debug", "unmounted"); } catch (Exception ex) { Emit("PhysicalBlockDevice", "error", "unmount_failed", ex); }
            }
        }

        private static async Task TestStreamAsync(IEncryptionKeyProvider provider, CancellationToken token)
        {
            Console.WriteLine("\n--- Testing StreamDevice ---");
            Emit("StreamDevice", "debug", "test_begin");

            var ms = new MemoryStream();
            var baseDevice = new StreamDevice(ms);
            SetExpectedManifest(baseDevice, baseDevice.Geometry);

            int blockSize = baseDevice.Geometry.LogicalBlockSize;

            await using var encDevice = new EncryptionDevice(baseDevice, provider);
            try
            {
                await encDevice.MountAsync(token);
                Emit("StreamDevice", "debug", "mounted");

                await RunCoreFrameTests(encDevice, baseDevice, blockSize, token);
                await RunExtendedTests(nameof(StreamDevice), encDevice, baseDevice, provider, blockSize, token, skipPersistence: true);

                Emit("StreamDevice", "info", "test_complete");
            }
            catch (Exception ex)
            {
                Emit("StreamDevice", "error", "test_failed", ex);
                throw;
            }
            finally
            {
                try { await encDevice.UnmountAsync(token); Emit("StreamDevice", "debug", "unmounted"); } catch (Exception ex) { Emit("StreamDevice", "error", "unmount_failed", ex); }
            }
        }

        private static void SetExpectedManifest(IStorageDevice baseDevice, StorageGeometry geometry)
        {
            if (baseDevice is IStackManifestHost host)
            {
                const ulong uuidLo = 0x1122334455667788UL;
                const ulong uuidHi = 0x99AABBCCDDEEFF00UL;

                var manifest = new DeviceManifest(
                    logicalBlockSize: geometry.LogicalBlockSize,
                    uuidLo: uuidLo,
                    uuidHi: uuidHi,
                    entries: Array.Empty<DeviceManifest.Entry>());

                host.SetExpectedManifest(manifest);
                Emit("Manifest", "debug", "expected_manifest_set", null, new Dictionary<string, string> {
                    { "lbs", geometry.LogicalBlockSize.ToString() }
                });
            }
        }

        // ---------------- Core tests ----------------

        private static async Task RunCoreFrameTests(
            IStorageDevice encDevice,
            IStorageDevice baseDevice,
            int blockSize,
            CancellationToken token)
        {
            Emit("CoreFrameTests", "debug", "begin", null, new Dictionary<string, string> { { "block_size", blockSize.ToString() } });

            var pattern1 = MakePattern(blockSize, b => (byte)(b & 0xFF));
            var pattern2 = MakePattern(blockSize, b => (byte)((255 - b) & 0xFF));

            await encDevice.WriteFrameAsync(0, pattern1, token);
            Emit("CoreFrameTests", "debug", "wrote_frame", null, new Dictionary<string, string> { { "frame", "0" } });

            await encDevice.WriteFrameAsync(1, pattern2, token);
            Emit("CoreFrameTests", "debug", "wrote_frame", null, new Dictionary<string, string> { { "frame", "1" } });

            var buf = new byte[blockSize];
            await encDevice.ReadFrameAsync(0, buf, token);
            bool rt0 = pattern1.AsSpan().SequenceEqual(buf);
            Console.WriteLine($"[Frame0] Round-trip match: {rt0}");
            Emit("CoreFrameTests", rt0 ? "info" : "warn", "rt_frame0", null, new Dictionary<string, string> { { "match", rt0.ToString() } });

            await encDevice.ReadFrameAsync(1, buf, token);
            bool rt1 = pattern2.AsSpan().SequenceEqual(buf);
            Console.WriteLine($"[Frame1] Round-trip match: {rt1}");
            Emit("CoreFrameTests", rt1 ? "info" : "warn", "rt_frame1", null, new Dictionary<string, string> { { "match", rt1.ToString() } });

            var raw = new byte[blockSize];
            await baseDevice.ReadFrameAsync(0, raw, token);
            bool c0 = !pattern1.AsSpan().SequenceEqual(raw);
            Console.WriteLine($"[Frame0] Ciphertext differs from plaintext: {c0}");
            Emit("CoreFrameTests", "debug", "ciphertext_differs_plaintext_frame0", null, new Dictionary<string, string> { { "differs", c0.ToString() } });

            await baseDevice.ReadFrameAsync(1, raw, token);
            bool c1 = !pattern2.AsSpan().SequenceEqual(raw);
            Console.WriteLine($"[Frame1] Ciphertext differs from plaintext: {c1}");
            Emit("CoreFrameTests", "debug", "ciphertext_differs_plaintext_frame1", null, new Dictionary<string, string> { { "differs", c1.ToString() } });

            var pattern1b = MakePattern(blockSize, b => (byte)(b ^ 0xAA));
            // Note: This is a rewrite of frame 0. With AES-CTR and a fixed counter derivation per frame,
            // the keystream is reused. We surface this explicitly in diagnostics to make it visible in CI.
            await encDevice.WriteFrameAsync(0, pattern1b, token);
            Emit("CoreFrameTests", "warn", "rewrote_frame_without_nonce_rotation",
                null, new Dictionary<string, string> { { "frame", "0" } });

            await encDevice.ReadFrameAsync(1, buf, token);
            bool unaffected = pattern2.AsSpan().SequenceEqual(buf);
            Console.WriteLine($"[Frame1] Unaffected by Frame0 change: {unaffected}");
            Emit("CoreFrameTests", unaffected ? "info" : "warn", "frame1_unaffected_by_frame0_change", null, new Dictionary<string, string> { { "ok", unaffected.ToString() } });

            Emit("CoreFrameTests", "debug", "complete");
        }

        // ---------------- Extended tests ----------------

        private static async Task RunExtendedTests(
            string deviceName,
            IStorageDevice encDevice,
            IStorageDevice baseDevice,
            IEncryptionKeyProvider provider,
            int blockSize,
            CancellationToken token,
            bool skipPersistence = false,
            string? persistencePath = null)
        {
            Emit(deviceName, "debug", "extended_begin");

            await TestColdRead(encDevice, blockSize, token);
            await TestInvalidLengthWrite(encDevice, token);
            await TestMultiFrameSequence(encDevice, blockSize, token);
            await TestRandomizedFrames(encDevice, blockSize, token, frames: 8);
            await TestConcurrentOps(encDevice, blockSize, token);

            if (!skipPersistence && deviceName == nameof(PhysicalBlockDevice) && !string.IsNullOrEmpty(persistencePath))
            {
                await TestPersistenceWithRemountUsingNewBase(
                    persistencePath!, encDevice, provider, blockSize, token);

                await TestWrongKeyReadMismatchUsingNewBase(
                    persistencePath!, encDevice, provider, blockSize, token);
            }

            Emit(deviceName, "debug", "extended_complete");
        }

        private static async Task TestColdRead(IStorageDevice encDevice, int blockSize, CancellationToken token)
        {
            Emit("ColdRead", "debug", "begin");
            var buf = new byte[blockSize];
            try
            {
                await encDevice.ReadFrameAsync(99, buf, token);
                Console.WriteLine("[ColdRead] Unexpected success (frame 99)");
                Emit("ColdRead", "warn", "unexpected_success", null, new Dictionary<string, string> { { "frame", "99" } });
            }
            catch (DeviceReadOutOfRangeException)
            {
                Console.WriteLine("[ColdRead] Got expected STORAGE.READ_OUT_OF_RANGE");
                Emit("ColdRead", "info", "expected_read_out_of_range");
            }
            catch (Exception ex)
            {
                Emit("ColdRead", "error", "cold_read_failed", ex);
                throw;
            }
        }

        private static async Task TestInvalidLengthWrite(IStorageDevice encDevice, CancellationToken token)
        {
            Emit("InvalidLengthWrite", "debug", "begin");
            try
            {
                var wrong = new byte[17]; // wrong size intentionally
                await encDevice.WriteFrameAsync(0, wrong, token);
                Console.WriteLine("[InvalidLength] Unexpected success");
                Emit("InvalidLengthWrite", "warn", "unexpected_success");
            }
            catch (InvalidFrameLengthException)
            {
                Console.WriteLine("[InvalidLength] Write rejected as expected");
                Emit("InvalidLengthWrite", "info", "rejected_as_expected");
            }
            catch (Exception ex)
            {
                Emit("InvalidLengthWrite", "error", "write_failed", ex);
                throw;
            }
        }

        private static async Task TestMultiFrameSequence(IStorageDevice encDevice, int blockSize, CancellationToken token)
        {
            Emit("MultiFrame", "debug", "begin");

            var f0 = MakePattern(blockSize, b => (byte)(b));
            var f1 = MakePattern(blockSize, b => (byte)(b + 1));
            var f2 = MakePattern(blockSize, b => (byte)(b + 2));
            var f3 = MakePattern(blockSize, b => (byte)(b + 3));

            await encDevice.WriteFrameAsync(0, f0, token);
            await encDevice.WriteFrameAsync(1, f1, token);
            await encDevice.WriteFrameAsync(2, f2, token);
            await encDevice.WriteFrameAsync(3, f3, token);

            var buf = new byte[blockSize];
            bool ok =
                (await encDevice.ReadFrameAsync(0, buf, token)) == blockSize && buf.AsSpan().SequenceEqual(f0) &&
                (await encDevice.ReadFrameAsync(1, buf, token)) == blockSize && buf.AsSpan().SequenceEqual(f1) &&
                (await encDevice.ReadFrameAsync(2, buf, token)) == blockSize && buf.AsSpan().SequenceEqual(f2) &&
                (await encDevice.ReadFrameAsync(3, buf, token)) == blockSize && buf.AsSpan().SequenceEqual(f3);

            Console.WriteLine($"[MultiFrame] 0..3 sequence round-trip: {ok}");
            Emit("MultiFrame", ok ? "info" : "warn", "sequence_rt", null, new Dictionary<string, string> { { "ok", ok.ToString() } });
        }

        private static async Task TestRandomizedFrames(IStorageDevice encDevice, int blockSize, CancellationToken token, int frames)
        {
            Emit("Randomized", "debug", "begin", null, new Dictionary<string, string> { { "frames", frames.ToString() } });

            var rnd = new Random(1234);
            var src = new byte[blockSize];
            var dst = new byte[blockSize];

            bool ok = true;
            for (int i = 0; i < frames; i++)
            {
                rnd.NextBytes(src);
                await encDevice.WriteFrameAsync(i, src, token);
            }
            rnd = new Random(1234); // reseed to same
            for (int i = 0; i < frames; i++)
            {
                rnd.NextBytes(src);
                await encDevice.ReadFrameAsync(i, dst, token);
                if (!src.AsSpan().SequenceEqual(dst)) ok = false;
            }
            Console.WriteLine($"[Randomized] {frames} frames round-trip: {ok}");
            Emit("Randomized", ok ? "info" : "warn", "frames_rt", null, new Dictionary<string, string> { { "ok", ok.ToString() }, { "frames", frames.ToString() } });
        }

        private static async Task TestConcurrentOps(IStorageDevice encDevice, int blockSize, CancellationToken token)
        {
            Emit("Concurrent", "debug", "begin");

            var frames = 8;
            var tasks = new Task[frames];
            for (int i = 0; i < frames; i++)
            {
                int frameId = i;
                tasks[i] = Task.Run(async () =>
                {
                    var write = MakePattern(blockSize, b => (byte)((b + frameId) & 0xFF));
                    await encDevice.WriteFrameAsync(frameId, write, token);

                    var read = new byte[blockSize];
                    await encDevice.ReadFrameAsync(frameId, read, token);
                    if (!write.AsSpan().SequenceEqual(read))
                        throw new InvalidOperationException($"[Concurrent] Mismatch at frame {frameId}");
                }, token);
            }

            try
            {
                await Task.WhenAll(tasks);
                Console.WriteLine("[Concurrent] Parallel write/read across frames: OK");
                Emit("Concurrent", "info", "ok");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Concurrent] FAILED: " + ex.Message);
                Emit("Concurrent", "error", "failed", ex);
            }
        }

        // Persistence and wrong-key tests using a fresh base device to avoid touching the live base

        private static async Task TestPersistenceWithRemountUsingNewBase(
            string path,
            IStorageDevice encDeviceLive,
            IEncryptionKeyProvider provider,
            int blockSize,
            CancellationToken token)
        {
            Emit("Persistence", "debug", "begin", null, new Dictionary<string, string> { { "path", path } });
            Console.WriteLine("[Persistence] Remount with same key (fresh base)");

            try
            {
                // 1) Write via the live encryption device
                var pattern = MakePattern(blockSize, b => (byte)(0x5A ^ b));
                await encDeviceLive.WriteFrameAsync(5, pattern, token);
                Emit("Persistence", "debug", "wrote_frame5");

                // 2) Open a fresh base on the same file path
                var base2 = new PhysicalBlockDevice(path);
                SetExpectedManifest(base2, base2.Geometry);

                await using var enc2 = new EncryptionDevice(base2, provider);
                await enc2.MountAsync(token);
                Emit("Persistence", "debug", "mounted_new_base");

                // 3) Read and verify
                var buf = new byte[blockSize];
                await enc2.ReadFrameAsync(5, buf, token);
                bool ok = pattern.AsSpan().SequenceEqual(buf);
                Console.WriteLine($"[Persistence] Same-key match after remount: {ok}");
                Emit("Persistence", ok ? "info" : "warn", "same_key_match", null, new Dictionary<string, string> { { "ok", ok.ToString() } });
                try { EncryptionMetrics.OnDecryptCompleted(DiagnosticsCoreBootstrap.IsStarted ? DiagnosticsCoreBootstrap.Instance.Metrics : new NoOpMetricsManager(), 0.0, blockSize); } catch { }

                await enc2.UnmountAsync(token);
                Emit("Persistence", "debug", "unmounted_new_base");
            }
            catch (Exception ex)
            {
                Emit("Persistence", "error", "failed", ex, new Dictionary<string, string> { { "path", path } });
                throw;
            }
        }

        private static async Task TestWrongKeyReadMismatchUsingNewBase(
            string path,
            IStorageDevice encDeviceLive,
            IEncryptionKeyProvider rightProvider,
            int blockSize,
            CancellationToken token)
        {
            Emit("WrongKey", "debug", "begin", null, new Dictionary<string, string> { { "path", path } });
            Console.WriteLine("[WrongKey] Verifying mismatch on read with different key/salt (fresh base)");

            try
            {
                // 1) Write via the live encryption device with correct key
                var pattern = MakePattern(blockSize, b => (byte)(0xA5 ^ b));
                await encDeviceLive.WriteFrameAsync(7, pattern, token);
                Emit("WrongKey", "debug", "wrote_frame",
                    null, new Dictionary<string, string> { { "frame", "7" }, { "bytes", blockSize.ToString() } });

                // 2) Prepare a slightly different key/salt
                var badKey = new byte[rightProvider.GetKey().Length];
                rightProvider.GetKey().CopyTo(badKey);
                badKey[0] ^= 0xFF;

                var badSalt = new byte[16];
                rightProvider.GetDeviceSalt().CopyTo(badSalt);
                badSalt[0] ^= 0x01;

                // 3) Open a fresh base on the same path with wrong credentials
                var base2 = new PhysicalBlockDevice(path);
                SetExpectedManifest(base2, base2.Geometry);

                await using var wrongEnc = new EncryptionDevice(base2, new FixedKeyProvider(badKey, badSalt));
                await wrongEnc.MountAsync(token);
                Emit("WrongKey", "debug", "mounted_wrong");

                var buf = new byte[blockSize];
                await wrongEnc.ReadFrameAsync(7, buf, token);

                bool matches = pattern.AsSpan().SequenceEqual(buf);
                Console.WriteLine($"[WrongKey] Read equals plaintext with wrong key: {matches} (expected False)");
                // If matches==true, this is a failure
                Emit("WrongKey", matches ? "error" : "info", "mismatch_observed", null, new Dictionary<string, string> { { "matches", matches.ToString() } });
                try
                {
                    var mm = DiagnosticsCoreBootstrap.IsStarted
                        ? (IMetricsManager)DiagnosticsCoreBootstrap.Instance.Metrics
                        : new NoOpMetricsManager();
                    if (matches)
                    {
                        // Extremely unlikely: keystream collision/pathological case; treat as transform failure symptomatically.
                        EncryptionMetrics.IncrementTransformFailure(mm);
                    }
                    else
                    {
                        // Expected mismatch due to wrong key/salt
                        EncryptionMetrics.IncrementWrongKeyOrSalt(mm);
                    }
                }
                catch { }

                await wrongEnc.UnmountAsync(token);
                Emit("WrongKey", "debug", "unmounted_wrong");
            }
            catch (Exception ex)
            {
                Emit("WrongKey", "error", "failed", ex, new Dictionary<string, string> { { "path", path } });
                throw;
            }
        }

        // ---------------- Helpers ----------------

        private static byte[] MakePattern(int size, Func<int, byte> f)
        {
            var b = new byte[size];
            for (int i = 0; i < size; i++) b[i] = f(i);
            return b;
        }
    }
}
