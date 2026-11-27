using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

            using var files = new QualificationFileProvider();
            var builder = PhotinoBlazorAppBuilder.CreateDefault(files, Array.Empty<string>());
            builder.Services.AddLogging(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Debug));
            builder.Services.AddSingleton(fixture);
            var focusBridge = new WindowsFocusBridge();
            builder.Services.AddSingleton(focusBridge);
            builder.Services.AddSingleton(new ProbeEvidence(args.Length == 2 ? Path.GetFullPath(args[1]) : null));
            builder.RootComponents.Add<HostProbe>("#app");
            var app = builder.Build();
            Console.WriteLine("Photino application built");
            focusBridge.Bind(app.MainWindow);
            app.MainWindow.SetTitle("WOW DI Host Qualification")
                .SetUseOsDefaultLocation(true)
                .SetDevToolsEnabled(false)
                .SetContextMenuEnabled(false)
                .SetWebSecurityEnabled(true)
                .SetFileSystemAccessEnabled(false)
                .SetJavascriptClipboardAccessEnabled(false)
                .SetMediaStreamEnabled(false)
                .SetMediaAutoplayEnabled(false)
                .SetGrantBrowserPermissions(false)
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
