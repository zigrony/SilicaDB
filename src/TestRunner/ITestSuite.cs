using System.Threading.Tasks;

namespace SilicaDB.TestRunner
{
    public interface ITestSuite
    {
        /// <summary>Used in the CLI and summary table.</summary>
        string Name { get; }

        /// <summary>Runs all tests in this suite; throw on failure.</summary>
        Task RunAsync(TestContext ctx);
    }
}
