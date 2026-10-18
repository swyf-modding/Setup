using System;
using System.Collections.Generic;
using System.Linq;

namespace ScamWYF.Setup.Cli
{
    /// <summary>
    /// A small argument parser: enough for `--name value`, `--name=value` and `--flag`.
    /// </summary>
    /// <remarks>
    /// Hand-rolled on purpose. It has to behave the same whether it is launched as `swyf-setup` or as
    /// `dotnet run -- ...`, and a command-line library would be one more dependency in a tool whose
    /// point is that it runs anywhere with nothing installed.
    /// </remarks>
    internal sealed class CommandLine
    {
        private static readonly HashSet<string> ValueOptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "game-dir", "payload-dir", "bepinex-zip", "bepinex-version", "assemblies",
            "override-dir", "unstripped", "managed", "dir", "core-dir"
        };

        private readonly Dictionary<string, string> _options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string Command { get; private set; }

        public static CommandLine Parse(string[] args)
        {
            var result = new CommandLine();
            args = args ?? Array.Empty<string>();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                if (arg.StartsWith("-", StringComparison.Ordinal))
                {
                    var name = arg.TrimStart('-');
                    var equals = name.IndexOf('=');
                    if (equals >= 0)
                    {
                        result._options[name.Substring(0, equals)] = name.Substring(equals + 1);
                        continue;
                    }

                    if (ValueOptions.Contains(name) && i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
                    {
                        result._options[name] = args[++i];
                        continue;
                    }

                    // An option that can take a value but was given without one is recorded as a flag,
                    // so a caller can tell "not given" from "given empty".
                    result._flags.Add(name);
                    continue;
                }

                if (result.Command == null) result.Command = arg;
            }

            return result;
        }

        public bool Has(string name) => _flags.Contains(name) || _options.ContainsKey(name);

        public string Get(string name) => _options.TryGetValue(name, out var value) ? value : null;

        public IEnumerable<string> Positionals => Enumerable.Empty<string>();
    }
}
