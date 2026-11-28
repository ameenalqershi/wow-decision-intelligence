using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScaleBench;
using WowDi.Dml.Binding;
using WowDi.Dml.Semantics;
using WowDi.Research.Certificates;
using WowDi.Research.Sparse;
using WowDi.Solver.Contracts;
using WowDi.Udir;
using WowDi.Verification.Independent;

// Modes (each prints one "RESULT {json}" line):
//   compare <n>                         UDIR reference vs sparse on an n-item 5-row knapsack (old vs new structure)
//   scale <nnz> <solve 0|1> <limit s>   lot sizing at a target nonzero count: build/encode/verify[/solve/certify]
//   vars <vars> <solve 0|1> <limit s>   lot sizing at a target variable count
//   mps <file> <reference|none> <limit s>
var mode=args.ElementAtOrDefault(0);
var res=mode switch
{
    "compare"=>Bench.Compare(int.Parse(args[1],CultureInfo.InvariantCulture)),
    "scale"=>Bench.Scale(LotSizing.ForNonzeros(long.Parse(args[1],CultureInfo.InvariantCulture)),args[2]=="1",double.Parse(args[3],CultureInfo.InvariantCulture),"nnz="+args[1]),
    "vars"=>Bench.Scale(((int)(long.Parse(args[1],CultureInfo.InvariantCulture)/3/20),20),args[2]=="1",double.Parse(args[3],CultureInfo.InvariantCulture),"vars="+args[1]),
    "mps"=>Bench.MpsRun(args[1],args[2],double.Parse(args[3],CultureInfo.InvariantCulture)),
    _=>throw new ArgumentException("mode")
};
Console.WriteLine("RESULT "+JsonSerializer.Serialize(res,new JsonSerializerOptions{NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals}));
return 0;

static class Bench
{
    static readonly Stopwatch Clock=new();
    static double T(Action a){Clock.Restart();a();return Math.Round(Clock.Elapsed.TotalMilliseconds,1);}
    static long Heap()=>GC.GetTotalMemory(false);
    static Dictionary<string,object?> Mem(Dictionary<string,object?> d)
    {
        d["peakWorkingSetMB"]=Process.GetCurrentProcess().PeakWorkingSet64>>20;
        d["allocatedMB"]=GC.GetTotalAllocatedBytes()>>20;
        d["heapMB"]=Heap()>>20;
        return d;
    }

