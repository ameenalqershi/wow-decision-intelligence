using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WowDi.Cli;
using Xunit;

namespace WowDi.Cli.Tests;

public sealed class CommandHostTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--version")]
    [InlineData("version")]
    public async Task CleanDirectoryLaunch(string arg)
    {
        using var directory = new TempDirectory();
        var result = await Launch(directory.Path, arg);
        Assert.Equal(0, result.Code);
        Assert.Empty(result.Error);
        if (arg is "--version" or "version")
            Assert.Equal(new BuildVersion().Value, result.Output.Trim());
        else
            Assert.Contains("version", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("--invalid")]
    [InlineData("version", "--invalid")]
    [InlineData("version", "unexpected")]
    [InlineData("@missing.rsp")]
    public async Task InvalidSyntaxReturnsUsage(params string[] args)
    {
        using var directory = new TempDirectory();
        var result = await Launch(directory.Path, args);
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Output);
        Assert.Contains("--help", result.Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public void VersionAndConfigurationAreResolvedByDi()
    {
        using var directory = new TempDirectory();
        var path = System.IO.Path.Combine(directory.Path, "config.json");
        File.WriteAllText(path, """{"Test":{"Value":"local"}}""");
        using var host = CliApplication.CreateHost(path);
        var services = host.Services;
        var first = services.GetRequiredService<BuildVersion>();
        Assert.Same(first, services.GetRequiredService<BuildVersion>());
        Assert.Equal(new BuildVersion().Value, first.Value);
        Assert.Equal("local", services.GetRequiredService<IConfiguration>()["Test:Value"]);
    }

    [Fact]
    public async Task CancelledInvocationReturns130()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var source = new CancellationTokenSource();
        source.Cancel();
        var code = await CliApplication.RunAsync(["version"], output, error, source.Token);
        Assert.Equal(130, code);
        Assert.Empty(output.ToString());
        Assert.Contains("cancelled", error.ToString());
    }

    private static async Task<(int Code, string Output, string Error)> Launch(string directory, params string[] args)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add(typeof(CliApplication).Assembly.Location);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        // Isolate from user configuration and network proxy credentials.
        foreach (var key in new[] { "HOME", "XDG_CONFIG_HOME", "APPDATA", "USERPROFILE" })
            info.Environment[key] = directory;
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, await output, await error);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("wowdi-cli-test-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
