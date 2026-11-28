using System.Diagnostics;
using System.Text.Json;
internal static class ProofReuseStudy
{
    internal static IEnumerable<WideModel> History(int n,int seed,string family,int revisions=4)
    {
        var rng=new Random(seed+n+family.Length*1009);
        var planted=Enumerable.Range(0,n).Select(_=>rng.Next(2)).ToArray();
        var edges=new List<(int,int)>();var fixes=new List<(int,int)>();var rows=new List<WideRow>();
        int covered=family is "signed" or "edited"?n:family=="weak"?n/8:0;
        int width=family=="weak"?2:4;
        for(int i=0;i<covered;i+=width)for(int j=i+1;j<Math.Min(i+width,covered);j++){edges.Add((i,j));planted[j]=1-planted[i];}
        if(family=="signed"){
            for(int i=0;i<n;i+=32){var scope=Enumerable.Range(0,Math.Min(8,(n-i)/4)).Select(j=>i+4*j+rng.Next(4)).ToArray();int k=scope.Sum(j=>planted[j]);rows.Add(new(scope,k,k));}
        }else{
            int step=family=="disjoint"?8:6;
            for(int i=0;i<n;i+=step){var scope=Enumerable.Range(i,Math.Min(8,n-i)).ToArray();int k=scope.Sum(j=>planted[j]);rows.Add(new(scope,Math.Max(0,k-1),Math.Min(scope.Length,k+1)));}
        }
        for(int rev=0;rev<revisions;rev++){
            if(rev==2&&family=="edited"){edges.RemoveAt(0);fixes.Add((0,planted[0]));}
            if(rev==3&&family=="edited"){edges.Add((0,1));fixes.Clear();rows[0]=new(rows[0].Scope.Reverse().ToArray(),rows[0].L,rows[0].U);}
            var m=new WideModel(n,edges.ToArray(),fixes.ToArray(),rows.ToArray(),Enumerable.Range(0,n).Select(_=>(long)rng.Next(-80,81)).ToArray());
            _=CertifiedStudy.Verify(m,planted);yield return m;
        }
    }
    internal static void Run(string split)
    {
        bool disk=split=="disk";if(!disk&&split!="holdout")throw new ArgumentException("split");
        string[] arms=["original","hint","G13","G14","cert-native","cert-direct","G16-cold","G16-reuse","G16"];
        using FileStream? stream=disk?new("g16-evidence.jsonl",FileMode.CreateNew):null;
        foreach(int n in disk?new[]{256}:new[]{128,1024})
        foreach(string family in new[]{"disjoint","signed","overlap","weak","edited"})
        for(int h=0;h<(disk?1:12);h++)
        {
            int seed=(disk?2490101:2460101)+h;
            var g13=new IncrementalQuotient(true);var g14=new IncrementalQuotient(true,true,true);
            var cold=new ProofReuse(false);var cached=new ProofReuse(true);var adaptive=new ProofReuse(true);
            int[]? hint=null;int rev=0;
            foreach(var input in History(n,seed,family))
            {
                var results=new Dictionary<string,WideResult>();
                for(int a=0;a<arms.Length;a++){
                    string arm=arms[(h+rev+a)%arms.Length];
                    long start=Stopwatch.GetTimestamp(),alloc=GC.GetAllocatedBytesForCurrentThread();var trace=new PhaseTrace();
                    // Common private snapshot; caller must not mutate input during copying.
                    var m=ProofReuse.Snapshot(input);
                    if(arm is not("G13" or "G14"))IncrementalQuotient.Admit(m);
                    trace.Mark("admit");string digest=CertifiedQuotient.Digest(m);trace.Mark("digest");
                    ProofOutcome o;bool hit=false;
                    if(arm is "original" or "hint"){
                        var r=CertifiedStudy.Original(m,arm=="hint"?hint:null);
                        if(arm=="hint"&&r.Bits!=null)hint=r.Bits.ToArray();
                        o=new(r,null,null,arm,false,false);
                    }else if(arm is "G13" or "G14"){
                        var r=(arm=="G13"?g13:g14).Solve(m,x=>CertifiedStudy.Original(x),trace);
                        o=new(r,null,null,arm,false,false);
                    }else if(arm is "cert-native" or "cert-direct"){
                        var c=CertifiedQuotient.Produce(m,digest);trace.Mark("transform");
                        if(c==null)o=new(CertifiedStudy.Original(m),null,null,"original-fallback",false,false);
                        else{
                            if(!TransformationVerifier.Verify(m,digest,c))throw new Exception("transform");trace.Mark("proof");
                            var r=CertifiedQuotient.SolveReduced(c,arm=="cert-direct",out string route,out var y);trace.Mark("solve");
                            bool proof=route=="direct";if(proof&&!TransformationVerifier.VerifyDirect(c,y!))throw new Exception("direct");
                            o=new(r,c,y,route,true,proof);
                        }
                    }else{
                        var session=arm=="G16-cold"?cold:arm=="G16-reuse"?cached:adaptive;
                        // G16 policy has no hints. Source overlap without meaningful edge/fix count
                        // bypasses transform. Other arms do not pay this feature cost.
                        bool bypass=false;
                        if(arm=="G16"){
                            var f=CertifiedStudy.Features(m);bypass=!f.Disjoint&&f.ReductionProxy<.125;
                            trace.Mark("features");
                        }
                        if(bypass)o=new(CertifiedStudy.Original(m),null,null,"original-bypass",false,false);
                        else{o=session.Solve(m,digest,trace);hit=session.Hit;}
                    }
                    trace.Mark("solve-rest");var result=o.Result;
                    if(result.Status is "Infeasible" or "ModelInvalid" or "Invalid")throw new Exception("planted model rejected");
                    if(result.Bits!=null&&CertifiedStudy.Verify(m,result.Bits)!=result.Value)throw new Exception("source witness");
                    trace.Mark("verify");
                    var e=new ProofEvidence(digest,n,family,seed,rev,arm,m,o);
                    byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(e,CertifiedQuotient.Json);trace.Mark("encode");
                    if(stream!=null){stream.Write(bytes);stream.WriteByte(10);stream.Flush(true);trace.Mark("flush");}
                    long ticks=Stopwatch.GetTimestamp()-start;
                    results.Add(arm,result);
                    Console.WriteLine(JsonSerializer.Serialize(new{split,n,family,seed,rev,arm,digest,route=o.Route,status=result.Status,value=result.Value,variables=result.Variables,rows=result.Rows,hit,certVerified=o.TransformVerified,directProof=o.OptimalityProved,verifiedWitness=result.Bits!=null,ticks,frequency=Stopwatch.Frequency,bytes=GC.GetAllocatedBytesForCurrentThread()-alloc,evidenceBytes=bytes.Length,phases=trace.Ticks}));
                }
                foreach(var a in results.Values)foreach(var b in results.Values){
                    if(a.Status=="Optimal"&&b.Status=="Optimal"&&a.Value!=b.Value)throw new Exception("optimal mismatch");
                    if(a.Status=="Optimal"&&b.Value<a.Value)throw new Exception("bound mismatch");
                }
                rev++;
            }
        }
    }
}
