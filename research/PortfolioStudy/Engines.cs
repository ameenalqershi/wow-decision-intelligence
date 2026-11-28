using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Google.OrTools.Sat;
using Highs;
using WowDi.Solver.Contracts;
using WowDi.Udir;

namespace PortfolioStudy;

/// <summary>Everything a research worker needs; written by the orchestrator, read by the worker process.</summary>
public sealed record WorkerInput(IlpInstance Instance,string[] VarIds,string TenantId,string ModelId,string RevisionId,string ObjectiveId,
    SolverIdentity Solver);

/// <summary>RESEARCH worker (not a production adapter). Maps the neutral instance directly to each engine API, as the
/// existing R07 qualification studies do, and reports an exact integer assignment. Objective values are recomputed
/// exactly from the integer assignment; floating engine bounds are rounded only in the direction that keeps them valid
/// for an integer objective, and remain solver attestations.</summary>
public static class Engines
{
    public static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){WriteIndented=false};
    public const string Marker="WOWDI_RESULT ";
    /// <summary>Decode the last framed result line; anything else on stdout is ignored, a missing frame decodes to null.</summary>
    public static SolverResult? Decode(byte[] bytes)
    {
        var line=System.Text.Encoding.UTF8.GetString(bytes).Split('\n').LastOrDefault(l=>l.StartsWith(Marker,StringComparison.Ordinal));
        return line is null?null:JsonSerializer.Deserialize<SolverResult>(line[Marker.Length..],Json);
    }

    public static int Worker(string engine,string inputPath,int threads,int softMs,string runId)
    {
        var input=JsonSerializer.Deserialize<WorkerInput>(File.ReadAllBytes(inputPath),Json)!;
        var x=input.Instance;
        // The soft limit is measured from process start: runtime start-up, JIT and native library loading consume part of
        // the budget before the engine clock starts. Without this the engine overran the hard deadline and lost its incumbent.
        var uptime=(int)(DateTime.Now-System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        softMs=Math.Max(100,softMs-uptime-50);
        var (status,term,values,bound)=engine switch
        {
            "cpsat"=>CpSat(x,threads,softMs),
            "highs"=>HighsMip(x,threads,softMs),
            _=>throw new ArgumentException("engine")
        };
        ImmutableDictionary<string,string>? incumbent=null;string? objective=null;
        if(values is not null)
        {
            incumbent=Enumerable.Range(0,x.N).ToImmutableDictionary(j=>input.VarIds[j],j=>values[j].ToString(CultureInfo.InvariantCulture),StringComparer.Ordinal);
            long v=0;for(int j=0;j<x.N;j++)v=checked(v+(long)x.Cost[j]*values[j]);
            objective=v.ToString(CultureInfo.InvariantCulture);
        }
        var dir=x.Maximize?Direction.Maximize:Direction.Minimize;
        var evidence=new Evidence(EvidenceSource.SolverAttestation,engine+":"+status);
        var result=new SolverResult(input.TenantId,input.ModelId,input.RevisionId,runId,input.Solver,status,term,incumbent,
            [new(input.ObjectiveId,objective,bound is null?null:new(bound,dir,evidence),"0","0")],
            status is MathematicalStatus.Optimal or MathematicalStatus.Infeasible?evidence:null,new(null,null,null));
        // Engines may print banners on stdout; the result is framed on its own final line.
        Console.Out.Write("\n"+Marker+JsonSerializer.Serialize(result,Json)+"\n");
        return 0;
    }

    private static string? SafeBound(double b,bool maximize)
    {
        if(!double.IsFinite(b)||Math.Abs(b)>9e15)return null;
        var v=maximize?Math.Ceiling(b-1e-6):Math.Floor(b+1e-6);
        return ((long)v).ToString(CultureInfo.InvariantCulture);
    }

    private static (MathematicalStatus,Termination,long[]?,string?) CpSat(IlpInstance x,int threads,int softMs)
    {
        var model=new CpModel();
        var vars=Enumerable.Range(0,x.N).Select(j=>model.NewIntVar(0,x.Upper[j],"x"+j)).ToArray();
        for(int i=0;i<x.Rhs.Length;i++)
            model.Add(LinearExpr.WeightedSum(x.RowIdx[i].Select(j=>vars[j]),x.RowCoef[i].Select(c=>(long)c))<=x.Rhs[i]);
        var obj=LinearExpr.WeightedSum(vars,x.Cost.Select(c=>(long)c));
        if(x.Maximize)model.Maximize(obj);else model.Minimize(obj);
        var solver=new CpSolver{StringParameters=FormattableString.Invariant(
            $"num_workers:{threads} random_seed:1 max_time_in_seconds:{softMs/1000.0} log_search_progress:false")};
        // In-process watchdog: CP-SAT may overrun max_time_in_seconds (presolve, LNS); StopSearch at the soft limit makes it
        // return its incumbent before the boundary's hard kill (which would discard everything).
        using var watchdog=new Timer(_=>solver.StopSearch(),null,softMs,Timeout.Infinite);
        var s=solver.Solve(model);
        long[]? values=s is CpSolverStatus.Optimal or CpSolverStatus.Feasible?vars.Select(v=>solver.Value(v)).ToArray():null;
        var status=s switch{CpSolverStatus.Optimal=>MathematicalStatus.Optimal,CpSolverStatus.Feasible=>MathematicalStatus.Feasible,
            CpSolverStatus.Infeasible=>MathematicalStatus.Infeasible,_=>MathematicalStatus.Unknown};
        var term=s is CpSolverStatus.Optimal or CpSolverStatus.Infeasible?Termination.Completed:Termination.Timeout;
        return (status,term,values,values is null?null:SafeBound(solver.BestObjectiveBound,x.Maximize));
    }

    private static (MathematicalStatus,Termination,long[]?,string?) HighsMip(IlpInstance x,int threads,int softMs)
    {
        // Row-wise CSR, all rows sum <= rhs, all columns integer.
        var start=new List<int>();var index=new List<int>();var value=new List<double>();
        for(int i=0;i<x.Rhs.Length;i++){start.Add(index.Count);index.AddRange(x.RowIdx[i]);value.AddRange(x.RowCoef[i].Select(c=>(double)c));}
        var inf=1e30;
        var model=new HighsModel(x.Cost.Select(c=>(double)c).ToArray(),new double[x.N],x.Upper.Select(u=>(double)u).ToArray(),
            Enumerable.Repeat(-inf,x.Rhs.Length).ToArray(),x.Rhs.Select(b=>(double)b).ToArray(),start.ToArray(),index.ToArray(),value.ToArray(),
            Enumerable.Repeat(1,x.N).ToArray(),0,HighsMatrixFormat.kRowwise,x.Maximize?HighsObjectiveSense.kMaximize:HighsObjectiveSense.kMinimize);
        using var solver=new HighsLpSolver();
        solver.setBoolOptionValue("output_flag",0);
        solver.setDoubleOptionValue("time_limit",softMs/1000.0);
        solver.setIntOptionValue("threads",threads);
        solver.setDoubleOptionValue("mip_rel_gap",0.0);
        solver.setIntOptionValue("random_seed",1);
        if(solver.passMip(model)!=HighsStatus.kOk)return (MathematicalStatus.Unknown,Termination.Failed,null,null);
        solver.run();
        var ms=solver.GetModelStatus();
        var info=solver.getInfo();
        long[]? values=null;
        var col=solver.getSolution().colvalue;
        if(col is {Length:>0}&&col.Length==x.N&&col.All(double.IsFinite)&&ms is HighsModelStatus.kOptimal or HighsModelStatus.kTimeLimit or
           HighsModelStatus.kInterrupt or HighsModelStatus.kSolutionLimit or HighsModelStatus.kObjectiveBound or HighsModelStatus.kObjectiveTarget&&
           double.IsFinite(info.ObjectiveValue)&&Math.Abs(info.ObjectiveValue)<1e29)
            values=col.Select(v=>(long)Math.Round(v)).ToArray(); // exact feasibility is re-checked by the independent verifier
        var status=ms switch{HighsModelStatus.kOptimal when values is not null=>MathematicalStatus.Optimal,
            HighsModelStatus.kInfeasible=>MathematicalStatus.Infeasible,_ when values is not null=>MathematicalStatus.Feasible,_=>MathematicalStatus.Unknown};
        var term=ms is HighsModelStatus.kOptimal or HighsModelStatus.kInfeasible?Termination.Completed:Termination.Timeout;
        return (status,term,values,values is null?null:SafeBound(info.DualBound,x.Maximize));
    }

    /// <summary>Containment probe: allocate and touch <paramref name="mb"/> MiB, then wait to be killed.</summary>
    public static int Hog(int mb)
    {
        var blocks=new List<byte[]>();
        for(int i=0;i<mb;i++){var b=new byte[1<<20];for(int j=0;j<b.Length;j+=4096)b[j]=1;blocks.Add(b);}
        Thread.Sleep(Timeout.Infinite);GC.KeepAlive(blocks);return 0;
    }
    /// <summary>Containment probe: spawn <paramref name="burners"/> CPU-burning grandchildren and one memory hog, then wait.
    /// Everything must be charged to, limited by and killed with the worker's cgroup.</summary>
    public static int Spawn(int burners,int mb)
    {
        var dotnet=Environment.ProcessPath!;var self=typeof(Engines).Assembly.Location;
        var kids=new List<System.Diagnostics.Process>();
        for(int i=0;i<burners;i++)kids.Add(System.Diagnostics.Process.Start(dotnet,[self,"burn","1"]));
        kids.Add(System.Diagnostics.Process.Start(dotnet,[self,"hog",mb.ToString(CultureInfo.InvariantCulture)]));
        Burn(1);GC.KeepAlive(kids);return 0;
    }

    /// <summary>CPU burner for the overspend study: T busy threads until killed.</summary>
    public static int Burn(int threads)
    {
        var ts=Enumerable.Range(0,threads).Select(_=>new Thread(()=>{long v=0;while(true)v=unchecked(v*31+1);}){IsBackground=false}).ToArray();
        foreach(var t in ts)t.Start();
        foreach(var t in ts)t.Join();
        return 0;
    }
}
