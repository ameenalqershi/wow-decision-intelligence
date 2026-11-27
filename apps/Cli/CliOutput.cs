using System.Text.Json;

namespace WowDi.Cli;

public enum CliExitCode
{
    Success = 0, Usage = 2, Unsupported = 3, VerificationFailed = 4,
    InfrastructureFailure = 5, Cancelled = 130
}

public enum OutputFormat { Text, Json, Csv }

public sealed record CliOutputOptions(
    OutputFormat Format = OutputFormat.Text,
    bool NonInteractive = false, bool NoColor = false,
    bool Quiet = false, bool Verbose = false)
{
    public bool CanPrompt(bool inputRedirected, bool outputRedirected) =>
        Format == OutputFormat.Text && !NonInteractive && !Quiet && !inputRedirected && !outputRedirected;

    public bool CanColor(bool redirected, bool noColorEnvironment) =>
        Format == OutputFormat.Text && !NoColor && !redirected && !noColorEnvironment;
}

public sealed class CliOutput(TextWriter stdout, TextWriter stderr, CliOutputOptions options)
{
    private readonly object gate = new();
    private bool written;

    // This is a CLI presentation envelope, not a replacement for domain/SDK contracts.
    public void WriteResult(string command, IReadOnlyDictionary<string, string?> fields, string plainText)
    {
        lock (gate)
        {
            if (written) throw new InvalidOperationException("Only one result is permitted per invocation.");
            var result = options.Format switch
            {
                OutputFormat.Json => JsonSerializer.Serialize(new
                {
                    contract = "wowdi.cli.result",
                    version = "1.0",
                    payload = new { command, fields }
                }),
                OutputFormat.Csv => string.Join(",", new[] { "contractVersion", "command" }.Concat(fields.Keys).Select(CsvCell))
                    + "\r\n" + string.Join(",", new string?[] { "1.0", command }.Concat(fields.Values).Select(CsvCell)),
                _ => plainText
            };
            stdout.Write(result);
            stdout.Write(options.Format == OutputFormat.Csv ? "\r\n" : "\n");
            written = true;
        }
    }

    public void Progress(string message)
    {
        if (!options.Quiet) WriteDiagnostic(message);
    }

    public void Verbose(string message)
    {
        if (options.Verbose && !options.Quiet) WriteDiagnostic(message);
    }

    public void Error(string message) => WriteDiagnostic(message);

    private void WriteDiagnostic(string message)
    {
        // Prevent untrusted control sequences and multiline terminal injection.
        lock (gate)
            stderr.WriteLine(string.Concat(message.Select(c => char.IsControl(c) ? ' ' : c)));
    }

    private static string CsvCell(string? value)
    {
        value ??= "";
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