    // ------------------------------------------------------------------ old (UDIR) vs new (sparse) at sizes UDIR can hold
    public static Dictionary<string,object?> Compare(int n)
    {
        var r=new Random(1);int m=5;
        var w=Enumerable.Range(0,m).Select(_=>Enumerable.Range(0,n).Select(_=>r.Next(1,101)).ToArray()).ToArray();
        var p=Enumerable.Range(0,n).Select(j=>w.Sum(row=>row[j])/m+r.Next(1,21)).ToArray();
        var cap=w.Select(row=>row.Sum()/2).ToArray();
        var x=new long[n];for(int j=0;j<n;j+=3)x[j]=1; // feasible: every third item (load ~1/3 < 1/2)
        var d=new Dictionary<string,object?>{["mode"]="compare",["n"]=n,["nonzeros"]=n*m+n};
        // old: DML -> UDIR -> OriginalVerifier
        string Sum(int[] c){string Tree(int lo,int hi)=>hi-lo==1?$"{c[lo]}*x{lo}":"("+Tree(lo,(lo+hi)/2)+" + "+Tree((lo+hi)/2,hi)+")";return Tree(0,c.Length);}
        var text=new StringBuilder("problem P { ");
        for(int j=0;j<n;j++)text.Append(CultureInfo.InvariantCulture,$"decision x{j} : Int in [0,1]; ");
        for(int i=0;i<m;i++)text.Append(CultureInfo.InvariantCulture,$"constraint r{i} : {Sum(w[i])} <= {cap[i]}; ");
        text.Append("maximize goal priority 1 : ").Append(Sum(p)).Append("; }");
        var catalog=Encoding.UTF8.GetBytes("""{"units":[{"symbol":"minute","dimension":"time","scale":"1"}],"currencies":[],"calendars":["shift"]}""");
        var units=new MeasurementSemantics([new("minute","time","1")],[],["shift"]);
        DmlCompilation? cc=null;string? diag=null;
        d["udirCompileMs"]=T(()=>
        {
            var bound=DmlBinder.Bind("s",Encoding.UTF8.GetBytes(text.ToString()),new BindingContext());
            if(!bound.Accepted){diag=bound.Diagnostic?.Code;return;}
            var c=DmlCompiler.Compile(bound.Document!,new("t","m","r",new("shift","UTC","2026-01-01T00:00:00Z","minute"),units,
                [Convert.ToHexStringLower(SHA256.HashData(catalog))],[],["integer.linear"]));
            if(c.Accepted)cc=c.Compilation;else diag=c.Diagnostic?.Code;
        });
        d["udirDiagnostic"]=diag;
        if(cc is not null)
        {
            var doc=cc.Original;var model=doc.Model;byte[] env=[];
            d["udirEnvelopeBytes"]=(env=doc.Write()).Length;d["udirBytesPerNnz"]=Math.Round(env.Length/(double)(n*m+n),1);
            var ids=Enumerable.Range(0,n).Select(j=>model.Symbols.Single(s=>s.Role==SymbolRole.Variable&&(s.Id=="x"+j||s.Id.EndsWith(".x"+j,StringComparison.Ordinal))).Id).ToArray();
            var value=Enumerable.Range(0,n).Sum(j=>(long)p[j]*x[j]).ToString(CultureInfo.InvariantCulture);
            var scope=new VerificationScope(model.TenantId,model.ModelId,model.RevisionId,"run",Convert.ToHexStringLower(SHA256.HashData(env)));
            var result=new SolverResult(model.TenantId,model.ModelId,model.RevisionId,"run",new("ref","1","rid"),MathematicalStatus.Feasible,Termination.Completed,
                Enumerable.Range(0,n).ToImmutableDictionary(j=>ids[j],j=>x[j].ToString(CultureInfo.InvariantCulture),StringComparer.Ordinal),
                [new(model.Objectives[0].Id,value,null,"0","0")],null,new(null,null,null));
            var facts=cc.GeneratedInputs[cc.FactsDigest].ToArray();
            VerificationReport? rep=null;
            d["udirVerifyMs"]=T(()=>rep=OriginalVerifier.Check(doc,units,scope,result,facts));
            d["udirAccepted"]=rep!.Accepted;
        }
        // new: sparse
        SparseModel sm=null!;
        d["sparseBuildMs"]=T(()=>
        {
            var b=new SparseBuilder("knap",true);
            for(int j=0;j<n;j++)b.AddColumn(0,1,true,p[j]);
            var cols=Enumerable.Range(0,n).ToArray();
            for(int i=0;i<m;i++)b.AddRow(cols,w[i].Select(v=>(long)v).ToArray(),null,cap[i]);
            sm=b.Build();
        });
        (byte[] Bytes,string Sha) enc=default;
        d["sparseEncodeMs"]=T(()=>enc=SparseCodec.Encode(sm));
        d["sparseBytes"]=enc.Bytes.Length;d["sparseBytesPerNnz"]=Math.Round(enc.Bytes.Length/(double)(n*m+n),1);
        SparseCheck? chk=null;
        d["sparseVerifyMs"]=T(()=>chk=SparseVerifier.Check(sm,x,1));
        d["sparseFeasible"]=chk!.ExactFeasible;
        d["objectiveAgrees"]=cc is null?null:chk.Objective==Q.Of(Enumerable.Range(0,n).Sum(j=>(long)p[j]*x[j]));
        return Mem(d);
    }

