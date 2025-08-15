using System.Threading.Tasks;
using SilicaDB.Diagnostics.Tracing;

namespace SilicaDB.Diagnostics.Tracing
{

    public interface ITraceSink : IAsyncDisposable, IDisposable
    {
        // Fast, non-blocking contract: sinks must buffer or drop; never block Emit().
        void Append(in TraceEvent evt);

        // Optional lifecycle
        ValueTask FlushAsync(CancellationToken ct = default);
    }
}