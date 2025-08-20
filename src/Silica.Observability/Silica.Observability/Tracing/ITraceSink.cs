using System.Threading.Tasks;
using Silica.Observability.Tracing;

namespace Silica.Observability.Tracing
{

    public interface ITraceSink : IAsyncDisposable, IDisposable
    {
        // Fast, non-blocking contract: sinks must buffer or drop; never block Emit().
        void Append(in TraceEvent evt);

        // Optional lifecycle
        ValueTask FlushAsync(CancellationToken ct = default);
    }
}