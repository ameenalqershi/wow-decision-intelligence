using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;

namespace WowDi.Studio.HostQualification;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--evidence")
        {
            var log = Path.GetFullPath(args[1]) + ".startup.log";
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            var writer = new StreamWriter(log, append: false) { AutoFlush = true };
            Console.SetOut(writer);
            Console.SetError(writer);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Console.Error.WriteLine(e.ExceptionObject);
            Console.WriteLine($"Starting on {Environment.OSVersion}, {Environment.Version}");
        }
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--evidence"))
        {
            Console.Error.WriteLine("Usage: WowDi.Studio.HostQualification [--evidence FILE]");
            return 2;
        }

        try
        {
            var fixture = JsonSerializer.Deserialize<LocalFixture>(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixture.json")))
                ?? throw new InvalidDataException("Missing local qualification fixture.");
            if (string.IsNullOrWhiteSpace(fixture.Revision) || string.IsNullOrWhiteSpace(fixture.Description))
                throw new InvalidDataException("Invalid local qualification fixture.");

            var builder = PhotinoBlazorAppBuilder.CreateDefault(Array.Empty<string>());
            builder.Services.AddLogging();
            builder.Services.AddSingleton(fixture);
            builder.Services.AddSingleton(new ProbeEvidence(args.Length == 2 ? Path.GetFullPath(args[1]) : null));
            builder.RootComponents.Add<HostProbe>("#app");
            var app = builder.Build();
            Console.WriteLine("Photino application built");
            app.MainWindow.SetTitle("WOW DI Host Qualification")
                .SetUseOsDefaultLocation(true)
                .SetWidth(800)
                .SetHeight(520);
            Console.WriteLine("Entering native event loop");
            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
}
