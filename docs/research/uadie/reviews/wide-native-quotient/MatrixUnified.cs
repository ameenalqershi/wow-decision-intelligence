using Google.OrTools.Sat;
using System.Text.Json;
using System.Security.Cryptography;
internal record IntegralMatrix(int N,long[] Lower,long[] Upper,long[] Cost,long Offset,string Sense,WeightedRow[] Rows);
internal record MatrixNetworkProof(string Digest,int[] From,int[] To);
internal record MatrixEvidence(string Digest,IntegralMatrix Source,string Route,string Status,long? Value,long[]? X,string Assurance,MatrixNetworkProof? Network,NetworkDecision? Flow,UnifiedEvidence? Boolean,WeightedDecision? Weighted);
internal static class MatrixAdmission
{
    internal static IntegralMatrix Snapshot(IntegralMatrix m)=>m with{Lower=m.Lower.ToArray(),Upper=m.Upper.ToArray(),Cost=m.Cost.ToArray(),Rows=m.Rows.Select(r=>r with{Index=r.Index.ToArray(),Coeff=r.Coeff.ToArray()}).ToArray()};
    internal static string Digest(IntegralMatrix m)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(m,CertifiedQuotient.Json)));
    internal static void Check(IntegralMatrix m)
    {
        if(m.N<1||m.N>100000||m.Lower.Length!=m.N||m.Upper.Length!=m.N||m.Cost.Length!=m.N||m.Rows.Length>100000||m.Sense is not("min" or "max"))throw new ArgumentException("grammar");
        Int128 cost=Int128.Abs(m.Offset);
        for(int i=0;i<m.N;i++){
            if(m.Lower[i]>m.Upper[i]||Int128.Abs(m.Lower[i])>1L<<40||Int128.Abs(m.Upper[i])>1L<<40)throw new ArgumentException("domain");
            cost+=Int128.Max(Int128.Abs(m.Lower[i]),Int128.Abs(m.Upper[i]))*Int128.Abs(m.Cost[i]);
            if(cost>1L<<50||Int128.Abs(m.Cost[i])>1L<<50)throw new OverflowException("objective");
        }
        long nnz=0;foreach(var r in m.Rows){
            if(r.Index.Length!=r.Coeff.Length||r.L>r.U||Int128.Abs(r.L)>1L<<50||Int128.Abs(r.U)>1L<<50||r.Index.Distinct().Count()!=r.Index.Length)throw new ArgumentException("row");
            Int128 magnitude=0;for(int j=0;j<r.Index.Length;j++){
                int i=r.Index[j];long a=r.Coeff[j];if(i<0||i>=m.N||a==0||Int128.Abs(a)>1L<<50)throw new ArgumentException("coefficient");
                magnitude+=Int128.Abs(a)*Int128.Max(Int128.Abs(m.Lower[i]),Int128.Abs(m.Upper[i]));
                if(magnitude>1L<<50)throw new OverflowException("row magnitude");
            }
            nnz+=r.Index.Length;if(nnz>2000000)throw new ArgumentException("matrix budget");
        }
    }
    internal static long Value(IntegralMatrix m,long[] x)
    {
        if(x.Length!=m.N)throw new Exception("dimension");
        Int128 v=m.Offset;for(int i=0;i<x.Length;i++){if(x[i]<m.Lower[i]||x[i]>m.Upper[i])throw new Exception("domain");v+=(Int128)m.Cost[i]*x[i];}
        foreach(var r in m.Rows){Int128 z=0;for(int j=0;j<r.Index.Length;j++)z+=(Int128)r.Coeff[j]*x[r.Index[j]];if(z<r.L||z>r.U)throw new Exception("source row");}
        return checked((long)v);
    }
    internal static IntegralMatrix From(GeneralNetwork n)
    {
        var rows=Enumerable.Range(0,n.N).Select(_=>new List<(int I,long C)>()).ToArray();
        for(int i=0;i<n.Arcs.Length;i++){var a=n.Arcs[i];rows[a.From].Add((i,1));rows[a.To].Add((i,-1));}
        return new(n.Arcs.Length,n.Arcs.Select(a=>a.Lower).ToArray(),n.Arcs.Select(a=>a.Upper).ToArray(),n.Arcs.Select(a=>a.Cost).ToArray(),0,"min",rows.Select((r,i)=>new WeightedRow(r.Select(x=>x.I).ToArray(),r.Select(x=>x.C).ToArray(),n.Supply[i],n.Supply[i])).ToArray());
    }
    internal static IntegralMatrix From(WideModel m)=>new(m.N,new long[m.N],Enumerable.Repeat(1L,m.N).ToArray(),m.Cost.ToArray(),0,"min",m.Edges.Select(e=>new WeightedRow([e.A,e.B],[1,1],1,1)).Concat(m.Fixes.Select(f=>new WeightedRow([f.Bit],[1],f.Value,f.Value))).Concat(m.Rows.Select(r=>new WeightedRow(r.Scope.ToArray(),Enumerable.Repeat(1L,r.Scope.Length).ToArray(),r.L,r.U))).ToArray());
    internal static IntegralMatrix From(WeightedModel m)=>new(m.N,new long[m.N],Enumerable.Repeat(1L,m.N).ToArray(),m.Cost.ToArray(),0,"min",m.Rows.Select(r=>r with{Index=r.Index.ToArray(),Coeff=r.Coeff.ToArray()}).ToArray());
    internal static long[] MinCost(IntegralMatrix m)=>m.Cost.Select(c=>m.Sense=="min"?c:-c).ToArray();
}
internal static class MatrixDiscovery
{
    internal static MatrixNetworkProof? Find(IntegralMatrix m,string d)
    {
        if(m.Rows.Length<2||m.Rows.Length>1024||m.N>100000||(long)m.N*m.Rows.Length>8000000||m.Lower.Any(v=>v<0)||m.Rows.Any(r=>r.L!=r.U)||m.Rows.Aggregate((Int128)0,(s,r)=>s+r.L)!=0)return null;
        var from=Enumerable.Repeat(-1,m.N).ToArray();var to=from.ToArray();
        for(int j=0;j<m.Rows.Length;j++){var r=m.Rows[j];for(int k=0;k<r.Index.Length;k++){
            int i=r.Index[k];long a=r.Coeff[k];if(a==1&&from[i]<0)from[i]=j;else if(a==-1&&to[i]<0)to[i]=j;else return null;
        }}
        if(from.Any(i=>i<0)||to.Any(i=>i<0))return null;
        var p=new MatrixNetworkProof(d,from,to);
        try{GeneralFlow.Admit(Build(m,p));}catch(Exception e)when(e is ArgumentException or OverflowException){return null;}
        return p;
    }
    internal static GeneralNetwork Build(IntegralMatrix m,MatrixNetworkProof p)=>new(m.Rows.Length,m.Rows.Select(r=>r.L).ToArray(),Enumerable.Range(0,m.N).Select(i=>new GeneralArc(p.From[i],p.To[i],m.Lower[i],m.Upper[i],m.Sense=="min"?m.Cost[i]:-m.Cost[i])).ToArray());
}
internal static class MatrixVerifier
{
    // Check original row incidence against certificate-generated row lists; independent of column scan.
    internal static GeneralNetwork VerifyNetwork(IntegralMatrix m,string d,MatrixNetworkProof p)
    {
        if(p.Digest!=d||p.From.Length!=m.N||p.To.Length!=m.N||m.Rows.Any(r=>r.L!=r.U))throw new Exception("mapping");
        var expected=Enumerable.Range(0,m.Rows.Length).Select(_=>new List<(int,long)>()).ToArray();
        var arcs=new GeneralArc[m.N];
        for(int i=0;i<m.N;i++){
            int a=p.From[i],b=p.To[i];if(a<0||b<0||a>=expected.Length||b>=expected.Length||a==b)throw new Exception("orientation");
            expected[a].Add((i,1));expected[b].Add((i,-1));
            arcs[i]=new(a,b,m.Lower[i],m.Upper[i],m.Sense=="min"?m.Cost[i]:-m.Cost[i]);
        }
        for(int j=0;j<expected.Length;j++){
            var r=m.Rows[j];if(!expected[j].OrderBy(x=>x.Item1).SequenceEqual(r.Index.Select((i,k)=>(i,r.Coeff[k])).OrderBy(x=>x.i)))throw new Exception("source coverage");
        }
        var n=new GeneralNetwork(m.Rows.Length,m.Rows.Select(r=>r.U).ToArray(),arcs);GeneralFlow.Admit(n);return n;
    }
    internal static string Assurance(string status,bool formal)=>status switch{"Optimal"=>formal?"ProvenOptimal":"SolverOptimal","Infeasible"=>formal?"ProvenInfeasible":"SolverInfeasible","Feasible"=>"Feasible","Unknown"=>"Unknown",_=>throw new Exception("status")};
    internal static void Replay(MatrixEvidence e)
    {
        var m=e.Source;MatrixAdmission.Check(m);if(MatrixAdmission.Digest(m)!=e.Digest)throw new Exception("revision");
        if(e.Status is "Optimal" or "Feasible"){if(e.X==null||MatrixAdmission.Value(m,e.X)!=e.Value)throw new Exception("source witness");}
        else if(e.X!=null||e.Value!=null)throw new Exception("status value");
        bool formal=false;
        void Bind(string status,long? value,long[]? x){
            long? original=value.HasValue?checked((m.Sense=="min"?value.Value:-value.Value)+m.Offset):null;
            if(status!=e.Status||original!=e.Value||!(x??[]).AsSpan().SequenceEqual(e.X??[]))throw new Exception("result binding");
        }
        int proofKinds=(e.Network!=null||e.Flow!=null?1:0)+(e.Boolean!=null?1:0)+(e.Weighted!=null?1:0);
        if(proofKinds>1)throw new Exception("proof kinds");
        if(e.Network!=null||e.Flow!=null){
            if(e.Network==null||e.Flow==null)throw new Exception("missing network proof");
            var n=VerifyNetwork(m,e.Digest,e.Network);if(!NetworkVerifier.Verify(n,e.Flow,true))throw new Exception("network result");
            Bind(e.Flow.Status,e.Flow.Value,e.Flow.Flow);formal=true;
        }else if(e.Boolean!=null){
            var b=e.Boolean;UnifiedVerifier.Verify(b);var src=MatrixAdmission.From(b.Model);
            CheckProjection(m,src);var r=b.Outcome.Result;Bind(r.Status,r.Value,r.Bits?.Select(i=>(long)i).ToArray());formal=b.Outcome.Formal;
        }else if(e.Weighted!=null){
            if(m.Lower.Any(x=>x!=0)||m.Upper.Any(x=>x!=1))throw new Exception("weighted domain");
            var w=new WeightedModel(m.N,m.Rows,MatrixAdmission.MinCost(m));WeightedVerifier.Replay(w,WeightedReduction.Digest(w),e.Weighted);
            var r=e.Weighted.Result;Bind(r.Status,r.Value,r.Bits?.Select(i=>(long)i).ToArray());formal=e.Weighted.InfeasibleProved;
        }
        if(e.Assurance!=Assurance(e.Status,formal))throw new Exception("assurance");
    }
    private static void CheckProjection(IntegralMatrix m,IntegralMatrix p)
    {
        if(m.N!=p.N||!m.Lower.AsSpan().SequenceEqual(p.Lower)||!m.Upper.AsSpan().SequenceEqual(p.Upper)||!MatrixAdmission.MinCost(m).AsSpan().SequenceEqual(p.Cost)||m.Rows.Length!=p.Rows.Length)throw new Exception("boolean projection");
        // Exact row multiset; reordering permitted, duplicates preserved.
        string Key(WeightedRow r)=>r.L+":"+r.U+":"+string.Join(",",r.Index.Select((i,k)=>(i,r.Coeff[k])).OrderBy(a=>a.i));
        if(!m.Rows.Select(Key).Order(StringComparer.Ordinal).SequenceEqual(p.Rows.Select(Key).Order(StringComparer.Ordinal)))throw new Exception("boolean rows");
    }
}
internal sealed class MatrixDispatcher(bool network=true,bool weighted=true,bool boolean=true,bool preserveNetworkRows=false,bool reuseStructure=false)
{
    private readonly DependencyDispatcher session=new(true,true,true,true);
    private int busy;
    private IntegralMatrix? previousShape;
    private MatrixNetworkProof? previousNetwork;
    private WeightedCertificate? previousWeighted;
    internal int StructuralHits {get;private set;}
    private static bool SameShape(IntegralMatrix a,IntegralMatrix b){
        if(a.N!=b.N||!a.Lower.AsSpan().SequenceEqual(b.Lower)||!a.Upper.AsSpan().SequenceEqual(b.Upper)||a.Rows.Length!=b.Rows.Length)return false;
        for(int i=0;i<a.Rows.Length;i++){var x=a.Rows[i];var y=b.Rows[i];if(x.L!=y.L||x.U!=y.U||!x.Index.AsSpan().SequenceEqual(y.Index)||!x.Coeff.AsSpan().SequenceEqual(y.Coeff))return false;}return true;
    }
    internal MatrixEvidence Decide(IntegralMatrix input,PhaseTrace trace,bool normalize=false)
    {
        if(Interlocked.Exchange(ref busy,1)!=0)throw new InvalidOperationException("single owner");
        try{
            var m=MatrixAdmission.Snapshot(input);MatrixAdmission.Check(m);string d=MatrixAdmission.Digest(m);trace.Mark("matrix-context");
            MatrixEvidence Pack(string route,string st,long? v,long[]? x,bool formal,MatrixNetworkProof? p=null,NetworkDecision? f=null,UnifiedEvidence? b=null,WeightedDecision? w=null)
                =>new(d,m,route,st,v.HasValue?checked((m.Sense=="min"?v.Value:-v.Value)+m.Offset):null,x,MatrixVerifier.Assurance(st,formal),p,f,b,w);
            bool bits=m.Lower.All(x=>x==0)&&m.Upper.All(x=>x==1);
            bool same=reuseStructure&&previousShape!=null&&SameShape(previousShape,m);
            if(!same){previousShape=null;previousNetwork=null;previousWeighted=null;}
            if(boolean&&bits&&m.Rows.All(r=>r.L>=0&&r.U<=r.Index.Length&&r.Coeff.All(a=>a==1))){
                var edges=new List<(int,int)>();var fixes=new List<(int,int)>();var rows=new List<WideRow>();
                var count=new int[preserveNetworkRows?m.N:0];var unit=new int[count.Length];
                if(preserveNetworkRows)foreach(var r in m.Rows)foreach(int i in r.Index){count[i]++;if(r.U==1)unit[i]++;}
                foreach(var r in m.Rows){
                    bool keep=preserveNetworkRows&&r.U==1&&r.Index.Length>0&&r.Index.All(i=>count[i]==2&&unit[i]==2);
                    if(!keep&&r.Index.Length==2&&r.L==1&&r.U==1)edges.Add((r.Index[0],r.Index[1]));
                    else if(!keep&&r.Index.Length==1&&r.L==r.U)fixes.Add((r.Index[0],(int)r.L));
                    else rows.Add(new(r.Index.ToArray(),(int)r.L,(int)r.U));
                }
                var b=session.DecideEvidence(new(m.N,edges.ToArray(),fixes.ToArray(),rows.ToArray(),MatrixAdmission.MinCost(m)),trace,true);var r0=b.Outcome.Result;
                return Pack("boolean/"+b.Outcome.Route,r0.Status,r0.Value,r0.Bits?.Select(i=>(long)i).ToArray(),b.Outcome.Formal,b:b);
            }
            MatrixNetworkProof? p=null;
            if(network){
                if(same&&previousNetwork!=null){
                    var candidate=previousNetwork with{Digest=d,From=previousNetwork.From.ToArray(),To=previousNetwork.To.ToArray()};
                    try{GeneralFlow.Admit(MatrixDiscovery.Build(m,candidate));p=candidate;StructuralHits++;}
                    catch(Exception ex)when(ex is ArgumentException or OverflowException){p=null;}
                }
                else p=MatrixDiscovery.Find(m,d);
            }
            trace.Mark("network-discovery");
            if(p!=null){
                var n=MatrixVerifier.VerifyNetwork(m,d,p);var f=GeneralFlow.Solve(n,true);
                if(f.Status!="Unknown"){
                    if(!NetworkVerifier.Verify(n,f,true))throw new Exception("proof");
                    if(reuseStructure&&!same){previousShape=MatrixAdmission.Snapshot(m);previousNetwork=p with{From=p.From.ToArray(),To=p.To.ToArray()};}
                    return Pack("network",f.Status,f.Value,f.Flow,true,p,f);
                }
            }
            if(weighted&&bits){
                var w=new WeightedModel(m.N,m.Rows,MatrixAdmission.MinCost(m));WeightedReduction.Admit(w);
                // Arithmetic proof always considered in weighted grammar; normalization optional.
                string wd=WeightedReduction.Digest(w);
                WeightedCertificate c;
                if(same&&previousWeighted!=null){c=previousWeighted with{Digest=wd,Rows=previousWeighted.Rows.ToArray()};StructuralHits++;}
                else c=WeightedReduction.Produce(w,wd);
                if(!WeightedVerifier.Verify(w,wd,c))throw new Exception("weighted proof");
                if(reuseStructure&&(!same||previousWeighted==null)){previousShape=MatrixAdmission.Snapshot(m);previousWeighted=c with{Rows=c.Rows.ToArray()};}
                WeightedDecision decision;
                if(c.Rows.Any(r=>r.Impossible))decision=new("arithmetic-infeasible",new("Infeasible",null,null,m.N,m.Rows.Length),c,true);
                else decision=WeightedReduction.Decide(w,normalize);
                var r=decision.Result;return Pack("weighted/"+decision.Route,r.Status,r.Value,r.Bits?.Select(i=>(long)i).ToArray(),decision.InfeasibleProved,w:decision);
            }
            var native=Original(m);return Pack("original",native.Status,native.Value,native.Flow,false);
        }finally{Volatile.Write(ref busy,0);}
    }
    internal static NetworkDecision Original(IntegralMatrix m)
    {
        var cp=new CpModel();var x=Enumerable.Range(0,m.N).Select(i=>cp.NewIntVar(m.Lower[i],m.Upper[i],"x"+i)).ToArray();
        foreach(var r in m.Rows)cp.AddLinearConstraint(LinearExpr.WeightedSum(r.Index.Select(i=>x[i]),r.Coeff),r.L,r.U);
        cp.Minimize(LinearExpr.WeightedSum(x,MatrixAdmission.MinCost(m)));
        var s=new CpSolver{StringParameters="num_search_workers:1 random_seed:1320101 max_time_in_seconds:0.5"};var st=s.Solve(cp);
        if(st is not(CpSolverStatus.Optimal or CpSolverStatus.Feasible))return new(st.ToString(),null,null,null,null);
        var bits=x.Select(s.Value).ToArray();long source=MatrixAdmission.Value(m,bits),value=m.Sense=="min"?source-m.Offset:m.Offset-source;
        if(!double.IsFinite(s.ObjectiveValue)||Math.Abs(s.ObjectiveValue-value)>1e-6||st==CpSolverStatus.Optimal&&(!double.IsFinite(s.BestObjectiveBound)||Math.Abs(s.BestObjectiveBound-value)>1e-6))throw new Exception("objective");
        return new(st.ToString(),value,bits,null,null);
    }
}