    // ------------------------------------------------------------------ scale ladder on the new structure
    public static Dictionary<string,object?> Scale((int P,int T) size,bool solve,double limit,string label)
    {
        var d=new Dictionary<string,object?>{["mode"]="scale",["target"]=label,["products"]=size.P,["periods"]=size.T,["resources"]=2};
        LotSizing.Instance inst=null!;
        d["buildMs"]=T(()=>inst=LotSizing.Build(size.P,size.T,2,42));
        var m=inst.Model;
        d["columns"]=m.Columns;d["rows"]=m.Rows;d["nonzeros"]=m.Nonzeros;d["distinctValues"]=m.Pool.Length;
        d["heapAfterBuildMB"]=Heap()>>20;
        d["validateMs"]=T(()=>m.Validate());
        (byte[] Bytes,string Sha) enc=default;
        d["encodeHashMs"]=T(()=>enc=SparseCodec.Encode(m));
        d["encodedMB"]=Math.Round(enc.Bytes.Length/1048576.0,2);d["bytesPerNnz"]=Math.Round(enc.Bytes.Length/(double)m.Nonzeros,2);
        d["sha256"]=enc.Sha;
        SparseModel back=null!;
        d["decodeValidateMs"]=T(()=>back=SparseCodec.Decode(enc.Bytes));
        d["roundTripDigestEqual"]=SparseCodec.Encode(back).Sha256==enc.Sha;
        enc=default;back=null!;GC.Collect();
        d["transposeMs"]=T(()=>m.Transpose());
        SparseCheck? c1=null,c2=null,bad=null;
        d["verify1ThreadMs"]=T(()=>c1=SparseVerifier.Check(m,inst.Feasible,1));
        d["verify2ThreadsMs"]=T(()=>c2=SparseVerifier.Check(m,inst.Feasible,2));
        d["feasibleVerified"]=c1!.ExactFeasible&&c2!.ExactFeasible&&c1.Objective==c2.Objective;
        d["fastPathRows"]=c1.FastPathRows;d["exactPathRows"]=c1.ExactPathRows;
        d["objectiveOfConstructedPlan"]=c1.Objective.ToString();
        var mutated=(long[])inst.Feasible.Clone();int victim=3*(size.P*size.T/2);mutated[victim]+=1; // break one balance row
        d["verifyMutatedMs"]=T(()=>bad=SparseVerifier.Check(m,mutated,2));
        d["mutationDetected"]=!bad!.ExactFeasible&&bad.ViolatedRows>=1;
        d["mutationFirstRow"]=bad.FirstViolatedRow;
        if(solve)Solve(m,limit,d,inst.Feasible);
        return Mem(d);
    }

