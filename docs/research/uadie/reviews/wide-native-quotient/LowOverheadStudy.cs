using System.Diagnostics;
using System.Text.Json;
internal static class LowOverheadStudy
{
    internal static void Run(string split)
    {
        bool disk=split=="disk";if(!disk&&split!="holdout")throw new ArgumentException("split");
        string[] arms=["gate-json","gate-typed","G20","G16"];
        using FileStream? output=disk?new("evidence.jsonl",FileMode.CreateNew):null;
        foreach(string family in new[]{"blocks","quotas","assignment","sparse","rectangular","mixed","overlap"})
        for(int h=0;h<(disk?1:12);h++){
            int seed=(disk?2910101:2880101)+h;
            var sessions=arms.Take(3).ToDictionary(a=>a,a=>new DependencyDispatcher(true,true,true,true));
            var g16=new ProofReuse(true);int rev=0;
            foreach(var input in UnifiedStudy.History(family,seed)){
                var results=new List<UnifiedEvidence>();
                for(int i=0;i<arms.Length;i++){
                    string arm=arms[(h+rev+i)%arms.Length];long start=Stopwatch.GetTimestamp(),alloc=GC.GetAllocatedBytesForCurrentThread();
                    var trace=new PhaseTrace();UnifiedEvidence e;
                    if(arm is "gate-typed" or "G20")e=sessions[arm].DecideEvidence(input,trace,arm=="G20");
                    else{
                        var m=IncrementalQuotient.Admit(ProofReuse.Snapshot(input));string d=CertifiedQuotient.Digest(m);trace.Mark("context");
                        UnifiedOutcome o;
                        if(arm=="gate-json")o=sessions[arm].Decide(m,trace);
                        else {var f=CertifiedStudy.Features(m);o=f.Disjoint||f.ReductionProxy>=.125?UnifiedStudy.Wrap(m,g16.Solve(m,d,trace)):new("original",CertifiedStudy.Original(m),[],0,0,0,false);}
                        e=new(d,m,o);
                    }
                    trace.Mark("solve-rest");UnifiedVerifier.Verify(e);trace.Mark("independent-replay");
                    byte[] b=JsonSerializer.SerializeToUtf8Bytes(e,CertifiedQuotient.Json);trace.Mark("encode");
                    if(output!=null){output.Write(b);output.WriteByte(10);output.Flush(true);trace.Mark("flush");}
                    long ticks=Stopwatch.GetTimestamp()-start,bytes=GC.GetAllocatedBytesForCurrentThread()-alloc;
                    var r=e.Outcome.Result;results.Add(e);
                    Console.WriteLine(JsonSerializer.Serialize(new{split,family,seed,rev,arm,digest=e.Digest,status=r.Status,value=r.Value,formal=e.Outcome.Formal,route=e.Outcome.Route,ticks,frequency=Stopwatch.Frequency,bytes,evidenceBytes=b.Length,phases=trace.Ticks}));
                }
                foreach(var a in results)foreach(var b in results)if(a.Digest!=b.Digest||a.Outcome.Result.Status=="Optimal"&&b.Outcome.Result.Status=="Optimal"&&a.Outcome.Result.Value!=b.Outcome.Result.Value)throw new Exception("disagreement");
                // Same policy and same evidence bytes for JSON/typed/context arms, allowing tie choices only if identical.
                var equivalent=results.Where((_,i)=>arms[(h+rev+i)%arms.Length]!="G16").ToArray();
                var first=JsonSerializer.SerializeToUtf8Bytes(equivalent[0],CertifiedQuotient.Json);
                foreach(var e in equivalent.Skip(1))if(!first.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(e,CertifiedQuotient.Json)))throw new Exception("equal policy evidence mismatch");
                rev++;
            }
        }
    }
    internal static void Audit()
    {
        int checkedArrays=0,cases=0;
        foreach(string family in new[]{"blocks","quotas","assignment","sparse","rectangular","mixed","overlap"})
        for(int seed=0;seed<12;seed++){
            var m=UnifiedStudy.History(family,2940101+seed).First();
            var s=new DependencyDispatcher(true,true,true,true);
            var first=s.DecideEvidence(m,new(),true);
            var saved=JsonSerializer.SerializeToUtf8Bytes(first,CertifiedQuotient.Json);
            // Mutate every exported array, including nested proof arrays and source arrays.
            void Mutate(object? o){
                if(o==null||o is string)return;
                if(o is int[] a){if(a.Length>0){a[0]^=1;checkedArrays++;}return;}
                if(o is long[] b){if(b.Length>0){b[0]^=1;checkedArrays++;}return;}
                if(o is Array ar){foreach(var item in ar)Mutate(item);return;}
                var t=o.GetType();if(t.IsValueType)return;
                foreach(var p in t.GetProperties())Mutate(p.GetValue(o));
            }
            Mutate(first);
            var next=s.DecideEvidence(m,new(),true);UnifiedVerifier.Verify(next);
            var restored=JsonSerializer.Deserialize<UnifiedEvidence>(saved,CertifiedQuotient.Json)!;
            if(next.Digest!=restored.Digest||next.Outcome.Result.Value!=restored.Outcome.Result.Value)throw new Exception("alias poisoning");
            // Caller mutation AFTER snapshot cannot change cached previous model.
            m.Cost[0]++;var changed=s.DecideEvidence(m,new(),true);UnifiedVerifier.Verify(changed);
            if(changed.Digest==next.Digest)throw new Exception("stale model");
            Console.WriteLine(JsonSerializer.Serialize(new{family,seed,valid=true,previous=next.Digest,current=changed.Digest}));cases++;
        }
        Console.WriteLine(JsonSerializer.Serialize(new{kind="summary",cases,checkedArrays,valid=true}));
    }
}
