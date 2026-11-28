using System.IO.Compression;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
internal static class IndustrialAdmission
{
    // Strict subset MPS reader; unknown semantics must abstain. No best-effort coercion.
    internal static WideModel Read(string path)
    {
        using var f=File.OpenRead(path);using var gz=new GZipStream(f,CompressionMode.Decompress);using var reader=new StreamReader(gz);
        string section="",obj="",rhsName="",boundName="";
        bool integer=false,ended=false;var names=new Dictionary<string,int>();var costs=new List<long>();var bounds=new HashSet<int>();
        var rows=new Dictionary<string,(char Type,List<int> Scope)>();var rhs=new Dictionary<string,long>();var seen=new HashSet<(int,string)>();
        long Number(string s){if(!decimal.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out decimal d)||d!=decimal.Truncate(d)||d<long.MinValue||d>long.MaxValue)throw new NotSupportedException("noninteger number");return (long)d;}
        void Require(bool b,string why){if(!b)throw new NotSupportedException(why);}
        for(string? line;(line=reader.ReadLine())!=null;)
        {
            if(string.IsNullOrWhiteSpace(line)||line.StartsWith('*'))continue;
            string[] a=line.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
            if(!char.IsWhiteSpace(line[0]))
            {
                string next=a[0];Require(next is "NAME" or "ROWS" or "COLUMNS" or "RHS" or "BOUNDS" or "ENDATA","unsupported section "+next);
                if(next=="ENDATA"){ended=true;break;}
                section=next;continue;
            }
            if(section=="ROWS"){
                Require(a.Length==2,"row format");
                if(a[0]=="N"){Require(obj=="","multiple free rows");obj=a[1];}
                else{Require(a[0] is "E" or "L" or "G","row type");rows.Add(a[1],(a[0][0],[]));}
            }else if(section=="COLUMNS"){
                if(a.Length==3&&a[1].Trim('\'')=="MARKER"){string mark=a[2].Trim('\'');Require(mark is "INTORG" or "INTEND","marker");integer=mark=="INTORG";continue;}
                Require(integer,"continuous or unmarked column");Require(a.Length is 3 or 5,"column format");
                if(!names.TryGetValue(a[0],out int i)){i=names.Count;names.Add(a[0],i);costs.Add(0);}
                for(int j=1;j<a.Length;j+=2){Require(seen.Add((i,a[j])),"duplicate coefficient");long v=Number(a[j+1]);
                    if(a[j]==obj)costs[i]=v;else{Require(rows.ContainsKey(a[j])&&(v==1||v==0),"nonunit/unknown coefficient");if(v==1)rows[a[j]].Scope.Add(i);}
                }
            }else if(section=="RHS"){
                Require(a.Length is 3 or 5,"RHS format");if(rhsName=="")rhsName=a[0];Require(rhsName==a[0],"multiple RHS sets");
                for(int j=1;j<a.Length;j+=2){Require(rows.ContainsKey(a[j]),"RHS objective offset/unknown row");rhs.Add(a[j],Number(a[j+1]));}
            }else if(section=="BOUNDS"){
                Require(a.Length==4&&a[0]=="UP"&&Number(a[3])==1,"unsupported bounds");if(boundName=="")boundName=a[1];Require(boundName==a[1],"multiple bound sets");Require(bounds.Add(names[a[2]]),"duplicate bound");
            }else throw new NotSupportedException("unexpected data");
        }
        Require(ended&&obj!=""&&bounds.Count==names.Count&&!integer,"incomplete binary domain");
        var edges=new List<(int,int)>();var fixes=new List<(int,int)>();var residual=new List<WideRow>();
        foreach(var (name,row) in rows){
            long k=rhs.GetValueOrDefault(name),l=row.Type=='L'?0:k,u=row.Type=='G'?row.Scope.Count:k;
            Require(l>=0&&u>=l&&u<=row.Scope.Count,"bounds outside admitted row grammar");
            if(row.Scope.Count==2&&l==1&&u==1)edges.Add((row.Scope[0],row.Scope[1]));
            else if(row.Scope.Count==1&&l==u)fixes.Add((row.Scope[0],(int)l));
            else residual.Add(new(row.Scope.ToArray(),(int)l,(int)u));
        }
        return IncrementalQuotient.Admit(new(names.Count,edges.ToArray(),fixes.ToArray(),residual.ToArray(),costs.ToArray()));
    }
    internal static void Run(string dir)
    {
        foreach(string name in new[]{"air03","air04","air05","10teams"})
        {
            string path=Path.Combine(dir,name+".mps.gz");string sha=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            WideModel? m=null;string? reason=null;
            try{m=Read(path);}catch(NotSupportedException e){reason=e.Message;}
            if(m==null){Console.WriteLine(JsonSerializer.Serialize(new{name,sha,admission="unsupported",reason,route="original-HiGHS-MPS",native=NativeSolve(path)}));continue;}
            var features=CertifiedStudy.Features(m);
            var original=CertifiedStudy.Original(m);
            if(original.Bits!=null&&CertifiedStudy.Verify(m,original.Bits)!=original.Value)throw new Exception("source witness");
            var policy=features.Disjoint||features.ReductionProxy>=.125?"certificate":"original";
            // G15 cold-start policy: no previous hint on first decision.
            WideResult result;
            if(policy=="certificate"){
                string digest=CertifiedQuotient.Digest(m);var c=CertifiedQuotient.Produce(m,digest);
                if(c==null){policy="fallback";result=CertifiedStudy.Original(m);}
                else{if(!TransformationVerifier.Verify(m,digest,c))throw new Exception("certificate");result=CertifiedQuotient.SolveReduced(c,true,out policy,out var y);if(policy=="direct"&&!TransformationVerifier.VerifyDirect(c,y!))throw new Exception("proof");}
            }else result=CertifiedStudy.Original(m);
            if(result.Bits!=null&&CertifiedStudy.Verify(m,result.Bits)!=result.Value)throw new Exception("source witness");
            if(original.Status=="Optimal"&&result.Status=="Optimal"&&original.Value!=result.Value)throw new Exception("native disagreement");
            var independent=NativeSolve(path);
            if(independent.Status==7&&result.Status=="Optimal"&&Math.Abs(independent.Objective-result.Value!.Value)>1e-6)throw new Exception("MPS reference mismatch");
            Console.WriteLine(JsonSerializer.Serialize(new{name,sha,admission="supported",n=m.N,edges=m.Edges.Length,fixes=m.Fixes.Length,rows=m.Rows.Length,nonzeros=m.Rows.Sum(r=>(long)r.Scope.Length)+2L*m.Edges.Length+m.Fixes.Length,features,route=policy,original=original.Status,originalValue=original.Value,g15=result.Status,g15Value=result.Value,native=independent},CertifiedQuotient.Json));
        }
    }
    private record NativeResult(int Status,double Objective,int PrimalStatus,double MaxPrimalInfeasibility);
    private static NativeResult NativeSolve(string path)
    {
        IntPtr h=Native.Highs_create();if(h==IntPtr.Zero)throw new Exception("create");
        void Ok(int s){if(s<0)throw new Exception("HiGHS error");}
        try{
            Ok(Native.Highs_setBoolOptionValue(h,"output_flag",0));Ok(Native.Highs_setIntOptionValue(h,"threads",1));Ok(Native.Highs_setDoubleOptionValue(h,"time_limit",2));Ok(Native.Highs_setDoubleOptionValue(h,"mip_rel_gap",0));Ok(Native.Highs_setDoubleOptionValue(h,"mip_abs_gap",0));
            Ok(Native.Highs_readModel(h,path.EndsWith(".gz",StringComparison.Ordinal)?path[..^3]:path));Ok(Native.Highs_run(h));
            int status=Native.Highs_getModelStatus(h);Ok(Native.Highs_getIntInfoValue(h,"primal_solution_status",out int primal));
            double objective=0,inf=0;if(primal==2){Ok(Native.Highs_getDoubleInfoValue(h,"objective_function_value",out objective));Ok(Native.Highs_getDoubleInfoValue(h,"max_primal_infeasibility",out inf));}
            return new(status,objective,primal,inf);
        }finally{Native.Highs_destroy(h);}
    }
    private static class Native{
        const string Lib="highs.so";
        [DllImport(Lib)]internal static extern IntPtr Highs_create();
        [DllImport(Lib)]internal static extern void Highs_destroy(IntPtr h);
        [DllImport(Lib)]internal static extern int Highs_readModel(IntPtr h,string path);
        [DllImport(Lib)]internal static extern int Highs_run(IntPtr h);
        [DllImport(Lib)]internal static extern int Highs_getModelStatus(IntPtr h);
        [DllImport(Lib)]internal static extern int Highs_setBoolOptionValue(IntPtr h,string name,int value);
        [DllImport(Lib)]internal static extern int Highs_setIntOptionValue(IntPtr h,string name,int value);
        [DllImport(Lib)]internal static extern int Highs_setDoubleOptionValue(IntPtr h,string name,double value);
        [DllImport(Lib)]internal static extern int Highs_getIntInfoValue(IntPtr h,string name,out int value);
        [DllImport(Lib)]internal static extern int Highs_getDoubleInfoValue(IntPtr h,string name,out double value);
    }
}
