// File: Program.cs
// Drop-in replacement for a console tester with colorized PASS/FAIL output.
// Assumes frame size of 8192 bytes.

using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.Devices;
using SilicaDB.Devices.Interfaces;

namespace SilicaDB.BufferPoolTest
{
    class Program
    {
        // Must match the device's FrameSize (8192 bytes by default)
        private const int FrameSize = 8192;

        static async Task<int> Main(string[] args)
        {
            var (path, frameId, pattern, doStress) = ParseArgs(args);

            Console.WriteLine(
                $"[SilicaDB.BufferPoolTest] file='{path}', " +
                $"frameId={frameId}, pattern='{pattern}', stress={doStress}");

            // Build a test buffer of exactly FrameSize bytes
            byte[] writeBuffer = BuildFramePayload(FrameSize, frameId, pattern);

            // Instantiate the device under test
            IStorageDevice device = new PhysicalBlockDevice(path);

            var swTotal = Stopwatch.StartNew();
            try
            {
                await device.MountAsync();
                WriteInfo("[Device] Mounted.");

                // WRITE
                var sw = Stopwatch.StartNew();
                await device.WriteFrameAsync(frameId, writeBuffer);
                sw.Stop();
                WriteInfo($"[Write] Wrote frame {frameId} in {sw.Elapsed.TotalMilliseconds:F3} ms.");

                // FLUSH
                sw.Restart();
                await device.FlushAsync(CancellationToken.None);
                sw.Stop();
                WriteInfo($"[Flush] Device flush in {sw.Elapsed.TotalMilliseconds:F3} ms.");

                // READ
                sw.Restart();
                var readBuffer = await device.ReadFrameAsync(frameId);
                sw.Stop();
                WriteInfo($"[Read] Read frame {frameId} in {sw.Elapsed.TotalMilliseconds:F3} ms.");

                // VERIFY
                bool ok = writeBuffer.AsSpan().SequenceEqual(readBuffer);
                if (ok)
                    WritePass("[Verify] Round-trip PASS.");
                else
                    WriteFail("[Verify] Round-trip FAIL!");

                // HEX PREVIEW
                Console.WriteLine("[Preview] First 64 bytes (hex):");
                Console.WriteLine(ToHex(readBuffer.AsSpan(0, Math.Min(64, readBuffer.Length))));

                // OPTIONAL STRESS TEST
                if (doStress)
                {
                    await StressTestAsync(device, frameId + 1, frames: 32, parallelism: 8);
                }

                return ok ? 0 : 2;
            }
            catch (Exception ex)
            {
                WriteFail("[Error] " + ex.Message);
                return 1;
            }
            finally
            {
                try
                {
                    await device.UnmountAsync();
                    WriteInfo("[Device] Unmounted.");
                }
                catch (Exception ex)
                {
                    WriteWarn("[Unmount Warning] " + ex.Message);
                }

                await device.DisposeAsync();
                swTotal.Stop();
                WriteInfo($"[Done] Total elapsed {swTotal.Elapsed.TotalMilliseconds:F3} ms.");
            }
        }

        private static (string Path, long FrameId, string Pattern, bool Stress)
            ParseArgs(string[] args)
        {
            string path = Path.GetFullPath("silica.data");
            long frameId = 0;
            string pattern = "Hello SilicaDB!";
            bool stress = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--file" when i + 1 < args.Length:
                        path = args[++i];
                        break;
                    case "--frame" when i + 1 < args.Length:
                        frameId = long.Parse(args[++i]);
                        break;
                    case "--pattern" when i + 1 < args.Length:
                        pattern = args[++i];
                        break;
                    case "--stress":
                        stress = true;
                        break;
                    case "--help":
                    case "-h":
                        PrintHelp();
                        Environment.Exit(0);
                        break;
                }
            }

            return (path, frameId, pattern, stress);
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  BufferPoolTest --file <path> --frame <id> --pattern <text> [--stress]");
            Console.WriteLine("Defaults:");
            Console.WriteLine("  --file    ./silica.data");
            Console.WriteLine("  --frame   0");
            Console.WriteLine("  --pattern 'Hello SilicaDB!'");
            Console.WriteLine("  --stress  disabled");
        }

        private static byte[] BuildFramePayload(int size, long frameId, string pattern)
        {
            var buffer = new byte[size];

            // 8 bytes frameId, 8 bytes ticks, 32 bytes SHA256(pattern)
            BitConverter.GetBytes(frameId).CopyTo(buffer, 0);
            BitConverter.GetBytes(DateTimeOffset.UtcNow.UtcTicks).CopyTo(buffer, 8);

            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(pattern));
            digest.CopyTo(buffer, 16);

            // Fill the remainder with the pattern bytes
            var body = Encoding.UTF8.GetBytes(pattern);
            int pos = 48;
            while (pos < buffer.Length)
            {
                int count = Math.Min(body.Length, buffer.Length - pos);
                body.AsSpan(0, count).CopyTo(buffer.AsSpan(pos, count));
                pos += count;
            }

            return buffer;
        }

        private static async Task StressTestAsync(
            IStorageDevice device,
            long startFrame,
            int frames,
            int parallelism)
        {
            WriteInfo($"[Stress] frames={frames}, parallelism={parallelism}");
            var sw = Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var tasks = new Task[parallelism];
            for (int p = 0; p < parallelism; p++)
            {
                tasks[p] = Task.Run(async () =>
                {
                    var rnd = new Random(unchecked(Environment.TickCount * (p + 1)));
                    for (int i = 0; i < frames; i++)
                    {
                        long fid = startFrame + i;
                        var pat = $"wr{p}-f{fid}";
                        var buf = BuildFramePayload(FrameSize, fid, pat);

                        await device.WriteFrameAsync(fid, buf, cts.Token);
                        if ((i & 0x3) == 0)
                        {
                            var r = await device.ReadFrameAsync(fid, cts.Token);
                            if (!buf.AsSpan().SequenceEqual(r))
                                throw new InvalidOperationException($"Mismatch f{fid} by writer{p}");
                        }
                    }
                }, cts.Token);
            }

            await Task.WhenAll(tasks);
            sw.Stop();
            WriteInfo($"[Stress] Completed in {sw.Elapsed.TotalMilliseconds:F1} ms.");
        }

        private static string ToHex(ReadOnlySpan<byte> span)
        {
            var sb = new StringBuilder(span.Length * 2);
            foreach (var b in span) sb.AppendFormat("{0:x2}", b);
            return sb.ToString();
        }

        // Colored helpers
        private static void WritePass(string text)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(text);
            Console.ResetColor();
        }

        private static void WriteFail(string text)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(text);
            Console.ResetColor();
        }

        private static void WriteWarn(string text)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(text);
            Console.ResetColor();
        }

        private static void WriteInfo(string text)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(text);
            Console.ResetColor();
        }
    }
}
