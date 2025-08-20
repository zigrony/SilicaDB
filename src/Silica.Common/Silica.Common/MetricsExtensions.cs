// Filename: MetricsExtensions.cs
// Namespace: SilicaDB.Common
using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Silica.Common
{
    /// <summary>
    /// Extension helpers to disambiguate Counter<T>.Add and Histogram<T>.Record overloads.
    /// </summary>
    public static class MetricsExtensions
    {
        /// <summary>
        /// Adds a delta to the counter with one or more tags.
        /// </summary>
        public static void Add<T>(
            this Counter<T> counter,
            T delta,
            params (string Key, object? Value)[] tags)
            where T : struct
        {
            // Allocate exactly once per call
            var pairs = new KeyValuePair<string, object?>[tags.Length];
            for (int i = 0; i < tags.Length; i++)
            {
                pairs[i] = new KeyValuePair<string, object?>(tags[i].Key, tags[i].Value);
            }
            counter.Add(delta, pairs);
        }

        /// <summary>
        /// Records a value into the histogram with one or more tags.
        /// </summary>
        public static void Record<T>(
            this Histogram<T> histogram,
            T value,
            params (string Key, object? Value)[] tags)
            where T : struct
        {
            var pairs = new KeyValuePair<string, object?>[tags.Length];
            for (int i = 0; i < tags.Length; i++)
            {
                pairs[i] = new KeyValuePair<string, object?>(tags[i].Key, tags[i].Value);
            }
            histogram.Record(value, pairs);
        }
        /// <summary>
        /// Adds a delta to the up/down counter with one or more tags.
        /// </summary>
        public static void Add<T>(
            this UpDownCounter<T> counter,
            T delta,
            params (string Key, object? Value)[] tags)
          where T : struct
        {
            var pairs = new KeyValuePair<string, object?>[tags.Length];
            for (int i = 0; i < tags.Length; i++)
                pairs[i] = new KeyValuePair<string, object?>(tags[i].Key, tags[i].Value);
            counter.Add(delta, pairs);
        }
    }
}
