using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using PortfolioStudy;
using WowDi.Dml.Lowering;
using WowDi.Runtime.Governance;
using WowDi.Solver.Contracts;
using WowDi.Solver.Planning;
using WowDi.Solver.Portfolio;
using WowDi.Udir;

// Modes:
//   worker <cpsat|highs> <input.json> <threads> <softMs> <runId>   (spawned inside the S07 boundary)
//   burn <threads>                                                 (overspend study load)
//   study <outDir> [quick]                                         (requires a delegated cgroup v2 root)
if(args.Length>=6&&args[0]=="worker")
    return Engines.Worker(args[1],args[2],int.Parse(args[3],CultureInfo.InvariantCulture),int.Parse(args[4],CultureInfo.InvariantCulture),args[5]);
if(args.Length==2&&args[0]=="burn")return Engines.Burn(int.Parse(args[1],CultureInfo.InvariantCulture));
if(args.Length==2&&args[0]=="hog")return Engines.Hog(int.Parse(args[1],CultureInfo.InvariantCulture));
if(args.Length==3&&args[0]=="spawn")return Engines.Spawn(int.Parse(args[1],CultureInfo.InvariantCulture),int.Parse(args[2],CultureInfo.InvariantCulture));
if(args.Length>=2&&args[0]=="study")return await Study.RunAsync(args[1],args.Skip(2).Contains("quick"),
    args.Skip(2).FirstOrDefault(a=>a.StartsWith("only=",StringComparison.Ordinal))?[5..].Split(','));
Console.Error.WriteLine("usage: worker|burn|study");
return 2;

static class Study
{
    static string Env(string k)=>Environment.GetEnvironmentVariable(k)??throw new InvalidOperationException(k+" is required.");
    static string Sha(byte[] b)=>Convert.ToHexStringLower(SHA256.HashData(b));

    public static async Task<int> RunAsync(string outDir,bool quick,string[]? only=null)
    {
        bool Want(string k)=>only is null||only.Contains(k);
        Directory.CreateDirectory(outDir);
        var dotnet=Env("WOWDI_STUDY_DOTNET");var bootstrap=Env("WOWDI_STUDY_BOOTSTRAP");var root=Env("WOWDI_STUDY_CGROUP_ROOT");
        var self=typeof(Study).Assembly.Location;var work=Path.GetDirectoryName(self)!;
        var boundary=new WorkerProcessBoundary(new LinuxCgroupScopeFactory(root),new(dotnet,[bootstrap],work),4*1024*1024);
        var env=new Dictionary<string,object>
        {
            ["host"]=Environment.MachineName.Length>0?"linux-x64":"unknown",["cores"]=Environment.ProcessorCount,
            ["dotnet"]=Environment.Version.ToString(),["ortools"]="9.15.6755",["highs"]="1.16.0",["utc"]=DateTime.UtcNow.ToString("O"),
            ["readiness"]="RESEARCH: engines run through the production S08/S07/S05 path, but S02/S03 adapters are not qualified (R07 open)."
        };
        var doc=new Dictionary<string,object>{["environment"]=env};
        if(Want("overspend"))doc["overspend"]=await OverspendAsync(boundary,dotnet,self,work,quick);
        if(Want("scale"))doc["scaleProbe"]=ScaleProbe(quick);
        if(Want("contain"))doc["containment"]=await ContainmentAsync(boundary,dotnet,self,work);
        if(Want("residual"))doc["residualStress"]=await ResidualAsync(boundary,dotnet,self,work,outDir,quick?20:200);
        if(Want("compare"))doc["comparison"]=await CompareAsync(boundary,dotnet,self,work,outDir,quick);
        var path=Path.Combine(outDir,"portfolio-study.json");
        await File.WriteAllTextAsync(path,JsonSerializer.Serialize(doc,new JsonSerializerOptions{WriteIndented=true,
            NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals}));
        Console.WriteLine("evidence: "+path);
        return 0;
    }

