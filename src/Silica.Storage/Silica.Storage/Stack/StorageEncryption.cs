using Silica.Storage.Encryption;
using Silica.Storage.Interfaces;

namespace Silica.Storage.Stack
{
    public static class StorageEncryption
    {
        public static IStorageDevice WithEncryption(
            this IStorageDevice device,
            IEncryptionKeyProvider keys,
            CounterDerivationConfig cfg)
        {
            if (device is null) throw new System.ArgumentNullException(nameof(device));
            if (keys is null) throw new System.ArgumentNullException(nameof(keys));
            return new EncryptionDevice(device, keys, cfg);
        }

        public static IStorageDevice WithEncryption(
            this IStorageDevice device,
            IEncryptionKeyProvider keys)
        {
            if (device is null) throw new System.ArgumentNullException(nameof(device));
            if (keys is null) throw new System.ArgumentNullException(nameof(keys));
            return new EncryptionDevice(device, keys);
        }
    }
}
