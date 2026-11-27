using System.Diagnostics;

namespace WowDi.Cli;

// For CLI-owned subprocesses only. The runtime owns solver admission and isolation.
public static class OwnedProcess
{
    public static async Task<int> WaitAsync(Process process, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }
}