    // ---------------------------------------------------------------- 1. budget overspend between deadline and kill
    static async Task<object> OverspendAsync(WorkerProcessBoundary boundary,string dotnet,string self,string work,bool quick)
    {
        var rows=new List<Dictionary<string,object>>();
        int reps=quick?2:10;
        foreach(var threads in new[]{1,2})
        foreach(var deadline in new[]{200,500,1000,2000})
        for(int rep=0;rep<reps;rep++)
        {
            var pool=new GovernedPool("study",threads,512L<<20,1,boundary);
            var key=new WorkerRunKey("study","burn","r",$"burn-{threads}-{deadline}-{rep}");
            var sw=Stopwatch.StartNew();
            var (_,res)=await pool.RunAsync(new WorkerRequest(key,new WorkerBudget(threads,512L<<20,TimeSpan.FromMilliseconds(deadline)),new string('0',64)),
                new WorkerProgram(dotnet,[self,"burn",threads.ToString(CultureInfo.InvariantCulture)],work),CancellationToken.None);
            var reserved=(long)threads*deadline;var cpu=res.Resources.CpuMs??-1;
            rows.Add(new(){["threads"]=threads,["deadlineMs"]=deadline,["elapsedMs"]=res.Resources.ElapsedMs??-1,["cpuMs"]=cpu,
                ["reservedCoreMs"]=reserved,["overspendCoreMs"]=Math.Max(0,cpu-reserved),["wallOverrunMs"]=(res.Resources.ElapsedMs??0)-deadline,
                ["termination"]=res.Termination.ToString(),["cleanup"]=res.CleanupConfirmed,["hostWallMs"]=sw.ElapsedMilliseconds});
            Console.WriteLine($"burn t={threads} d={deadline} cpu={cpu} elapsed={res.Resources.ElapsedMs} term={res.Termination} clean={res.CleanupConfirmed}");
        }
        var over=rows.Select(r=>(long)r["overspendCoreMs"]).OrderBy(v=>v).ToArray();
        var lat=rows.Select(r=>(long)r["wallOverrunMs"]).OrderBy(v=>v).ToArray();
        var ratio=rows.Select(r=>(double)(long)r["cpuMs"]/(long)r["reservedCoreMs"]).OrderBy(v=>v).ToArray();
        static T P<T>(T[] s,double q)=>s[Math.Min(s.Length-1,(int)Math.Ceiling(q*s.Length)-1)];
        return new Dictionary<string,object>{["runs"]=rows.Count,["allCleanupConfirmed"]=rows.All(r=>(bool)r["cleanup"]),
            ["overspendCoreMs"]=new{max=over[^1],p95=P(over,0.95),p50=P(over,0.5)},["wallOverrunMs"]=new{max=lat[^1],p95=P(lat,0.95),p50=P(lat,0.5),min=lat[0]},
            ["cpuOverReserved"]=new{max=ratio[^1],p50=P(ratio,0.5)},["rows"]=rows};
    }

    // ---------------------------------------------------------------- 1b. containment of grandchildren (CPU, memory, kill)
    static async Task<object> ContainmentAsync(WorkerProcessBoundary boundary,string dotnet,string self,string work)
    {
        var rows=new List<object>();
        foreach(var (threads,mb,limitMb,deadline) in new[]{(1,64,512,1500),(2,64,512,1500),(1,400,256,3000)})
        for(int rep=0;rep<3;rep++)
        {
            var pool=new GovernedPool("study",threads,(long)limitMb<<20,1,boundary);
            var key=new WorkerRunKey("study","contain","r",$"contain-{threads}-{mb}-{rep}");
            var (_,res)=await pool.RunAsync(new WorkerRequest(key,new WorkerBudget(threads,(long)limitMb<<20,TimeSpan.FromMilliseconds(deadline)),new string('0',64)),
                new WorkerProgram(dotnet,[self,"spawn","3",mb.ToString(CultureInfo.InvariantCulture)],work),CancellationToken.None);
            var cpu=res.Resources.CpuMs??-1;var elapsed=res.Resources.ElapsedMs??-1;
            rows.Add(new{threads,childMemoryMB=mb,limitMB=limitMb,deadlineMs=deadline,elapsedMs=elapsed,cpuMs=cpu,cpuBoundMs=threads*(elapsed+100),
                withinCpuQuota=cpu<=threads*(elapsed+100),peakMB=(res.ScopePeakBytes??-1)>>20,withinMemory=(res.ScopePeakBytes??0)<=((long)limitMb<<20),
                termination=res.Termination.ToString(),cleanup=res.CleanupConfirmed,diagnostic=res.Diagnostic});
            Console.WriteLine($"contain t={threads} hog={mb}MB limit={limitMb}MB cpu={cpu} elapsed={elapsed} peak={(res.ScopePeakBytes??-1)>>20}MB term={res.Termination} clean={res.CleanupConfirmed} {res.Diagnostic}");
        }
        return rows;
    }

