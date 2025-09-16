using Silica.Storage.Encryption.Exceptions;
using System;
using Silica.Storage.Encryption.Diagnostics;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;

namespace Silica.Storage.Encryption
{
    public sealed class FixedKeyProvider : IEncryptionKeyProvider, IDisposable
    {
        private readonly byte[] _key;
        private readonly byte[] _salt; // 16 bytes
        private bool _disposed;

        public FixedKeyProvider(byte[] key, byte[] salt16)
        {
            if (key is null)
            {
                try { EncryptionDiagnostics.Emit("key_provider_ctor", "error", "key_null"); } catch { }
                throw new EncryptionKeyNullException();
            }
            if (salt16 is null)
            {
                try { EncryptionDiagnostics.Emit("key_provider_ctor", "error", "salt_null"); } catch { }
                throw new EncryptionSaltNullException();
            }

            int len = key.Length;
            if (len != 16 && len != 24 && len != 32)
            {
                try
                {
                    EncryptionDiagnostics.Emit("key_provider_ctor", "error", "invalid_key_length",
                        more: new Dictionary<string, string> { { "key_bytes", len.ToString(CultureInfo.InvariantCulture) } });
                }
                catch { }
                throw new InvalidKeyException(len);
            }

            if (salt16.Length != 16)
            {
                try
                {
                    EncryptionDiagnostics.Emit("key_provider_ctor", "error", "invalid_salt_length",
                        more: new Dictionary<string, string> { { "salt_bytes", salt16.Length.ToString(CultureInfo.InvariantCulture) } });
                }
                catch { }
                throw new InvalidSaltException(salt16.Length);
            }

            _key = new byte[len];
            Buffer.BlockCopy(key, 0, _key, 0, len);

            _salt = new byte[16];
            Buffer.BlockCopy(salt16, 0, _salt, 0, 16);
            try
            {
                EncryptionDiagnostics.Emit("key_provider_ctor", "info", "provider_initialized",
                    more: new Dictionary<string, string>
                    {
                        { "key_bytes", len.ToString(CultureInfo.InvariantCulture) },
                        { "salt_bytes", "16" }
                    });
            }
            catch { }
        }

        public ReadOnlySpan<byte> GetKey()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FixedKeyProvider));
            try
            {
                EncryptionDiagnostics.Emit("key_provider", "debug", "get_key",
                    more: new Dictionary<string, string> { { "key_bytes", _key.Length.ToString(CultureInfo.InvariantCulture) } });
            }
            catch { }
            return _key;
        }

        public ReadOnlySpan<byte> GetDeviceSalt()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(FixedKeyProvider));
            try
            {
                EncryptionDiagnostics.Emit("key_provider", "debug", "get_salt",
                    more: new Dictionary<string, string> { { "salt_bytes", _salt.Length.ToString(CultureInfo.InvariantCulture) } });
            }
            catch { }
            return _salt;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                CryptographicOperations.ZeroMemory(_key);
                CryptographicOperations.ZeroMemory(_salt);
            }
            catch { }
        }
    }
}
