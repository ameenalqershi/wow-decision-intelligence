using System.Collections.Immutable;

namespace WowDi.Studio.Shell;
public static class ShellLayoutRecovery
{
    public const int MaximumTabs = 64;
    public const double DefaultShare = .69;
    public static double ClampShare(double value) => double.IsFinite(value) ? Math.Clamp(value, .25, .75) : DefaultShare;
    public static bool ValidIdentity(string? s) => !string.IsNullOrWhiteSpace(s) && s.Length <= 512 && !s.Any(char.IsControl);
    public static bool ValidContext(WorkspaceContext? context) => context is not null && ValidIdentity(context.WorkspaceKey) && ValidIdentity(context.RevisionId);

    public static ShellLayout Recover(ShellLayout? saved, WorkspaceContext? confirmed, out bool recovered)
    {
        recovered = saved is not null && saved.LayoutVersion != 1;
        var candidate = saved is { LayoutVersion: 1 } ? saved : new ShellLayout();
        var context = ValidContext(confirmed) ? confirmed : null;
        var tabs = ImmutableArray.CreateBuilder<ShellTab>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (context is not null && candidate.Context == context && !candidate.Tabs.IsDefault)
        {
            foreach (var tab in candidate.Tabs)
            {
                if (tab is null || !ValidIdentity(tab.Key) || StudioViews.Find(tab.ViewId) is null ||
                    tab.WorkspaceKey != context.WorkspaceKey || tab.RevisionId != context.RevisionId ||
                    tabs.Count >= MaximumTabs || !keys.Add(tab.Key))
                { recovered = true; continue; }
                tabs.Add(tab);
            }
        }
        else if (candidate.Context is not null || !candidate.Tabs.IsDefaultOrEmpty) recovered = true;
        var active = tabs.Any(t => t.Key == candidate.ActiveTabKey) ? candidate.ActiveTabKey : tabs.FirstOrDefault()?.Key;
        var share = ClampShare(candidate.PrimaryShare);
        var dock = Enum.IsDefined(candidate.InspectorDock) ? candidate.InspectorDock : InspectorDock.End;
        var placement = candidate.DisplayPlacement;
        recovered |= active != candidate.ActiveTabKey || share != candidate.PrimaryShare ||
            dock != candidate.InspectorDock;
        return new ShellLayout
        {
            Context = context, Tabs = tabs.ToImmutable(), ActiveTabKey = active, PrimaryShare = share,
            InspectorVisible = candidate.InspectorVisible, InspectorDock = dock,
            DisplayPlacement = placement
        };
    }

    // Validate the host's proposed recovery before requesting native placement.
    // DS03 owns recovery policy; do not duplicate its scale-aware algorithm.
    public static bool IsVisible(DisplayPlacement? placement, IReadOnlyList<DisplayArea> displays)
    {
        if (placement is null || !ValidIdentity(placement.DisplayId) ||
            !double.IsFinite(placement.X) || !double.IsFinite(placement.Y) ||
            !double.IsFinite(placement.Width) || !double.IsFinite(placement.Height) ||
            placement.Width <= 0 || placement.Height <= 0 ||
            !double.IsFinite(placement.X + placement.Width) || !double.IsFinite(placement.Y + placement.Height)) return false;
        var matching = displays.Where(d => d.Id == placement.DisplayId).ToArray();
        if (matching.Length != 1) return false;
        var d = matching[0];
        return double.IsFinite(d.X) && double.IsFinite(d.Y) && double.IsFinite(d.Width) && double.IsFinite(d.Height) &&
            d.Width > 0 && d.Height > 0 && double.IsFinite(d.X+d.Width) && double.IsFinite(d.Y+d.Height) &&
            placement.X >= d.X && placement.Y >= d.Y &&
            placement.X + placement.Width <= d.X + d.Width && placement.Y + placement.Height <= d.Y + d.Height;
    }
}
