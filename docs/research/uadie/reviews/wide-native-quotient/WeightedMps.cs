using System.IO.Compression;
using System.Globalization;
internal record WeightedMpsInput(WeightedModel Model,string[] Columns,string[] Rows);
internal static class WeightedMps
{
    internal static WeightedMpsInput Read(string path)
    {
        using var file=File.OpenRead(path);using var gzip=new GZipStream(file,CompressionMode.Decompress);using var reader=new StreamReader(gzip);
        string section="",obj="",rhsName="",boundName="";bool integer=false,ended=false;
        var names=new Dictionary<string,int>();var cost=new List<long>();var bounds=new HashSet<int>();
        var rows=new Dictionary<string,(char Type,List<int> Index,List<long> Coeff)>();var rhs=new Dictionary<string,long>();var seen=new HashSet<(int,string)>();
        long Number(string s){if(!decimal.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out decimal d)||d!=decimal.Truncate(d)||d< -WeightedReduction.Limit||d>WeightedReduction.Limit)throw new NotSupportedException("not exact bounded integer");return (long)d;}
        void Require(bool b,string message){if(!b)throw new NotSupportedException(message);}
        var sections=new HashSet<string>();
        for(string? line;(line=reader.ReadLine())!=null;){
            if(string.IsNullOrWhiteSpace(line)||line.StartsWith('*'))continue;
            string[] a=line.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            if(!char.IsWhiteSpace(line[0])){
                string next=a[0];Require(next is "NAME" or "ROWS" or "COLUMNS" or "RHS" or "BOUNDS" or "ENDATA","unsupported section "+next);
                Require(sections.Add(next),"duplicate section");
                if(next=="ENDATA"){ended=true;break;}section=next;continue;
            }
            if(section=="ROWS"){
                Require(a.Length==2,"row format");
                if(a[0]=="N"){Require(obj=="","multiple objective rows");obj=a[1];}
                else{Require(a[0] is "E" or "L" or "G","row type");rows.Add(a[1],(a[0][0],[],[]));}
            }else if(section=="COLUMNS"){
                if(a.Length==3&&a[1].Trim('\'')=="MARKER"){
                    string mark=a[2].Trim('\'');Require(mark=="INTORG"||mark=="INTEND"&&integer,"marker");Require(mark!="INTORG"||!integer,"nested marker");integer=mark=="INTORG";continue;
                }
                Require(integer,"continuous/unmarked column");Require(a.Length is 3 or 5,"column format");
                if(!names.TryGetValue(a[0],out int i)){i=names.Count;names.Add(a[0],i);cost.Add(0);}
                for(int j=1;j<a.Length;j+=2){
                    Require(seen.Add((i,a[j])),"duplicate coefficient");long v=Number(a[j+1]);
                    if(a[j]==obj)cost[i]=v;else{Require(rows.ContainsKey(a[j]),"unknown row");if(v!=0){rows[a[j]].Index.Add(i);rows[a[j]].Coeff.Add(v);}}
                }
            }else if(section=="RHS"){
                Require(a.Length is 3 or 5,"rhs format");if(rhsName=="")rhsName=a[0];Require(rhsName==a[0],"multiple rhs");
                for(int j=1;j<a.Length;j+=2){Require(rows.ContainsKey(a[j]),"offset/unknown row");rhs.Add(a[j],Number(a[j+1]));}
            }else if(section=="BOUNDS"){
                Require(a.Length==3&&a[0]=="BV"||a.Length==4&&a[0]=="UP"&&Number(a[3])==1,"nonbinary bounds");
                if(boundName=="")boundName=a[1];Require(boundName==a[1],"multiple bound sets");Require(bounds.Add(names[a[2]]),"duplicate bound");
            }else throw new NotSupportedException("unexpected data");
        }
        Require(ended&&obj!=""&&bounds.Count==names.Count&&!integer,"incomplete domain");
        while(reader.ReadLine() is {} trailing)Require(string.IsNullOrWhiteSpace(trailing)||trailing.StartsWith('*'),"trailing data");
        var result=new List<WeightedRow>();
        foreach(var(name,r) in rows){
            long b=rhs.GetValueOrDefault(name);long min=r.Coeff.Where(a=>a<0).Sum(),max=r.Coeff.Where(a=>a>0).Sum();
            // One-sided bounds converted using proven Boolean cube extrema; preserve contradiction.
            long l=r.Type=='L'?Math.Min(min,b):b,u=r.Type=='G'?Math.Max(max,b):b;
            result.Add(new(r.Index.ToArray(),r.Coeff.ToArray(),l,u));
        }
        return new(WeightedReduction.Admit(new(names.Count,result.ToArray(),cost.ToArray())),names.Keys.ToArray(),rows.Keys.ToArray());
    }
}
