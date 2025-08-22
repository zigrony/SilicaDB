 using System;
 using System.Diagnostics;
 using System.Threading;

 namespace Silica.DiagnosticsCore.Instrumentation
 {
     /// <summary>
     /// Represents the lifetime of a lock acquisition for instrumentation purposes.
     /// Automatically measures the time a lock is held and invokes a completion callback.
     /// </summary>
     public sealed class LockScope : IDisposable
     {
        private readonly Action<LockScope> _onComplete;
        private readonly Action<Exception>? _onError;
        private readonly Stopwatch _stopwatch;
        private int _completed;

         /// <summary>
         /// Gets the component or subsystem that owns the lock.
         /// </summary>
         public string Component { get; }

         /// <summary>
         /// Gets the name or purpose of the lock (e.g., "BufferPoolEvictionLock").
         /// </summary>
         public string LockName { get; }

         /// <summary>
         /// Gets the time when the lock scope began (UTC).
         /// </summary>
         public DateTimeOffset StartTime { get; }

         /// <summary>
         /// Gets how long the lock has been held so far.
         /// If called after disposal, returns the final duration.
         /// </summary>
         public TimeSpan Elapsed => _stopwatch.Elapsed;

         /// <summary>
         /// Creates a new lock scope and begins timing immediately.
         /// </summary>
         /// <param name="component">The component or subsystem that owns the lock.</param>
         /// <param name="lockName">The identifier for the lock being measured.</param>
         /// <param name="onComplete">
         /// Callback invoked when the scope is completed or disposed, 
         /// allowing emission of lock timing metrics.
         /// </param>
        public LockScope(string component, string lockName, Action<LockScope> onComplete)
         {
            if (string.IsNullOrWhiteSpace(component)) throw new ArgumentException("Component cannot be null/empty.", nameof(component));
            if (string.IsNullOrWhiteSpace(lockName)) throw new ArgumentException("LockName cannot be null/empty.", nameof(lockName));
            Component = component;
            LockName = lockName;
            _onComplete = onComplete ?? throw new ArgumentNullException(nameof(onComplete));

            StartTime = DateTimeOffset.UtcNow;
            _stopwatch = Stopwatch.StartNew();
         }

        /// <summary>
        /// Creates a new lock scope with an error callback for completion failures.
        /// </summary>
        public LockScope(string component, string lockName, Action<LockScope> onComplete, Action<Exception> onError)
            : this(component, lockName, onComplete)
        {
            _onError = onError ?? throw new ArgumentNullException(nameof(onError));
        }

         /// <summary>
         /// Marks the scope as complete and emits the lock timing data.
         /// </summary>
         public void Complete()
         {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return; // idempotent & thread-safe
                _stopwatch.Stop();
                try
                {
                    _onComplete(this);
                }
            catch (Exception ex)
            {
                // Do not throw instrumentation exceptions into callers.
                _onError?.Invoke(ex);
                // Optional: Debug hook without impacting production.
                Debug.Fail($"LockScope onComplete error for {Component}/{LockName}: {ex}");
            }
         }

         /// <summary>
         /// Disposes the scope, ensuring <see cref="Complete"/> is called exactly once.
         /// </summary>
         public void Dispose()
         {
             Complete();
         }
     }
 }
