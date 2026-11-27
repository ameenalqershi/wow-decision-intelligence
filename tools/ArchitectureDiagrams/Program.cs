using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

if(args.Length!=2)throw new ArgumentException("Usage: ArchitectureDiagrams REPOSITORY_ROOT OUTPUT_DIR");
var root=Path.GetFullPath(args[0]);var output=Path.GetFullPath(args[1]);
var sourceDir=Path.Combine(root,"docs/design/diagrams/sp03");
Directory.CreateDirectory(output);
var map=JsonSerializer.Deserialize<Dictionary<string,int>>(File.ReadAllText(Path.Combine(root,"planning/issue-map.json")))!;
using var requirements=JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root,"planning/requirements.json")));
var ids=requirements.RootElement.EnumerateArray().SelectMany(x=>x.GetProperty("requirements").EnumerateArray())
    .Select(x=>x.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
var diagrams=new List<Diagram>();
Diagram? current=null;
int line=0;
foreach(var row in File.ReadLines(Path.Combine(sourceDir,"diagrams.tsv")))
{
    line++;if(string.IsNullOrWhiteSpace(row)||row.StartsWith('#'))continue;
    var p=row.Split('\t');
    switch(p[0])
    {
        case "D" when p.Length==7:
            Need(Regex.IsMatch(p[1],"^[a-z0-9-]+$")&&diagrams.All(d=>d.Id!=p[1]),"diagram id");
            Need(ids.Contains(p[2])&&p[2].StartsWith("SP03-"),"requirement");
            Need(p[3] is "graph" or "sequence","diagram kind");
            Need(p[4].Length>0&&p[5].Length>20&&Regex.IsMatch(p[6],"[\u0600-\u06ff]"),"bilingual description");
            current=new(p[1],p[2],p[3],p[4],p[5],p[6]);diagrams.Add(current);break;
        case "N" when p.Length==4&&current is not null:
            Need(Regex.IsMatch(p[1],"^[A-Za-z0-9-]+$")&&current.Nodes.All(n=>n.Id!=p[1]),"node id");
            Need(map.ContainsKey(p[2])&&p[3].Length>0,"node issue or label");
            current.Nodes.Add(new(p[1],p[2],p[3].Replace("\\n","\n")));break;
        case "E" when p.Length==7&&current is not null:
            Need(current.Nodes.Any(n=>n.Id==p[1])&&current.Nodes.Any(n=>n.Id==p[2]),"edge endpoints");
            Need(map.ContainsKey(p[6])&&p[3].Length>0&&p[5].Length>=10,"edge issue/label/anchor");
            var path=Path.GetFullPath(Path.Combine(root,p[4]));
            Need(path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.Ordinal)&&File.Exists(path),"source path");
            Need(File.ReadAllText(path).Contains(p[5],StringComparison.Ordinal),"source anchor: "+p[4]+" / "+p[5]);
            Need(current.Edges.All(e=>e.From!=p[1]||e.To!=p[2]||e.Label!=p[3]),"duplicate edge");
            current.Edges.Add(new(p[1],p[2],p[3],p[4],p[5],p[6]));break;
        default: throw new InvalidDataException("Invalid row "+line);
    }
    void Need(bool condition,string message){if(!condition)throw new InvalidDataException($"Line {line}: {message}");}
}
Require(diagrams.Count>=13,"diagram coverage");
var covered=diagrams.Select(d=>d.Requirement).Append("SP03-06").ToHashSet();
Require(Enumerable.Range(1,7).All(i=>covered.Contains($"SP03-{i:00}")),"SP03 scope");
var nav=diagrams.Single(d=>d.Id=="studio-navigation");
Require(nav.Nodes.Where(n=>Regex.IsMatch(n.Id,"^UI[0-9]{2}$")).Select(n=>n.Id).ToHashSet().SetEquals(
    Enumerable.Range(1,23).Select(i=>$"UI{i:00}")),"all UI modules");
var state=diagrams.Single(d=>d.Id=="decision-state");
var lifecycle=File.ReadLines(Path.Combine(root,"docs/design/lifecycle.md"))
    .Where(l=>l.StartsWith("| ")&&l.Split('|').Length>=5)
    .Select(l=>l.Split('|',StringSplitOptions.TrimEntries))
    .Where(a=>state.Nodes.Any(n=>n.Id==a[1].ToLowerInvariant())&&a[2]!="None")
    .SelectMany(a=>a[2].Split(',',StringSplitOptions.TrimEntries).Select(b=>(a[1].ToLowerInvariant(),b.ToLowerInvariant()))).ToHashSet();
