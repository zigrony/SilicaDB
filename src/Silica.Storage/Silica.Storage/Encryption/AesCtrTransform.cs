using System;
using System.Security.Cryptography;
using Silica.Storage.Encryption.Exceptions;
using Silica.Storage.Encryption.Diagnostics;
using System.Collections.Generic;
using System.Globalization;

namespace Silica.Storage.Encryption
{
    internal sealed class AesCtrTransform : IDisposable
    {
        private readonly Aes _aes;
        private readonly ICryptoTransform _ecb; // AES-ECB encryptor, no padding
        private readonly byte[] _counter = new byte[16];
        // Reused keystream block (16 bytes) per transform instance. Not thread-safe by design.
        private readonly byte[] _keystream = new byte[16];
        private bool _disposed;

        public AesCtrTransform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> counterBlock)
        {
            // Validate key length explicitly to preserve contract-first exceptions
            int keyLen = key.Length;
            if (keyLen != 16 && keyLen != 24 && keyLen != 32)
            {
                try { EncryptionDiagnostics.Emit("ctr_ctor", "error", "invalid_key_length", more: new Dictionary<string, string> { { "key_bytes", keyLen.ToString(CultureInfo.InvariantCulture) } }); } catch { }
                throw new InvalidKeyException(keyLen);
            }

            _aes = Aes.Create();
            if (_aes is null)
            {
                try { EncryptionDiagnostics.Emit("ctr_ctor", "error", "aes_create_null"); } catch { }
                throw new TransformFailedException("aes_create");
            }
            try
            {
                _aes.Mode = CipherMode.ECB;
                _aes.Padding = PaddingMode.None;
                // Make intent explicit, then verify provider honors it
                _aes.BlockSize = 128;
                _aes.KeySize = keyLen * 8;
                _aes.Key = key.ToArray(); // copy
                // Validate provider actually took the requested key size (defensive)
                if (_aes.KeySize != keyLen * 8 || _aes.Key is null || _aes.Key.Length != keyLen)
                {
                    try
                    {
                        EncryptionDiagnostics.Emit("ctr_ctor", "error", "provider_keysize_mismatch",
                            more: new Dictionary<string, string>
                            {
                                { "requested_bits", (keyLen * 8).ToString(CultureInfo.InvariantCulture) },
                                { "actual_bits", _aes.KeySize.ToString(CultureInfo.InvariantCulture) },
                                { "actual_key_bytes", (_aes.Key?.Length ?? 0).ToString(CultureInfo.InvariantCulture) }
                            });
                    }
                    catch { }
                    throw new TransformFailedException("provider_keysize_mismatch");
                }
                // Fail-fast if block size is not 128 bits (AES requirement). Defensive guard against misconfigured providers.
                if (_aes.BlockSize != 128)
                {
                    try { EncryptionDiagnostics.Emit("ctr_ctor", "error", "invalid_block_size", more: new Dictionary<string, string> { { "block_bits", _aes.BlockSize.ToString(CultureInfo.InvariantCulture) } }); } catch { }
                    throw new TransformFailedException("invalid_block_size");
                }
                _ecb = _aes.CreateEncryptor();
            }
            catch (Exception ex)
            {
                try { _aes?.Dispose(); } catch { }
                try { EncryptionDiagnostics.Emit("ctr_ctor", "error", "create_encryptor_failed", ex); } catch { }
                throw new TransformFailedException("create_encryptor", ex);
            }
            if (counterBlock.Length != 16)
            {
                try
                {
                    EncryptionDiagnostics.Emit("ctr_ctor", "error", "invalid_counter_block_length",
                        more: new Dictionary<string, string> { { "length", counterBlock.Length.ToString(CultureInfo.InvariantCulture) } });
                }
                catch { }
                // Ensure we do not leak crypto handles on ctor failure
                try { _ecb?.Dispose(); } catch { }
                try
                {
                    // Best-effort zero the AES key before disposing the algorithm instance
                    var zero = new byte[_aes.Key?.Length ?? 0];
                    if (zero.Length > 0)
                    {
                        try { _aes.Key = zero; } catch { }
                    }
                }
                catch { }
                finally
                {
                    try { _aes.Dispose(); } catch { }
                }
                throw new InvalidCounterBlockException(counterBlock.Length);
            }
            counterBlock.CopyTo(_counter);
            try
            {
                EncryptionDiagnostics.Emit("ctr_ctor", "info", "transform_created",
                    more: new Dictionary<string, string> { { "key_bits", (key.Length * 8).ToString(CultureInfo.InvariantCulture) } });
            }
            catch { }
        }

