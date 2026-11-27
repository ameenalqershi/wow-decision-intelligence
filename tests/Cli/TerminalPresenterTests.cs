using WowDi.Cli;
using Xunit;

namespace WowDi.Cli.Tests;

public sealed class TerminalPresenterTests
{
    [Theory]
    [InlineData(36)]
    [InlineData(100)]
    public void TableAndTreeRespectWidthAndEscapeMarkup(int width)
    {
        using var output = new StringWriter();
        var presenter = new TerminalPresenter(output, new(width, true, false));
        presenter.WriteTable("WOW DI · fixture", new Dictionary<string, string?> { ["status"] = "[not markup]", ["gap"] = null });
        presenter.WriteTree(new("Root", [new("Child [literal]", [])]));
        var rendered = output.ToString();
        Assert.Contains("WOW DI", rendered);
        Assert.Contains("Unknown", rendered);
        Assert.Contains("[literal]", rendered);
        Assert.DoesNotContain('\u001b', rendered);
        Assert.All(rendered.Split('\n'), line => Assert.True(line.TrimEnd('\r').Length <= width, line));
    }

    [Fact]
    public void OnlyMeasuredCountsProduceProgressPercentage()
    {
        using var output = new StringWriter();
        var presenter = new TerminalPresenter(output, new(80, true, false));
        presenter.WriteStage(new("load", "Load model", StageState.Started));
        presenter.WriteStage(new("load", "Load model", StageState.Completed));
        Assert.Contains("Progress: indeterminate", output.ToString());
        Assert.DoesNotContain('%', output.ToString());
        presenter.WriteStage(new("solve", "Observed count", StageState.Started, 1, 4));
        Assert.Contains("25.0%", output.ToString());
        Assert.DoesNotContain('\u001b', output.ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => presenter.WriteStage(new("bad", "Invalid", StageState.Started, 5, 4)));
        Assert.Throws<ArgumentOutOfRangeException>(() => presenter.WriteStage(new("bad", "Invalid", StageState.Started, 0, 0)));
    }

    [Fact]
    public void RedirectedCapabilitiesDoNotColorOrPrompt()
    {
        using var output = new StringWriter();
        var capabilities = TerminalCapabilities.Detect(output, new());
        Assert.False(capabilities.Interactive);
        Assert.False(capabilities.Color);
        Assert.Equal("danger [x]  [2J", TerminalPresenter.SafeText("danger [x] \u001b[2J"));
    }

    [Fact]
    public async Task MachineOutputRemainsUnstyled()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await CliApplication.RunAsync(["doctor", "--format", "json", "--no-color"], output, error);
        Assert.Equal(0, code);
        using var json = System.Text.Json.JsonDocument.Parse(output.ToString());
        Assert.Equal("doctor", json.RootElement.GetProperty("payload").GetProperty("command").GetString());
        Assert.Empty(error.ToString());
        Assert.DoesNotContain('\u001b', output.ToString());
    }
}
