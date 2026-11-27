using Microsoft.Extensions.Logging;

namespace WowDi.Hosting;

public enum LocalDiagnosticLevel { Error, Warning, Information, Debug, None }

public sealed record LocalSettingsSnapshot(string WorkspaceId, long Revision, LocalDiagnosticLevel DiagnosticLevel);
public sealed record LocalSettingsChange(string WorkspaceId, long ExpectedRevision, LocalDiagnosticLevel DiagnosticLevel);
public sealed record LocalDiagnosticSnapshot(string WorkspaceId, long ConfigRevision, LocalDiagnosticLevel DiagnosticLevel,
    string StorageHealth, string SolverHealth, string RemoteHealth);

/// <summary>Workspace-only settings. No secret, remote address or arbitrary key/value bag is accepted.</summary>
public sealed class LocalSettings(LocalWorkspace workspace)
{
    public LocalSettingsSnapshot Read()
    {
        using var db = workspace.OpenDatabase(readOnly: true);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT revision,diagnostic_level FROM local_settings WHERE singleton=1";
        using var row = cmd.ExecuteReader();
        if (!row.Read()) throw LocalWorkspace.Error("settingsMissing");
        var revision = row.GetInt64(0);
        var raw = row.GetInt64(1);
        if (revision < 0 || raw < 0 || raw > (int)LocalDiagnosticLevel.None)
            throw LocalWorkspace.Error("settingsInvalid");
        return new(workspace.Identity.WorkspaceId, revision, (LocalDiagnosticLevel)raw);
    }

    public LocalSettingsSnapshot Apply(LocalSettingsChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.WorkspaceId != workspace.Identity.WorkspaceId) throw LocalWorkspace.Error("settingsScope");
        if (!Enum.IsDefined(change.DiagnosticLevel) || change.ExpectedRevision < 0 || change.ExpectedRevision == long.MaxValue)
            throw LocalWorkspace.Error("settingsInvalid");
        using var db = workspace.OpenDatabase();
        using var tx = db.BeginTransaction(deferred: false);
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE local_settings SET revision=revision+1, diagnostic_level=$level
            WHERE singleton=1 AND revision=$expected
            """;
        cmd.Parameters.AddWithValue("$level", (int)change.DiagnosticLevel);
        cmd.Parameters.AddWithValue("$expected", change.ExpectedRevision);
        if (cmd.ExecuteNonQuery() != 1) throw LocalWorkspace.Error("settingsConflict");
        tx.Commit();
        return new(workspace.Identity.WorkspaceId, checked(change.ExpectedRevision + 1), change.DiagnosticLevel);
    }

    internal static LogLevel MinimumLevel(LocalDiagnosticLevel level) => level switch
    {
        LocalDiagnosticLevel.Error => LogLevel.Error,
        LocalDiagnosticLevel.Warning => LogLevel.Warning,
        LocalDiagnosticLevel.Information => LogLevel.Information,
        LocalDiagnosticLevel.Debug => LogLevel.Debug,
        _ => LogLevel.None
    };
}

public sealed class LocalDiagnostics(LocalWorkspace workspace, LocalSettings settings, ILogger<LocalDiagnostics> logger)
{
    public LocalDiagnosticSnapshot Inspect()
    {
        var config = settings.Read();
        // Only emit fixed messages, never source data, paths, arbitrary exception messages or settings secrets.
        string storage = "not assessed";
        try
        {
            using var db = workspace.OpenDatabase(readOnly: true);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check";
            using var rows = cmd.ExecuteReader();
            storage = rows.Read() && rows.GetString(0) == "ok" && !rows.Read() ? "integrity check passed" : "integrity check failed";
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { storage = "unavailable"; }
        var level = storage == "integrity check passed" ? LogLevel.Information : LogLevel.Error;
        var minimum = LocalSettings.MinimumLevel(config.DiagnosticLevel);
        if (minimum != LogLevel.None && level >= minimum)
        {
            if (level == LogLevel.Information) logger.LogInformation("Local storage integrity check passed.");
            else logger.LogError("Local storage integrity could not be confirmed.");
        }
        return new(config.WorkspaceId, config.Revision, config.DiagnosticLevel, storage, "not assessed", "not assessed");
    }
}
