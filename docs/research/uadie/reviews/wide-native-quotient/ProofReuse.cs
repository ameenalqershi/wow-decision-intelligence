using System.Text.Json;
using System.Threading;

// The verified structural token is private to this session. Neither input arrays
// nor returned proof arrays alias it. Current objective and solution are never cached.
internal sealed class ProofReuse(bool reuse)
{
    private sealed record Token(WideModel Shape,TransformCertificate Structural);
    private Token? token;
    private int busy;
    internal bool Hit {get;private set;}
    internal int Verifications {get;private set;}
    internal static WideModel Snapshot(WideModel m)=>new(m.N,m.Edges.ToArray(),m.Fixes.ToArray(),
        m.Rows.Select(r=>new WideRow(r.Scope.ToArray(),r.L,r.U)).ToArray(),m.Cost.ToArray());
    private static bool Same(WideModel a,WideModel b)
    {
        if(a.N!=b.N||!a.Edges.AsSpan().SequenceEqual(b.Edges)||!a.Fixes.AsSpan().SequenceEqual(b.Fixes)||a.Rows.Length!=b.Rows.Length)return false;
        for(int i=0;i<a.Rows.Length;i++)if(a.Rows[i].L!=b.Rows[i].L||a.Rows[i].U!=b.Rows[i].U||!a.Rows[i].Scope.AsSpan().SequenceEqual(b.Rows[i].Scope))return false;
        return true;
    }
    internal static TransformCertificate Bind(TransformCertificate t,WideModel m,string digest)
    {
        long offset=0;long[] cost=new long[t.Free];
        for(int i=0;i<m.N;i++){
            long v=m.Cost[i];if(t.Base[i]==1)offset+=v;
            if(t.Map[i]>=0)cost[t.Map[i]]+=t.Base[i]==1?-v:v;
        }
        return new(digest,t.Free,t.Map.ToArray(),t.Base.ToArray(),t.Rows.Select(r=>new SignedRow(r.Index.ToArray(),r.Coeff.ToArray(),r.L,r.U)).ToArray(),offset,cost);
    }
    // Independently compute orientation costs as two separate sums, then difference.
    internal static bool VerifyObjective(WideModel m,TransformCertificate t,string digest)
    {
        if(t.Digest!=digest||t.Map.Length!=m.N||t.Base.Length!=m.N||t.Cost.Length!=t.Free)return false;
        Int128 fixedCost=0;var zero=new Int128[t.Free];var one=new Int128[t.Free];
        for(int i=0;i<m.N;i++){
            int j=t.Map[i],b=t.Base[i];if(b is not(0 or 1)||j< -1||j>=t.Free)return false;
            if(j<0){if(b==1)fixedCost+=m.Cost[i];}
            else if(b==1)zero[j]+=m.Cost[i];else one[j]+=m.Cost[i];
        }
        Int128 off=fixedCost;for(int j=0;j<t.Free;j++){off+=zero[j];if(one[j]-zero[j]!=t.Cost[j])return false;}
        return off==t.Offset;
    }
    internal ProofOutcome Solve(WideModel m,string digest,PhaseTrace trace,bool direct=true,CancellationToken ct=default)
    {
        if(Interlocked.Exchange(ref busy,1)!=0)throw new InvalidOperationException("single owner");
        try{
            ct.ThrowIfCancellationRequested();
            Hit=reuse&&token!=null&&Same(token.Shape,m);trace.Mark("identity");
            Token? current=Hit?token:null;
            if(current==null){
                var shape=Snapshot(m) with{Cost=new long[m.N]};
                string shapeDigest=CertifiedQuotient.Digest(shape);
                var structural=CertifiedQuotient.Produce(shape,shapeDigest);trace.Mark("structural-transform");
                if(structural==null){
                    if(reuse)token=null;
                    var r=CertifiedStudy.Original(m);ct.ThrowIfCancellationRequested();
                    return new(r,null,null,"original-contradiction-fallback",false,false);
                }
                if(!TransformationVerifier.Verify(shape,shapeDigest,structural))throw new Exception("structural proof rejected");
                Verifications++;trace.Mark("structural-proof");
                current=new(shape,structural);ct.ThrowIfCancellationRequested();if(reuse)token=current;
            }
            var cert=Bind(current.Structural,m,digest);trace.Mark("objective-transform");
            if(!VerifyObjective(m,cert,digest))throw new Exception("current objective proof rejected");
            trace.Mark("objective-proof");
            var result=CertifiedQuotient.SolveReduced(cert,direct,out string route,out var y);trace.Mark("solve");
            ct.ThrowIfCancellationRequested();
            bool optimalProof=route=="direct";
            if(optimalProof&&!TransformationVerifier.VerifyDirect(cert,y!))throw new Exception("direct proof rejected");
            trace.Mark("optimality-proof");
            return new(result,cert,y,route,true,optimalProof);
        }finally{Volatile.Write(ref busy,0);}
    }
}
internal record ProofOutcome(WideResult Result,TransformCertificate? Cert,int[]? Reduced,string Route,bool TransformVerified,bool OptimalityProved);
internal record ProofEvidence(string Digest,int N,string Family,int Seed,int Revision,string Arm,WideModel Model,ProofOutcome Outcome);
internal static class ProofReadback
{
    // Replay is independent of the session/cache. Native optimality is not asserted
    // by this verifier: only transformation and primal, plus direct exchange proof.
    internal static void Verify(ProofEvidence e)
    {
        var m=IncrementalQuotient.Admit(e.Model);var o=e.Outcome;
        if(m.N!=e.N||CertifiedQuotient.Digest(m)!=e.Digest)throw new Exception("source binding");
        var r=o.Result;
        if(r.Bits!=null&&CertifiedStudy.Verify(m,r.Bits)!=r.Value)throw new Exception("source witness");
        if(r.Status is "Optimal" or "Feasible"){
            if(r.Bits==null||r.Value==null)throw new Exception("missing witness");
        }else if(r.Bits!=null||r.Value!=null)throw new Exception("nonconclusive witness");
        if(o.Cert==null){
            if(o.TransformVerified||o.OptimalityProved||o.Reduced!=null||o.Route=="direct")throw new Exception("missing certificate");
            return;
        }
        if(!o.TransformVerified||!TransformationVerifier.Verify(m,e.Digest,o.Cert))throw new Exception("full transformation proof");
        var c=o.Cert;
        if(r.Bits!=null){
            var y=o.Reduced??throw new Exception("missing reduced witness");
            if(y.Length!=c.Free||y.Any(v=>v is not(0 or 1)))throw new Exception("reduced domain");
            for(int i=0;i<m.N;i++)if(r.Bits[i]!=(c.Base[i]^(c.Map[i]<0?0:y[c.Map[i]])))throw new Exception("reconstruction mismatch");
            Int128 cost=c.Offset;for(int j=0;j<y.Length;j++)cost+=(Int128)c.Cost[j]*y[j];
            if(cost!=r.Value)throw new Exception("reduced objective");
        }else if(o.Reduced!=null)throw new Exception("unexpected reduced witness");
        if(o.OptimalityProved!=(o.Route=="direct"))throw new Exception("proof level");
        if(o.OptimalityProved&&(r.Status!="Optimal"||o.Reduced==null||!TransformationVerifier.VerifyDirect(c,o.Reduced)))throw new Exception("direct proof");
    }
    internal static void Run(string file)
    {
        int records=0,transforms=0,direct=0;
        foreach(string line in File.ReadLines(file)){
            var e=JsonSerializer.Deserialize<ProofEvidence>(line,CertifiedQuotient.Json)??throw new Exception("missing record");
            Verify(e);records++;if(e.Outcome.TransformVerified)transforms++;if(e.Outcome.OptimalityProved)direct++;
        }
        Console.WriteLine(JsonSerializer.Serialize(new{records,transforms,direct,valid=true}));
    }
}
