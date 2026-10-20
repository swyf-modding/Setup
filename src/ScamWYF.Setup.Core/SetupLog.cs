using System;
using System.IO;

namespace ScamWYF.Setup
{
    /// <summary>How a line of setup output should be read.</summary>
    public enum SetupLogLevel
    {
        Info,
        Success,
        Warning,
        Error,
        Detail
    }

    /// <summary>
    /// Somewhere for the setup steps to report what they are doing.
    /// </summary>
    /// <remarks>
    /// The same steps feed a terminal, a GUI and the test suite. They write here rather than to the
    /// console so Switchboard can marshal each line onto its own thread, and so a test can assert on
    /// what was said without capturing a process's stdout.
    /// </remarks>
    public interface ISetupLog
    {
        void Write(SetupLogLevel level, string message);
    }

    public sealed class NullSetupLog : ISetupLog
    {
        public static readonly NullSetupLog Instance = new NullSetupLog();
        private NullSetupLog() { }
        public void Write(SetupLogLevel level, string message) { }
    }

    public sealed class DelegateSetupLog : ISetupLog
    {
        private readonly Action<SetupLogLevel, string> _sink;

        public DelegateSetupLog(Action<SetupLogLevel, string> sink)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        public void Write(SetupLogLevel level, string message)
        {
            _sink(level, message);
        }
    }

    public sealed class TextWriterSetupLog : ISetupLog
    {
        private readonly TextWriter _writer;

        public TextWriterSetupLog(TextWriter writer)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        }

        public void Write(SetupLogLevel level, string message)
        {
            _writer.WriteLine(message);
        }
    }

    public static class SetupLogExtensions
    {
        public static void Info(this ISetupLog log, string message) => log.Write(SetupLogLevel.Info, message);
        public static void Success(this ISetupLog log, string message) => log.Write(SetupLogLevel.Success, message);
        public static void Warning(this ISetupLog log, string message) => log.Write(SetupLogLevel.Warning, message);
        public static void Error(this ISetupLog log, string message) => log.Write(SetupLogLevel.Error, message);
        public static void Detail(this ISetupLog log, string message) => log.Write(SetupLogLevel.Detail, message);

        public static void Heading(this ISetupLog log, string message)
        {
            log.Write(SetupLogLevel.Info, string.Empty);
            log.Write(SetupLogLevel.Info, "=== " + message + " ===");
        }
    }
}
