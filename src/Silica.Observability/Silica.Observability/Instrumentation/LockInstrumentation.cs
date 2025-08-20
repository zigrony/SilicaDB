// LockImplementation.cs
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace Silica.Observability.Instrumentation
{
    public class LockMetrics : IDisposable
    {
        private readonly ConcurrentDictionary<string, LockStatistic> _lockStats = new();
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lockStats.Clear();
        }

        public IDisposable StartWaitEvent(string lockType, string contextTag = null)
        {
            var frame = new StackFrame(1);
            var method = frame.GetMethod();
            string callerClass = method?.DeclaringType?.FullName ?? "UnknownClass";
            string callerMethod = method?.Name ?? "UnknownMethod";
            string module = Assembly.GetExecutingAssembly().GetName().Name;
            string user = Environment.UserName;
            int pid = Process.GetCurrentProcess().Id;
            int tid = Thread.CurrentThread.ManagedThreadId;

            return new LockEvent(this, lockType, contextTag, pid, tid, user, module, callerClass, callerMethod);
        }

        internal void RecordLockEvent(string lockType, long durationTicks,
            int pid, int threadId, string userId,
            string moduleName, string callerClass, string callerMethod)
        {
            string key = $"{lockType}::{pid}::{threadId}::{userId}::{moduleName}::{callerClass}::{callerMethod}";
            var stat = _lockStats.GetOrAdd(key,
                _ => new LockStatistic(pid, threadId, userId, moduleName, callerClass, callerMethod));
            stat.Add(durationTicks);
        }

        public (int count, long totalTicks) GetStatistics(string compositeKey)
        {
            if (_lockStats.TryGetValue(compositeKey, out var s))
                return (s.Count, s.TotalTicks);
            return (0, 0);
        }
    }

    public class LockStatistic
    {
        private int _count;
        private long _totalTicks;
        public int Count => _count;
        public long TotalTicks => _totalTicks;

        public int ProcessId { get; }
        public int ThreadId { get; }
        public string UserId { get; }
        public string ModuleName { get; }
        public string CallerClass { get; }
        public string CallerMethod { get; }

        public LockStatistic(int processId, int threadId, string userId,
                             string moduleName, string callerClass, string callerMethod)
        {
            ProcessId = processId;
            ThreadId = threadId;
            UserId = userId;
            ModuleName = moduleName;
            CallerClass = callerClass;
            CallerMethod = callerMethod;
        }

        public void Add(long ticks)
        {
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _totalTicks, ticks);
        }
    }

    internal class LockEvent : IDisposable
    {
        private readonly LockMetrics _metrics;
        private readonly string _waitType;
        private readonly Stopwatch _sw;
        private readonly int _pid, _tid;
        private readonly string _user, _module, _class, _method;

        public LockEvent(LockMetrics metrics, string lockType, string contextTag,
                         int pid, int tid, string userId,
                         string moduleName, string callerClass, string callerMethod)
        {
            _metrics = metrics;
            _waitType = lockType;
            _sw = Stopwatch.StartNew();
            _pid = pid;
            _tid = tid;
            _user = userId;
            _module = moduleName;
            _class = callerClass;
            _method = callerMethod;
        }

        public void Dispose()
        {
            _sw.Stop();
            _metrics.RecordLockEvent(
                _waitType, _sw.ElapsedTicks,
                _pid, _tid, _user, _module, _class, _method);
        }
    }
}
