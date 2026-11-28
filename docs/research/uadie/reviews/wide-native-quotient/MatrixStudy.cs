using System.Diagnostics;
using System.Text.Json;
internal static class MatrixStudy
{
    internal static IntegralMatrix Model(string family,int seed,int rev)
    {
        IntegralMatrix m;
        if(family is "flow" or "lower" or "infeasible" or "side"){
            m=MatrixAdmission.From(GeneralFlowStudy.Model(12,seed,family=="side"?"signed":family=="flow"?"signed":family,rev));
            if(family=="side")m=m with{Rows=m.Rows.Append(new WeightedRow([0],[1],m.Lower[0],m.Upper[0])).ToArray()};
        }else if(family is "weighted" or "contradiction")m=MatrixAdmission.From(WeightedStudy.Model(64,seed,family=="weighted"?"overlap":"contradiction",rev));
        else if(family=="signed-domain")m=new(3,[-2,-3,0],[2,3,4],[3,-2,1],0,"min",[new([0,1,2],[2,-3,1],-2,4)]);
        else m=MatrixAdmission.From(UnifiedStudy.History(family,seed).ElementAt(rev));
        // Change sense and offset, reorder rows and terms without changing feasible set.
        return m with{Sense=rev==1?"max":"min",Offset=seed%17-8,Rows=m.Rows.Reverse().Select(r=>r with{Index=r.Index.Reverse().ToArray(),Coeff=r.Coeff.Reverse().ToArray()}).ToArray()};
    }
    internal static MatrixEvidence Wrap(IntegralMatrix m,string d,string route,NetworkDecision r)=>new(d,m,route,r.Status,r.Value.HasValue?checked((m.Sense=="min"?r.Value.Value:-r.Value.Value)+m.Offset):null,r.Flow,MatrixVerifier.Assurance(r.Status,false),null,null,null,null);
    internal static void Run(string split)
    {
        bool disk=split=="disk";if(!disk&&split!="holdout")throw new ArgumentException("split");
        string[] arms=["CP-SAT","HiGHS","G24"];
        using FileStream? output=disk?new("evidence.jsonl",FileMode.CreateNew):null;
        foreach(string family in new[]{"flow","lower","infeasible","side","weighted","contradiction","signed-domain","quotas","assignment","sparse","mixed","overlap"})
        for(int h=0;h<(disk?1:12);h++){
            int seed=(disk?3120101:3090101)+h;var dispatcher=new MatrixDispatcher();
            for(int rev=0;rev<2;rev++){
                var input=Model(family,seed,rev);var results=new List<MatrixEvidence>();
                for(int a=0;a<arms.Length;a++){
                    string arm=arms[(h+rev+a)%3];long start=Stopwatch.GetTimestamp(),alloc=GC.GetAllocatedBytesForCurrentThread();var trace=new PhaseTrace();
                    MatrixEvidence e;
                    if(arm=="G24")e=dispatcher.Decide(input,trace);
                    else{
                        var m=MatrixAdmission.Snapshot(input);MatrixAdmission.Check(m);string d=MatrixAdmission.Digest(m);trace.Mark("matrix-context");
                        e=Wrap(m,d,arm,arm=="HiGHS"?MatrixReference.Highs(m):MatrixDispatcher.Original(m));
                    }
                    trace.Mark("solve");MatrixVerifier.Replay(e);trace.Mark("replay");
                    byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(e,CertifiedQuotient.Json);trace.Mark("encode");
                    if(output!=null){output.Write(bytes);output.WriteByte(10);output.Flush(true);trace.Mark("flush");}
                    long ticks=Stopwatch.GetTimestamp()-start;
                    results.Add(e);Console.WriteLine(JsonSerializer.Serialize(new{family,seed,rev,arm,digest=e.Digest,status=e.Status,value=e.Value,formal=e.Assurance.StartsWith("Proven"),assurance=e.Assurance,route=e.Route,ticks,frequency=Stopwatch.Frequency,bytes=GC.GetAllocatedBytesForCurrentThread()-alloc,evidenceBytes=bytes.Length,phases=trace.Ticks}));
                }
                foreach(var x in results)foreach(var y in results){
                    if(x.Digest!=y.Digest||x.Status=="Optimal"&&y.Status=="Optimal"&&x.Value!=y.Value||x.Status=="Infeasible"&&y.X!=null||x.Status=="Optimal"&&y.Value.HasValue&&(input.Sense=="min"?y.Value<x.Value:y.Value>x.Value))throw new Exception("reference disagreement");
                }
            }
        }
    }
    internal static void Readback(string file){
        int records=0,formal=0;foreach(var line in File.ReadLines(file)){
            var e=JsonSerializer.Deserialize<MatrixEvidence>(line,CertifiedQuotient.Json)??throw new Exception("evidence");MatrixVerifier.Replay(e);records++;if(e.Assurance.StartsWith("Proven"))formal++;
        }Console.WriteLine(JsonSerializer.Serialize(new{records,formal,valid=true}));
    }
    internal static void Audit()
    {
        int finite=0,tamper=0,abstain=0,checks=0;long vectors=0;
        void Pass(string name,bool ok){if(!ok)throw new Exception(name);checks++;Console.WriteLine(JsonSerializer.Serialize(new{kind="check",name,valid=true}));}
        void Reject(string name,MatrixEvidence e){bool rejected=false;try{MatrixVerifier.Replay(e);}catch{rejected=true;}if(!rejected)throw new Exception(name);tamper++;Console.WriteLine(JsonSerializer.Serialize(new{kind="tamper",name,rejected=true}));}
        for(int h=0;h<96;h++){
            var rng=new Random(3150101+h);long b0=rng.Next(-4,5),b1=rng.Next(-4,5);
            var n=new GeneralNetwork(4,[b0,b1,-b0-b1,0],[new(0,1,0,2,rng.Next(-5,6)),new(1,2,1,2,rng.Next(-5,6)),new(2,0,0,2,rng.Next(-5,6)),new(0,2,0,1,rng.Next(-5,6)),new(1,0,0,2,rng.Next(-5,6)),new(3,2,0,0,0)]);
            var m=MatrixAdmission.From(n) with{Offset=h%13-6,Sense=h%2==0?"max":"min"};
            long? best=null;var x=new long[m.N];
            void Visit(int i){
                if(i==x.Length){
                    vectors++;foreach(var r in m.Rows){long z=0;for(int j=0;j<r.Index.Length;j++)z+=r.Coeff[j]*x[r.Index[j]];if(z<r.L||z>r.U)return;}
                    long v=m.Offset;for(int j=0;j<x.Length;j++)v+=m.Cost[j]*x[j];if(best==null||(m.Sense=="min"?v<best:v>best))best=v;return;
                }for(long a=m.Lower[i];a<=m.Upper[i];a++){x[i]=a;Visit(i+1);}
            }Visit(0);
            var d=new MatrixDispatcher();var e=d.Decide(m,new());MatrixVerifier.Replay(e);
            var hi=MatrixReference.Highs(m);long? hiValue=hi.Value.HasValue?(m.Sense=="min"?hi.Value.Value:-hi.Value.Value)+m.Offset:null;
            if(e.Value!=best||hiValue!=best||e.Status!=hi.Status||e.Assurance!=(best.HasValue?"ProvenOptimal":"ProvenInfeasible"))throw new Exception("enumeration");
            finite++;Console.WriteLine(JsonSerializer.Serialize(new{kind="finite",h,e,expected=best,highs=hi},CertifiedQuotient.Json));
            Reject("digest",e with{Digest="forged"});
            Reject("assurance",e with{Assurance="SolverOptimal"});
            var p=e.Network!;var from=p.From.ToArray();from[0]=p.To[0];Reject("orientation",e with{Network=p with{From=from}});
            var side=m with{Rows=m.Rows.Append(new WeightedRow([0],[1],0,2)).ToArray()};string sd=MatrixAdmission.Digest(side);
            Reject("extra-row-rebound",e with{Source=side,Digest=sd,Network=p with{Digest=sd}});
            if(MatrixDiscovery.Find(side,sd)!=null)throw new Exception("side accepted");
            var se=d.Decide(side,new());MatrixVerifier.Replay(se);if(se.Route=="network")throw new Exception("fallback");abstain++;
        }
        var domain=Model("signed-domain",1,0);var de=new MatrixDispatcher().Decide(domain,new());MatrixVerifier.Replay(de);Pass("signed-domain-fallback",de.Route=="original"&&de.Status=="Optimal");
        var src=UnifiedStudy.History("blocks",3160101).First();var session=new DependencyDispatcher(true,true,true,true);
        var first=session.DecideEvidence(src,new(),true);var prior=first.Outcome.Result.Value;
        first.Model.Edges[0]=(0,0);var second=session.DecideEvidence(src,new(),true);UnifiedVerifier.Verify(second);Pass("tuple-export-isolation",second.Outcome.Result.Value==prior&&second.Model.Edges[0]==src.Edges[0]);
        var m0=MatrixAdmission.From(src);var md=new MatrixDispatcher();var firstM=md.Decide(m0,new());long? v0=firstM.Value;firstM.Source.Rows[0].Index[0]=1;firstM.Source.Cost[0]++;
        var next=md.Decide(m0,new());MatrixVerifier.Replay(next);Pass("matrix-export-isolation",next.Value==v0);
        var invalid=m0 with{Upper=m0.Upper.Select((v,i)=>i==0?-1:v).ToArray()};bool rejected=false;try{md.Decide(invalid,new());}catch(ArgumentException){rejected=true;}Pass("invalid-domain-rejected",rejected);
        Console.WriteLine(JsonSerializer.Serialize(new{kind="summary",finite,vectors,tamper,abstain,checks}));
    }
}
