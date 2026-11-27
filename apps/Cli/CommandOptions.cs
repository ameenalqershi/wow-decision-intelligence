using System.CommandLine;

namespace WowDi.Cli;

internal sealed class CommandOptions
{
    private readonly Option<string> format = new("--format") { Description = "Result format: text, json or csv.", Recursive = true, DefaultValueFactory = _ => "text" };
    private readonly Option<bool> nonInteractive = new("--non-interactive") { Description = "Never prompt for input.", Recursive = true };
    private readonly Option<bool> noColor = new("--no-color") { Description = "Disable terminal colors.", Recursive = true };
    private readonly Option<bool> quiet = new("--quiet") { Description = "Suppress progress and informational messages.", Recursive = true };
    private readonly Option<bool> verbose = new("--verbose") { Description = "Include diagnostic detail on stderr.", Recursive = true };

    public void Register(RootCommand root)
    {
        format.AcceptOnlyFromAmong("text", "json", "csv");
        foreach (var option in new Option[] { format, nonInteractive, noColor, quiet, verbose })
            root.Options.Add(option);
    }

    public CliOutputOptions Read(ParseResult parsed) => new(
        parsed.GetValue(format) switch { "json" => OutputFormat.Json, "csv" => OutputFormat.Csv, _ => OutputFormat.Text },
        parsed.GetValue(nonInteractive), parsed.GetValue(noColor),
        parsed.GetValue(quiet), parsed.GetValue(verbose));
}
