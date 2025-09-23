using System;
using System.Security.Cryptography;
using System.Text;

namespace Silica.Authentication.Local
{
    /// <summary>
    /// Simple PBKDF2-based password hasher.
    /// Replace/extend with stronger algorithms as needed.
    /// </summary>
    public sealed class PasswordHasher
    {
        public static byte[] CreateSalt(int size = 32)
        {
            if (size <= 0) size = 16;
            var salt = new byte[size];
            RandomNumberGenerator.Fill(salt);
            return salt;
        }

        public string Hash(string password, byte[] salt, int iterations = 310000) // NIST 800-63B guidance
        {
            if (password == null) throw new ArgumentNullException(nameof(password));
            if (salt == null || salt.Length == 0) throw new ArgumentException("Salt must be non-empty.", nameof(salt));
            using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
            var derived = pbkdf2.GetBytes(32);
            try
            {
                return Convert.ToBase64String(derived);
            }
            finally { Array.Clear(derived, 0, derived.Length); }
        }

        public bool Verify(string password, string expectedHash, byte[] salt, int iterations = 310000)
        {
            if (password == null) return false;
            if (expectedHash == null) return false;
            if (salt == null || salt.Length == 0) return false;
            // Decode expected hash from Base64; compare raw derived bytes in constant time
            byte[] expectedBytes;
            try
            {
                expectedBytes = Convert.FromBase64String(expectedHash);
            }
            catch
            {
                return false;
            }

            // Derive the same length bytes
            using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
            {
                var actualBytes = pbkdf2.GetBytes(expectedBytes.Length);
                var equal = CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
                Array.Clear(actualBytes, 0, actualBytes.Length);
                Array.Clear(expectedBytes, 0, expectedBytes.Length);
                return equal;
            }
        }
    }
}
