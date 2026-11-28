using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
internal static class GapSourceAudit
{
    private static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
    // Independent lexical rational parser; reject exponent/rounding and noncanonical grammar.
    private static (BigInteger N,int Scale) Lex(string s){
        if(s.Length>32)throw new FormatException("token length");
        var scientific=s.ToLowerInvariant().Split('e');int exponent=0;
        if(scientific.Length>2||scientific.Length==2&&(!int.TryParse(scientific[1],NumberStyles.AllowLeadingSign,Inv,out exponent)||exponent< -18||exponent>18))throw new FormatException("exponent");
        var parts=scientific[0].Split('.');if(parts.Length>2||parts.Any(p=>p.Length==0||p.Any(c=>c<'0'||c>'9')))throw new FormatException("decimal token");
        int scale=(parts.Length==2?parts[1].Length:0)-exponent;
        if(scale>18||s.Length>32)throw new FormatException("numeric envelope");
        BigInteger n=BigInteger.Parse(string.Concat(parts),Inv);
        if(scale<0){n*=BigInteger.Pow(10,-scale);scale=0;}return(n,scale);
    }
    internal static void Run(string zip,string split)
    {
        int id=split switch{"dev"=>1,"holdout"=>11,"final"=>20,_=>throw new ArgumentException("split")};
        using var archive=ZipFile.OpenRead(zip);
        var files=archive.Entries.ToDictionary(e=>e.FullName,StringComparer.Ordinal);
        string[] classes=archive.Entries.Where(e=>e.FullName.EndsWith('/')&&e.FullName.Count(c=>c=='/')==2).Select(e=>e.FullName.Split('/')[1]).Select(n=>n[..n.LastIndexOf("_GEN",StringComparison.Ordinal)]).Distinct().Order().ToArray();
        if(classes.Length!=54)throw new Exception("classes");
        foreach(string c in classes){
            string name=c+"_GEN"+id,prefix="data-gen/"+name+"/";
            var expected=new HashSet<string>(StringComparer.Ordinal);
            long tokens=0,nonintegral=0;int maxScale=0;
            string[] Read(string file){
                string key=prefix+file;if(!expected.Add(key)||!files.TryGetValue(key,out var entry)||entry.Length>2_000_000)throw new Exception("entry");
                using var stream=entry.Open();using var r=new StreamReader(stream);
                return r.ReadToEnd().Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            }
            int Count(string file){var a=Read(file);if(a.Length!=1||!int.TryParse(a[0],NumberStyles.None,Inv,out int v)||v<=0||v>10000)throw new Exception("count");return v;}
            (BigInteger N,int Scale)[] Parse(string file,int length){
                var a=Read(file);if(a.Length!=length)throw new Exception("vector length");
                return a.Select(s=>{
                    var x=Lex(s);decimal d=decimal.Parse(s,NumberStyles.Float,Inv);
                    decimal scale=1;for(int i=0;i<x.Scale;i++)scale*=10;
                    if(d*scale!=(decimal)x.N)throw new Exception("independent decimal mismatch");
                    tokens++;if(d!=decimal.Truncate(d))nonintegral++;maxScale=Math.Max(maxScale,x.Scale);return x;
                }).ToArray();
            }
            int resources=Count("data-nResources.csv"),tasks=Count("data-nTasks.csv");
            string[] fields=c.Split('_');if(resources!=int.Parse(fields[0][1..])||tasks!=int.Parse(fields[1][1..]))throw new Exception("name dimensions");
            var capacities=Parse("data-dt.csv",resources);var profits=Parse("data-taskPriorities.csv",tasks);
            BigInteger maxRow=0;int maxRowScale=0;long scaledRows=0;
            BigInteger Scale((BigInteger N,int Scale) x,int k)=>x.N*BigInteger.Pow(10,k-x.Scale);
            for(int i=0;i<resources;i++){
                var a=Parse("data-taskLoadsR"+(i+1)+".csv",tasks);int k=Math.Max(capacities[i].Scale,a.Max(x=>x.Scale));
                var cap=Scale(capacities[i],k);var row=a.Select(x=>Scale(x,k)).ToArray();
                BigInteger g=row.Aggregate(BigInteger.Abs(cap),(s,x)=>BigInteger.GreatestCommonDivisor(s,BigInteger.Abs(x)));if(g==0)g=1;
                BigInteger mag=row.Aggregate(BigInteger.Zero,(s,x)=>s+BigInteger.Abs(x))/g;
                if(mag<=1L<<50&&BigInteger.Abs(cap/g)<=1L<<50)scaledRows++;
                maxRow=BigInteger.Max(maxRow,mag);maxRowScale=Math.Max(maxRowScale,k);
            }
            int pk=profits.Max(x=>x.Scale);var pc=profits.Select(x=>Scale(x,pk)).ToArray();
            BigInteger pg=pc.Aggregate(BigInteger.Zero,(s,x)=>BigInteger.GreatestCommonDivisor(s,x));if(pg==0)pg=1;
            var objectiveEnvelope=pc.Aggregate(BigInteger.Zero,(s,x)=>s+x)/pg*resources;
            var found=files.Keys.Where(x=>x.StartsWith(prefix,StringComparison.Ordinal)&&x!=prefix).ToHashSet(StringComparer.Ordinal);
            if(!found.SetEquals(expected))throw new Exception("unexpected/missing entries");
            Console.WriteLine(JsonSerializer.Serialize(new{split,name,resources,tasks,tokens,nonintegral,maxScale,maxRowScale,maxRow=maxRow.ToString(),scaledRows,objectiveEnvelope=objectiveEnvelope.ToString(),numericEnvelope=scaledRows==resources&&objectiveEnvelope<=1L<<50&&resources*tasks<=100000,valid=true}));
        }
    }
}
