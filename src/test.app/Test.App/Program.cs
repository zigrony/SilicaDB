using Silica.Exceptions.Testing;
using Silica.DiagnosticsCore.Tests;
using Silica.Evictions;
using Silica.Evictions.Tests;
using Silica.Storage.Tests;
class Program
{
    static void Main(string[] args)
    {
        ExceptionTestHarness.Run();
        DiagnosticsCoreTestHarness.Run();
        EvictionsTestHarness.Run();
        StorageTestHarness.Run();

    }
}
