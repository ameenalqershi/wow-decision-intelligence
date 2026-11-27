using System.Globalization;
using Spectre.Console;

namespace WowDi.Cli;

public sealed record TerminalCapabilities(int Width, bool Interactive, bool Color)
{
    public static TerminalCapabilities Detect(TextWriter writer, CliOutputOptions options, bool error = false)
    {
        bool redirected = error ? Console.IsErrorRedirected : Console.IsOutputRedirected;
        bool actualConsole = ReferenceEquals(writer, error ? Console.Error : Console.Out);
        bool terminal = actualConsole && !redirected && Environment.GetEnvironmentVariable("TERM") != "dumb"
            && options.Format == OutputFormat.Text && !options.NonInteractive && !options.Quiet;
        int width = 80;
        if (terminal)
        {
            try { width = Math.Clamp(Console.WindowWidth, 20, 240); }
            catch (IOException) { }
        }
        return new(width, terminal,
            terminal && options.CanColor(redirected, Environment.GetEnvironmentVariable("NO_COLOR") is not null));
    }
}

public static class TerminalPalette
{
    public const string Accent = "#58d6b3", Primary = "#c9d1d9", Muted = "#9ca3af",
        Border = "#30363d", Success = "#56d364", Progress = "#79c0ff",
        ProgressFill = "#238636", BackgroundReference = "#0d1117";
}

public enum StageState { Started, Completed, Failed }
public sealed record StageEvent(string Id, string Label, StageState State, long? Completed = null, long? Total = null);
public sealed record TerminalNode(string Label, IReadOnlyList<TerminalNode> Children);

public sealed class TerminalPresenter
{
    private readonly IAnsiConsole console;
    private readonly TerminalCapabilities capabilities;

    public TerminalPresenter(TextWriter writer, TerminalCapabilities capabilities)
    {
        this.capabilities = capabilities;
        console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = capabilities.Color ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = capabilities.Color ? ColorSystemSupport.Detect : ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No
        });
        console.Profile.Width = Math.Clamp(capabilities.Width, 20, 240);
    }

    public void WriteTable(string heading, IReadOnlyDictionary<string, string?> fields)
    {
        console.MarkupLine(Styled(heading, TerminalPalette.Accent, true));
        var table = new Table().Border(TableBorder.Rounded);
        if (capabilities.Color) table.BorderColor(new Color(48, 54, 61));
        table.AddColumn(new TableColumn(Styled("Field", TerminalPalette.Muted)));
        table.AddColumn(new TableColumn(Styled("Value", TerminalPalette.Muted)));
        foreach (var (key, value) in fields)
            table.AddRow(new Markup(Styled(key, TerminalPalette.Muted)), new Markup(Styled(value ?? "Unknown", TerminalPalette.Primary)));
        console.Write(table);
    }

    public void WriteTree(TerminalNode root)
    {
        var tree = new Tree(Styled(root.Label, TerminalPalette.Accent, true));
        foreach (var child in root.Children) Add(tree, child);
        console.Write(tree);

        void Add(IHasTreeNodes parent, TerminalNode node)
        {
            var branch = parent.AddNode(Styled(node.Label, TerminalPalette.Primary));
            foreach (var child in node.Children) Add(branch, child);
        }
    }

    // Only callers observing a real operation may emit an event. No clock-based or inferred progress.
    public void WriteStage(StageEvent stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage.Id);
        if ((stage.Completed is null) != (stage.Total is null) ||
            stage.Total is <= 0 || stage.Completed is < 0 || stage.Completed > stage.Total)
            throw new ArgumentOutOfRangeException(nameof(stage), "Progress requires a valid measured count and total.");
        var (indicator, color) = stage.State switch
        {
            StageState.Completed => ("OK", TerminalPalette.Success),
            StageState.Failed => ("FAILED", "#f85149"),
            _ => ("RUNNING", TerminalPalette.Progress)
        };
        console.MarkupLine(Styled(indicator, color, true) + " " + Styled(stage.Id + ": " + stage.Label, TerminalPalette.Primary));
        if (stage.State != StageState.Started && stage.Completed is null) return;
        if (stage.Total is null)
        {
            console.MarkupLine(Styled("Progress: indeterminate", TerminalPalette.Progress));
            return;
        }
        var ratio = (decimal)stage.Completed!.Value / stage.Total.Value;
        var length = Math.Clamp(capabilities.Width - 24, 5, 40);
        var filled = (int)(ratio * length);
        var bar = new string('#', filled) + new string('-', length - filled);
        console.MarkupLine(Styled(bar, TerminalPalette.ProgressFill) + " " +
            Styled((ratio * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%", TerminalPalette.Progress));
    }

    public static string SafeText(string value) => string.Concat(value.Select(c => char.IsControl(c) ? ' ' : c));

    private string Styled(string value, string color, bool bold = false) =>
        !capabilities.Color ? Markup.Escape(SafeText(value)) : "[" + (bold ? "bold " : "") + color + "]" + Markup.Escape(SafeText(value)) + "[/]";
}
