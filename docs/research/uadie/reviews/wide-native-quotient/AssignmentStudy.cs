using System.Diagnostics;
using System.Text.Json;
internal static class AssignmentStudy
{
    internal static WideModel Model(int k,int seed,string family,int revision)
    {
        var rng=new Random(seed+k*101+revision*10007);var permutation=Enumerable.Range(0,k*k).OrderBy(_=>rng.Next()).ToArray();
        var rows=new List<WideRow>();
        for(int i=0;i<k;i++)rows.Add(new(permutation.Skip(i*k).Take(k).OrderBy(_=>rng.Next()).ToArray(),1,1));
        for(int j=0;j<k;j++)rows.Add(new(Enumerable.Range(0,k).Select(i=>permutation[i*k+j]).OrderBy(_=>rng.Next()).ToArray(),1,1));
        var cost=Enumerable.Range(0,k*k).Select(_=>(long)rng.Next(family=="ties"?-2:-100,family=="ties"?3:101)).ToArray();
        return new(k*k,[],[],rows.OrderBy(_=>rng.Next()).ToArray(),cost);
    }
    internal static void Run()
    {
        string[] arms=["CP-SAT","native-assignment","native-certified","assignment-proof"];
        foreach(int k in new[]{8,24,48})
        foreach(string family in new[]{"signed","ties"})
        for(int h=0;h<12;h++)
        for(int rev=0;rev<2;rev++)
        {
            int seed=2580101+h;var input=Model(k,seed,family,rev);var results=new Dictionary<string,WideResult>();
            for(int a=0;a<arms.Length;a++){
                string arm=arms[(h+rev+a)%arms.Length];long start=Stopwatch.GetTimestamp(),alloc=GC.GetAllocatedBytesForCurrentThread();var trace=new PhaseTrace();
                var m=IncrementalQuotient.Admit(ProofReuse.Snapshot(input));trace.Mark("admit");string digest=CertifiedQuotient.Digest(m);trace.Mark("digest");
                WideResult r;AssignmentProof? proof=null;
                if(arm=="CP-SAT")r=CertifiedStudy.Original(m);
                else{
                    var shape=CertifiedAssignment.Discover(m)??throw new Exception("assignment admission");trace.Mark("discover");
                    int[] match;
                    if(arm=="assignment-proof"){proof=CertifiedAssignment.Hungarian(m,shape,digest);match=proof.Match;}
                    else match=CertifiedAssignment.Native(m,shape);
                    trace.Mark("solve");
                    if(arm=="native-certified")proof=CertifiedAssignment.CertifyMatching(m,shape,match,digest);
                    trace.Mark("dual");
                    r=CertifiedAssignment.Reconstruct(m,shape,match);
                    if(proof!=null&&!AssignmentVerifier.Verify(m,digest,proof,r))throw new Exception("proof rejected");trace.Mark("proof");
                }
                trace.Mark("solve-rest");
                if(r.Bits!=null&&CertifiedStudy.Verify(m,r.Bits)!=r.Value)throw new Exception("source witness");
                trace.Mark("verify");
                byte[] evidence=JsonSerializer.SerializeToUtf8Bytes(new{digest,m,proof,result=r},CertifiedQuotient.Json);trace.Mark("encode");
                long ticks=Stopwatch.GetTimestamp()-start;results.Add(arm,r);
                Console.WriteLine(JsonSerializer.Serialize(new{k,n=m.N,family,seed,rev,arm,digest,status=r.Status,value=r.Value,proof=proof!=null,witness=r.Bits!=null,ticks,frequency=Stopwatch.Frequency,bytes=GC.GetAllocatedBytesForCurrentThread()-alloc,evidenceBytes=evidence.Length,phases=trace.Ticks}));
            }
            long value=results["assignment-proof"].Value!.Value;
            foreach(var r in results.Values){
                if(r.Status=="Infeasible"||r.Status=="Optimal"&&r.Value!=value||r.Value<value)throw new Exception("optimum mismatch");
            }
        }
    }
    internal static void Audit()
    {
        int exact=0,highs=0,tamper=0,abstain=0;
        void Assert(bool b,string name){if(!b)throw new Exception(name);}
        long Enumerate(WideModel m)
        {
            // Independent original row enumeration using a row and source assignments,
            // no Hungarian graph or recovered matrix.
            int k=(int)Math.Sqrt(m.N);var counts=new int[m.Rows.Length];var incident=Enumerable.Range(0,m.N).Select(i=>Enumerable.Range(0,m.Rows.Length).Where(r=>m.Rows[r].Scope.Contains(i)).ToArray()).ToArray();
            long best=long.MaxValue;
            void Visit(int chosen,long cost){
                int r=Array.FindIndex(counts,x=>x==0);
                if(r<0){if(chosen==k)best=Math.Min(best,cost);return;}
                foreach(int x in m.Rows[r].Scope){
                    if(incident[x].Any(j=>counts[j]!=0))continue;
                    foreach(int j in incident[x])counts[j]++;
                    Visit(chosen+1,cost+m.Cost[x]);
                    foreach(int j in incident[x])counts[j]--;
                }
            }Visit(0,0);return best;
        }
        for(int k=3;k<=7;k++)for(int seed=0;seed<12;seed++){
            var m=Model(k,2610101+seed,seed%2==0?"signed":"ties",0);string d=CertifiedQuotient.Digest(m);
            var s=CertifiedAssignment.Discover(m)??throw new Exception("shape");var p=CertifiedAssignment.Hungarian(m,s,d);var r=CertifiedAssignment.Reconstruct(m,s,p.Match);
            long expected=Enumerate(m);Assert(AssignmentVerifier.Verify(m,d,p,r)&&r.Value==expected,"enumeration");
            var native=CertifiedAssignment.Native(m,s);var np=CertifiedAssignment.CertifyMatching(m,s,native,d);var nr=CertifiedAssignment.Reconstruct(m,s,native);
            Assert(AssignmentVerifier.Verify(m,d,np,nr)&&nr.Value==expected,"native certificate");
            Console.WriteLine(JsonSerializer.Serialize(new{kind="finite",k,seed,model=m,digest=d,proof=p,result=r,nativeProof=np,nativeResult=nr,expected},CertifiedQuotient.Json));exact++;
            void Reject(string test,AssignmentProof bad,WideResult result){
                bool ok=!AssignmentVerifier.Verify(m,d,bad,result);
                Console.WriteLine(JsonSerializer.Serialize(new{kind="tamper",test,rejected=ok}));Assert(ok,test);tamper++;
            }
            Reject("digest",p with{Digest="stale"},r);
            var u=p.U.ToArray();u[0]++;Reject("dual",p with{U=u},r);
            var map=s.Matrix.ToArray();map[0]=map[1];Reject("mapping",p with{Shape=s with{Matrix=map}},r);
            var bits=r.Bits!.ToArray();bits[0]^=1;Reject("source-witness",p,r with{Bits=bits});
            Reject("value",p,r with{Value=r.Value+1});
            var wrong=p.Match.ToArray();(wrong[0],wrong[1])=(wrong[1],wrong[0]);
            Reject("reconstruction",p with{Match=wrong},r);
            foreach(var b in new[]{m with{Rows=m.Rows.Append(new WideRow([0,1],0,1)).ToArray()},m with{Fixes=[(0,0)]},m with{Edges=[(0,1)]},m with{Rows=m.Rows.Skip(1).ToArray()}}){
                Assert(CertifiedAssignment.Discover(b)==null,"unsupported accepted");
                var fallback=CertifiedStudy.Original(b);
                if(fallback.Bits!=null)Assert(CertifiedStudy.Verify(b,fallback.Bits)==fallback.Value,"fallback witness");
                Console.WriteLine(JsonSerializer.Serialize(new{kind="abstain",k,seed,status=fallback.Status,value=fallback.Value}));abstain++;
            }
        }
        foreach(int k in new[]{8,24,48})for(int seed=0;seed<12;seed++){
            var m=Model(k,2640101+seed,"signed",0);string d=CertifiedQuotient.Digest(m);var s=CertifiedAssignment.Discover(m)!;
            var p=CertifiedAssignment.Hungarian(m,s,d);var r=CertifiedAssignment.Reconstruct(m,s,p.Match);var h=IncrementalAudit.Highs(m);
            Assert(AssignmentVerifier.Verify(m,d,p,r)&&h.Status=="Optimal"&&h.Value==r.Value&&CertifiedStudy.Verify(m,h.Bits!)==h.Value,"HiGHS");
            Console.WriteLine(JsonSerializer.Serialize(new{kind="highs",k,seed,model=m,digest=d,proof=p,result=r,highs=h},CertifiedQuotient.Json));highs++;
        }
        Console.WriteLine(JsonSerializer.Serialize(new{kind="summary",exact,highs,tamper,abstain}));
    }
}
