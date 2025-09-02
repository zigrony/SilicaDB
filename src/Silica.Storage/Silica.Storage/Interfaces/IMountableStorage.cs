using Silica.Storage.Interfaces;
using System.Threading;
using System.Threading.Tasks;

namespace Silica.Storage.Interfaces
{
    public interface IMountableStorage
    {
        Task MountAsync(CancellationToken cancellationToken = default);
        Task UnmountAsync(CancellationToken cancellationToken = default);
    }
}