    static void Solve(SparseModel m,double limit,Dictionary<string,object?> d,long[]? known)
    {
        // LP relaxation: duals -> exact safe bound (valid for the MILP); then MILP incumbent -> exact verification -> certified gap.
        var lp=HighsBridge.Solve(m,true,limit,2);
        d["lpStatus"]=lp.Status;d["lpSeconds"]=Math.Round(lp.Seconds,2);d["lpObjective"]=lp.Objective;
        BoundCertificate? cert=null;
        if(lp.RowDual is {} y)
        {
            var yq=y.Select(v=>Q.FromDouble(double.IsFinite(v)?v:0)).ToArray();var neg=yq.Select(v=>-v).ToArray();
            d["certificateMs"]=T(()=>cert=SafeBound.Cleaned(m,y));
            d["certifiedBound"]=cert!.Bound?.ToDouble();d["boundIntegerRounded"]=cert.IntegerRounded;
            if(cert.Bound is {} cb&&lp.Objective is {} lo)d["lpObjectiveMinusCertifiedBound"]=Math.Abs(lo-cb.ToDouble());
        }
        if(lp.X is {} xl){var ch=SparseVerifier.Check(m,xl,2);d["lpPointMaxRowViolation"]=ch.MaxRowViolation.ToDouble();d["lpPointExactFeasible"]=ch.ExactFeasible;}
        if(!m.Integer.Any(b=>b))return;
        var mip=HighsBridge.Solve(m,false,limit,2);
        d["mipStatus"]=mip.Status;d["mipSeconds"]=Math.Round(mip.Seconds,2);d["mipObjective"]=mip.Objective;d["mipSolverDualBound"]=mip.DualBound;
        if(mip.X is {} raw)
        {
            // Raw floating point first (honest: binary64 values are rarely exactly feasible), then the integer-rounded point:
            // integer columns rounded to the nearest integer, continuous columns kept exact. Only the exact check decides.
            var rawCheck=SparseVerifier.Check(m,raw,2);
            d["mipRawExactFeasible"]=rawCheck.ExactFeasible;d["mipRawMaxRowViolation"]=rawCheck.MaxRowViolation.ToDouble();
            d["mipRawMaxColumnViolation"]=rawCheck.MaxColumnViolation.ToDouble();
            var x=raw.Select((v,j)=>m.Integer[j]?Math.Round(v):v).ToArray();
            SparseCheck? ch=null;
            d["mipVerifyMs"]=T(()=>ch=SparseVerifier.Check(m,x,2));
            d["mipExactFeasible"]=ch!.ExactFeasible;d["mipMaxRowViolation"]=ch.MaxRowViolation.ToDouble();
            d["mipMaxColumnViolation"]=ch.MaxColumnViolation.ToDouble();d["mipVerifiedObjective"]=ch.Objective.ToString();
            d["mipToleranceFeasible1e-6"]=SparseVerifier.WithinTolerance(ch,Q.Parse("1e-6"));
            if(ch.ExactFeasible&&cert is not null)
            {
                var a=SafeBound.Assess(m,ch.Objective,cert);
                d["certifiedStatus"]=a.Status.ToString();d["certifiedGap"]=a.AbsoluteGap?.ToDouble();
                d["certifiedRelativeGap"]=a.AbsoluteGap is {} g?Math.Round(g.ToDouble()/Math.Max(1,Math.Abs(ch.Objective.ToDouble())),6):null;
            }
        }
    }

    // ------------------------------------------------------------------ public benchmark instances
    public static Dictionary<string,object?> MpsRun(string file,string reference,double limit)
    {
        var d=new Dictionary<string,object?>{["mode"]="mps",["instance"]=Path.GetFileName(file),["reference"]=reference};
        SparseModel m=null!;
        try{d["readMs"]=T(()=>m=Mps.Read(file));}
        catch(Exception ex){d["error"]=ex.GetType().Name+":"+ex.Message;return Mem(d);}
        d["columns"]=m.Columns;d["rows"]=m.Rows;d["nonzeros"]=m.Nonzeros;d["integers"]=m.Integer.Count(b=>b);
        d["distinctValues"]=m.Pool.Length;d["nonDyadicValues"]=m.Pool.Count(q=>!IsPow2(q.D));
        var enc=SparseCodec.Encode(m);d["encodedBytes"]=enc.Bytes.Length;d["sha256"]=enc.Sha256;
        Solve(m,limit,d,null);
        if(reference!="none"&&double.TryParse(reference,NumberStyles.Float,CultureInfo.InvariantCulture,out var refv))
        {
            var got=d.GetValueOrDefault("mipVerifiedObjective") is string s?Q.Parse(s).ToDouble():(d.GetValueOrDefault("lpObjective") as double?);
            if(got is {} g)d["relativeErrorVsReference"]=Math.Abs(g-refv)/Math.Max(1,Math.Abs(refv));
            if(d.GetValueOrDefault("certifiedBound") is double cb)
                d["referenceRespectsBound"]=m.Maximize?refv<=cb*(1+1e-12)+1e-9:refv>=cb*(1-1e-12)-1e-9;
        }
        return Mem(d);
    }
    static bool IsPow2(System.Numerics.BigInteger v)=>!v.IsZero&&(v&(v-1)).IsZero;
}
