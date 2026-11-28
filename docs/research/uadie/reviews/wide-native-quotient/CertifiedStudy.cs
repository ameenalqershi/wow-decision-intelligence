using Google.OrTools.Sat;
using System.Diagnostics;
using System.Text.Json;
internal static class CertifiedStudy
{
    internal static WideResult Original(WideModel m,int[]? hint=null)
    {
        var cp=new CpModel();var x=Enumerable.Range(0,m.N).Select(i=>cp.NewBoolVar("x"+i)).ToArray();
        foreach(var(a,b) in m.Edges)cp.Add(x[a]+x[b]==1);
        foreach(var(i,v) in m.Fixes)cp.Add(x[i]==v);
        foreach(var r in m.Rows)cp.AddLinearConstraint(LinearExpr.Sum(r.Scope.Select(i=>x[i])),r.L,r.U);
        if(hint!=null&&hint.Length==m.N)for(int i=0;i<m.N;i++)cp.AddHint(x[i],hint[i]);
        cp.Minimize(LinearExpr.WeightedSum(x,m.Cost));
        var solver=new CpSolver{StringParameters="num_search_workers:1 random_seed:1320101 max_time_in_seconds:0.5"};
        var status=solver.Solve(cp);
        if(status is not(CpSolverStatus.Optimal or CpSolverStatus.Feasible))return new(status.ToString(),null,null,m.N,m.Edges.Length+m.Fixes.Length+m.Rows.Length);
        var bits=x.Select(v=>checked((int)solver.Value(v))).ToArray();long value=0;for(int i=0;i<m.N;i++)value+=bits[i]*m.Cost[i];
        if(!double.IsFinite(solver.ObjectiveValue)||Math.Abs(solver.ObjectiveValue-value)>1e-6||status==CpSolverStatus.Optimal&&(!double.IsFinite(solver.BestObjectiveBound)||Math.Abs(solver.BestObjectiveBound-value)>1e-6))throw new Exception("native report");
        return new(status.ToString(),value,bits,m.N,m.Edges.Length+m.Fixes.Length+m.Rows.Length);
    }
    internal static long Verify(WideModel m,int[] bits)
    {
        if(bits.Length!=m.N||bits.Any(v=>v is not(0 or 1)))throw new Exception("domain");
        foreach(var(a,b) in m.Edges)if(bits[a]+bits[b]!=1)throw new Exception("edge");
        foreach(var(i,v) in m.Fixes)if(bits[i]!=v)throw new Exception("fix");
        foreach(var r in m.Rows){int k=0;foreach(int i in r.Scope)k+=bits[i];if(k<r.L||k>r.U)throw new Exception("row");}
        return m.Cost.Select((c,i)=>c*bits[i]).Sum();
    }
    // Cheap structural work proxy, no solver launch to choose a route.
    internal static (bool Disjoint,double ReductionProxy) Features(WideModel m)
    {
        var seen=new bool[m.N];bool disjoint=true;
        foreach(var r in m.Rows)foreach(int i in r.Scope){if(seen[i])disjoint=false;seen[i]=true;}
        return(disjoint,Math.Min(1,(m.Edges.Length+(double)m.Fixes.Length)/m.N));
    }
    internal static void Run(string split,double threshold)
    {
        bool dev=split=="dev";bool disk=split=="disk";
        if(!dev&&!disk&&split!="holdout")throw new ArgumentException("split");
        string[] arms=dev?["original","warm","cert-native","cert-direct"]:["original","warm","G13","G14","cert-native","cert-direct","G15"];
        FileStream? output=disk?new FileStream("g15-evidence.jsonl",FileMode.CreateNew):null;
        try{
        foreach(int n in disk?new[]{256}:new[]{256,2048})
        foreach(string family in new[]{"disjoint","signed-pairs","overlap","weak-overlap","strong-overlap"})
        for(int h=0;h<(dev?3:disk?1:12);h++)
        {
            int seed=(dev?2250101:disk?2310101:2280101)+h;var rng=new Random(seed+n+family.Length*1009);
            var planted=Enumerable.Range(0,n).Select(_=>rng.Next(2)).ToArray();
            var edges=new List<(int,int)>();var fixes=new List<(int,int)>();var rows=new List<WideRow>();
            int covered=family=="signed-pairs"||family=="strong-overlap"?n:family=="weak-overlap"?n/4:0;
            int width=family=="weak-overlap"?2:4;
            for(int i=0;i<covered;i+=width)for(int j=i+1;j<Math.Min(i+width,covered);j++){edges.Add((i,j));planted[j]=1-planted[i];}
            if(family=="signed-pairs")
            {
                // One representative from each component: disjoint signed unit quota rows.
                for(int i=0;i<n;i+=32){int[] scope=Enumerable.Range(0,Math.Min(8,(n-i+3)/4)).Select(j=>i+4*j+rng.Next(Math.Min(4,n-i-4*j))).ToArray();int k=scope.Sum(j=>planted[j]);rows.Add(new(scope,k,k));}
            }
            else
            {
                int step=family=="disjoint"?8:6;
                for(int i=0;i<n;i+=step){int[] scope=Enumerable.Range(i,Math.Min(8,n-i)).ToArray();int k=scope.Sum(j=>planted[j]);rows.Add(new(scope,Math.Max(0,k-1),Math.Min(scope.Length,k+1)));}
            }
            var g13=new IncrementalQuotient(true);var g14=new IncrementalQuotient(true,true,true);
            var hints=new Dictionary<string,int[]>();
            for(int rev=0;rev<3;rev++)
            {
                if(rev==2){fixes.Add((0,planted[0]));if(edges.Count>0)edges.RemoveAt(0);}
                var m=new WideModel(n,edges.ToArray(),fixes.ToArray(),rows.ToArray(),Enumerable.Range(0,n).Select(_=>(long)rng.Next(-50,51)).ToArray());
                _=Verify(m,planted);
                var results=new Dictionary<string,WideResult>();
                for(int a=0;a<arms.Length;a++)
                {
                    string arm=arms[(h+rev+a)%arms.Length];long start=Stopwatch.GetTimestamp(),alloc=GC.GetAllocatedBytesForCurrentThread();
                    var trace=new PhaseTrace();if(arm is not("G13" or "G14"))IncrementalQuotient.Admit(m);
                    trace.Mark("admit");string digest=CertifiedQuotient.Digest(m);trace.Mark("digest");
                    string route=arm;TransformCertificate? cert=null;int[]? reduced=null;bool certVerified=false,directVerified=false;
                    var f=Features(m);trace.Mark("features");
                    bool useCert=arm is "cert-native" or "cert-direct" || arm=="G15"&&(f.Disjoint||f.ReductionProxy>=threshold);
                    WideResult r;
                    if(useCert)
                    {
                        cert=CertifiedQuotient.Produce(m,digest);trace.Mark("transform");
                        if(cert==null){route="fallback-contradiction";r=Original(m);}
                        else
                        {
                            certVerified=TransformationVerifier.Verify(m,digest,cert);if(!certVerified)throw new Exception("certificate rejected");trace.Mark("certificate");
                            r=CertifiedQuotient.SolveReduced(cert,arm!="cert-native",out route,out reduced);trace.Mark("solve");
                            if(route=="direct"){directVerified=TransformationVerifier.VerifyDirect(cert,reduced!);if(!directVerified)throw new Exception("direct certificate");}
                            trace.Mark("direct-proof");
                        }
                    }
                    else if(arm=="G13"||arm=="G14")
                        r=(arm=="G13"?g13:g14).Solve(m,x=>Original(x),trace);
                    else
                    {
                        route=arm=="original"?"original":"warm";
                        hints.TryGetValue(arm,out var hint);
                        r=Original(m,route=="warm"?hint:null);
                    }
                    trace.Mark("solve-rest");
                    if(r.Status is "Infeasible" or "ModelInvalid" or "Invalid")throw new Exception("planted feasible rejected");
                    if(r.Bits!=null&&Verify(m,r.Bits)!=r.Value)throw new Exception("source witness");
                    if(r.Bits!=null)hints[arm]=r.Bits;trace.Mark("verify");
                    byte[] evidence=JsonSerializer.SerializeToUtf8Bytes(new{digest,n,family,seed,rev,arm,r.Status,r.Value,r.Bits,cert,reduced,certVerified,directVerified},CertifiedQuotient.Json);
                    trace.Mark("encode");if(output!=null){output.Write(evidence);output.WriteByte(10);output.Flush(true);trace.Mark("flush");}
                    long ticks=Stopwatch.GetTimestamp()-start;
                    results.Add(arm,r);
                    Console.WriteLine(JsonSerializer.Serialize(new{split,n,family,seed,rev,arm,digest,route,status=r.Status,value=r.Value,variables=r.Variables,certVerified,directVerified,verifiedWitness=r.Bits!=null,features=f,ticks,frequency=Stopwatch.Frequency,bytes=GC.GetAllocatedBytesForCurrentThread()-alloc,evidenceBytes=evidence.Length,phases=trace.Ticks},CertifiedQuotient.Json));
                }
                foreach(var a in results.Values)foreach(var b in results.Values){if(a.Status=="Optimal"&&b.Status=="Optimal"&&a.Value!=b.Value)throw new Exception("optimal disagreement");if(a.Status=="Optimal"&&b.Value<a.Value)throw new Exception("bound disagreement");}
            }
        }
        }finally{output?.Dispose();}
    }
}
