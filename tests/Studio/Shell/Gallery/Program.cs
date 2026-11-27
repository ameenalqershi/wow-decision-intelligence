using WowDi.Studio.Shell;
using WowDi.Studio.ShellGallery.Components;
using WowDi.Studio.ShellGallery;

var builder=WebApplication.CreateBuilder(args);
var layout=Environment.GetEnvironmentVariable("WOWDI_SHELL_LAYOUT") ??
    throw new InvalidOperationException("Explicit isolated fixture layout path required.");
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton(new FixtureStore(new FileStudioLayoutStore(layout)));
builder.Services.AddSingleton<FixtureWorkspaceProvider>();
builder.Services.AddScoped(sp=>new StudioShellSession(sp.GetRequiredService<FixtureStore>(),sp.GetRequiredService<FixtureWorkspaceProvider>()));
var app=builder.Build();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
