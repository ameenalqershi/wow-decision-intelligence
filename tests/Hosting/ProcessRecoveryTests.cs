using System.Diagnostics;
using WowDi.Hosting;
using Xunit;

namespace WowDi.Tests;

public sealed class ProcessRecoveryTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("wowdi-process-").FullName;
    private static Process Start(string mode, LocalWorkspace workspace)
    {
        var info = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "WowDi.Hosting.ProcessProbe.dll"));
        info.ArgumentList.Add(mode);
        info.ArgumentList.Add(workspace.DirectoryPath);
        return Process.Start(info) ?? throw new InvalidOperationException("Probe process did not start.");
    }

    [Fact]
    public async Task IndependentProcessCrashRollsBackAndReleasesWorkspaceLease()
    {
        var workspace = LocalWorkspace.Create(Path.Combine(root, "original"), "tenant");
        var settings = new LocalSettings(workspace);
        var before = settings.Read();
        using var child = Start("interrupt", workspace);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stderr = child.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            var marker = await child.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.Equal("UNCOMMITTED", marker);
            // Another host process owns the lease. A competing backup must wait/cancel.
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            Assert.Throws<OperationCanceledException>(() =>
                WorkspaceBackup.RestoreCopy(workspace, Path.Combine(root, "blocked"), cancel.Token));
            Assert.False(Directory.Exists(Path.Combine(root, "blocked")));
            // SQLite readers see committed state, not the child's uncommitted update.
            Assert.Equal(before, settings.Read());
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(timeout.Token);
            Assert.NotEqual(0, child.ExitCode);
            Assert.Equal(before, new LocalSettings(LocalWorkspace.Open(workspace.DirectoryPath)).Read());
            using var successor = Start("commit", workspace);
            try
            {
                var successorError = successor.StandardError.ReadToEndAsync(timeout.Token);
                Assert.Equal("COMMITTED:1", await successor.StandardOutput.ReadLineAsync(timeout.Token));
                await successor.WaitForExitAsync(timeout.Token);
                Assert.True(successor.ExitCode == 0, await successorError);
            }
            finally
            {
                if (!successor.HasExited) { successor.Kill(entireProcessTree: true); await successor.WaitForExitAsync(); }
            }
            var after = new LocalSettings(LocalWorkspace.Open(workspace.DirectoryPath)).Read();
            Assert.Equal(1, after.Revision);
            Assert.Equal(LocalDiagnosticLevel.Warning, after.DiagnosticLevel);
            var copy = WorkspaceBackup.RestoreCopy(workspace, Path.Combine(root, "restored"), timeout.Token);
            Assert.NotEqual(workspace.Identity.WorkspaceId, copy.Identity.WorkspaceId);
            Assert.Equal(after with { WorkspaceId = copy.Identity.WorkspaceId }, new LocalSettings(copy).Read());
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            _ = await stderr;
        }
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
