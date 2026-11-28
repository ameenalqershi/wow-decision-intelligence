using System.Diagnostics;
using System.Text.Json;
internal static class MatrixSelectorStudy
{
    internal static void Run(){
        string[] arms=["G24","preserve","preserve-reuse"];
        foreach(string family in new[]{"flow","lower","infeasible","side","weighted","contradiction","quotas","assignment","sparse","mixed","overlap","blocks"})
        for(int h=0;h<12;h++){
            int seed=3180101+h;
            var sessions=new[]{new MatrixDispatcher(),new MatrixDispatcher(preserveNetworkRows:true),new MatrixDispatcher(preserveNetworkRows:true,reuseStructure:true)};
            var original=MatrixStudy.Model(family,seed,0);
            for(int rev=0;rev<4;rev++){
                var m=MatrixAdmission.Snapshot(original);
                if(rev>0)m.Cost[rev%m.N]+=rev;
                if(rev==3)m=m with{Rows=m.Rows.Reverse().ToArray()};
                var results=new List<MatrixEvidence>();
                for(int a=0;a<3;a++){
                    int i=(h+rev+a)%3;long start=Stopwatch.GetTimestamp(),alloc=GC.GetAllocatedBytesForCurrentThread();
                    var trace=new PhaseTrace();int old=sessions[i].StructuralHits;
                    var e=sessions[i].Decide(m,trace);trace.Mark("solve");MatrixVerifier.Replay(e);trace.Mark("replay");
                    byte[] b=JsonSerializer.SerializeToUtf8Bytes(e,CertifiedQuotient.Json);trace.Mark("encode");
                    long ticks=Stopwatch.GetTimestamp()-start;results.Add(e);
                    Console.WriteLine(JsonSerializer.Serialize(new{family,seed,rev,arm=arms[i],digest=e.Digest,status=e.Status,value=e.Value,formal=e.Assurance.StartsWith("Proven"),assurance=e.Assurance,route=e.Route,hits=sessions[i].StructuralHits-old,ticks,frequency=Stopwatch.Frequency,bytes=GC.GetAllocatedBytesForCurrentThread()-alloc,evidenceBytes=b.Length,phases=trace.Ticks}));
                }
                foreach(var x in results)foreach(var y in results)if(x.Status=="Optimal"&&y.Status=="Optimal"&&x.Value!=y.Value||x.Status=="Infeasible"&&y.X!=null)throw new Exception("disagreement");
                var same=results.Where((_,a)=>arms[(h+rev+a)%3]!="G24").ToArray();
                if(!JsonSerializer.SerializeToUtf8Bytes(same[0],CertifiedQuotient.Json).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(same[1],CertifiedQuotient.Json)))throw new Exception("reuse evidence differs");
            }
        }
    }
    internal static void Audit(){
        int checks=0;
        void Pass(string name,bool ok){if(!ok)throw new Exception(name);checks++;Console.WriteLine(JsonSerializer.Serialize(new{kind="check",name,valid=true}));}
        foreach(string family in new[]{"flow","lower","infeasible","weighted","contradiction"}){
            var m=MatrixStudy.Model(family,3210101,0);var s=new MatrixDispatcher(preserveNetworkRows:true,reuseStructure:true);
            var e=s.Decide(m,new());MatrixVerifier.Replay(e);
            if(e.Network!=null)e.Network.From[0]=e.Network.To[0];
            if(e.Weighted?.Proof!=null)e.Weighted.Proof.Rows[0]=e.Weighted.Proof.Rows[0] with{Gcd=0};
            e.Source.Cost[0]++;
            var edit=MatrixAdmission.Snapshot(m);edit.Cost[0]++;
            var next=s.Decide(edit,new());MatrixVerifier.Replay(next);Pass(family+"-objective-reuse",s.StructuralHits==1);
            var reordered=edit with{Rows=edit.Rows.Reverse().ToArray()};
            var r=s.Decide(reordered,new());MatrixVerifier.Replay(r);Pass(family+"-row-order-invalidate",s.StructuralHits==1);
            if(family is "flow" or "lower" or "infeasible"){
                var cap=MatrixAdmission.Snapshot(reordered);cap.Upper[0]++;
                MatrixVerifier.Replay(s.Decide(cap,new()));Pass(family+"-capacity-invalidate",s.StructuralHits==1);
            }
        }
        for(int i=0;i<12;i++){
            var m=MatrixStudy.Model("sparse",3210201+i,0);
            var e=new MatrixDispatcher(preserveNetworkRows:true).Decide(m,new());MatrixVerifier.Replay(e);
            Pass("sparse-preserved-"+i,e.Assurance=="ProvenOptimal"&&e.Route is "boolean/flow" or "boolean/assignment");
        }
        Console.WriteLine(JsonSerializer.Serialize(new{kind="summary",checks,valid=true}));
    }
}
