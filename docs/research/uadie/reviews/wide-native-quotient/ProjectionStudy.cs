using System.Diagnostics;
using System.Text.Json;
internal static class ProjectionStudy
{
    internal static void Run(){
        string[] arms=["text","typed"];
        foreach(string family in new[]{"blocks","quotas","assignment","sparse","mixed","overlap"})
        for(int h=0;h<12;h++){
            int seed=3240101+h;var sessions=new[]{new MatrixDispatcher(preserveNetworkRows:true),new MatrixDispatcher(preserveNetworkRows:true)};
            for(int rev=0;rev<2;rev++){
                var m=MatrixStudy.Model(family,seed,rev);byte[]? expected=null;
                for(int j=0;j<2;j++){
                    int i=(h+rev+j)%2;long alloc=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp(),projection=0;var t=new PhaseTrace();
                    var e=sessions[i].Decide(m,t);t.Mark("solve");
                    MatrixVerifier.Replay(e,i==1,v=>projection=v);t.Mark("replay");
                    byte[] b=JsonSerializer.SerializeToUtf8Bytes(e,CertifiedQuotient.Json);t.Mark("encode");long ticks=Stopwatch.GetTimestamp()-start;
                    if(expected!=null&&!expected.AsSpan().SequenceEqual(b))throw new Exception("evidence mismatch");expected=b;
                    Console.WriteLine(JsonSerializer.Serialize(new{family,seed,rev,arm=arms[i],digest=e.Digest,status=e.Status,value=e.Value,formal=e.Assurance.StartsWith("Proven"),ticks,frequency=Stopwatch.Frequency,bytes=GC.GetAllocatedBytesForCurrentThread()-alloc,evidenceBytes=b.Length,projectionTicks=projection,phases=t.Ticks}));
                }
            }
        }
    }
    internal static void Audit(){
        int cases=0,rejected=0;
        foreach(string family in new[]{"blocks","quotas","assignment","sparse","mixed","overlap"})
        for(int h=0;h<12;h++){
            var m=MatrixStudy.Model(family,3270101+h,0);var e=new MatrixDispatcher(preserveNetworkRows:true).Decide(m,new());
            MatrixVerifier.Replay(e,false);MatrixVerifier.Replay(e,true);if(e.Boolean==null)throw new Exception("boolean scope");
            var p=MatrixAdmission.From(e.Boolean.Model);
            var permutation=p with{Rows=p.Rows.Reverse().Select(r=>r with{Index=r.Index.Reverse().ToArray(),Coeff=r.Coeff.Reverse().ToArray()}).ToArray()};
            MatrixVerifier.CheckProjection(m,permutation,true);cases++;
            void Reject(string kind,IntegralMatrix bad){
                bool x=false,y=false;try{MatrixVerifier.CheckProjection(m,bad,false);}catch{x=true;}try{MatrixVerifier.CheckProjection(m,bad,true);}catch{y=true;}
                if(!x||!y)throw new Exception(kind);rejected++;Console.WriteLine(JsonSerializer.Serialize(new{kind,family,h,rejected=true}));
            }
            var rows=p.Rows.ToArray();rows[0]=rows[0] with{U=rows[0].U+1};Reject("row-bound",p with{Rows=rows});
            rows=p.Rows.ToArray();rows[0]=rows[0] with{Coeff=rows[0].Coeff.Select(a=>a+1).ToArray()};Reject("coefficient",p with{Rows=rows});
            Reject("dropped-row",p with{Rows=p.Rows.Skip(1).ToArray()});
            if(p.Rows.Length>1){rows=p.Rows.ToArray();int at=Array.FindIndex(rows,1,r=>!r.Index.Order().SequenceEqual(rows[0].Index.Order())||r.L!=rows[0].L||r.U!=rows[0].U);if(at>=0){rows[at]=rows[0];Reject("duplicate-substitution",p with{Rows=rows});}}
        }
        Console.WriteLine(JsonSerializer.Serialize(new{kind="summary",cases,rejected,valid=true}));
    }
}