Require(lifecycle.SetEquals(state.Edges.Select(e=>(e.From,e.To))),"lifecycle exhaustive edge set");
var index=new StringBuilder("# SP03 — editable architecture and subsystem diagrams\n\n");
index.AppendLine("All diagrams describe the reviewed target design unless explicitly identified as observed synthetic evidence or merged managed classes. They do not represent a deployed system. Each edge is linked to its reviewed source and owning issue. Source file: [diagrams.tsv](diagrams.tsv).\n");
index.AppendLine("المخططات تصف التصميم المعتمد المستهدف، وليس ادعاءً بنشر المكونات. لكل علاقة مرجع نصي ورقم قضية. النتائج المقاسة محدودة بالأمثلة الاصطناعية الموضحة.\n");
var report=new List<object>();
foreach(var d in diagrams)
{
    Require(d.Edges.Count>0&&d.Nodes.All(n=>d.Edges.Any(e=>e.From==n.Id||e.To==n.Id)),"disconnected node "+d.Id);
    var dot=Render(d,map);
    var dotPath=Path.Combine(output,d.Id+".dot");var svgPath=Path.Combine(output,d.Id+".svg");
    File.WriteAllText(dotPath,dot.ReplaceLineEndings("\n"),new UTF8Encoding(false));
    if(d.Kind=="sequence") RenderSequence(d,map,svgPath); else RunDot(dotPath,svgPath);
    var xml=XDocument.Load(svgPath);XNamespace svg="http://www.w3.org/2000/svg";
    var rootSvg=xml.Root??throw new InvalidDataException("missing svg");
    Require(rootSvg.Name==svg+"svg"&&rootSvg.Attribute("viewBox") is not null,"invalid SVG "+d.Id);
    var visible=xml.Descendants(svg+"text").Select(t=>t.Value).ToArray();
    Require(visible.Length>=d.Nodes.Count,"missing labels "+d.Id);
    // Graphviz may include invisible lifelines; only named semantic edge
    // labels carry the explicit edge IDs and are required to render.
    for(int i=0;i<d.Edges.Count;i++)
        Require(visible.Any(t=>t.Contains($"E{i+1:00} ",StringComparison.Ordinal)),"missing rendered edge "+d.Id+":"+i);
    index.AppendLine($"## {d.Title}\n\n{d.English}\n\n{d.Arabic}\n\nRequirement: {d.Requirement}; bilingual SP03-06; issue #161. [Editable DOT (alternate graph for sequences)]({d.Id}.dot) · [Rendered SVG]({d.Id}.svg).\n\n![{d.Title}]({d.Id}.svg)\n");
    index.AppendLine("| Edge | Relation | Issue | Reviewed source anchor |\n|---|---|---|---|");
    for(int i=0;i<d.Edges.Count;i++)
    {
        var e=d.Edges[i];var link=Path.GetRelativePath(sourceDir,Path.Combine(root,e.Source)).Replace('\\','/');
        index.AppendLine($"| E{i+1:00} | {Md(e.From)} → {Md(e.To)}: {Md(e.Label)} | [#{map[e.Scope]}](https://github.com/ameenalqershi/wow-decision-intelligence/issues/{map[e.Scope]}) | [{Md(e.Source)}]({link}): {Md(e.Anchor)} |");
    }
    index.AppendLine();
    report.Add(new {diagram=d.Id,requirement=d.Requirement,nodes=d.Nodes.Count,edges=d.Edges.Count,renderedText=visible.Length,
        sourceSha256=Hash(dotPath),renderedSha256=Hash(svgPath),viewBox=rootSvg.Attribute("viewBox")!.Value});
}
File.WriteAllText(Path.Combine(output,"README.md"),index.ToString().ReplaceLineEndings("\n").TrimEnd()+"\n",new UTF8Encoding(false));
var evidence=new {diagramCount=diagrams.Count,nodeCount=diagrams.Sum(d=>d.Nodes.Count),edgeCount=diagrams.Sum(d=>d.Edges.Count),
    requirementCount=covered.Count,lifecycleTransitions=lifecycle.Count,uiModules=23,
    renderer=DotVersion(),scope="Rendered specification, not deployment or functional acceptance",diagrams=report};
File.WriteAllText(Path.Combine(output,"render-evidence.json"),JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true})+"\n");
Console.WriteLine(JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true}));

