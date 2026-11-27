namespace WowDi.Hosting;

/// <summary>Cooperative cross-process writer exclusion for the local host. Never delete the lock file.
/// All host writes and two-file backups take this lease. Direct database access is outside this boundary.</summary>
internal static class WorkspaceWriteGate
{
    internal static FileStream Acquire(string directory, CancellationToken ct = default)
    {
        var path = Path.Combine(directory, ".workspace-write.lock");
        var start = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (start.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (ct.WaitHandle.WaitOne(25)) ct.ThrowIfCancellationRequested();
            }
            catch (IOException) { throw LocalWorkspace.Error("busy"); }
        }
    }
}