        public void Transform(ReadOnlySpan<byte> input, Span<byte> output)
        {
            if (_disposed)
            {
                try { EncryptionDiagnostics.Emit("ctr_transform", "error", "object_disposed"); } catch { }
                throw new TransformObjectDisposedException();
            }
            if (input.Length == 0)
            {
                // Nothing to do; preserve contract without side effects.
                return;
            }
            if (output.Length != input.Length)
            {
                try
                {
                    EncryptionDiagnostics.Emit("ctr_transform", "error", "mismatched_buffer_length",
                        more: new Dictionary<string, string>
                        {
                            { "input", input.Length.ToString(CultureInfo.InvariantCulture) },
                            { "output", output.Length.ToString(CultureInfo.InvariantCulture) }
                        });
                }
                catch { }
                throw new MismatchedBufferLengthException(input.Length, output.Length);
            }

            try
            {
                EncryptionDiagnostics.Emit("ctr_transform", "debug", "begin",
                    more: new Dictionary<string, string> { { "bytes", input.Length.ToString(CultureInfo.InvariantCulture) } });
            }
            catch { }

            try
            {
                // Preflight: ensure we won't wrap the 64-bit counter space mid-transform
                // Compute blocks needed and blocks available from current counter low 64-bit (big-endian)
                int blocksNeeded = (input.Length + 15) / 16;
                ulong ctrLow = GetCounterLow64BigEndian(_counter);
                ulong blocksAvailable = (ulong.MaxValue - ctrLow) + 1UL; // inclusive capacity
                if ((ulong)blocksNeeded > blocksAvailable)
                {
                    try
                    {
                        EncryptionDiagnostics.Emit("ctr_transform", "error", "counter_capacity_exceeded",
                            more: new Dictionary<string, string>
                            {
                                { "blocks_needed", blocksNeeded.ToString(CultureInfo.InvariantCulture) },
                                { "blocks_available", blocksAvailable.ToString(CultureInfo.InvariantCulture) }
                            });
                    }
                    catch { }
                    throw new TransformFailedException("counter_capacity");
                }

                // Process in 16-byte keystream blocks using the reusable buffer

                int remaining = input.Length;
                int offset = 0;
                bool wrapped = false;
                while (remaining > 0)
                {
                    // Encrypt current counter to produce keystream
                    int produced = _ecb.TransformBlock(_counter, 0, 16, _keystream, 0);
                    if (produced != 16)
                    {
                        try { EncryptionDiagnostics.Emit("ctr_transform", "error", "transform_block_wrong_length", more: new Dictionary<string, string> { { "produced", produced.ToString(CultureInfo.InvariantCulture) } }); } catch { }
                        throw new TransformFailedException("transform_block");
                    }

                    int take = remaining >= 16 ? 16 : remaining;
                    // XOR keystream directly into destination (supports in-place)
                    XorInto(input.Slice(offset, take), output.Slice(offset, take), _keystream.AsSpan(0, take));

                    // Increment counter (big-endian increment of last 8 bytes)
                    if (IncrementCounter())
                    {
                        wrapped = true;
                    }

                    if (wrapped && remaining - take > 0)
                    {
                        // Prevent keystream reuse after 64-bit counter rollover
                        try { EncryptionDiagnostics.Emit("ctr_transform", "error", "counter_wrap_detected"); } catch { }
                        throw new TransformFailedException("counter_wrap");
                    }

                    offset += take;
                    remaining -= take;
                }
            }
            catch (Exception ex)
            {
                if (ex is TransformFailedException) throw;
                try { EncryptionDiagnostics.Emit("ctr_transform", "error", "transform_failed", ex); } catch { }
                throw new TransformFailedException("transform", ex);
            }
            try
            {
                EncryptionDiagnostics.Emit("ctr_transform", "info", "done",
                    more: new Dictionary<string, string>
                    {
                        { "bytes", input.Length.ToString(CultureInfo.InvariantCulture) },
                        { "blocks", ((input.Length + 15) / 16).ToString(CultureInfo.InvariantCulture) }
                    });
            }
            catch { }
        }

        // Returns true if the 64-bit counter region (bytes 8..15) wrapped to zero.
        private bool IncrementCounter()
        {
            // Interpret the last 8 bytes as a big-endian counter
            for (int i = 15; i >= 8; i--)
            {
                unchecked
                {
                    byte b = (byte)(_counter[i] + 1);
                    _counter[i] = b;
                    if (b != 0)
                    {
                        // No full 64-bit wrap occurred.
                        return false;
                    }
                }
            }
            // If all 8 bytes rolled over from 0xFF to 0x00, we completed a full 64-bit wrap.
            return true;
        }

        // Extracts the low 64 bits (big-endian) from a 16-byte counter block.
        private static ulong GetCounterLow64BigEndian(ReadOnlySpan<byte> ctr)
        {
            // Bytes 8..15 form the counter domain (big-endian)
            ulong v =
                ((ulong)ctr[8] << 56) |
                ((ulong)ctr[9] << 48) |
                ((ulong)ctr[10] << 40) |
                ((ulong)ctr[11] << 32) |
                ((ulong)ctr[12] << 24) |
                ((ulong)ctr[13] << 16) |
                ((ulong)ctr[14] << 8) |
                (ulong)ctr[15];
            return v;
        }

        // XOR 'src' with 'ks' into 'dst'. Supports in-place (dst==src) and overlapping spans.
        // Uses BinaryPrimitives for clarity and unaligned-safe wide XOR, with a byte tail.
        private static void XorInto(ReadOnlySpan<byte> src, Span<byte> dst, ReadOnlySpan<byte> ks)
        {
            int len = src.Length;
            int offset = 0;

            int u64Count = len / 8;
            for (int w = 0; w < u64Count; w++)
            {
                int o = offset + (w * 8);
                ulong a = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(src.Slice(o));
                ulong b = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(ks.Slice(o));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(dst.Slice(o), a ^ b);
            }
            offset += u64Count * 8;

            for (int i = offset; i < len; i++)
            {
                dst[i] = (byte)(src[i] ^ ks[i]);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Best-effort zero sensitive state before disposing crypto primitives
            try
            {
                int keyLen = _aes.Key?.Length ?? 0;
                if (keyLen > 0)
                {
                    var zero = new byte[keyLen];
                    try { CryptographicOperations.ZeroMemory(zero); _aes.Key = zero; } catch { }
                }
            }
            catch { }
            try { _ecb.Dispose(); } catch { }
            try { _aes.Dispose(); } catch { }
            try
            {
                // Zero sensitive state (counter block)
                CryptographicOperations.ZeroMemory(_counter);
                CryptographicOperations.ZeroMemory(_keystream);
            }
            catch { }
            try { EncryptionDiagnostics.Emit("ctr_dispose", "info", "transform_disposed"); } catch { }
        }
    }
}
