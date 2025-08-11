// LockMode.cs
using SilicaDB.Instrumentation;
using SilicaDB.Metrics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SilicaDB.Concurrency
{
    /// <summary>
    /// LockMode
    /// </summary>
    public enum LockMode
    {
        Shared,
        Exclusive
    }
}
