using System;

namespace ScamWYF.Setup
{
    /// <summary>
    /// A setup step could not be completed, and the message already says what to do about it.
    /// </summary>
    /// <remarks>
    /// Distinct from the framework exceptions so a caller can show the text as-is - the CLI prints it
    /// without a stack trace and the GUI shows it in a dialog - while an unexpected exception still
    /// surfaces as a bug.
    /// </remarks>
    public sealed class SetupException : Exception
    {
        public SetupException(string message) : base(message) { }
        public SetupException(string message, Exception inner) : base(message, inner) { }
    }
}
