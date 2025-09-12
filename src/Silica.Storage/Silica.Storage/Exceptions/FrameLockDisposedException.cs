using Silica.Exceptions;

namespace Silica.Storage.Exceptions
{
    public sealed class FrameLockDisposedException : SilicaException
    {
        public string Reason { get; }

        public FrameLockDisposedException()
            : this("Evicted or closed during acquisition.") { }

        public FrameLockDisposedException(string reason)
            : base(
                code: StorageExceptions.FrameLockDisposed.Code,
                message: "Frame lock was disposed/evicted during acquisition.",
                category: StorageExceptions.FrameLockDisposed.Category,
                exceptionId: StorageExceptions.Ids.FrameLockDisposed)
        {
            StorageExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Reason = reason ?? string.Empty;
        }
    }
}
