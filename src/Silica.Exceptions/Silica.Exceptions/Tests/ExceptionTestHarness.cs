// Filename: Silica.Exceptions/Tests/ExceptionTestHarness.cs
using System;
using Silica.Exceptions;

namespace Silica.Exceptions.Testing
{
    public static class ExceptionTestHarness
    {
        private sealed class ConsoleObserver : IExceptionObserver
        {
            public void OnCreated(SilicaException ex)
            {
                Console.WriteLine($"[Observer] Exception created: {ex.Code} ({ex.Category}) Id={ex.ExceptionId}");
            }
        }

        // One CLR type per (Code, Category) schema
        private sealed class TestInvalidException : SilicaException
        {
            // Optional: declare the exact CLR type name to enable strict registry/type enforcement
            // when EnforceRegisteredCodes is enabled.
            protected override string? DeclaredExceptionTypeName =>
                "Silica.Exceptions.Testing.ExceptionTestHarness+TestInvalidException";

            public TestInvalidException(string code, string message, FailureCategory category, int exceptionId)
                : base(code, message, category, exceptionId) { }
        }

        private sealed class TestNotFoundException : SilicaException
        {
            protected override string? DeclaredExceptionTypeName =>
                "Silica.Exceptions.Testing.ExceptionTestHarness+TestNotFoundException";

            public TestNotFoundException(string code, string message, FailureCategory category, int exceptionId)
                : base(code, message, category, exceptionId) { }
        }

        public static void Run()
        {
            Console.WriteLine("=== Silica.Exceptions Test Harness ===");

            // Register an observer
            ExceptionRegistry.AddObserver(new ConsoleObserver());

            // Define and register test exceptions (distinct CLR types for distinct schemas)
            var defInvalid = ExceptionDefinition.Create(
                "TEST.INVALID",
                "Silica.Exceptions.Testing.ExceptionTestHarness+TestInvalidException",
                FailureCategory.Validation,
                "An invalid operation occurred."
            );

            var defNotFound = ExceptionDefinition.Create(
                "TEST.NOTFOUND",
                "Silica.Exceptions.Testing.ExceptionTestHarness+TestNotFoundException",
                FailureCategory.Configuration,
                "The requested resource was not found."
            );

            ExceptionRegistry.Register(defInvalid);
            ExceptionRegistry.Register(defNotFound);

            Console.WriteLine("Registered test exception definitions.");

            // Throw and catch: Invalid
            try
            {
                throw new TestInvalidException(defInvalid.Code, "Simulated invalid operation", defInvalid.Category, exceptionId: 1000);
            }
            catch (SilicaException ex)
            {
                Console.WriteLine($"[Catch] Caught SilicaException: {ex.Code} ({ex.Category}) Id={ex.ExceptionId}");
                Console.WriteLine($"        Message: {ex.Message}");

                if (ExceptionRegistry.TryGetByCode(ex.Code, out var def))
                {
                    Console.WriteLine($"        Definition: {def.ExceptionTypeName} - {def.Description}");
                }
            }

            // Throw and catch: NotFound
            try
            {
                throw new TestNotFoundException(defNotFound.Code, "Simulated missing resource", defNotFound.Category, exceptionId: 1001);
            }
            catch (SilicaException ex)
            {
                Console.WriteLine($"[Catch] Caught SilicaException: {ex.Code} ({ex.Category}) Id={ex.ExceptionId}");
                Console.WriteLine($"        Message: {ex.Message}");

                if (ExceptionRegistry.TryGetByCode(ex.Code, out var def))
                {
                    Console.WriteLine($"        Definition: {def.ExceptionTypeName} - {def.Description}");
                }
            }

            Console.WriteLine("=== Test Harness Complete ===");
        }
    }
}
