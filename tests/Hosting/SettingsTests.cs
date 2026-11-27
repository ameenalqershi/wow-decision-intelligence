using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WowDi.Contracts;
using WowDi.Hosting;
using Xunit;

namespace WowDi.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("wowdi-settings-").FullName;
    private LocalWorkspace Create(string name = "a") => LocalWorkspace.Create(Path.Combine(root,name), "fixture");
    private static void Sql(LocalWorkspace w, string statement)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(w.DirectoryPath,"workspace.db"), Pooling=false }.ToString());
        db.Open(); using var cmd=db.CreateCommand();cmd.CommandText=statement;cmd.ExecuteNonQuery();
    }
    [Fact]
    public void SettingsCompareAndSwapAndReopen()
    {
        var a=Create();var settings=new LocalSettings(a);var first=settings.Read();
        Assert.Equal(0,first.Revision);
        Assert.Equal(LocalDiagnosticLevel.Information,first.DiagnosticLevel);
        var next=settings.Apply(new(a.Identity.WorkspaceId,first.Revision,LocalDiagnosticLevel.Warning));
        Assert.Equal(1,next.Revision);
        Assert.Equal(next,new LocalSettings(LocalWorkspace.Open(a.DirectoryPath)).Read());
        Assert.Equal("workspace.settingsConflict",Assert.Throws<ContractException>(()=>settings.Apply(new(a.Identity.WorkspaceId,0,LocalDiagnosticLevel.Debug))).Code);
        Assert.Equal("workspace.settingsInvalid",Assert.Throws<ContractException>(()=>settings.Apply(new(a.Identity.WorkspaceId,1,(LocalDiagnosticLevel)100))).Code);
        Assert.Equal("workspace.settingsScope",Assert.Throws<ContractException>(()=>settings.Apply(new(Create("b").Identity.WorkspaceId,1,LocalDiagnosticLevel.Debug))).Code);
        Assert.Equal(next,settings.Read());
    }
    [Fact]
    public async Task ConcurrentWritersHaveSingleWinner()
    {
        var a=Create();
        async Task<bool> Write(LocalDiagnosticLevel level) => await Task.Run(()=>{
            try { new LocalSettings(a).Apply(new(a.Identity.WorkspaceId,0,level)); return true; }
            catch(ContractException e) when(e.Code=="workspace.settingsConflict"){return false;}
        });
        var results=await Task.WhenAll(Write(LocalDiagnosticLevel.Warning),Write(LocalDiagnosticLevel.Debug));
        Assert.Single(results,x=>x);
        Assert.Equal(1,new LocalSettings(a).Read().Revision);
    }
    [Fact]
    public void MigrationAndFailedMutationAreAtomic()
    {
        var a=Create();
        Sql(a,"DROP TABLE local_settings; UPDATE workspace_identity SET schema_version=1;");
        a=LocalWorkspace.Open(a.DirectoryPath);
        Assert.Equal(2,a.Identity.SchemaVersion);
        var settings=new LocalSettings(a);
        Assert.Equal(0,settings.Read().Revision);
        Sql(a,"CREATE TRIGGER fail_settings BEFORE UPDATE ON local_settings BEGIN SELECT RAISE(ABORT,'interrupted'); END;");
        Assert.Throws<SqliteException>(()=>settings.Apply(new(a.Identity.WorkspaceId,0,LocalDiagnosticLevel.Debug)));
        Assert.Equal(LocalDiagnosticLevel.Information,settings.Read().DiagnosticLevel);
        Assert.Equal(0,settings.Read().Revision);
    }
    [Fact]
    public void DiagnosticsUseEffectiveLevelAndDoNotClaimSolverHealth()
    {
        var a=Create();var settings=new LocalSettings(a);var logger=new CapturingLogger();
        var report=new LocalDiagnostics(a,settings,logger).Inspect();
        Assert.Equal("integrity check passed",report.StorageHealth);
        Assert.Equal("not assessed",report.SolverHealth);
        Assert.Equal("not assessed",report.RemoteHealth);
        Assert.Equal("Local storage integrity check passed.",Assert.Single(logger.Messages));
        settings.Apply(new(a.Identity.WorkspaceId,0,LocalDiagnosticLevel.Warning));
        logger.Messages.Clear();
        new LocalDiagnostics(a,settings,logger).Inspect();
        Assert.Empty(logger.Messages);
        using var host=LocalHost.Build(new(a.DirectoryPath));
        Assert.Equal(LocalDiagnosticLevel.Warning,host.Services.GetRequiredService<LocalDiagnostics>().Inspect().DiagnosticLevel);
    }
    private sealed class CapturingLogger : ILogger<LocalDiagnostics>
    {
        public List<string> Messages {get;}=[];
        public IDisposable? BeginScope<TState>(TState state) where TState:notnull => null;
        public bool IsEnabled(LogLevel logLevel)=>true;
        public void Log<TState>(LogLevel logLevel,EventId eventId,TState state,Exception? exception,Func<TState,Exception?,string> formatter)=>Messages.Add(formatter(state,exception));
    }
    public void Dispose()=>Directory.Delete(root,true);
}
