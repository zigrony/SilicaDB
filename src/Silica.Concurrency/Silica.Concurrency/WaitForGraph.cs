// WaitForGraph.cs
using System;
using System.Collections.Generic;

namespace Silica.Concurrency
{
    public sealed class WaitForGraph
    {
        private readonly object _graphLock = new object();
        private readonly Dictionary<long, HashSet<long>> _graph = new Dictionary<long, HashSet<long>>();

        public void AddEdge(long fromTx, long toTx)
        {
            lock (_graphLock)
            {
                // Defensive: do not record self-edges
                if (fromTx == toTx) return;
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
                // Remove the node itself.
                _graph.Remove(txId);

                // Snapshot keys to safely mutate dictionary entries.
                long[] keys;
                int used = 0;
                {
                    var count = _graph.Keys.Count;
                    keys = new long[count];
                    int i = 0;
                    foreach (var k in _graph.Keys)
                    {
                        if (i < keys.Length) { keys[i] = k; i++; } else { break; }
                    }
                    used = i;
                }

                // Remove inbound edges and prune empty adjacency sets.
                for (int i = 0; i < used; i++)
                {
                    var from = keys[i];
                    if (_graph.TryGetValue(from, out var set))
                    {
                        set.Remove(txId);
                        if (set.Count == 0)
                        {
                            _graph.Remove(from);
                        }
                    }
                }
            }
        }

        public bool DetectCycle()
        {
            lock (_graphLock)
            {
                var visited = new HashSet<long>();
                var onStack = new HashSet<long>();

                // Snapshot keys without LINQ
                long[] keys;
                int used = 0;
                {
                    var count = _graph.Keys.Count;
                    keys = new long[count];
                    int i = 0;
                    foreach (var k in _graph.Keys)
                    {
                        if (i < keys.Length) { keys[i] = k; i++; } else { break; }
                    }
                    used = i;
                }

                for (int i = 0; i < used; i++)
                {
                    var start = keys[i];
                    if (visited.Contains(start)) continue;

                    // Iterative DFS with explicit stack frames
                    var frameNodeStack = new Stack<long>();
                    var frameIterStack = new Stack<IEnumerator<long>>();

                    frameNodeStack.Push(start);
                    onStack.Add(start);
                    visited.Add(start);

                    // Create enumerator for start's neighbors if any
                    if (_graph.TryGetValue(start, out var startNeighbors))
                    {
                        var it = startNeighbors.GetEnumerator();
                        frameIterStack.Push(it);
                    }
                    else
                    {
                        // No neighbors; pop immediately
                        onStack.Remove(start);
                        frameNodeStack.Pop();
                        continue;
                    }

                    while (frameNodeStack.Count > 0)
                    {
                        var iter = frameIterStack.Peek();
                        if (iter.MoveNext())
                        {
                            var n = iter.Current;
                            if (!visited.Contains(n))
                            {
                                visited.Add(n);
                                frameNodeStack.Push(n);
                                onStack.Add(n);
                                if (_graph.TryGetValue(n, out var nNeighbors))
                                {
                                    var nIt = nNeighbors.GetEnumerator();
                                    frameIterStack.Push(nIt);
                                }
                                else
                                {
                                    // leaf; unwind once on next iteration
                                }
                            }
                            else if (onStack.Contains(n))
                            {
                                // back-edge -> cycle
                                // Dispose enumerators before returning
                                while (frameIterStack.Count > 0)
                                {
                                    var e = frameIterStack.Pop();
                                    try { e.Dispose(); } catch { }
                                }
                                return true;
                            }
                        }
                        else
                        {
                            // Finished neighbors of the node on top
                            try { iter.Dispose(); } catch { }
                            frameIterStack.Pop();
                            var node = frameNodeStack.Pop();
                            onStack.Remove(node);
                        }
                    }
                }
                return false;
            }
        }
        public int CountNodes()
        {
            lock (_graphLock)
            {
                // Count distinct vertices = keys (outgoing) ∪ all targets (incoming-only)
                // No LINQ. Bound allocations to worst-case counts.
                // 1) Snapshot keys
                int keyCount = _graph.Keys.Count;
                long[] keys = keyCount > 0 ? new long[keyCount] : Array.Empty<long>();
                if (keyCount > 0)
                {
                    int i = 0;
                    foreach (var k in _graph.Keys)
                    {
                        if (i < keys.Length) { keys[i] = k; i++; }
                        else { break; }
                    }
                    keyCount = i;
                }

                // 2) Mark seen nodes in a HashSet<long>
                var seen = new HashSet<long>();
                for (int i = 0; i < keyCount; i++)
                {
                    seen.Add(keys[i]);
                }

                // 3) Walk adjacency sets and add targets
                // Snapshot values by iterating the dictionary (still under lock)
                foreach (var kv in _graph)
                {
                    var set = kv.Value;
                    if (set == null || set.Count == 0) continue;
                    var it = set.GetEnumerator();
                    try
                    {
                        while (it.MoveNext())
                        {
                            var tgt = it.Current;
                            seen.Add(tgt);
                        }
                    }
                    finally
                    {
                        try { it.Dispose(); } catch { }
                    }
                }

                // 4) Result is count of distinct nodes
                return seen.Count;
            }
        }
        public void Clear()
        {
            lock (_graphLock)
            {
                _graph.Clear();
            }
        }

    }
}
