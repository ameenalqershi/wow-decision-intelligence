using System.CommandLine;

namespace WowDi.Cli;

internal static class DiagnosticCommands
{
    public static void Register(RootCommand root, CommandOptions options, TextWriter output, TextWriter error)
    {
        Add("doctor", "Check the local CLI host and optional workspace directory.", true);
        Add("health", "Report local host readiness without contacting a server.", true);
        Add("config", "Inspect configuration sources without disclosing values.", false);
        Add("logs", "Inspect diagnostic logging availability without exposing log contents.", false);

        void Add(string name, string description, bool acceptsWorkspace)
        {
            var command = new Command(name, description);
            var workspace = new Option<string?>("--workspace") { Description = "Optional local workspace directory." };
            if (acceptsWorkspace) command.Options.Add(workspace);
            command.SetAction((parsed, token) =>
            {
                var report = LocalDiagnostics.Inspect(name, acceptsWorkspace ? parsed.GetValue(workspace) : null, token);
                var writer = new CliOutput(output, error, options.Read(parsed));
                var fields = new Dictionary<string, string?>(report.Fields) { ["message"] = report.Message };
                var text = string.Join(Environment.NewLine, fields.Select(pair => $"{pair.Key}: {pair.Value}"));
                writer.WriteResult(name, fields, text);
                if (report.ExitCode != 0) writer.Error(report.Message);
                return Task.FromResult(report.ExitCode);
            });
            root.Subcommands.Add(command);
        }
    }
}
