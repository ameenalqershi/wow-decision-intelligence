using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using WowDi.Cli;
using Xunit;

namespace WowDi.Cli.Tests;

public sealed class AutomationTests
{
    [Fact]
    public async Task JsonPipeSeparatesDiagnostics()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await CliApplication.RunAsync(["version", "--format", "json", "--verbose", "--non-interactive"], output, error));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("1.0", json.RootElement.GetProperty("version").GetString());
        Assert.Equal("version", json.RootElement.GetProperty("payload").GetProperty("command").GetString());
        Assert.Equal(new BuildVersion().Value, json.RootElement.GetProperty("payload").GetProperty("fields").GetProperty("version").GetString());
        Assert.Contains("Resolved installed build version", error.ToString());
        Assert.DoesNotContain('\u001b', output.ToString());
    }

    [Fact]
    public void CsvQuotedCellsRoundTrip()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var writer = new CliOutput(output, error, new(OutputFormat.Csv));
        writer.Progress("Loaded input.");
        writer.WriteResult("fixture", new Dictionary<string, string?> { ["value"] = "a,\"b\"\r\nc" }, "unused");
        using var parser = new TextFieldParser(new StringReader(output.ToString())) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        Assert.Equal(new[] { "contractVersion", "command", "value" }, parser.ReadFields());
        Assert.Equal(new[] { "1.0", "fixture", "a,\"b\"\r\nc" }, parser.ReadFields());
        Assert.True(parser.EndOfData);
        Assert.Contains("Loaded input.", error.ToString());
        Assert.Throws<InvalidOperationException>(() => writer.WriteResult("duplicate", new Dictionary<string, string?>(), ""));
    }

    [Fact]
    public async Task CsvAndQuietThroughParser()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await CliApplication.RunAsync(["version", "--format", "csv", "--quiet", "--no-color"], output, error));
        Assert.StartsWith("contractVersion,command,version\r\n1.0,version,", output.ToString());
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData(OutputFormat.Json, false, false, false)]
    [InlineData(OutputFormat.Csv, false, false, false)]
    [InlineData(OutputFormat.Text, true, false, false)]
    [InlineData(OutputFormat.Text, false, true, true)]
    public void TerminalPolicy(OutputFormat format, bool nonInteractive, bool noColor, bool expectedPrompt)
    {
        var options = new CliOutputOptions(format, nonInteractive, noColor);
        Assert.Equal(expectedPrompt, options.CanPrompt(false, false));
        Assert.False(options.CanPrompt(true, false));
        Assert.False(options.CanColor(false, true));
        Assert.False(options.CanColor(true, false));
        if (noColor || format != OutputFormat.Text) Assert.False(options.CanColor(false, false));
    }

    [Fact]
    public void QuietSuppressesProgressButNotErrors()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var writer = new CliOutput(output, error, new(Quiet: true));
        writer.Progress("progress");
        writer.Verbose("detail");
        writer.Error("failed\u001b[2J");
        Assert.Empty(output.ToString());
        Assert.DoesNotContain("progress", error.ToString());
        Assert.DoesNotContain('\u001b', error.ToString());
        Assert.Contains("failed", error.ToString());
    }

    [Theory]
    [InlineData("version", "--format", "xml")]
    [InlineData("version", "--quiet", "--verbose")]
    public async Task InvalidAutomationOptions(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await CliApplication.RunAsync(args, output, error));
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task ShellCompletionUsesRegisteredCommands()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await CliApplication.RunAsync(["[suggest]", "ver"], output, error));
        Assert.Contains("version", output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task CancellationTerminatesOwnedChild()
    {
        var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "ping.exe" : "/bin/sleep") { UseShellExecute = false, RedirectStandardOutput = true };
        if (OperatingSystem.IsWindows())
        {
            foreach (var arg in new[] { "-n", "60", "127.0.0.1" }) info.ArgumentList.Add(arg);
        }
        else info.ArgumentList.Add("60");
        using var process = Process.Start(info)!;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OwnedProcess.WaitAsync(process, cancellation.Token));
            Assert.True(process.HasExited);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    [Fact]
    public void ExitCodesStayStable()
    {
        Assert.Equal(new[] { 0, 2, 3, 4, 5, 130 }, Enum.GetValues<CliExitCode>().Select(x => (int)x));
    }
}
