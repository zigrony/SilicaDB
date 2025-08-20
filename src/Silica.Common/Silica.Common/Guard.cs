// Guard.cs
using System;
using System.Collections.Generic;

namespace Silica.Common
{
    /// <summary>
    /// Centralizes precondition checks and lifecycle guards.
    /// </summary>
    internal static class Guard
    {
        /// <summary>
        /// Throws if <paramref name="value"/> is null.
        /// </summary>
        public static T NotNull<T>(T value, string name)
            where T : class
        {
            if (value is null)
                throw new ArgumentNullException(name);
            return value;
        }

        /// <summary>
        /// Throws if <paramref name="value"/> is null or empty.
        /// </summary>
        public static string NotNullOrEmpty(string value, string name)
        {
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException("Value cannot be null or empty.", name);
            return value;
        }

        /// <summary>
        /// Throws if the object is already disposed.
        /// </summary>
        public static void Disposed(bool isDisposed, string name)
        {
            if (isDisposed)
                throw new ObjectDisposedException(name);
        }

        /// <summary>
        /// Throws if the object has already been initialized.
        /// </summary>
        public static void AgainstAlreadyInitialized(bool initialized, string name)
        {
            if (initialized)
                throw new InvalidOperationException($"{name} has already been initialized.");
        }

        /// <summary>
        /// Throws if <paramref name="value"/> is outside the inclusive [min..max] range.
        /// </summary>
        public static void InRange(int value, int min, int max, string name)
        {
            if (value < min || value > max)
                throw new ArgumentOutOfRangeException(
                    name,
                    value,
                    $"Expected {name} to be between {min} and {max} (inclusive).");
        }

        /// <summary>
        /// Throws if the given <paramref name="condition"/> is false.
        /// </summary>
        public static void Against(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException(message);
        }

        /// <summary>
        /// Throws if the collection is null or contains no elements.
        /// </summary>
        public static ICollection<T> NotNullOrEmpty<T>(ICollection<T> collection, string name)
        {
            if (collection is null)
                throw new ArgumentNullException(name);
            if (collection.Count == 0)
                throw new ArgumentException("Collection cannot be empty.", name);
            return collection;
        }
    }
}
