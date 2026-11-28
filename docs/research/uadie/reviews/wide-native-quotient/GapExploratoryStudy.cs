using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
internal static class GapExploratoryStudy
{
    private static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
    private static readonly string[] Classes=["r10_t100_c25_d","r10_t500_c50_s","r10_t1000_c75_d","r10_t100_c75_s","r50_t100_c50_d","r50_t500_c75_s","r50_t1000_c25_d","r50_t100_c25_s","r100_t100_c75_d","r100_t500_c25_s","r100_t1000_c50_d","r100_t1000_c75_s"];
    private sealed record Model(IntegralMatrix Matrix,int Resources,int Tasks,int ProfitScale,BigInteger ProfitDivisor,decimal[] Profit,decimal[] Capacity,decimal[][] Loads);
    private static Model Read(ZipArchive zip,string name){
        string prefix="data-gen/"+name+"/";var entries=zip.Entries.Where(e=>e.FullName.StartsWith(prefix,StringComparison.Ordinal)&&e.FullName!=prefix).ToDictionary(e=>e.FullName[prefix.Length..],StringComparer.Ordinal);
        string[] ReadTokens(string key){if(!entries.TryGetValue(key,out var e)||e.Length>2_000_000)throw new FormatException("file "+key);using var r=new StreamReader(e.Open());return r.ReadToEnd().Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);}
        int resources=int.Parse(ReadTokens("data-nResources.csv").Single(),Inv),tasks=int.Parse(ReadTokens("data-nTasks.csv").Single(),Inv);
        string[] declared=name.Split('_');if(resources!=int.Parse(declared[0][1..])||tasks!=int.Parse(declared[1][1..]))throw new FormatException("dimensions");
        var allowed=Enumerable.Range(1,resources).Select(i=>$"data-taskLoadsR{i}.csv").Concat(["data-nResources.csv","data-nTasks.csv","data-dt.csv","data-taskPriorities.csv"]).ToHashSet(StringComparer.Ordinal);
        if(!entries.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(allowed))throw new FormatException("unexpected files");
        var cap=ReadTokens("data-dt.csv");var profits=ReadTokens("data-taskPriorities.csv");if(cap.Length!=resources||profits.Length!=tasks)throw new FormatException("vector length");
        decimal Dec(string x)=>decimal.Parse(x,NumberStyles.Float,Inv);
        BigInteger[] Scale(string[] tokens,out int digits,out BigInteger divisor){
            var p=tokens.Select(GapSourceAudit.Lex).ToArray();int k=p.Max(x=>x.Scale);
            var values=p.Select(x=>x.N*BigInteger.Pow(10,k-x.Scale)).ToArray();var g=values.Aggregate(BigInteger.Zero,(a,b)=>BigInteger.GreatestCommonDivisor(a,b));if(g==0)g=1;
            decimal ten=1;for(int i=0;i<k;i++)ten*=10;
            for(int i=0;i<tokens.Length;i++)if(Dec(tokens[i])*ten!=(decimal)values[i])throw new Exception("source scale mismatch");
            digits=k;divisor=g;return values.Select(x=>x/g).ToArray();
        }
        var pc=Scale(profits,out int pk,out BigInteger pg).Select(x=>checked((long)x)).ToArray();
        int n=checked(resources*tasks);var rows=new List<WeightedRow>();var loads=new decimal[resources][];
        for(int i=0;i<resources;i++){
            var a=ReadTokens($"data-taskLoadsR{i+1}.csv");if(a.Length!=tasks)throw new FormatException("load vector");
            loads[i]=a.Select(Dec).ToArray();var ints=Scale(a.Append(cap[i]).ToArray(),out _,out _);
            int[] idx=Enumerable.Range(i*tasks,tasks).Where(j=>ints[j-i*tasks]!=0).ToArray();
            rows.Add(new(idx,idx.Select(j=>checked((long)ints[j-i*tasks])).ToArray(),0,checked((long)ints[^1])));
        }
        // Explicit exploratory hypothesis: optional task assignment, at most one resource per task.
        for(int j=0;j<tasks;j++)rows.Add(new(Enumerable.Range(0,resources).Select(i=>i*tasks+j).ToArray(),Enumerable.Repeat(1L,resources).ToArray(),0,1));
        var m=new IntegralMatrix(n,new long[n],Enumerable.Repeat(1L,n).ToArray(),Enumerable.Range(0,n).Select(i=>pc[i%tasks]).ToArray(),0,"max",rows.ToArray());
        MatrixAdmission.Check(m);return new(m,resources,tasks,pk,pg,profits.Select(Dec).ToArray(),cap.Select(Dec).ToArray(),loads);
    }
    private static string? VerifySource(Model m,long[]? bits,long? value){
        if(bits==null){if(value!=null)throw new Exception("status");return null;}
        decimal profit=0;var counts=new int[m.Tasks];
        for(int i=0;i<m.Resources;i++){decimal use=0;for(int j=0;j<m.Tasks;j++){
            long x=bits[i*m.Tasks+j];if(x is not(0 or 1))throw new Exception("domain");if(x==1){counts[j]++;use+=m.Loads[i][j];profit+=m.Profit[j];}
        }if(use>m.Capacity[i])throw new Exception("decimal source capacity");}
        if(counts.Any(c=>c>1))throw new Exception("source assignment");
        decimal scale=1;for(int i=0;i<m.ProfitScale;i++)scale*=10;
        if(value==null||profit*scale!=(decimal)m.ProfitDivisor*value.Value)throw new Exception("source objective");
        return profit.ToString(Inv);
    }
    internal static void Run(string path,string split){
        int id=split switch{"dev"=>1,"holdout"=>11,_=>throw new ArgumentException("split")};
        using var zip=ZipFile.OpenRead(path);string[] arms=["CP-SAT","HiGHS","G24"];int pos=0;
        foreach(string c in Classes){
            string name=c+"_GEN"+id;Model m;
            try{m=Read(zip,name);}catch(Exception ex)when(ex is FormatException or ArgumentException or OverflowException){Console.WriteLine(JsonSerializer.Serialize(new{split,name,admitted=false,reason=ex.Message}));pos++;continue;}
            string digest=MatrixAdmission.Digest(m.Matrix);var answers=new List<MatrixEvidence>();
            for(int j=0;j<3;j++){
                string arm=arms[(pos+j)%3];long start=Stopwatch.GetTimestamp();var trace=new PhaseTrace();MatrixEvidence e;
                try{
                    e=arm=="G24"?new MatrixDispatcher(preserveNetworkRows:true).Decide(m.Matrix,trace):MatrixStudy.Wrap(m.Matrix,digest,arm,arm=="HiGHS"?MatrixReference.Highs(m.Matrix):MatrixDispatcher.Original(m.Matrix));
                    MatrixVerifier.Replay(e);string? decimalProfit=VerifySource(m,e.X,e.Value);byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(e,CertifiedQuotient.Json);
                    long ticks=Stopwatch.GetTimestamp()-start;answers.Add(e);
                    Console.WriteLine(JsonSerializer.Serialize(new{split,name,admitted=true,arm,digest,status=e.Status,e.Assurance,e.Route,e.Value,decimalProfit,variables=m.Matrix.N,rows=m.Matrix.Rows.Length,profitScale=m.ProfitScale,profitDivisor=m.ProfitDivisor.ToString(),ticks,frequency=Stopwatch.Frequency,evidenceBytes=bytes.Length,sourceVerified=e.X!=null}));
                }catch(Exception ex){Console.WriteLine(JsonSerializer.Serialize(new{split,name,admitted=true,arm,digest,status="RejectedResult",reason=ex.Message,ticks=Stopwatch.GetTimestamp()-start,frequency=Stopwatch.Frequency}));}
            }
            foreach(var x in answers)foreach(var y in answers)if(x.Status=="Optimal"&&y.Status=="Optimal"&&x.Value!=y.Value||x.Status=="Optimal"&&y.Value>x.Value||x.Status=="Infeasible"&&y.X!=null)throw new Exception("reference disagreement");
            pos++;
        }
    }
}
