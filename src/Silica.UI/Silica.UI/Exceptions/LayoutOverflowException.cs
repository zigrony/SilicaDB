using System;
using Silica.Exceptions;
using System.Drawing;

namespace Silica.UI.Exceptions
{
    public sealed class LayoutOverflowException : SilicaException
    {
        public string ContainerId { get; }
        public Rectangle ChildBounds { get; }
        public Rectangle ContainerBounds { get; }

        public LayoutOverflowException(string containerId, Rectangle childBounds, Rectangle containerBounds)
            : base(
                code: UIExceptions.LayoutOverflow.Code,
                message: $"Layout overflow in container '{containerId}'. Child {childBounds} exceeds {containerBounds}.",
                category: UIExceptions.LayoutOverflow.Category,
                exceptionId: UIExceptions.Ids.LayoutOverflow)
        {
            UIExceptions.ValidateId(ExceptionId, nameof(ExceptionId));
            ContainerId = containerId ?? string.Empty;
            ChildBounds = childBounds;
            ContainerBounds = containerBounds;
        }
    }
}
