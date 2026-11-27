using System.CommandLine;
using System.CommandLine.Invocation;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WowDi.Cli;

public static class CliApplication
{
    public static async Task<int> RunAsync(
        string[] args, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var root = new RootCommand("WOW DI · Decision Intelligence Engine");
        // Replace the default action so the flag and subcommand use the same build identity.
        var versionFlag = root.Options.OfType<VersionOption>().Single();
        versionFlag.Action = new PrintVersionAction(output);
        var version = new Command("version", "Show the installed WOW DI build version.");
        version.SetAction((_, token) => Task.FromResult(PrintVersion(output, token)));
        root.Subcommands.Add(version);

        var parsed = root.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null });
        if (parsed.Errors.Count != 0)
        {
            error.WriteLine("Invalid command or arguments.");
            error.WriteLine("Use --help for usage.");
            return 2;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await parsed.InvokeAsync(new InvocationConfiguration
            {
                Output = output,
                Error = error,
                EnableDefaultExceptionHandler = false
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("Operation cancelled.");
            return 130;
        }
        catch (Exception)
        {
            // Do not print configuration values, credentials or arbitrary exception payloads.
            error.WriteLine("WOW DI could not execute the command.");
            return 5;
        }
    }

    private static int PrintVersion(TextWriter output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var host = CreateHost();
        output.WriteLine(host.Services.GetRequiredService<BuildVersion>().Value);
        return 0;
    }

    public static IHost CreateHost(string? configurationPath = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ApplicationName = typeof(CliApplication).Assembly.GetName().Name
        });
        var home = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var path = configurationPath ?? (string.IsNullOrEmpty(home)
            ? null : Path.Combine(home, "wowdi", "config.json"));
        if (path is not null)
            builder.Configuration.AddJsonFile(Path.GetFullPath(path), optional: true, reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables("WOWDI_");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<BuildVersion>();
        return builder.Build();
    }

    private sealed class PrintVersionAction(TextWriter output) : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult) => PrintVersion(output, CancellationToken.None);
    }
}

public sealed class BuildVersion
{
    public string Value { get; } = typeof(BuildVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
}
