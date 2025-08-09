using System;

namespace SilicaDB.TestRunner
{
    public class TestContext
    {
        // parameterless ctor for our suites
        public TestContext() { }

        // overload so Program.cs that does `new TestContext("foo")` still compiles
        public TestContext(string _ignored) : this() { }

        public void WriteInfo(string message)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(message);
            Console.ResetColor();
        }

        public void WritePass(string message)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(message);
            Console.ResetColor();
        }

        public void WriteFail(string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(message);
            Console.ResetColor();
        }
    }
}
