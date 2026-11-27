using System.Collections.Immutable;
using WowDi.Studio.Shell;
using Xunit;

namespace WowDi.Studio.Shell.Tests;

public sealed class ShellTests
{
    private static readonly WorkspaceContext A = new("workspace-A","revision-17-A");
    private static readonly WorkspaceContext B = new("workspace-B","revision-8-B");
    private sealed class Provider : IStudioWorkspaceContextProvider
    {
        public bool DenyB { get; set; }
        public bool WrongRevision { get; set; }
        public ValueTask<IReadOnlyList<WorkspaceChoice>> ListAuthorizedAsync(CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyList<WorkspaceChoice>>([new(A,"A"),new(B,"B")]);
        public ValueTask<WorkspaceContext?> ResolveAsync(WorkspaceContext requested,CancellationToken ct) =>
            ValueTask.FromResult<WorkspaceContext?>(WrongRevision?requested with { RevisionId = "wrong" }:
                requested == A || (requested == B && !DenyB) ? requested : null);
    }
    private sealed class MemoryStore : IStudioLayoutStore
    {
        public ShellLayout? Saved;
        public bool Fail;
        public ValueTask<LayoutLoad> LoadAsync(CancellationToken ct) => ValueTask.FromResult(new LayoutLoad(Saved));
        public ValueTask SaveAsync(ShellLayout layout,CancellationToken ct)
        { if(Fail)throw new IOException("fixture write failed"); Saved=layout; return ValueTask.CompletedTask; }
    }
    private sealed class DisplayHost : IStudioDisplayHost
    {
        public IReadOnlyList<DisplayArea> Displays { get; set; } = [new("primary",0,0,1280,800,true)];
        public DisplayPlacement? Applied;
        public bool ReturnInvalid;
        public ValueTask<DisplayPlacement?> RecoverPlacementAsync(DisplayPlacement? saved,CancellationToken ct)
        {
            if(ReturnInvalid)return ValueTask.FromResult<DisplayPlacement?>(new("removed",99999,0,960,640));
            var d=Displays.FirstOrDefault(x=>x.Id==saved?.DisplayId)??Displays.First(x=>x.Primary);
            var w=Math.Min(960,d.Width);var h=Math.Min(640,d.Height);
            return ValueTask.FromResult<DisplayPlacement?>(new(d.Id,d.X+(d.Width-w)/2,d.Y+(d.Height-h)/2,w,h));
        }
        public ValueTask ApplyPlacementAsync(DisplayPlacement placement,CancellationToken ct){Applied=placement;return ValueTask.CompletedTask;}
    }
    [Fact]
    public void RegistryMatchesReferenceAndEveryModule()
    {
        Assert.Equal(new[]{"Overview","Decision Models","Decision Graph","Optimization","Scenarios","Simulation","Integrations","Benchmarks","Evidence","Settings"},StudioViews.Navigation.Select(x=>x.English));
        Assert.Equal(Enumerable.Range(1,23).Select(i=>$"UI{i:00}"),StudioViews.All.Select(v=>v.Id));
        Assert.All(StudioViews.All,v=>{Assert.False(string.IsNullOrWhiteSpace(v.Arabic));Assert.Contains(StudioViews.Navigation,g=>g.Id==v.GroupId);});
        Assert.All(StudioViews.Navigation,g=>Assert.Equal(g.Id,StudioViews.Find(g.ViewId)!.GroupId));
    }
    [Fact]
    public async Task AuthorizedSwitchTabsResizeAndReopenPreserveRevision()
    {
        var store=new MemoryStore();var p=new Provider();var s=new StudioShellSession(store,p);
        await s.InitializeAsync();
        Assert.Null(s.Layout.Context);
        Assert.False(await s.NavigateAsync("UI05"));
        Assert.True(await s.SwitchWorkspaceAsync(A));
        Assert.True(await s.NavigateAsync("UI03"));var first=s.ActiveTab!.Key;
        Assert.True(await s.NavigateAsync("UI05",true));var second=s.ActiveTab!.Key;
        await s.ResizeAsync(.57);await s.SetInspectorAsync(true,InspectorDock.Start);
        await s.SelectTabAsync(first);
        var reopened=new StudioShellSession(store,p);await reopened.InitializeAsync();
        Assert.Equal(new[]{first,second},reopened.Layout.Tabs.Select(t=>t.Key));
        Assert.Equal(first,reopened.Layout.ActiveTabKey);
        Assert.Equal(.57,reopened.Layout.PrimaryShare);
        Assert.Equal(InspectorDock.Start,reopened.Layout.InspectorDock);
        Assert.Equal(A,reopened.Layout.Context);
        Assert.All(reopened.Layout.Tabs,t=>Assert.Equal(A.RevisionId,t.RevisionId));
        await reopened.CloseTabAsync(first);Assert.Equal(second,reopened.Layout.ActiveTabKey);
        await reopened.CloseTabAsync(second);Assert.Null(reopened.Layout.ActiveTabKey);
    }
    [Fact]
    public async Task RejectedWorkspaceAndWrongRevisionDoNotChangeContextOrTabs()
    {
        var store=new MemoryStore();var p=new Provider();var s=new StudioShellSession(store,p);
        await s.InitializeAsync();await s.SwitchWorkspaceAsync(A);await s.NavigateAsync("UI05");
        var before=s.Layout;
        p.DenyB=true;Assert.False(await s.SwitchWorkspaceAsync(B));
        Assert.Same(before,s.Layout);Assert.Same(before,store.Saved);
        p.DenyB=false;p.WrongRevision=true;Assert.False(await s.SwitchWorkspaceAsync(B));Assert.Same(before,s.Layout);
        p.WrongRevision=false;Assert.True(await s.SwitchWorkspaceAsync(B));Assert.Equal(B,s.Layout.Context);Assert.Empty(s.Layout.Tabs);
    }
    [Fact]
    public void RecoveryDropsUnknownDuplicateForeignAndOversizedTabsWithoutAlteringSource()
    {
        var valid=new ShellTab("good","UI05",A.WorkspaceKey,A.RevisionId);
        var saved=new ShellLayout{Context=A,ActiveTabKey="bad",PrimaryShare=1.8,Tabs=[valid,valid with {ViewId="UI04"},valid with {Key="bad",ViewId="UNKNOWN"},valid with {Key="foreign",WorkspaceKey=B.WorkspaceKey},valid with {Key="stale",RevisionId=B.RevisionId}]};
        var result=ShellLayoutRecovery.Recover(saved,A,out var recovered);
        Assert.True(recovered);Assert.Single(result.Tabs);Assert.Equal("good",result.ActiveTabKey);
        Assert.Equal(.75,result.PrimaryShare);Assert.Equal(5,saved.Tabs.Length);
        Assert.Empty(ShellLayoutRecovery.Recover(saved,null,out _).Tabs);
    }
    [Theory]
    [InlineData(-1,.25)][InlineData(2,.75)][InlineData(.54,.54)][InlineData(double.NaN,.69)][InlineData(double.PositiveInfinity,.69)]
    public void SplitShareClampsFiniteRange(double supplied,double expected)=>Assert.Equal(expected,ShellLayoutRecovery.ClampShare(supplied));
    [Fact]
    public async Task DisconnectedDisplayRestoresVisibleBoundsThroughHostBoundary()
    {
        var saved=new ShellLayout{Context=A,DisplayPlacement=new("removed",2500,100,1600,1100)};
        var store=new MemoryStore{Saved=saved};var host=new DisplayHost();
        var session=new StudioShellSession(store,new Provider(),host);await session.InitializeAsync();
        Assert.NotNull(host.Applied);Assert.Equal("primary",host.Applied.DisplayId);
        Assert.Equal(160,host.Applied.X);Assert.Equal(80,host.Applied.Y);
        Assert.Equal(960,host.Applied.Width);Assert.Equal(640,host.Applied.Height);
        Assert.Equal(ShellNotice.Recovered,session.Notice);Assert.Same(saved,store.Saved);
        // Host-supplied negative-coordinate display is valid.
        host.Displays=[new("left",-1920,0,1920,1080,true)];
        await session.RestoreDisplayAsync(host.Applied);
        Assert.Equal("left",host.Applied.DisplayId);Assert.InRange(host.Applied.X,-1920,0);
        Assert.True(host.Applied.X+host.Applied.Width<=0);
        var previous=host.Applied;host.ReturnInvalid=true;await session.RestoreDisplayAsync(previous);
        Assert.Same(previous,host.Applied);Assert.Equal(ShellNotice.DisplayUnavailable,session.Notice);
    }
    [Fact]
    public async Task FailedSaveRetainsVisibleLayoutAndExplicitRetryPersists()
    {
        var store=new MemoryStore();var session=new StudioShellSession(store,new Provider());await session.InitializeAsync();
        await session.SwitchWorkspaceAsync(A);var before=store.Saved;
        store.Fail=true;await session.NavigateAsync("UI05");
        Assert.True(session.IsUnsaved);Assert.Equal(ShellNotice.LayoutUnsaved,session.Notice);
        Assert.Equal("UI05",session.ActiveTab!.ViewId);Assert.Same(before,store.Saved);
        store.Fail=false;await session.RetrySaveAsync();
        Assert.False(session.IsUnsaved);Assert.Equal(session.Layout,store.Saved);
    }
    [Fact]
    public async Task RealFileRoundtripAndMalformedLayoutNeverOverwritesOnRestore()
    {
        var directory=Path.Combine(Path.GetTempPath(),"wowdi-layout-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var file=Path.Combine(directory,"layout.json");var store=new FileStudioLayoutStore(file);
            var s=new StudioShellSession(store,new Provider());await s.InitializeAsync();await s.SwitchWorkspaceAsync(A);
            await s.NavigateAsync("UI05");await s.ResizeAsync(.61);await s.SetInspectorAsync(false,InspectorDock.Start);
            var saved=await File.ReadAllBytesAsync(file);var loaded=await store.LoadAsync(default);
            Assert.False(loaded.Invalid);Assert.Equal(.61,loaded.Layout!.PrimaryShare);Assert.False(loaded.Layout.InspectorVisible);
            var reopened=new StudioShellSession(store,new Provider());await reopened.InitializeAsync();
            Assert.Equal(s.ActiveTab!.Key,reopened.ActiveTab!.Key);Assert.Equal(A,reopened.Layout.Context);
            Assert.Equal(saved,await File.ReadAllBytesAsync(file));
            foreach(var corrupt in new[]{"{\"layoutVersion\":2}","{\"layoutVersion\":1,\"LayoutVersion\":2}","{\"layoutVersion\":1,\"secret\":\"x\"}","{"})
            {
                await File.WriteAllTextAsync(file,corrupt);
                var recovery=new StudioShellSession(store,new Provider());await recovery.InitializeAsync();
                Assert.Null(recovery.Layout.Context);Assert.Equal(ShellNotice.Recovered,recovery.Notice);
                Assert.Equal(corrupt,await File.ReadAllTextAsync(file));
            }
        }
        finally{Directory.Delete(directory,true);}
    }
    [Fact]
    public async Task UnavailableProviderCannotRestoreSavedRevision()
    {
        var store=new MemoryStore{Saved=new ShellLayout{Context=A,Tabs=[new("a","UI05",A.WorkspaceKey,A.RevisionId)],ActiveTabKey="a"}};
        var session=new StudioShellSession(store,new UnavailableWorkspaceContextProvider());
        await session.InitializeAsync();
        Assert.Null(session.Layout.Context);Assert.Empty(session.Layout.Tabs);Assert.Empty(session.Choices);
        Assert.False(await session.SwitchWorkspaceAsync(A));
    }
    [Fact]
    public async Task LimitsAndInvalidViewNeverPublishNewTab()
    {
        var store=new MemoryStore();var s=new StudioShellSession(store,new Provider());await s.InitializeAsync();await s.SwitchWorkspaceAsync(A);
        Assert.False(await s.NavigateAsync("UI99"));
        for(int i=0;i<ShellLayoutRecovery.MaximumTabs;i++)Assert.True(await s.NavigateAsync("UI05",true));
        Assert.False(await s.NavigateAsync("UI05",true));
        Assert.Equal(ShellNotice.TabLimit,s.Notice);Assert.Equal(64,s.Layout.Tabs.Length);
    }
}
