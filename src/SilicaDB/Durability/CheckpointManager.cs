// File: CheckpointManager.cs
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SilicaDB.Evictions;              // for AsyncLock
using SilicaDB.Evictions.Interfaces;   // AsyncLock interface

namespace SilicaDB.Durability
{
    public sealed class CheckpointManager : ICheckpointManager
    {
        private const string CheckpointFilePattern = "checkpoint_*.json";
        private readonly string _directory;
        private readonly IWalManager _wal;
        private readonly AsyncLock _lock = new AsyncLock();
        private bool _started;
        private bool _disposed;

        public CheckpointManager(string checkpointDirectory, IWalManager walManager)
        {
            _directory = checkpointDirectory ?? throw new ArgumentNullException(nameof(checkpointDirectory));
            _wal = walManager ?? throw new ArgumentNullException(nameof(walManager));
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_started) throw new InvalidOperationException("CheckpointManager already started.");
            Directory.CreateDirectory(_directory);
            _started = true;
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public async Task WriteCheckpointAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfNotStarted();
            ThrowIfDisposed();

            // serialize under lock so two writers can't race
            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                // 1) ensure WAL has flushed all recent appends
                await _wal.FlushAsync(cancellationToken).ConfigureAwait(false);

                // 2) query the latest LSN
                long lastLsn = await _wal.GetLastSequenceNumberAsync(cancellationToken)
                                         .ConfigureAwait(false);

                var checkpoint = new CheckpointData
                {
                    LastSequenceNumber = lastLsn,
                    CreatedUtc = DateTimeOffset.UtcNow
                };

                // 3) write to a temp file + rename—atomic on most file systems
                string fileName = $"checkpoint_{lastLsn:0000000000000000}.json";
                string tmpPath = Path.Combine(_directory, fileName + ".tmp");
                string outPath = Path.Combine(_directory, fileName);

                await using (var fs = new FileStream(
                                 tmpPath,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 4096,
                                 useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(fs, checkpoint, new JsonSerializerOptions
                    {
                        WriteIndented = false
                    }, cancellationToken).ConfigureAwait(false);

                    await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(tmpPath, outPath, overwrite: true);

                // 4) prune older checkpoints
                var all = Directory.GetFiles(_directory, CheckpointFilePattern)
                                   .OrderByDescending(Path.GetFileNameWithoutExtension)
                                   .Skip(1);
                foreach (var old in all)
                    File.Delete(old);
            }
        }

        public async Task<CheckpointData?> ReadLatestCheckpointAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfNotStarted();
            ThrowIfDisposed();

            using (await _lock.LockAsync().ConfigureAwait(false))
            {
                var files = Directory.GetFiles(_directory, CheckpointFilePattern)
                                     .OrderByDescending(fn => fn)
                                     .ToArray();
                if (files.Length == 0)
                    return null;

                var latest = files[0];
                await using var fs = new FileStream(
                         latest,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         bufferSize: 4096,
                         useAsync: true);

                var cp = await JsonSerializer.DeserializeAsync<CheckpointData>(
                    fs,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                    cancellationToken).ConfigureAwait(false);

                return cp;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            // no background work to stop, but we'll prevent further calls
            _disposed = true;

            // clean up the internal AsyncLock
            _lock.Dispose();


            await Task.CompletedTask.ConfigureAwait(false);
        }

        private void ThrowIfNotStarted()
        {
            if (!_started)
                throw new InvalidOperationException("CheckpointManager not started.");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CheckpointManager));
        }
    }
}
