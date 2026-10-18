using System;
using ScamWYF.Setup;

namespace ScamWYF.Setup.Cli
{
    /// <summary>The setup log, as coloured terminal output.</summary>
    internal sealed class ConsoleSetupLog : ISetupLog
    {
        public void Write(SetupLogLevel level, string message)
        {
            var previous = Console.ForegroundColor;
            try
            {
                Console.ForegroundColor = ColourFor(level);
                Console.WriteLine(message ?? string.Empty);
            }
            finally
            {
                Console.ForegroundColor = previous;
            }
        }

        private static ConsoleColor ColourFor(SetupLogLevel level)
        {
            switch (level)
            {
                case SetupLogLevel.Success: return ConsoleColor.Green;
                case SetupLogLevel.Warning: return ConsoleColor.Yellow;
                case SetupLogLevel.Error: return ConsoleColor.Red;
                case SetupLogLevel.Detail: return ConsoleColor.DarkGray;
                default: return Console.ForegroundColor;
            }
        }
    }
}
