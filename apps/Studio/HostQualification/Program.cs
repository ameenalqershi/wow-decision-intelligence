using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Photino.Blazor;

namespace WowDi.Studio.HostQualification;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
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
            app.MainWindow.SetTitle("WOW DI Host Qualification")
                .SetUseOsDefaultLocation(true)
                .SetWidth(800)
                .SetHeight(520);
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
