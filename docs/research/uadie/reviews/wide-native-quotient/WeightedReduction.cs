using Google.OrTools.Sat;
using System.Text.Json;
internal record WeightedRow(int[] Index,long[] Coeff,long L,long U);
internal record WeightedModel(int N,WeightedRow[] Rows,long[] Cost);
internal record WeightedRowProof(long Gcd,long Minimum,long Maximum,long L,long U,bool Drop,bool Impossible);
internal record WeightedCertificate(string Digest,WeightedRowProof[] Rows);
internal record WeightedDecision(string Route,WideResult Result,WeightedCertificate? Proof,bool InfeasibleProved);
internal static class WeightedReduction
{
    internal const long Limit=1L<<50;
    internal static string Digest(WeightedModel m)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(m,CertifiedQuotient.Json)));
    internal static WeightedModel Admit(WeightedModel m)
    {
        if(m.N<1||m.N>100000||m.Cost.Length!=m.N||m.Rows.Length>100000)throw new ArgumentException("dimension");
        if(m.Cost.Aggregate((Int128)0,(s,c)=>s+Int128.Abs(c))>Limit)throw new OverflowException("objective");
        long total=0;
        foreach(var r in m.Rows){
            if(r.Index.Length!=r.Coeff.Length||r.Index.Any(i=>i<0||i>=m.N)||r.Index.Distinct().Count()!=r.Index.Length||r.Coeff.Any(c=>c==0)||r.L>r.U)throw new ArgumentException("row");
            if(Int128.Abs(r.L)>Limit||Int128.Abs(r.U)>Limit||r.Coeff.Aggregate((Int128)0,(s,c)=>s+Int128.Abs(c))>Limit)throw new OverflowException("row");
            total+=r.Index.Length;if(total>2000000)throw new ArgumentException("matrix limit");
        }
        return m;
    }
    internal static long Gcd(long a,long b){while(b!=0){long t=a%b;a=b;b=t;}return a;}
    internal static long Floor(long a,long b)=>a/b-(a%b<0?1:0);
    internal static long Ceil(long a,long b)=>a/b+(a%b>0?1:0);
    internal static WeightedCertificate Produce(WeightedModel m,string d)
    {
        var rows=new WeightedRowProof[m.Rows.Length];
        for(int j=0;j<rows.Length;j++){
            var r=m.Rows[j];long g=0,min=0,max=0;
            foreach(long a in r.Coeff){g=Gcd(g,Math.Abs(a));if(a<0)min+=a;else max+=a;}
            if(g==0)g=1;
            long l=Ceil(Math.Max(r.L,min),g),u=Floor(Math.Min(r.U,max),g);
            rows[j]=new(g,min,max,l,u,l<=u&&l==min/g&&u==max/g,l>u);
        }
        return new(d,rows);
    }
    internal static WeightedModel Apply(WeightedModel m,WeightedCertificate c)
    {
        var rows=new List<WeightedRow>();
        for(int j=0;j<m.Rows.Length;j++){
            var p=c.Rows[j];var r=m.Rows[j];if(p.Impossible)throw new ArgumentException("contradiction");if(p.Drop)continue;
            rows.Add(new(r.Index.ToArray(),r.Coeff.Select(a=>a/p.Gcd).ToArray(),p.L,p.U));
        }
        return new(m.N,rows.ToArray(),m.Cost.ToArray());
    }
    internal static long VerifyWitness(WeightedModel m,int[] x)
    {
        if(x.Length!=m.N||x.Any(i=>i is not(0 or 1)))throw new Exception("bits");
        foreach(var r in m.Rows){
            Int128 v=0;for(int i=0;i<r.Index.Length;i++)v+=(Int128)r.Coeff[i]*x[r.Index[i]];
            if(v<r.L||v>r.U)throw new Exception("source row");
        }
        Int128 cost=0;for(int i=0;i<m.N;i++)cost+=(Int128)m.Cost[i]*x[i];return checked((long)cost);
    }
    internal static WideResult Native(WeightedModel m)
    {
        var cp=new CpModel();var x=Enumerable.Range(0,m.N).Select(i=>cp.NewBoolVar("x"+i)).ToArray();
        foreach(var r in m.Rows)cp.AddLinearConstraint(LinearExpr.WeightedSum(r.Index.Select(i=>x[i]),r.Coeff),r.L,r.U);
        cp.Minimize(LinearExpr.WeightedSum(x,m.Cost));
        var solver=new CpSolver{StringParameters="num_search_workers:1 random_seed:1320101 max_time_in_seconds:0.5"};
        var s=solver.Solve(cp);
        if(s is not(CpSolverStatus.Optimal or CpSolverStatus.Feasible))return new(s.ToString(),null,null,m.N,m.Rows.Length);
        var bits=x.Select(v=>checked((int)solver.Value(v))).ToArray();long cost=VerifyWitness(m,bits);
        if(!double.IsFinite(solver.ObjectiveValue)||Math.Abs(solver.ObjectiveValue-cost)>1e-6||s==CpSolverStatus.Optimal&&(!double.IsFinite(solver.BestObjectiveBound)||Math.Abs(solver.BestObjectiveBound-cost)>1e-6))throw new Exception("native objective");
        return new(s.ToString(),cost,bits,m.N,m.Rows.Length);
    }
    // Only exact interval/gcd contradictions; never infer infeasibility from a timeout.
    internal static WeightedDecision Decide(WeightedModel m,bool reduce)
    {
        if(!reduce)return new("original",Native(m),null,false);
        string d=Digest(m);var c=Produce(m,d);
        if(!WeightedVerifier.Verify(m,d,c))throw new Exception("normalization proof");
        if(c.Rows.Any(r=>r.Impossible))return new("arithmetic-infeasible",new("Infeasible",null,null,m.N,m.Rows.Length),c,true);
        var q=Apply(m,c);var r=Native(q);
        if(r.Bits!=null&&VerifyWitness(m,r.Bits)!=r.Value)throw new Exception("source objective");
        return new("normalized",r,c,false);
    }
}
internal static class WeightedVerifier
{
    // Independent check uses inequalities defining ceil/floor; no shared division rounding implementation.
    // A common divisor need not be greatest: any positive divisor satisfying these checks is sound.
    internal static bool Verify(WeightedModel m,string d,WeightedCertificate c)
    {
        if(c.Digest!=d||c.Rows.Length!=m.Rows.Length)return false;
        for(int j=0;j<m.Rows.Length;j++){
            var r=m.Rows[j];var p=c.Rows[j];long g=p.Gcd;
            if(g<=0||g>WeightedReduction.Limit||r.Coeff.Any(a=>a%g!=0))return false;
            Int128 lo=r.Coeff.Where(a=>a<0).Aggregate((Int128)0,(s,a)=>s+a),hi=r.Coeff.Where(a=>a>0).Aggregate((Int128)0,(s,a)=>s+a);
            if(lo!=p.Minimum||hi!=p.Maximum)return false;
            Int128 l=Int128.Max(lo,r.L),u=Int128.Min(hi,r.U),gl=(Int128)g*p.L,gu=(Int128)g*p.U;
            if(gl<l||gl-g>=l||gu>u||gu+g<=u)return false;
            if(p.Impossible!=(p.L>p.U)||p.Drop!=(!p.Impossible&&gl==lo&&gu==hi))return false;
        }
        return true;
    }
    internal static void Replay(WeightedModel m,string d,WeightedDecision o)
    {
        WeightedReduction.Admit(m);if(WeightedReduction.Digest(m)!=d)throw new Exception("binding");
        var r=o.Result;if(r.Status is not("Optimal" or "Feasible" or "Infeasible" or "Unknown"))throw new Exception("status");
        if(r.Status is "Optimal" or "Feasible"){
            if(r.Bits==null||WeightedReduction.VerifyWitness(m,r.Bits)!=r.Value)throw new Exception("primal");
        }else if(r.Bits!=null||r.Value.HasValue)throw new Exception("nonconclusive value");
        if(o.Proof!=null&&!Verify(m,d,o.Proof))throw new Exception("proof");
        if(o.InfeasibleProved!=(o.Proof?.Rows.Any(p=>p.Impossible)??false)||o.InfeasibleProved&&r.Status!="Infeasible")throw new Exception("infeasibility");
    }
}
