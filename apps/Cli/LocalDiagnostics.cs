using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace WowDi.Cli;

internal sealed record DiagnosticReport(int ExitCode, IReadOnlyDictionary<string, string?> Fields, string Message);

internal static class LocalDiagnostics
{
    public static DiagnosticReport Inspect(string command, string? workspace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["scope"] = "local CLI host",
            ["runtime"] = "not assessed",
            ["remote"] = "not assessed"
        };

        try
        {
            using var host = CliApplication.CreateHost();
            fields["configuration"] = "loaded";
            if (command == "config")
            {
                fields["sources"] = "optional user configuration; WOWDI_ environment overrides";
                fields["values"] = "redacted";
            }
            if (command == "logs")
            {
                fields["loggingProviders"] = host.Services.GetServices<ILoggerProvider>().Count().ToString(System.Globalization.CultureInfo.InvariantCulture);
                fields["diagnostics"] = "stderr";
                fields["persistentLogStore"] = "not configured by CLI host";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or InvalidOperationException)
        {
            fields["configuration"] = "unreadable or invalid";
            return new(5, fields, "Check the user configuration file and its access permissions. Configuration values are not displayed.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (workspace is null)
        {
            fields["workspace"] = "not selected";
            return new(0, fields, "CLI host inspection completed; no workspace or solver initialized.");
        }

        try
        {
            // Enumerate only to verify access, never disclose a filename or path.
            using var entries = Directory.EnumerateFileSystemEntries(workspace).GetEnumerator();
            _ = entries.MoveNext();
            cancellationToken.ThrowIfCancellationRequested();
            fields["workspace"] = "readable directory";
            return new(0, fields, "Directory access verified; workspace data and solver health were not assessed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            fields["workspace"] = "unreadable or not a directory";
            return new(5, fields, "Check that the selected workspace is an existing readable directory and verify permissions.");
        }
    }
}
