using System.Text.RegularExpressions;
using Xunit;

namespace WowDi.Studio.DesignSystem.Tests;

public sealed class StyleAudit
{
    [Fact]
    public void StudioStylesUseCentralTokens()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "planning")))
            root = root.Parent;
        Assert.NotNull(root);
        var sources = new[] { "apps/Studio", "tests/Studio" };
        var violations = new List<string>();
        foreach (var source in sources)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root.FullName, source), "*.css", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root.FullName, file).Replace('\\', '/');
                if (relative.Split('/').Any(p => p is "bin" or "obj" or "artifacts") ||
                    relative == "apps/Studio/DesignSystem/wwwroot/tokens.css") continue;
                violations.AddRange(FindViolations(File.ReadAllText(file)).Select(v => relative + ": " + v));
            }
        }
        Assert.Empty(violations);
    }

    [Theory]
    [InlineData(".x { color: #e2e8f0; }")]
    [InlineData(".x { color: red; }")]
    [InlineData(".x { padding: 4px; }")]
    [InlineData(".x { border-radius: 5px; }")]
    [InlineData(".x { font-size: 0.875rem; }")]
    [InlineData(".x { --local: #e2e8f0; }")]
    [InlineData(".x { color: var(--wow-text); padding: 4px; }")]
    public void RejectsPrivateVisualValues(string css) => Assert.NotEmpty(FindViolations(css));

    [Theory]
    [InlineData(".x { color: var(--wow-text); padding: var(--wow-space-1); }")]
    [InlineData(".x { border: 0; color: currentColor; background: transparent; }")]
    public void AcceptsTokenReferences(string css) => Assert.Empty(FindViolations(css));

    internal static IEnumerable<string> FindViolations(string css)
    {
        css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        foreach (Match declaration in Regex.Matches(css, @"(?<=^|[;{])\s*([\w-]+)\s*:\s*([^;}]+)"))
        {
            var property = declaration.Groups[1].Value;
            var value = declaration.Groups[2].Value.Trim();
            if (Regex.IsMatch(value, @"#[\da-f]{3,8}\b|\b(?:rgba?|hsla?|oklch|oklab|lab|lch|color)\(", RegexOptions.IgnoreCase))
                yield return declaration.Value;
            else if (Regex.IsMatch(property, @"^(?:color|[\w-]+-color|fill|stroke)$") &&
                     !Regex.IsMatch(value, @"^(?:var\(--wow-[\w-]+\)|currentColor|transparent|inherit|initial|unset|none|Highlight|CanvasText|Canvas)$", RegexOptions.IgnoreCase))
                yield return declaration.Value;
            else if (Regex.IsMatch(property, @"^(?:padding|margin|gap|row-gap|column-gap|font-size|border(?:-[\w-]+)?|outline(?:-[\w-]+)?|box-shadow|min-block-size)[\w-]*$") &&
                     Regex.IsMatch(value, @"(?<![\w.-])(?:[1-9]\d*(?:\.\d+)?|0?\.\d*[1-9]\d*)(?:px|rem|em|pt)\b"))
                yield return declaration.Value;
        }
    }
}