    // ---------------------------------------------------------------- 1c. residual-process stress (CP-SAT, 2 workers)
    static async Task<object> ResidualAsync(WorkerProcessBoundary boundary,string dotnet,string self,string work,string outDir,int runs)
    {
        var inst=Generators.Knapsack(60,5,7);var (c,_,_)=Generators.Compile(inst);var m=c!.Original.Model;
        var varIds=Enumerable.Range(0,inst.N).Select(j=>m.Symbols.Single(s=>s.Role==SymbolRole.Variable&&(s.Id=="x"+j||s.Id.EndsWith(".x"+j,StringComparison.Ordinal))).Id).ToArray();
        var input=Path.Combine(outDir,"residual-input.json");
        File.WriteAllBytes(input,JsonSerializer.SerializeToUtf8Bytes(new WorkerInput(inst,varIds,m.TenantId,m.ModelId,m.RevisionId,m.Objectives[0].Id,new("cpsat","research","linux-x64")),Engines.Json));
        var counts=new Dictionary<string,int>();
        for(int i=0;i<runs;i++)
        {
            var pool=new GovernedPool("study",2,512L<<20,1,boundary);
            var key=new WorkerRunKey("study","residual","r",$"res-{i}");
            var (_,res)=await pool.RunAsync(new WorkerRequest(key,new WorkerBudget(2,512L<<20,TimeSpan.FromMilliseconds(2500)),new string('0',64)),
                new WorkerProgram(dotnet,[self,"worker","cpsat",input,"2",(300+(i%10)*150).ToString(CultureInfo.InvariantCulture),$"res-{i}"],work),CancellationToken.None);
            var k=res.Termination+":"+(res.Diagnostic??"ok");counts[k]=counts.GetValueOrDefault(k)+1;
        }
        Console.WriteLine("residual "+string.Join(" ",counts.Select(kv=>kv.Key+"="+kv.Value)));
        var residual=counts.Where(kv=>kv.Key.Contains("residual",StringComparison.Ordinal)).Sum(kv=>kv.Value);
        // exact one-sided 95% Clopper-Pearson upper bound for the residual rate (0 -> rule of three)
        double up=residual==0?3.0/runs:ClopperPearsonUpper(residual,runs);
        return new{runs,outcomes=counts,residual,rate=(double)residual/runs,upper95=up};
    }

    static double ClopperPearsonUpper(int k,int n)
    {
        // bisection on the binomial tail P[X<=k | p] = 0.05
        double lo=0,hi=1;
        for(int it=0;it<100;it++){var p=(lo+hi)/2;double cdf=0,term=Math.Pow(1-p,n);for(int i=0;i<=k;i++){cdf+=term;term*=(double)(n-i)/(i+1)*p/(1-p);}if(cdf>0.05)lo=p;else hi=p;}
        return hi;
    }

    // ---------------------------------------------------------------- 2. where does the current front end stop?
    static object ScaleProbe(bool quick)
    {
        var rows=new List<object>();
        foreach(var balanced in new[]{false,true})
        foreach(var n in quick?new[]{100,400}:new[]{50,100,200,400,800,1600,3200})
        {
            var inst=Generators.Knapsack(n,5,1);
            var sw=Stopwatch.StartNew();
            var (c,diag,dmlBytes)=Generators.Compile(inst,balanced);
            long? envelope=null;string? writeError=null;
            if(c is not null){try{envelope=c.Original.Write().Length;}catch(Exception ex){writeError=ex.GetType().Name+":"+(ex as WowDi.Contracts.ContractException)?.Code;}}
            rows.Add(new{form=balanced?"balanced":"chain",n,nonzeros=inst.Nonzeros,dmlBytes,accepted=c is not null&&writeError is null,diagnostic=diag,
                envelopeBytes=envelope,writeError,compileMs=sw.ElapsedMilliseconds});
            Console.WriteLine($"scale {(balanced?"balanced":"chain"),-8} n={n} nnz={inst.Nonzeros} dml={dmlBytes} accepted={c is not null} diag={diag} env={envelope} {writeError} {sw.ElapsedMilliseconds}ms");
        }
        return rows;
    }

