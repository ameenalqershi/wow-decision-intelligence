using System.Diagnostics;
using System.Text.Json;
internal static class UnifiedStudy
{
    internal static WideModel Sparse(int k,int seed,bool rectangular)
    {
        var rng=new Random(seed);int right=rectangular?k+3:k;
        var ls=Enumerable.Range(0,k).Select(_=>new List<int>()).ToArray();
        var rs=Enumerable.Range(0,right).Select(_=>new List<int>()).ToArray();
        var costs=new List<long>();
        for(int i=0;i<k;i++)for(int j=0;j<right;j++)if(j==i||j==(i+1)%right||j>=k&&i==j-k||rng.Next(5)==0){
            int v=costs.Count;ls[i].Add(v);rs[j].Add(v);costs.Add(rng.Next(-80,81));
        }
        var rows=ls.Select(r=>new WideRow(r.ToArray(),1,1)).Concat(rs.Select(r=>new WideRow(r.ToArray(),rectangular?0:1,1))).OrderBy(_=>rng.Next()).ToArray();
        return new(costs.Count,[],[],rows,costs.ToArray());
    }
    internal static IEnumerable<WideModel> History(string family,int seed,int revisions=5)
    {
        var rng=new Random(seed+8000);WideModel m;
        if(family=="assignment")m=AssignmentStudy.Model(8,seed,"signed",0);
        else if(family=="sparse"||family=="rectangular")m=Sparse(9,seed,family=="rectangular");
        else if(family=="blocks"){
            int n=128;var edges=new List<(int,int)>();
            for(int i=0;i<n;i+=8)for(int j=1;j<8;j++)edges.Add((i,i+j));
            m=new(n,edges.ToArray(),[],[],Enumerable.Range(0,n).Select(_=>(long)rng.Next(-80,81)).ToArray());
        }else if(family=="mixed"){
            var a=AssignmentStudy.Model(6,seed,"signed",0);var b=Sparse(6,seed+1,true);
            var rows=a.Rows.Concat(b.Rows.Select(r=>new WideRow(r.Scope.Select(i=>i+a.N).ToArray(),r.L,r.U))).ToList();
            int n=a.N+b.N+32;
            for(int i=a.N+b.N;i<n;i+=8)rows.Add(new(Enumerable.Range(i,8).ToArray(),3,4));
            m=new(n,[],[],rows.ToArray(),a.Cost.Concat(b.Cost).Concat(Enumerable.Range(0,32).Select(_=>(long)rng.Next(-80,81))).ToArray());
        }else{
            int n=128;var rows=new List<WideRow>();
            for(int i=0;i<n;i+=family=="quotas"?8:4){int[] s=Enumerable.Range(i,Math.Min(8,n-i)).ToArray();rows.Add(new(s,0,Math.Min(3,s.Length)));}
            m=new(n,[],[],rows.ToArray(),Enumerable.Range(0,n).Select(_=>(long)rng.Next(-80,81)).ToArray());
        }
        for(int rev=0;rev<revisions;rev++){
            if(rev>0){
                m=ProofReuse.Snapshot(m);
                // Small objective perturbation for one block, unchanged costs elsewhere.
                if(family=="blocks"||family=="mixed"||family=="quotas")m.Cost[rev%m.N]+=rev;
                else for(int i=0;i<m.N;i++)if(i%5==rev%5)m.Cost[i]+=rng.Next(-3,4);
                if(family=="blocks"&&rev==2)m=m with{Edges=m.Edges.Append((0,9)).ToArray()};
                if(family=="blocks"&&rev==3)m=m with{Edges=m.Edges.SkipLast(1).ToArray(),Fixes=[(0,0)]};
                if(family=="blocks"&&rev==4)m=m with{Fixes=[],Rows=[new([0,1],1,1)]};
                if(family=="quotas"&&rev==3)m.Rows[0]=m.Rows[0] with{U=2};
                if(family=="mixed"&&rev==3)m=m with{Rows=m.Rows.Append(new WideRow([0,m.N-1],0,1)).ToArray()};
                if(family=="mixed"&&rev==4)m=m with{Rows=m.Rows.SkipLast(1).ToArray()};
            }
            yield return m;
        }
    }
    internal static UnifiedOutcome Wrap(WideModel m,ProofOutcome o)=>new(o.Route,o.Result,[new(Enumerable.Range(0,m.N).ToArray(),o,null,null,o.Result)],0,0,0,o.OptimalityProved);
    internal static void Run(string split)
    {
        bool disk=split=="disk",selector=split=="selector";if(!disk&&!selector&&split!="holdout")throw new ArgumentException("split");
        string[] arms=selector?["original","G16","unified-mono","unified-reuse","unified-gate","unified-gate-flow"]:["original","G13","G14","G16","unified-mono","unified-cold","unified-reuse"];
        using FileStream? output=disk?new("unified-evidence.jsonl",FileMode.CreateNew):null;
        foreach(string family in new[]{"blocks","quotas","assignment","sparse","rectangular","mixed","overlap"})
        for(int h=0;h<(disk?1:12);h++){
            int seed=(disk?2720101:selector?2810101:2690101)+h;var g13=new IncrementalQuotient(true);var g14=new IncrementalQuotient(true,true,true);var g16=new ProofReuse(true);
            var mono=new DependencyDispatcher(true,false);var cold=new DependencyDispatcher(false,true);var reuse=new DependencyDispatcher(true,true);var gate=new DependencyDispatcher(true,true,true,true);var gateFlow=new DependencyDispatcher(true,true,true,true,true);
            int rev=0;
            foreach(var input in History(family,seed)){
                var results=new Dictionary<string,WideResult>();
                for(int a=0;a<arms.Length;a++){
                    string arm=arms[(h+rev+a)%arms.Length];long start=Stopwatch.GetTimestamp(),alloc=GC.GetAllocatedBytesForCurrentThread();var trace=new PhaseTrace();
                    var m=IncrementalQuotient.Admit(ProofReuse.Snapshot(input));string d=CertifiedQuotient.Digest(m);trace.Mark("admit-digest");
                    UnifiedOutcome o;
                    if(arm.StartsWith("unified",StringComparison.Ordinal)){
                        var s=arm switch{"unified-mono"=>mono,"unified-cold"=>cold,"unified-gate"=>gate,"unified-gate-flow"=>gateFlow,_=>reuse};
                        o=s.Decide(m,trace);
                    }else if(arm=="G16"){
                        var f=CertifiedStudy.Features(m);
                        if(f.Disjoint||f.ReductionProxy>=.125)o=Wrap(m,g16.Solve(m,d,trace));
                        else o=new("original",CertifiedStudy.Original(m),[],0,0,0,false);
                    }else{
                        var r=arm=="original"?CertifiedStudy.Original(m):(arm=="G13"?g13:g14).Solve(m,x=>CertifiedStudy.Original(x),trace);
                        o=new(arm,r,[],0,0,0,false);
                    }
                    trace.Mark("solve-rest");
                    if(o.Result.Bits!=null&&CertifiedStudy.Verify(m,o.Result.Bits)!=o.Result.Value)throw new Exception("source witness");
                    trace.Mark("source-check");
                    var evidence=new UnifiedEvidence(d,m,o);byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(evidence,CertifiedQuotient.Json);trace.Mark("encode");
                    if(output!=null){output.Write(bytes);output.WriteByte(10);output.Flush(true);trace.Mark("flush");}
                    long ticks=Stopwatch.GetTimestamp()-start;
                    // Complete independent replay outside the timed boundary on EVERY outcome.
                    UnifiedVerifier.Verify(evidence);
                    results.Add(arm,o.Result);
                    Console.WriteLine(JsonSerializer.Serialize(new{split,family,seed,rev,n=m.N,arm,digest=d,route=o.Route,status=o.Result.Status,value=o.Result.Value,formal=o.Formal,blocks=o.Blocks.Length,reused=o.Reused,transported=o.Transported,rebuilt=o.Rebuilt,witness=o.Result.Bits!=null,ticks,frequency=Stopwatch.Frequency,bytes=GC.GetAllocatedBytesForCurrentThread()-alloc,evidenceBytes=bytes.Length,phases=trace.Ticks}));
                }
                foreach(var x in results.Values)foreach(var y in results.Values){
                    if(x.Status=="Optimal"&&y.Status=="Optimal"&&x.Value!=y.Value||x.Status=="Optimal"&&y.Value<x.Value||x.Status=="Infeasible"&&y.Bits!=null)throw new Exception("reference disagreement");
                }
                rev++;
            }
        }
    }
    internal static void Readback(string file)
    {
        int count=0,formal=0;foreach(string l in File.ReadLines(file)){
            var e=JsonSerializer.Deserialize<UnifiedEvidence>(l,CertifiedQuotient.Json)!;UnifiedVerifier.Verify(e);count++;if(e.Outcome.Formal)formal++;
        }
        Console.WriteLine(JsonSerializer.Serialize(new{count,formal,valid=true}));
    }
    internal static void Audit()
    {
        int exhaustive=0,highs=0,tamper=0,transport=0,abstain=0;
        void Assert(bool b,string msg){if(!b)throw new Exception(msg);}
        long? Exact(WideModel m){
            long? best=null;for(int mask=0;mask<(1<<m.N);mask++){
                int X(int i)=>(mask>>i)&1;
                if(m.Edges.Any(e=>X(e.A)+X(e.B)!=1)||m.Fixes.Any(f=>X(f.Bit)!=f.Value)||m.Rows.Any(r=>{int s=r.Scope.Sum(X);return s<r.L||s>r.U;}))continue;
                long val=Enumerable.Range(0,m.N).Sum(i=>m.Cost[i]*X(i));if(best==null||val<best)best=val;
            }return best;
        }
        for(int h=0;h<48;h++){
            var rng=new Random(2750101+h);var d=new DependencyDispatcher(true,true);
            // n16 permits actual component dispatch, 4 blocks; 65536 exhaustive masks each.
            var m=new WideModel(16,[(0,1),(2,3),(0,2),(4,5),(4,6),(4,7),(8,9),(8,10),(8,11),(12,13),(12,14),(12,15)],[],[],Enumerable.Range(0,16).Select(_=>(long)rng.Next(-10,11)).ToArray());
            for(int rev=0;rev<5;rev++){
                m=ProofReuse.Snapshot(m);
                if(rev==1)m.Cost[0]++;
                if(rev==2)m=m with{Edges=m.Edges.Append((0,4)).ToArray()};
                if(rev==3)m=m with{Edges=m.Edges.SkipLast(1).Where(e=>e!=(0,2)).ToArray()};
                if(rev==4)m=m with{Fixes=[(0,0),(0,1)]};
                var o=d.Decide(m,new());var e=new UnifiedEvidence(CertifiedQuotient.Digest(m),m,o);
                UnifiedVerifier.Verify(e);long? expected=Exact(m);
                Assert(o.Result.Value==expected&&o.Result.Status==(expected.HasValue?"Optimal":"Infeasible"),"finite");
                if(rev==1)Assert(o.Reused==3,"unaffected blocks");
                if(rev==2)Assert(o.Reused==2,"merged blocks invalidated");
                if(rev==3)Assert(o.Reused==2,"split blocks invalidated");
                Console.WriteLine(JsonSerializer.Serialize(new{kind="finite",h,rev,e,expected},CertifiedQuotient.Json));exhaustive++;transport+=o.Transported;
                if(rev==0){
                    // Exported proof modification must not mutate the private session.
                    var b=o.Blocks[0];b.Members[0]=b.Members[1];bool rejected=false;
                    try{UnifiedVerifier.Verify(e);}catch{rejected=true;}Assert(rejected,"export tamper");
                    Console.WriteLine(JsonSerializer.Serialize(new{kind="tamper",test="duplicate-member",rejected}));tamper++;
                }
            }
        }
        foreach(bool rectangular in new[]{false,true})for(int h=0;h<12;h++){
            var m=Sparse(4,2780101+h,rectangular);var d=new DependencyDispatcher(true,true);
            for(int rev=0;rev<2;rev++){
                m=ProofReuse.Snapshot(m);if(rev==1)m.Cost[0]++;
                var o=d.Decide(m,new());var e=new UnifiedEvidence(CertifiedQuotient.Digest(m),m,o);UnifiedVerifier.Verify(e);
                var native=IncrementalAudit.Highs(m);Assert(o.Formal&&native.Status=="Optimal"&&native.Value==o.Result.Value&&CertifiedStudy.Verify(m,native.Bits!)==native.Value,"flow highs");
                Console.WriteLine(JsonSerializer.Serialize(new{kind="highs",rectangular,h,rev,e,native},CertifiedQuotient.Json));highs++;
                var b=o.Blocks[0];if(b.Flow!=null){
                    var bad=b.Flow with{Digest="forged"};Assert(!CertifiedSparseFlow.Verify(m,e.Digest,bad,b.Result),"stale flow");
                    var from=b.Flow.From.ToArray();from[0]=b.Flow.To[0];Assert(!CertifiedSparseFlow.Verify(m,e.Digest,b.Flow with{From=from},b.Result),"flow map");
                    Console.WriteLine(JsonSerializer.Serialize(new{kind="tamper",test="flow-digest/map",rejected=true,count=2}));tamper+=2;
                }
                var side=m with{Rows=m.Rows.Append(new WideRow([0],0,1)).ToArray()};
                Assert(CertifiedSparseFlow.Solve(side,CertifiedQuotient.Digest(side))==null,"side constraint not abstained");abstain++;
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new{kind="summary",exhaustive,highs,tamper,transport,abstain}));
    }
}