static string Md(string s)=>s.Replace("|","\\|").Replace("\n"," ");
static string Quoted(string s)=>"\""+s.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\n","\\n")+"\"";
static string Hash(string p)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)));
static void Require(bool condition,string message){if(!condition)throw new InvalidDataException(message);}
static void RunDot(string input,string output)
{
    var info=new ProcessStartInfo("dot"){RedirectStandardError=true,UseShellExecute=false};
    foreach(var arg in new[]{"-Tsvg",input,"-o",output})info.ArgumentList.Add(arg);
    using var proc=Process.Start(info)??throw new InvalidOperationException("Graphviz unavailable");
    string errors=proc.StandardError.ReadToEnd();proc.WaitForExit();
    Require(proc.ExitCode==0&&string.IsNullOrWhiteSpace(errors),"Graphviz error/warning: "+errors);
}
static string DotVersion()
{
    var psi=new ProcessStartInfo("dot","-V"){RedirectStandardError=true,UseShellExecute=false};
    using var p=Process.Start(psi)!;var version=p.StandardError.ReadToEnd().Trim();p.WaitForExit();
    Require(p.ExitCode==0,"dot version");return version;
}
// Sequence messages use explicit coordinate geometry, not a generic graph
// optimizer: lifelines never change columns and messages remain horizontal.
// The TSV is the editable source for this SVG; DOT is an alternate graph view.
static void RenderSequence(Diagram d,Dictionary<string,int> map,string path)
{
    XNamespace ns="http://www.w3.org/2000/svg";XNamespace xlink="http://www.w3.org/1999/xlink";
    const int gap=240,margin=120,top=110,row=82;
    int width=(d.Nodes.Count-1)*gap+margin*2,height=top+(d.Edges.Count+1)*row;
    var svg=new XElement(ns+"svg",new XAttribute("viewBox",$"0 0 {width} {height}"),
        new XAttribute("width",width),new XAttribute("height",height),
        new XAttribute("role","img"),new XElement(ns+"title",d.Title+" — design / specification"),
        new XElement(ns+"desc",d.English+" "+d.Arabic));
    svg.Add(new XElement(ns+"defs",new XElement(ns+"marker",new XAttribute("id","arrow"),new XAttribute("markerWidth",8),new XAttribute("markerHeight",8),new XAttribute("refX",7),new XAttribute("refY",4),new XAttribute("orient","auto"),
        new XElement(ns+"path",new XAttribute("d","M0,0 L8,4 L0,8 Z"),new XAttribute("fill","#49697b")))));
    svg.Add(new XElement(ns+"rect",new XAttribute("width",width),new XAttribute("height",height),new XAttribute("fill","#ffffff")));
    XElement Text(int x,int y,string content,int size=14)=>new(ns+"text",new XAttribute("x",x),new XAttribute("y",y),new XAttribute("font-family","DejaVu Sans, Arial, sans-serif"),new XAttribute("font-size",size),new XAttribute("fill","#18352e"),new XAttribute("text-anchor","middle"),content);
    XElement Line(int x1,int y1,int x2,int y2,string color)=>new(ns+"line",new XAttribute("x1",x1),new XAttribute("x2",x2),new XAttribute("y1",y1),new XAttribute("y2",y2),new XAttribute("stroke",color),new XAttribute("stroke-width",1.6));
    int X(string id)=>margin+d.Nodes.FindIndex(n=>n.Id==id)*gap;
    svg.Add(Text(width/2,28,d.Title+" — design / specification",18));
    foreach(var n in d.Nodes)
    {
        int x=X(n.Id);
        var link=new XElement(ns+"a",new XAttribute(xlink+"href","https://github.com/ameenalqershi/wow-decision-intelligence/issues/"+map[n.Scope]));
        link.Add(new XElement(ns+"rect",new XAttribute("x",x-105),new XAttribute("y",48),new XAttribute("width",210),new XAttribute("height",50),new XAttribute("rx",7),new XAttribute("fill","#e8f4f1"),new XAttribute("stroke","#277664")));
        link.Add(Text(x,68,n.Label),Text(x,87,"#"+map[n.Scope],12));svg.Add(link);
        var life=Line(x,98,x,height-24,"#a7b7c1");life.Add(new XAttribute("stroke-dasharray","6 5"));svg.Add(life);
    }
    for(int i=0;i<d.Edges.Count;i++)
    {
        var e=d.Edges[i];int x1=X(e.From),x2=X(e.To),y=top+(i+1)*row,sign=Math.Sign(x2-x1);
        var link=new XElement(ns+"a",new XAttribute(xlink+"href","https://github.com/ameenalqershi/wow-decision-intelligence/issues/"+map[e.Scope]),new XElement(ns+"title",e.Source+": "+e.Anchor));
        var arrow=Line(x1,y,x2-7*sign,y,"#49697b");arrow.Add(new XAttribute("marker-end","url(#arrow)"));
        link.Add(arrow);link.Add(Text((x1+x2)/2,y-10,$"E{i+1:00} "+e.Label,12));svg.Add(link);
    }
    new XDocument(svg).Save(path);
}
static string Render(Diagram d,Dictionary<string,int> map)
{
    var s=new StringBuilder("digraph G {\n");
    s.AppendLine("  graph [rankdir="+(d.Id is "studio-navigation" or "cli-tree"?"LR":"TB")+", bgcolor=\"#ffffff\", pad=0.2, nodesep=0.28, ranksep=0.65, fontname=\"DejaVu Sans\", labelloc=t, fontsize=15, label="+Quoted(d.Title+" — design / specification")+"];");
    s.AppendLine("  node [shape=box, style=\"rounded,filled\", fillcolor=\"#e8f4f1\", color=\"#277664\", fontname=\"DejaVu Sans\", fontsize=10, fontcolor=\"#18352e\"];");
    s.AppendLine("  edge [color=\"#49697b\", fontcolor=\"#263d4b\", fontname=\"DejaVu Sans\", fontsize=9, arrowsize=0.65];");
    string Link(string scope)=>Quoted("https://github.com/ameenalqershi/wow-decision-intelligence/issues/"+map[scope]);
    if(d.Kind=="sequence")
    {
        s.AppendLine("  {rank=same; "+string.Join("; ",d.Nodes.Select(n=>Quoted(n.Id)))+";}");
        foreach(var n in d.Nodes)
            s.AppendLine($"  {Quoted(n.Id)} [label={Quoted(n.Label+"\n#"+map[n.Scope])}, URL={Link(n.Scope)}];");
        for(int i=0;i<d.Edges.Count;i++)
        {
            var e=d.Edges[i];int row=i+1;
            string Row(string id,int index)=>Quoted(id+"__"+index);
            s.AppendLine("  {rank=same; "+string.Join("; ",d.Nodes.Select(n=>Row(n.Id,row)))+";}");
            foreach(var n in d.Nodes)
            {
                s.AppendLine($"  {Row(n.Id,row)} [shape=point, label=\"\", width=0.06, height=0.06, fixedsize=true, style=filled, fillcolor=\"#49697b\"];");
                s.AppendLine($"  {(i==0?Quoted(n.Id):Row(n.Id,row-1))} -> {Row(n.Id,row)} [arrowhead=none, style=dashed, color=\"#a7b7c1\", weight=20];");
            }
            for(int j=1;j<d.Nodes.Count;j++)s.AppendLine($"  {Row(d.Nodes[j-1].Id,row)} -> {Row(d.Nodes[j].Id,row)} [style=invis, weight=100];");
            s.AppendLine($"  {Row(e.From,row)} -> {Row(e.To,row)} [constraint=false, label={Quoted($"E{row:00} "+e.Label)}, tooltip={Quoted(e.Source+": "+e.Anchor)}, URL={Link(e.Scope)}];");
        }
    }
    else
    {
        foreach(var n in d.Nodes)
        {
            bool failure=n.Id is "diag" or "diagnostic" or "reject" or "deny" or "unavailable" or "failed" or "cancelled" or "rejected" or "superseded" or "fail";
            string colors=failure?", fillcolor=\"#fbece7\", color=\"#a55236\", fontcolor=\"#78351f\"":"";
            s.AppendLine($"  {Quoted(n.Id)} [label={Quoted(n.Label+"\n#"+map[n.Scope])}, URL={Link(n.Scope)}{colors}];");
        }
        for(int i=0;i<d.Edges.Count;i++)
        {
            var e=d.Edges[i];
            s.AppendLine($"  {Quoted(e.From)} -> {Quoted(e.To)} [label={Quoted($"E{i+1:00} "+e.Label)}, tooltip={Quoted(e.Source+": "+e.Anchor)}, URL={Link(e.Scope)}];");
        }
    }
    s.AppendLine("}");return s.ToString();
}
sealed record Node(string Id,string Scope,string Label);
sealed record Edge(string From,string To,string Label,string Source,string Anchor,string Scope);
sealed record Diagram(string Id,string Requirement,string Kind,string Title,string English,string Arabic)
{
    public List<Node> Nodes {get;}=[];
    public List<Edge> Edges {get;}=[];
}
