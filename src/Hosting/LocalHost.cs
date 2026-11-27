using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WowDi.Hosting;

public sealed record LocalWorkspaceOptions(string Path, string? CreateTenant = null);

public static class LocalHost
{
    // No default remote endpoints, network exporters, listeners or environment credential discovery.
    public static IHost Build(LocalWorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(sp =>
        {
            var selected = sp.GetRequiredService<LocalWorkspaceOptions>();
            return selected.CreateTenant is null ? LocalWorkspace.Open(selected.Path) : LocalWorkspace.Create(selected.Path, selected.CreateTenant);
        });
        return builder.Build();
    }
}
