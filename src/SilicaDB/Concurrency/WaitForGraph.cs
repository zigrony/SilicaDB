// WaitForGraph.cs
using System;
using System.Collections.Generic;
using System.Linq;

namespace SilicaDB.Concurrency
{
    public class WaitForGraph
    {
        private readonly object _graphLock = new object();
        private readonly Dictionary<long, HashSet<long>> _graph = new Dictionary<long, HashSet<long>>();

        public void AddEdge(long fromTx, long toTx)
        {
            lock (_graphLock)
            {
                if (!_graph.TryGetValue(fromTx, out var set))
                {
                    set = new HashSet<long>();
                    _graph[fromTx] = set;
                }
                set.Add(toTx);
            }
        }

        public void RemoveEdge(long fromTx, long toTx)
        {
            lock (_graphLock)
            {
                if (_graph.TryGetValue(fromTx, out var set))
                {
                    set.Remove(toTx);
                    if (set.Count == 0)
                        _graph.Remove(fromTx);
                }
            }
        }

        public void RemoveTransaction(long txId)
        {
            lock (_graphLock)
            {
                _graph.Remove(txId);
                foreach (var set in _graph.Values)
                    set.Remove(txId);
            }
        }

        public bool DetectCycle()
        {
            lock (_graphLock)
            {
                var visited = new HashSet<long>();
                var stack = new HashSet<long>();

                foreach (var node in _graph.Keys.ToList())
                {
                    if (DetectCycleUtil(node, visited, stack))
                        return true;
                }
                return false;
            }
        }

        private bool DetectCycleUtil(long current, HashSet<long> visited, HashSet<long> stack)
        {
            if (!visited.Contains(current))
            {
                visited.Add(current);
                stack.Add(current);

                if (_graph.TryGetValue(current, out var neighbors))
                {
                    foreach (var n in neighbors)
                    {
                        if (!visited.Contains(n) && DetectCycleUtil(n, visited, stack))
                            return true;
                        if (stack.Contains(n))
                            return true;
                    }
                }
            }

            stack.Remove(current);
            return false;
        }
    }
}
