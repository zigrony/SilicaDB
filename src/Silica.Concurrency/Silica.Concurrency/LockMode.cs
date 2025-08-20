// LockMode.cs
using Silica.Observability.Metrics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Silica.Concurrency
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
