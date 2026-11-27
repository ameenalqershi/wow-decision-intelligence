using Microsoft.Data.Sqlite;
using WowDi.Hosting;

// Test-only executable. Never shipped as a product tool.
if (args.Length != 2) return 2;
var workspace = LocalWorkspace.Open(args[1]);
if (args[0] == "commit")
{
    var settings = new LocalSettings(workspace);
    var current = settings.Read();
    var changed = settings.Apply(new(current.WorkspaceId, current.Revision, LocalDiagnosticLevel.Warning));
    Console.WriteLine($"COMMITTED:{changed.Revision}");
    return 0;
}
if (args[0] != "interrupt") return 2;
// Reproduce an abrupt process exit while an actual SQLite write transaction and
// the cooperative workspace lease are held. No commit is attempted.
using var lease = new FileStream(Path.Combine(workspace.DirectoryPath, ".workspace-write.lock"),
    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
using var db = new SqliteConnection(new SqliteConnectionStringBuilder
{
    DataSource = Path.Combine(workspace.DirectoryPath, "workspace.db"),
    Pooling = false, ForeignKeys = true
}.ToString());
db.Open();
using var tx = db.BeginTransaction(deferred: false);
using var cmd = db.CreateCommand();
cmd.Transaction = tx;
cmd.CommandText = "UPDATE local_settings SET revision=revision+1, diagnostic_level=3 WHERE singleton=1";
if (cmd.ExecuteNonQuery() != 1) return 3;
Console.WriteLine("UNCOMMITTED");
Console.Out.Flush();
// Parent explicitly terminates this test process; stdin closure is a safe cleanup fallback.
Console.ReadLine();
return 0;
