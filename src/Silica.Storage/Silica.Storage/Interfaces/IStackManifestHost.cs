using System;
using System.Threading;
using System.Threading.Tasks;

namespace Silica.Storage.Interfaces
{
    /// <summary>
    /// Implemented by devices that own the on-device metadata frame (frame 0)
    /// and can validate or initialize the stack manifest at mount time.
    /// </summary>
    public interface IStackManifestHost
    {
        /// <summary>
        /// Provide the expected manifest for this device instance.
        /// Must be called before MountAsync. Subsequent calls overwrite.
        /// </summary>
        void SetExpectedManifest(DeviceManifest manifest);
    }
}
