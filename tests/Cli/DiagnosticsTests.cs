using System.Diagnostics;
using System.Text.Json;
using WowDi.Cli;
using Xunit;

namespace WowDi.Cli.Tests;

public sealed class DiagnosticsTests
{
    [Theory]
    [InlineData("doctor")]
    [InlineData("health")]
    [InlineData("config")]
    [InlineData("logs")]
    public async Task LocalDiagnosticDoesNotClaimRuntimeHealth(string command)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await CliApplication.RunAsync([command], output, error);
        Assert.Equal(0, code);
        Assert.Contains("local CLI host", output.ToString());
        Assert.Contains("not assessed", output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task DoctorCanInspectDirectoryWithoutChangingIt()
    {
        var directory = Directory.CreateTempSubdirectory("wowdi-doctor-");
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, await CliApplication.RunAsync(["doctor", "--workspace", directory.FullName], output, error));
            Assert.Contains("readable directory", output.ToString());
            Assert.Empty(directory.EnumerateFileSystemInfos());
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task DoctorReportsUnreadablePathWithoutEchoingIt()
    {
        var missing = Path.Combine(Path.GetTempPath(), "private-" + Guid.NewGuid().ToString("N"));
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(5, await CliApplication.RunAsync(["doctor", "--workspace", missing], output, error));
        Assert.Contains("unreadable", output.ToString());
        Assert.Contains("permissions", error.ToString());
        Assert.DoesNotContain(missing, output.ToString() + error.ToString());
    }

    [Fact]
    public async Task CredentialsNeverAppearInAnyDiagnosticStream()
    {
        var directory = Directory.CreateTempSubdirectory("wowdi-diag-");
        var marker = Guid.NewGuid().ToString("N");
        try
        {
            foreach (var command in new[] { "doctor", "health", "config", "logs" })
            {
                var info = new ProcessStartInfo("dotnet") { WorkingDirectory = directory.FullName, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                info.ArgumentList.Add(typeof(CliApplication).Assembly.Location);
                info.ArgumentList.Add(command);
                info.ArgumentList.Add("--format");
                info.ArgumentList.Add("json");
                info.Environment["WOWDI_API_TOKEN"] = marker;
                using var process = Process.Start(info)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await process.WaitForExitAsync(deadline.Token); }
                catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
                Assert.Equal(0, process.ExitCode);
                var result = await stdout;
                using var json = JsonDocument.Parse(result);
                Assert.Equal(command, json.RootElement.GetProperty("payload").GetProperty("command").GetString());
                Assert.DoesNotContain(marker, result + await stderr);
            }
        }
        finally { directory.Delete(true); }
    }
}
