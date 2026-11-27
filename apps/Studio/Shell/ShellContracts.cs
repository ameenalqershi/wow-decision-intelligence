using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace WowDi.Studio.Shell;

// Presentation contracts only. Workspace and revision are opaque authoritative
// identities supplied by the owning runtime, not a database or API schema.
public sealed record WorkspaceContext(string WorkspaceKey, string RevisionId);
public sealed record WorkspaceChoice(WorkspaceContext Context, string Label);
public interface IStudioWorkspaceContextProvider
{
    ValueTask<IReadOnlyList<WorkspaceChoice>> ListAuthorizedAsync(CancellationToken cancellationToken);
    ValueTask<WorkspaceContext?> ResolveAsync(WorkspaceContext requested, CancellationToken cancellationToken);
}
public sealed class UnavailableWorkspaceContextProvider : IStudioWorkspaceContextProvider
{
    public ValueTask<IReadOnlyList<WorkspaceChoice>> ListAuthorizedAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<WorkspaceChoice>>([]);
    public ValueTask<WorkspaceContext?> ResolveAsync(WorkspaceContext requested, CancellationToken cancellationToken) =>
        ValueTask.FromResult<WorkspaceContext?>(null);
}

public sealed record StudioView(string Id, string GroupId, string English, string Arabic);
public sealed record StudioNavigationGroup(string Id, string ViewId, string English, string Arabic);
public static class StudioViews
{
    public static ImmutableArray<StudioNavigationGroup> Navigation { get; } =
    [
        new("overview","UI03","Overview","نظرة عامة"), new("models","UI04","Decision Models","نماذج القرارات"),
        new("graph","UI05","Decision Graph","رسم القرارات"), new("optimization","UI11","Optimization","التحسين"),
        new("scenarios","UI13","Scenarios","السيناريوهات"), new("simulation","UI14","Simulation","المحاكاة"),
        new("integrations","UI20","Integrations","التكاملات"), new("benchmarks","UI19","Benchmarks","المقاييس"),
        new("evidence","UI17","Evidence","الأدلة"), new("settings","UI23","Settings","الإعدادات")
    ];
    public static ImmutableArray<StudioView> All { get; } =
    [
        new("UI01","overview","Workspace overview","نظرة عامة لمساحة العمل"),
        new("UI02","overview","Workspace manager","إدارة مساحات العمل"),
        new("UI03","overview","Overview dashboard","لوحة النظرة العامة"),
        new("UI04","models","Visual model designer","مصمم النموذج المرئي"),
        new("UI05","graph","Decision Graph","رسم القرارات"),
        new("UI06","models","Decision DSL editor","محرر لغة القرارات"),
        new("UI07","models","Constraint editor","محرر القيود"),
        new("UI08","models","Objective editor","محرر الأهداف"),
        new("UI09","models","Model inspector","فاحص النموذج"),
        new("UI10","optimization","Solver configuration","إعدادات المحرك"),
        new("UI11","optimization","Optimization monitor","مراقبة التحسين"),
        new("UI12","optimization","Algorithm execution","تنفيذ الخوارزميات"),
        new("UI13","scenarios","Scenario comparison","مقارنة السيناريوهات"),
        new("UI14","simulation","Simulation","المحاكاة"),
        new("UI15","optimization","Production scheduling","جدولة الإنتاج"),
        new("UI16","optimization","Resource allocation","تخصيص الموارد"),
        new("UI17","evidence","Decision evidence","أدلة القرارات"),
        new("UI18","evidence","Explainability","تفسير القرارات"),
        new("UI19","benchmarks","Benchmarks","المقاييس"),
        new("UI20","integrations","Integration mapping","ربط التكامل"),
        new("UI21","settings","Administration","الإدارة"),
        new("UI22","overview","Command palette","لوحة الأوامر"),
        new("UI23","settings","Settings and diagnostics","الإعدادات والتشخيص")
    ];
    public static StudioView? Find(string? id) => All.FirstOrDefault(v => v.Id == id);
}
public sealed record ShellTab(string Key, string ViewId, string WorkspaceKey, string RevisionId);
public enum InspectorDock { End, Start }
// Logical work areas are supplied by the desktop host. This library does not
// discover monitors, perform native positioning, or claim DPI qualification.
public sealed record DisplayArea(string Id, double X, double Y, double Width, double Height, bool Primary = false);
public sealed record DisplayPlacement(string DisplayId, double X, double Y, double Width, double Height);
public interface IStudioDisplayHost
{
    IReadOnlyList<DisplayArea> Displays { get; }
    // The DS03 host adapter owns the actual DPI/topology recovery policy.
    ValueTask<DisplayPlacement?> RecoverPlacementAsync(DisplayPlacement? saved, CancellationToken cancellationToken);
    ValueTask ApplyPlacementAsync(DisplayPlacement placement, CancellationToken cancellationToken);
}
public sealed record ShellLayout
{
    [JsonRequired] public int LayoutVersion { get; init; } = 1;
    public WorkspaceContext? Context { get; init; }
    public ImmutableArray<ShellTab> Tabs { get; init; } = [];
    public string? ActiveTabKey { get; init; }
    public double PrimaryShare { get; init; } = .69;
    public bool InspectorVisible { get; init; } = true;
    public InspectorDock InspectorDock { get; init; } = InspectorDock.End;
    public DisplayPlacement? DisplayPlacement { get; init; }
}
public sealed record LayoutLoad(ShellLayout? Layout, bool Invalid = false);
public interface IStudioLayoutStore
{
    ValueTask<LayoutLoad> LoadAsync(CancellationToken cancellationToken);
    ValueTask SaveAsync(ShellLayout layout, CancellationToken cancellationToken);
}
public sealed record StudioViewContext(StudioView View, WorkspaceContext Workspace, string TabKey);
