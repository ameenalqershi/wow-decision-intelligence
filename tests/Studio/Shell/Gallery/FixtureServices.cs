using WowDi.Studio.Shell;

namespace WowDi.Studio.ShellGallery;
// Deliberately isolated synthetic identities. No engine or production
// runtime provider is registered by this gallery.
public sealed class FixtureWorkspaceProvider : IStudioWorkspaceContextProvider
{
    public static WorkspaceContext A { get; } = new("manufacturing-fixture","revision-17-A");
    public static WorkspaceContext B { get; } = new("second-fixture","revision-8-B");
    public static WorkspaceContext Denied { get; } = new("revoked-fixture","revision-denied");
    public ValueTask<IReadOnlyList<WorkspaceChoice>> ListAuthorizedAsync(CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<WorkspaceChoice>>([new(A,"Manufacturing fixture"),new(B,"Second fixture"),new(Denied,"Previously authorized fixture")]);
    public ValueTask<WorkspaceContext?> ResolveAsync(WorkspaceContext requested,CancellationToken ct) =>
        ValueTask.FromResult<WorkspaceContext?>(requested==A||requested==B?requested:null);
}
public sealed class FixtureStore(FileStudioLayoutStore inner) : IStudioLayoutStore
{
    public bool Fail { get; set; }
    public ValueTask<LayoutLoad> LoadAsync(CancellationToken ct) => inner.LoadAsync(ct);
    public ValueTask SaveAsync(ShellLayout layout,CancellationToken ct)
    {
        if(Fail)throw new IOException("Isolated fixture failure.");
        return inner.SaveAsync(layout,ct);
    }
}
