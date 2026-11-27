using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Completions;
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
        var options = new CommandOptions();
        options.Register(root);
        var suggest = new SuggestDirective();
        root.Directives.Add(suggest);
        var versionFlag = root.Options.OfType<VersionOption>().Single();
        versionFlag.Action = new PrintVersionAction(output, error, options);
        var version = new Command("version", "Show the installed WOW DI build version.");
        version.SetAction((parsed, token) => Task.FromResult(PrintVersion(output, error, options.Read(parsed), token)));
        root.Subcommands.Add(version);

        var parsed = root.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null });
        if (!ReferenceEquals(parsed.Action, suggest.Action) && (parsed.Errors.Count != 0 || options.Read(parsed) is { Quiet: true, Verbose: true }))
        {
            error.WriteLine("Invalid command or arguments.");
            error.WriteLine("Use --help for usage.");
            return (int)CliExitCode.Usage;
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
            return (int)CliExitCode.Cancelled;
        }
        catch (Exception)
        {
            // Do not print configuration values, credentials or arbitrary exception payloads.
            error.WriteLine("WOW DI could not execute the command.");
            return (int)CliExitCode.InfrastructureFailure;
        }
    }

    private static int PrintVersion(TextWriter output, TextWriter error, CliOutputOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var host = CreateHost();
        var value = host.Services.GetRequiredService<BuildVersion>().Value;
        var writer = new CliOutput(output, error, options);
        writer.Verbose("Resolved installed build version.");
        writer.WriteResult("version", new Dictionary<string, string?> { ["version"] = value }, value);
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

    private sealed class PrintVersionAction(TextWriter output, TextWriter error, CommandOptions options) : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult) => PrintVersion(output, error, options.Read(parseResult), CancellationToken.None);
    }
}

public sealed class BuildVersion
{
    public string Value { get; } = typeof(BuildVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
}
