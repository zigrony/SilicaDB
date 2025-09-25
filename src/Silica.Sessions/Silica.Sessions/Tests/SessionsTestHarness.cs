using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Silica.Sessions.Contracts;
using Silica.Sessions.Implementation;
using Silica.Exceptions;
using Silica.DiagnosticsCore;
using Silica.DiagnosticsCore.Metrics;

namespace Silica.Sessions.Tests
{
    /// <summary>
    /// Comprehensive test harness for Silica.Sessions.
    /// Covers creation, retrieval, activity updates, closure, eviction, and invalid operations.
    /// No LINQ, no reflection, no JSON, no third-party libraries.
    /// </summary>
    public static class SessionsTestHarness
    {
        public static async Task Run()
        {
            Console.WriteLine("=== Sessions Test Harness ===");
            Console.WriteLine("Working directory: " + Environment.CurrentDirectory);

            // Bootstrap diagnostics
            if (!DiagnosticsCoreBootstrap.IsStarted)
            {
                try
                {
                    var options = new DiagnosticsOptions
                    {
                        EnableTracing = true,
                        EnableMetrics = true
                    };
                    DiagnosticsCoreBootstrap.Start(options, null);
                }
                catch { }
            }

            IMetricsManager? metrics = null;
            try { metrics = DiagnosticsCoreBootstrap.Instance.Metrics; } catch { }

            ISessionManager mgr = new SessionManager(metrics);

            await RunTest("[Test] Create and retrieve session", () => TestCreateAndRetrieve(mgr));
            await RunTest("[Test] Touch updates activity and state", () => TestTouchUpdatesState(mgr));
            await RunTest("[Test] Close session removes it", () => TestCloseSession(mgr));
            await RunTest("[Test] Invalid session lookup throws", () => TestInvalidLookup(mgr));
            await RunTest("[Test] Multiple sessions tracked independently", () => TestMultipleSessions(mgr));
            await RunTest("[Test] Evict idle sessions", () => TestEvictIdleSessions(mgr));

            Console.WriteLine("=== Sessions Test Harness Complete ===");
        }

        private static async Task RunTest(string name, Action test)
        {
            Console.WriteLine(name);
            var sw = Stopwatch.StartNew();
            try
            {
                test();
                Console.WriteLine(name + " passed in " + sw.Elapsed.TotalMilliseconds + " ms.");
            }
            catch (Exception ex)
            {
                Console.WriteLine(name + " FAILED: " + ex.GetType().Name + " - " + ex.Message);
                throw;
            }
            finally
            {
                sw.Stop();
            }
            await Task.CompletedTask;
        }

        private static void TestCreateAndRetrieve(ISessionManager mgr)
        {
            var s = mgr.CreateSession("user1", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), "nodeA");
            var retrieved = mgr.GetSession(s.SessionId);
            if (retrieved.SessionId != s.SessionId) throw new Exception("SessionId mismatch");
        }

        private static void TestTouchUpdatesState(ISessionManager mgr)
        {
            var s = mgr.CreateSession("user2", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), "nodeA");
            mgr.Touch(s.SessionId);
            var retrieved = mgr.GetSession(s.SessionId);
            if (retrieved.State != SessionState.Active) throw new Exception("Expected Active state after Touch");
        }

        private static void TestCloseSession(ISessionManager mgr)
        {
            var s = mgr.CreateSession("user3", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), "nodeA");
            mgr.Close(s.SessionId);

            // Expect SessionNotFoundException when retrieving a closed session
            try
            {
                mgr.GetSession(s.SessionId);
                throw new Exception("Expected SessionNotFoundException not thrown");
            }
            catch (SessionNotFoundException)
            {
                // success
            }
        }

        private static void TestInvalidLookup(ISessionManager mgr)
        {
            var bogusId = Guid.NewGuid();
            try
            {
                mgr.GetSession(bogusId);
                throw new Exception("Expected SessionNotFoundException not thrown");
            }
            catch (SessionNotFoundException)
            {
                // success
            }
        }

        private static void TestMultipleSessions(ISessionManager mgr)
        {
            var s1 = mgr.CreateSession("alpha", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), "nodeA");
            var s2 = mgr.CreateSession("beta", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1), "nodeA");

            var r1 = mgr.GetSession(s1.SessionId);
            var r2 = mgr.GetSession(s2.SessionId);

            if (r1.SessionId == r2.SessionId) throw new Exception("Sessions not independent");
        }

        private static void TestEvictIdleSessions(ISessionManager mgr)
        {
            var s = mgr.CreateSession("idleUser", TimeSpan.FromMilliseconds(1), TimeSpan.FromMinutes(1), "nodeA");
            System.Threading.Thread.Sleep(10); // ensure idle timeout passes
            var evicted = mgr.EvictIdleSessions(DateTime.UtcNow);
            if (evicted < 1) throw new Exception("Expected at least one session to be evicted");
        }
    }
}
