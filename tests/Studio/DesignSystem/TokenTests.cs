using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace WowDi.Studio.DesignSystem.Tests;

public sealed class TokenTests
{
    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
    private static Dictionary<string, string> Parse(string css) => Regex.Matches(css, @"(--wow-[\w-]+)\s*:\s*([^;]+);")
        .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());
    private static Dictionary<string, string> Dark() => Parse(Read("tokens.css").Split(".wow-theme[data-wow-theme=")[0]);

    [Fact]
    public void ReferencePaletteAndGeometryArePreserved()
    {
        var root = XElement.Parse(Read("reference.svg"));
        var rects = root.Descendants().Where(x => x.Name.LocalName == "rect").ToArray();
        var tokens = Dark();
        void Rect(string x, string y, string width, string height, string color, string? widthToken = null, string? heightToken = null)
        {
            var r = Assert.Single(rects, r => (string?)r.Attribute("x") == x && (string?)r.Attribute("y") == y && (string?)r.Attribute("width") == width && (string?)r.Attribute("height") == height);
            Assert.Equal((string?)r.Attribute("fill"), tokens[color]);
            if (widthToken != null) Assert.Equal(width + "px", tokens[widthToken]);
            if (heightToken != null) Assert.Equal(height + "px", tokens[heightToken]);
        }
        Rect("0", "0", "720", "31", "--wow-chrome", "--wow-reference-width", "--wow-title-height");
        Rect("0", "31", "144", "407", "--wow-sidebar", "--wow-sidebar-width");
        Rect("0", "438", "720", "22", "--wow-chrome", null, "--wow-status-height");
        Rect("157", "82", "130", "58", "--wow-card", "--wow-kpi-width", "--wow-kpi-height");
        Rect("157", "152", "375", "276", "--wow-graph-bg", "--wow-graph-width", "--wow-panel-height");
        Rect("542", "152", "167", "276", "--wow-monitor", "--wow-monitor-width", "--wow-panel-height");
        Assert.Equal("460", (string?)root.Attribute("height"));
        Assert.Equal("460px", tokens["--wow-reference-height"]);
        var colors = root.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName is "fill" or "stroke")
            .Select(a => a.Value).Where(v => v.StartsWith('#')).Distinct();
        foreach (var color in colors)
        {
            var canonical = color.Length == 4 ? "#" + string.Concat(color.Skip(1).Select(c => new string(c, 2))) : color;
            Assert.Contains(canonical.ToLowerInvariant(), tokens.Values);
        }
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    [InlineData("high-contrast")]
    public void TextAndFocusContrast(string theme)
    {
        var tokens = Dark();
        if (theme != "dark")
        {
            var section = Regex.Match(Read("tokens.css"), @"\.wow-theme\[data-wow-theme=""" + theme + @"""\]\s*\{([^}]+)\}");
            Assert.True(section.Success);
            var overrides = Parse(section.Groups[1].Value);
            Assert.Equal(tokens.Where(p => p.Value.StartsWith('#')).Select(p => p.Key).Order(), overrides.Keys.Order());
            foreach (var pair in overrides) tokens[pair.Key] = pair.Value;
        }
        foreach (var (foreground, background) in new[] {
            ("text","bg"),("text","card"),("text","chrome"),("muted","card"),
            ("nav-text","sidebar"),("monitor-text","monitor"),("active-text","active-bg"),("text","node-bg"),
            ("text","optimizer-bg"),("success","card"),("warning","card"),("error","card"),("action-text","action-bg") })
            AssertContrast(tokens, foreground, background, 4.5);
        foreach (var background in new[] { "bg", "card", "sidebar", "monitor", "graph-bg" })
            AssertContrast(tokens, "focus", background, 3);
        AssertContrast(tokens, "control-border", "bg", 3);
        AssertContrast(tokens, "edge", "graph-bg", 3);
        AssertContrast(tokens, "nav-icon", "sidebar", 3);
        AssertContrast(tokens, "active-icon", "active-bg", 3);
        AssertContrast(tokens, "active-marker", "active-bg", 3);
        AssertContrast(tokens, "interactive-node-border", "node-bg", 3);
        AssertContrast(tokens, "interactive-axis", "monitor", 3);
    }

    private static void AssertContrast(Dictionary<string,string> tokens, string f, string b, double minimum)
    {
        static double L(string hex)
        {
            double Channel(int i) { var c = Convert.ToInt32(hex.Substring(i,2),16)/255d; return c <= .04045 ? c/12.92 : Math.Pow((c+.055)/1.055,2.4); }
            return .2126*Channel(1)+.7152*Channel(3)+.0722*Channel(5);
        }
        double a=L(tokens["--wow-"+f]), z=L(tokens["--wow-"+b]);
        var contrast=(Math.Max(a,z)+.05)/(Math.Min(a,z)+.05);
        Assert.True(contrast >= minimum, $"{f}/{b}: {contrast:F2} < {minimum}");
    }

    [Fact]
    public void ComponentStylesRejectPrivateColors()
    {
        Assert.Empty(PrivateColors(Read("gallery.css")));
        Assert.NotEmpty(PrivateColors(".bad { color: #e2e8f0; }"));
        Assert.NotEmpty(PrivateColors(".bad { background: rgb(0,0,0); }"));
    }

    private static MatchCollection PrivateColors(string css) => Regex.Matches(css, @"#[0-9a-fA-F]{3,8}\b|\b(?:rgba?|hsla?|oklch|color)\(", RegexOptions.IgnoreCase);

    [Fact]
    public async Task RenderSameFixtureInAllThemes()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<Gallery>()).ToHtmlString());
        foreach (var theme in Enum.GetValues<StudioTheme>())
            Assert.Contains($"data-wow-theme=\"{theme.ToAttribute()}\"", html);
        Assert.Equal(3, Regex.Matches(html, "Metric not available").Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => ((StudioTheme)99).ToAttribute());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((StudioDensity)99).ToAttribute());
        var output = Environment.GetEnvironmentVariable("WOWDI_GALLERY_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "gallery.html"), "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>DS01 fixture</title><style>" + Read("tokens.css") + Read("gallery.css") + "</style>" + html + "</html>");
        }
    }
}
