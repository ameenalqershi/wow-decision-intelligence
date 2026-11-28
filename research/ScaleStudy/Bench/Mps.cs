using System.IO.Compression;
using WowDi.Research.Sparse;

namespace ScaleBench;

/// <summary>Free-format MPS reader (MIPLIB/Netlib). Decimal literals are parsed to exact rationals (no binary rounding).
/// Supports ROWS N/E/L/G, COLUMNS with INTORG/INTEND markers, RHS (including objective constant), RANGES, BOUNDS
/// UP/LO/FX/FR/MI/PL/BV/LI/UI and OBJSENSE MAX. Semi-continuous and SOS sections are rejected explicitly.</summary>
public static class Mps
{
    public static SparseModel Read(string path)
    {
        using Stream raw=File.OpenRead(path);
        using Stream s=path.EndsWith(".gz",StringComparison.Ordinal)?new GZipStream(raw,CompressionMode.Decompress):raw;
        using var reader=new StreamReader(s);
        string section="";string? objRow=null;bool maximize=false,inInt=false;
        var rowType=new Dictionary<string,char>(StringComparer.Ordinal);var rowOrder=new List<string>();
        var colIndex=new Dictionary<string,int>(StringComparer.Ordinal);var colInt=new List<bool>();
        var entries=new List<(int Row,int Col,Q V)>();var cost=new Dictionary<int,Q>();
        var rhs=new Dictionary<string,Q>(StringComparer.Ordinal);var range=new Dictionary<string,Q>(StringComparer.Ordinal);
        var lo=new Dictionary<int,Q?>();var hi=new Dictionary<int,Q?>();Q offset=Q.Zero;
        var rowId=new Dictionary<string,int>(StringComparer.Ordinal);
        string? line;
        while((line=reader.ReadLine())!=null)
        {
            if(line.Length==0||line[0]=='*')continue;
            var f=line.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            if(f.Length==0)continue;
            if(!char.IsWhiteSpace(line[0]))
            {
                section=f[0];
                if(section=="OBJSENSE"&&f.Length>1)maximize=f[1].StartsWith("MAX",StringComparison.Ordinal);
                if(section is "SOS" or "QUADOBJ" or "QMATRIX" or "QCMATRIX")throw new NotSupportedException("MPS section "+section);
                continue;
            }
            switch(section)
            {
                case "OBJSENSE":maximize=f[0].StartsWith("MAX",StringComparison.Ordinal);break;
                case "ROWS":
                    var t=char.ToUpperInvariant(f[0][0]);
                    if(t=='N'){objRow??=f[1];if(f[1]!=objRow)break;}
                    else{rowType[f[1]]=t;rowId[f[1]]=rowOrder.Count;rowOrder.Add(f[1]);}
                    break;
                case "COLUMNS":
                    if(f.Length>=3&&f[1]=="'MARKER'"){inInt=f[2]=="'INTORG'";break;}
                    if(!colIndex.TryGetValue(f[0],out var c)){c=colIndex.Count;colIndex.Add(f[0],c);colInt.Add(inInt);}
                    for(int k=1;k+1<f.Length;k+=2)
                    {
                        var v=Q.Parse(f[k+1]);
                        if(f[k]==objRow)cost[c]=cost.GetValueOrDefault(c,Q.Zero)+v;
                        else if(rowId.TryGetValue(f[k],out var ri))entries.Add((ri,c,v));
                    }
                    break;
                case "RHS":
                    for(int k=f.Length%2==0?0:1;k+1<f.Length;k+=2)
                    {if(f[k]==objRow)offset=-Q.Parse(f[k+1]);else rhs[f[k]]=Q.Parse(f[k+1]);}
                    break;
                case "RANGES":
                    for(int k=f.Length%2==0?0:1;k+1<f.Length;k+=2)range[f[k]]=Q.Parse(f[k+1]);
                    break;
                case "BOUNDS":
                    var type=f[0];var col=colIndex[f[2]];Q? val=f.Length>3?Q.Parse(f[3]):null;
                    switch(type)
                    {
                        case "UP":hi[col]=val;if(val!.Value.Sign<0&&!lo.ContainsKey(col))lo[col]=null;break;
                        case "LO":lo[col]=val;break;
                        case "FX":lo[col]=val;hi[col]=val;break;
                        case "FR":lo[col]=null;hi[col]=null;break;
                        case "MI":lo[col]=null;break;
                        case "PL":hi[col]=null;break;
                        case "BV":lo[col]=Q.Zero;hi[col]=Q.One;colInt[col]=true;break;
                        case "LI":lo[col]=val;colInt[col]=true;break;
                        case "UI":hi[col]=val;colInt[col]=true;break;
                        default:throw new NotSupportedException("MPS bound "+type);
                    }
                    break;
                case "ENDATA":break;
            }
        }
        var b=new SparseBuilder(Path.GetFileName(path),maximize);
        for(int c=0;c<colIndex.Count;c++)
            b.AddColumn(lo.TryGetValue(c,out var l)?l:Q.Zero,hi.TryGetValue(c,out var h)?h:null,colInt[c],cost.GetValueOrDefault(c,Q.Zero));
        if(offset.Sign!=0)b.SetObjectiveOffset(offset);
        var byRow=entries.GroupBy(e=>e.Row).ToDictionary(g=>g.Key,g=>g.ToArray());
        for(int i=0;i<rowOrder.Count;i++)
        {
            var name=rowOrder[i];var r=rhs.GetValueOrDefault(name,Q.Zero);Q? rl,ru;
            switch(rowType[name])
            {
                case 'E':rl=r;ru=r;if(range.TryGetValue(name,out var re)){if(re.Sign>0)ru=r+re;else rl=r+re;}break;
                case 'L':rl=null;ru=r;if(range.TryGetValue(name,out var rL))rl=r-Q.Abs(rL);break;
                case 'G':rl=r;ru=null;if(range.TryGetValue(name,out var rG))ru=r+Q.Abs(rG);break;
                default:throw new NotSupportedException("row type");
            }
            var es=byRow.GetValueOrDefault(i)??[];
            b.AddRow(es.Select(e=>e.Col).ToArray(),es.Select(e=>e.V).ToArray(),rl,ru);
        }
        return b.Build();
    }
}
