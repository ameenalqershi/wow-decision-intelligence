using System.Globalization;
using System.Text;

internal static class DependencyDrawing
{
    public static string Render(IEnumerable<Work> rows, IReadOnlyDictionary<string, int> numbers)
    {
        static string Label(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
        var graph = new StringBuilder();
        graph.AppendLine("digraph Dependencies {");
        graph.AppendLine("  graph [rankdir=TB, bgcolor=white, nodesep=0.18, ranksep=0.65, outputorder=edgesfirst];");
        graph.AppendLine("  node [shape=box, style=\"rounded,filled\", fillcolor=\"#e8f4f1\", color=\"#277664\", fontname=Helvetica, fontsize=10];");
        graph.AppendLine("  edge [color=\"#8997a5\", arrowsize=0.55];");
        foreach (var row in rows.OrderBy(r => numbers[r.Id]))
        {
            var number = numbers[row.Id].ToString(CultureInfo.InvariantCulture);
            var label = Label(row.Id + "  #" + number);
            var tooltip = Label(row.Area + " / " + row.Title);
            var url = Label("https://github.com/ameenalqershi/wow-decision-intelligence/issues/" + number);
            graph.AppendLine($"  {Label(row.Id)} [label={label}, tooltip={tooltip}, URL={url}];");
            foreach (var prerequisite in row.Deps.Order(StringComparer.Ordinal))
                graph.AppendLine($"  {Label(prerequisite)} -> {Label(row.Id)};");
        }
        graph.AppendLine("}");
        return graph.ToString().ReplaceLineEndings("\n");
    }
}