    // ---------------------------------------------------------------- 3. equal-budget comparison
    static AdapterProfile Profile(string id,SolverFamily family,string qualification)=>new(
        new(new(id,"research","linux-x64"),["integer.linear"],[ScalarKind.Integer,ScalarKind.Boolean],null,null,2,true,true,true),
        family,family==SolverFamily.CpSat?NumericEncoding.Int64:NumericEncoding.Binary64ExactInput,qualification,1,64L<<20,TimeSpan.FromMilliseconds(100));

    static async Task<object> CompareAsync(WorkerProcessBoundary boundary,string dotnet,string self,string work,string outDir,bool quick)
    {
        var nuget=Path.Combine(Environment.GetEnvironmentVariable("HOME")??"/root",".nuget/packages");
        string Pkg(string p)=>File.Exists(p)?Sha(File.ReadAllBytes(p)):new string('0',64);
        var cpsat=Profile("cpsat",SolverFamily.CpSat,Pkg(Path.Combine(nuget,"google.ortools.runtime.linux-x64/9.15.6755/google.ortools.runtime.linux-x64.9.15.6755.nupkg")));
        var highs=Profile("highs",SolverFamily.Highs,Pkg(Path.Combine(nuget,"highs.native/1.16.0/highs.native.1.16.0.nupkg")));
        var instances=new List<IlpInstance>();
        int seeds=quick?1:5;
        for(int s=1;s<=seeds;s++)
        {
            foreach(var n in quick?new[]{30}:new[]{30,60,100})instances.Add(Generators.Knapsack(n,5,s));
            foreach(var (a,j) in quick?new[]{(5,20)}:new[]{(5,20),(8,30)})instances.Add(Generators.Assignment(a,j,100+s));
            foreach(var (n,m) in quick?new[]{(30,20)}:new[]{(30,20),(60,40)})instances.Add(Generators.Covering(n,m,200+s));
        }
        const int Threads=2;const long Memory=1L<<30;const int WallMs=3000;
        var budget=new PortfolioBudget((long)Threads*WallMs,Threads,Memory,TimeSpan.FromMilliseconds(WallMs));
        // HiGHS has no interrupt in the managed API and overran its time limit in the scale study: wider soft-limit margin.
        var grace=new Dictionary<string,TimeSpan>{["highs"]=TimeSpan.FromMilliseconds(700)};
        PortfolioPolicy Pol(PortfolioMode mode,StopRule rule)=>new(mode,SmallModelVariables:0,Enumerate:true,CleanupGrace:TimeSpan.FromSeconds(15),
            Stop:rule,ProbeFraction:0.3,MemberGrace:grace);
        var configs=new (string Name,AdapterProfile[] Members,PortfolioPolicy Policy)[]{
            ("cpsat",[cpsat],Pol(PortfolioMode.Race,StopRule.ProofOnly)),("highs",[highs],Pol(PortfolioMode.Race,StopRule.ProofOnly)),
            ("race-proof",[cpsat,highs],Pol(PortfolioMode.Race,StopRule.ProofOnly)),("race-concordant",[cpsat,highs],Pol(PortfolioMode.Race,StopRule.Concordant)),
            ("escalate-single",[cpsat,highs],Pol(PortfolioMode.Escalate,StopRule.SingleAttestation)),
            ("escalate-concordant",[cpsat,highs],Pol(PortfolioMode.Escalate,StopRule.Concordant))};
        var rows=new List<Dictionary<string,object?>>();
        var inputs=Path.Combine(outDir,"inputs");Directory.CreateDirectory(inputs);
        foreach(var inst in instances)
        {
            var (c,diag,_)=Generators.Compile(inst);
            if(c is null){rows.Add(new(){["instance"]=inst.Name,["error"]=diag});continue;}
            var m=c.Original.Model;
            var varIds=Enumerable.Range(0,inst.N).Select(j=>m.Symbols.Single(s=>s.Role==SymbolRole.Variable&&(s.Id=="x"+j||s.Id.EndsWith(".x"+j,StringComparison.Ordinal))).Id).ToArray();
            var facts=c.GeneratedInputs[c.FactsDigest].ToArray();
            foreach(var (name,members,policy) in configs)
            {
                var pool=new GovernedPool("study",Threads,Memory,2,boundary);
                var portfolioMembers=members.Select(p=>
                {
                    var input=Path.Combine(inputs,$"{inst.Name}.{p.Manifest.Solver.SolverId}.json");
                    File.WriteAllBytes(input,JsonSerializer.SerializeToUtf8Bytes(new WorkerInput(inst,varIds,m.TenantId,m.ModelId,m.RevisionId,
                        m.Objectives[0].Id,p.Manifest.Solver),Engines.Json));
                    return (IPortfolioMember)new GovernedMember(p,pool,a=>new WorkerProgram(dotnet,[self,"worker",p.Manifest.Solver.SolverId,input,
                        a.Threads.ToString(CultureInfo.InvariantCulture),((int)a.SoftLimit.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),a.RunId],work),
                        Engines.Decode,c.FactsDigest);
                }).ToArray();
                var request=new PortfolioRequest($"{name}-{inst.Name}",c.Normalized,Generators.Catalog,facts,"linux-x64",NumericRequirement.ExactInput,budget,policy);
                var r=await PortfolioRunner.RunAsync(request,portfolioMembers);
                var row=new Dictionary<string,object?>
                {
                    ["instance"]=inst.Name,["family"]=inst.Family,["n"]=inst.N,["nnz"]=inst.Nonzeros,["config"]=name,["status"]=r.Status.ToString(),
                    ["accepted"]=r.Accepted,["objective"]=r.Objective,["strength"]=r.Strength.ToString(),["bound"]=r.BestBound,["boundStrength"]=r.BoundStrength.ToString(),
                    ["gap"]=r.AbsoluteGap,["selected"]=r.Selected?.Solver.SolverId,["wallMs"]=r.WallMs,["chargedCoreMs"]=r.Ledger.ChargedCoreMs,
                    ["reservedCoreMs"]=r.Ledger.ReservedCoreMs,["overspendCoreMs"]=r.Ledger.OverspendCoreMs,["peakThreads"]=r.Ledger.PeakConcurrentThreads,
                    ["alerts"]=r.Alerts.Select(a=>a.Code+":"+a.Solver.SolverId).ToArray(),["stopReason"]=r.StopReason.ToString(),["escalated"]=r.Escalated,
                    ["concordant"]=r.ConcordantAttestations,
                    ["members"]=r.Members.Select(x=>new{solver=x.Solver.SolverId,phase=x.Phase.ToString(),code=x.Code,threads=x.Assignment?.Threads,
                        termination=x.Termination?.ToString(),reported=x.Assessment?.ReportedStatus.ToString(),verified=x.VerifiedObjective,
                        rejected=x.Assessment?.Rejected,diagnostic=x.Assessment?.Diagnostic??x.Diagnostic,cpuMs=x.CpuMs,cpuSource=x.CpuSource?.ToString(),
                        elapsedMs=x.ElapsedMs,peakBytes=x.PeakBytes,bound=x.Assessment?.Bound,stage=x.Stage,softMs=(long?)x.Assignment?.SoftLimit.TotalMilliseconds,hardMs=(long?)x.Assignment?.HardDeadline.TotalMilliseconds}).ToArray()
                };
                rows.Add(row);
                Console.WriteLine($"{inst.Name,-22} {name,-9} {r.Status,-16} obj={r.Objective,-8} bound={r.BestBound,-8} sel={r.Selected?.Solver.SolverId,-5} wall={r.WallMs}ms cpu={r.Ledger.ChargedCoreMs} alerts={r.Alerts.Length}");
            }
        }
        return rows;
    }
}
