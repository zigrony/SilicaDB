using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.Devices;
using SilicaDB.TestRunner;

namespace SilicaDB.TestRunner.Suites
{
    public class BufferPoolTestSuite : ITestSuite
    {
        public string Name => "BufferPool";
        private const int FrameSize = 8192;

        public async Task RunAsync(TestContext ctx)
        {
            // total timer
            var swTotal = Stopwatch.StartNew();

            // header
            ctx.WriteInfo(
              "[SilicaDB.BufferPoolTest] " +
              $"file='{Path.GetFullPath(\"silica.data\")}', " +
              "frameId=0, pattern='Hello SilicaDB!', stress=False");

            // 1) Round-trip + verify
            await DoRoundTripAsync(ctx);

            // 2) Hex preview
            await DoHexPreviewAsync(ctx);

            // 3) Invalid read
            await DoInvalidReadAsync(ctx);

            // 4) (optional) Stress
            // await DoStressAsync(ctx, startFrame: 1, frames: 32, parallelism: 8);

            swTotal.Stop();
            ctx.WriteInfo($"[Done] Total elapsed {swTotal.Elapsed.TotalMilliseconds:F3} ms.");
        }

        private async Task DoRoundTripAsync(TestContext ctx)
            {
                await using var device = new PhysicalBlockDevice("silica.data");
                await device.MountAsync();
                ctx.WriteInfo("[Device] Mounted.");

                // WRITE
                await MeasureAsync(ctx, "[Write] Wrote frame 0",
                    () => device.WriteFrameAsync(
                        0,
                        BuildFramePayload(0, "Hello SilicaDB!"))
                );

                // FLUSH
                await MeasureAsync(ctx, "[Flush] Device flush",
                    () => device.FlushAsync(CancellationToken.None)
                );

                // READ
                byte[] read = null!;
                await MeasureAsync(ctx, "[Read] Read frame 0",
                    async () => read = await device.ReadFrameAsync(0)
                );

                // VERIFY
                var expected = BuildFramePayload(0, "Hello SilicaDB!");
                if (expected.AsSpan().SequenceEqual(read))
                    ctx.WritePass("[Verify] Round-trip PASS.");
                else
                    ctx.WriteFail("[Verify] Round-trip FAIL!");

                await device.UnmountAsync();
                ctx.WriteInfo("[Device] Unmounted.");
            }

            private async Task DoHexPreviewAsync(TestContext ctx)
            {
                await using var device = new PhysicalBlockDevice("silica.data");
                await device.MountAsync();

                var read = await device.ReadFrameAsync(0);
                ctx.WriteInfo("[Preview] First 64 bytes (hex):");
                ctx.WriteInfo(ToHex(read.AsSpan(0, Math.Min(64, read.Length))));

                await device.UnmountAsync();
            }

            private async Task DoInvalidReadAsync(TestContext ctx)
            {
                await using var device = new PhysicalBlockDevice("silica.data");
                await device.MountAsync();

                try
                {
                    await device.ReadFrameAsync(999);
                    ctx.WriteFail("[InvalidRead] expected exception, but read succeeded");
                }
                catch
                {
                    ctx.WritePass("[InvalidRead] exception thrown as expected");
                }

                await device.UnmountAsync();
            }

            //────────────────────────────────────────────────────────────────────
            // utility: measure a Task-returning action
            private static async Task MeasureAsync(
              TestContext ctx,
              string label,
              Func<Task> action)
            {
                var sw = Stopwatch.StartNew();
                await action();
                sw.Stop();
                ctx.WriteInfo($"{label} in {sw.Elapsed.TotalMilliseconds:F3} ms.");
            }

            private static byte[] BuildFramePayload(long frameId, string pattern)
            {
                var buffer = new byte[FrameSize];
                BitConverter.GetBytes(frameId).CopyTo(buffer, 0);
                BitConverter.GetBytes(DateTimeOffset.UtcNow.UtcTicks)
                             .CopyTo(buffer, 8);

                var digest = SHA256.HashData(Encoding.UTF8.GetBytes(pattern));
                digest.CopyTo(buffer, 16);

                var body = Encoding.UTF8.GetBytes(pattern);
                int pos = 48;
                while (pos < buffer.Length)
                {
                    int cnt = Math.Min(body.Length, buffer.Length - pos);
                    body.AsSpan(0, cnt)
                        .CopyTo(buffer.AsSpan(pos, cnt));
                    pos += cnt;
                }

                return buffer;
            }

            private static string ToHex(ReadOnlySpan<byte> span)
            {
                var sb = new StringBuilder(span.Length * 2);
                foreach (var b in span)
                    sb.AppendFormat("{0:x2}", b);
                return sb.ToString();
            }
        }
    }
