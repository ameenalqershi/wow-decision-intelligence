using System.Collections.Immutable;

namespace WowDi.Studio.Shell;
public enum ShellNotice { None, Recovered, LayoutReadFailed, ContextUnavailable, LayoutUnsaved, DisplayUnavailable, ProviderUnavailable, TabLimit, LayoutDiscarded }

// A single presentation session; UI dispatches operations serially. The
// semaphore also serializes store writes and protects against async overlap.
public sealed class StudioShellSession(IStudioLayoutStore store, IStudioWorkspaceContextProvider provider, IStudioDisplayHost? displayHost = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ShellLayout lastAccepted = new();
    public ShellLayout Layout { get; private set; } = new();
    public IReadOnlyList<WorkspaceChoice> Choices { get; private set; } = [];
    public ShellNotice Notice { get; private set; }
    public bool IsUnsaved { get; private set; }
    public bool Initialized { get; private set; }
    public ShellTab? ActiveTab => Layout.Tabs.FirstOrDefault(t => t.Key == Layout.ActiveTabKey);

    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (Initialized) return;
            LayoutLoad loaded;
            try { loaded = await store.LoadAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { loaded = new LayoutLoad(null, true); Notice = ShellNotice.LayoutReadFailed; }
            var saved = loaded.Layout;
            WorkspaceContext? confirmed = null;
            try
            {
                Choices = (await provider.ListAuthorizedAsync(ct)).Where(c => c is not null &&
                    ShellLayoutRecovery.ValidContext(c.Context) && !string.IsNullOrWhiteSpace(c.Label)).DistinctBy(c => c.Context).ToArray();
                if (saved is { LayoutVersion: 1 } && ShellLayoutRecovery.ValidContext(saved.Context))
                {
                    var resolved = await provider.ResolveAsync(saved.Context!, ct);
                    if (resolved == saved.Context) confirmed = resolved;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Notice = ShellNotice.ProviderUnavailable; }
            Layout = ShellLayoutRecovery.Recover(saved, confirmed, out var recovered);
            if (Notice == ShellNotice.None && (loaded.Invalid || recovered)) Notice = ShellNotice.Recovered;
            if (displayHost is not null && Layout.DisplayPlacement is { } placement)
            {
                var restored = await RecoverDisplayCoreAsync(placement, ct);
                if (restored is not null)
                {
                    Layout = Layout with { DisplayPlacement = restored };
                    if (restored != placement && Notice == ShellNotice.None) Notice = ShellNotice.Recovered;
                }
            }
            // A recovered or corrupt file is preserved until an explicit
            // user action changes/saves the layout.
            lastAccepted = Layout;
            Initialized = true;
        }
        finally { gate.Release(); }
    }

    public async ValueTask<bool> SwitchWorkspaceAsync(WorkspaceContext requested, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!ShellLayoutRecovery.ValidContext(requested)) { Notice = ShellNotice.ContextUnavailable; return false; }
            WorkspaceContext? actual;
            try { actual = await provider.ResolveAsync(requested, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Notice = ShellNotice.ProviderUnavailable; return false; }
            if (actual != requested) { Notice = ShellNotice.ContextUnavailable; return false; }
            if (Layout.Context == actual) { Notice = ShellNotice.None; return true; }
            Layout = Layout with { Context = actual, Tabs = [], ActiveTabKey = null };
            await SaveCoreAsync(ct);
            return true;
        }
        finally { gate.Release(); }
    }

    public async ValueTask<bool> NavigateAsync(string viewId, bool newTab = false, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (StudioViews.Find(viewId) is null || Layout.Context is not { } context)
            { Notice = ShellNotice.ContextUnavailable; return false; }
            var existing = newTab ? null : Layout.Tabs.FirstOrDefault(t => t.ViewId == viewId);
            if (existing is not null) Layout = Layout with { ActiveTabKey = existing.Key };
            else
            {
                if (Layout.Tabs.Length >= ShellLayoutRecovery.MaximumTabs) { Notice = ShellNotice.TabLimit; return false; }
                var tab = new ShellTab(Guid.NewGuid().ToString("N"), viewId, context.WorkspaceKey, context.RevisionId);
                Layout = Layout with { Tabs = Layout.Tabs.Add(tab), ActiveTabKey = tab.Key };
            }
            await SaveCoreAsync(ct);
            return true;
        }
        finally { gate.Release(); }
    }

    public async ValueTask SelectTabAsync(string key, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!Layout.Tabs.Any(t => t.Key == key)) return;
            Layout = Layout with { ActiveTabKey = key };
            await SaveCoreAsync(ct);
        }
        finally { gate.Release(); }
    }
    public async ValueTask CloseTabAsync(string key, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var index = -1;
            for (var i = 0; i < Layout.Tabs.Length; i++) if (Layout.Tabs[i].Key == key) { index = i; break; }
            if (index < 0) return;
            var tabs = Layout.Tabs.RemoveAt(index);
            var active = Layout.ActiveTabKey != key ? Layout.ActiveTabKey : tabs.IsEmpty ? null : tabs[Math.Min(index, tabs.Length - 1)].Key;
            Layout = Layout with { Tabs = tabs, ActiveTabKey = active };
            await SaveCoreAsync(ct);
        }
        finally { gate.Release(); }
    }
    public async ValueTask ResizeAsync(double share, CancellationToken ct = default) =>
        await ChangeAsync(l => l with { PrimaryShare = ShellLayoutRecovery.ClampShare(share) }, ct);
    public async ValueTask SetInspectorAsync(bool visible, InspectorDock dock, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(dock)) throw new ArgumentOutOfRangeException(nameof(dock));
        await ChangeAsync(l => l with { InspectorVisible = visible, InspectorDock = dock }, ct);
    }
    public async ValueTask RetrySaveAsync(CancellationToken ct = default) =>
        await ChangeAsync(l => l, ct);
    // Discard presentation changes only. Never restore an old workspace
    // without fresh authorization, nor persist the discard over source bytes.
    public async ValueTask<bool> DiscardUnsavedAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!IsUnsaved) return false;
            WorkspaceContext? confirmed = null;
            if (lastAccepted.Context is { } requested)
            {
                try
                {
                    var actual = await provider.ResolveAsync(requested, ct);
                    if (actual == requested) confirmed = actual;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Notice = ShellNotice.ProviderUnavailable;
                    return false;
                }
                if (confirmed is null) { Notice = ShellNotice.ContextUnavailable; return false; }
            }
            var restored = ShellLayoutRecovery.Recover(lastAccepted, confirmed, out _);
            if (displayHost is not null && restored.DisplayPlacement is { } placement)
            {
                var visible = await RecoverDisplayCoreAsync(placement, ct);
                if (visible is null) return false;
                restored = restored with { DisplayPlacement = visible };
            }
            Layout = restored;
            IsUnsaved = false;
            Notice = ShellNotice.LayoutDiscarded;
            return true;
        }
        finally { gate.Release(); }
    }

    public async ValueTask RestoreDisplayAsync(DisplayPlacement? placement, CancellationToken ct = default)
    {
        if (displayHost is null) return;
        await gate.WaitAsync(ct);
        try
        {
            var restored = await RecoverDisplayCoreAsync(placement, ct);
            if (restored is null) return;
            Layout = Layout with { DisplayPlacement = restored };
            await SaveCoreAsync(ct);
        }
        finally { gate.Release(); }
    }
    private async ValueTask<DisplayPlacement?> RecoverDisplayCoreAsync(DisplayPlacement? saved, CancellationToken ct)
    {
        if (displayHost is null) return null;
        try
        {
            var restored = await displayHost.RecoverPlacementAsync(saved, ct);
            if (!ShellLayoutRecovery.IsVisible(restored, displayHost.Displays))
            { Notice = ShellNotice.DisplayUnavailable; return null; }
            await displayHost.ApplyPlacementAsync(restored!, ct);
            return restored;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { Notice = ShellNotice.DisplayUnavailable; return null; }
    }
    private async ValueTask ChangeAsync(Func<ShellLayout, ShellLayout> change, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { Layout = change(Layout); await SaveCoreAsync(ct); }
        finally { gate.Release(); }
    }
    private async ValueTask SaveCoreAsync(CancellationToken ct)
    {
        IsUnsaved = true;
        try { await store.SaveAsync(Layout, ct); lastAccepted = Layout; IsUnsaved = false; Notice = ShellNotice.None; }
        catch (Exception ex) when (ex is not OperationCanceledException) { Notice = ShellNotice.LayoutUnsaved; }
    }
}
