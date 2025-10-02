using System;
using Silica.BufferPool;
using Silica.Exceptions;
using Silica.Common.Primitives;

namespace Silica.PageAccess
{
    public sealed class PageHeaderInvalidException : SilicaException
    {
        // Optional context (not always available in low-level helpers)
        public long FileId { get; }
        public long PageIndex { get; }
        public string Reason { get; }

        // Minimal constructor (keeps call sites simple where only a message exists)
        public PageHeaderInvalidException(string message)
            : base(
                code: PageAccessExceptions.PageHeaderInvalid.Code,
                message: message ?? "Invalid page header.",
                category: PageAccessExceptions.PageHeaderInvalid.Category,
                exceptionId: PageAccessExceptions.Ids.PageHeaderInvalid)
        {
            PageAccessExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            Reason = message ?? string.Empty;
        }

        public PageHeaderInvalidException(in PageId id, string reason)
            : base(
                code: PageAccessExceptions.PageHeaderInvalid.Code,
                message: $"Invalid page header at {id.FileId}/{id.PageIndex}: {reason}",
                category: PageAccessExceptions.PageHeaderInvalid.Category,
                exceptionId: PageAccessExceptions.Ids.PageHeaderInvalid)
        {
            PageAccessExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            FileId = id.FileId;
            PageIndex = id.PageIndex;
            Reason = reason ?? string.Empty;
        }
    }
}
