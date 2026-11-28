using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using WowDi.Dml.Binding;
using WowDi.Dml.Lowering;
using WowDi.Dml.Semantics;
using WowDi.Research.Certificates;
using WowDi.Research.Sparse;
using WowDi.Solver.Contracts;
using WowDi.Udir;
using WowDi.Verification.Independent;
using Xunit;
using Xunit.Abstractions;

namespace WowDi.Research.ScaleStudy.Tests;

public sealed class SparseTests(ITestOutputHelper output)
{
    private static int Cases(string name,int dflt)=>int.TryParse(Environment.GetEnvironmentVariable(name),out var n)?n:dflt;

    [Fact]
    public void RationalsAreExactAndCanonical()
    {
        Assert.Equal("1/3",(Q.One/Q.Of(3)).ToString());
        Assert.Equal("-1250",Q.Parse("-1.25e3").ToString());
        Assert.Equal("1/10",Q.Parse("0.1").ToString());
        Assert.Equal("3602879701896397/36028797018963968",Q.FromDouble(0.1).ToString()); // exact binary value of 0.1
        Assert.Equal(-3,(int)Q.Parse("-5/2").Floor());Assert.Equal(-2,(int)Q.Parse("-5/2").Ceiling());
        var r=new Random(7);
        for(int k=0;k<20000;k++)
        {
            long a=r.Next(-1000,1000),b=r.Next(1,1000),c=r.Next(-1000,1000),d=r.Next(1,1000);
            var x=Q.Of(a,b);var y=Q.Of(c,d);
            Assert.Equal(Q.Of(a*d+c*b,b*d),x+y);Assert.Equal(Q.Of(a*c,b*d),x*y);
            Assert.Equal(Math.Sign(a*d-c*b),x.CompareTo(y));
            Assert.Equal(x,Q.Parse(x.ToString()));
            var v=r.NextDouble()*1e6-5e5;Assert.Equal(v,Q.FromDouble(v).ToDouble());
        }
        Assert.Throws<OverflowException>(()=>Q.Of(BigInteger.One<<(Q.MaxBits+1),BigInteger.One));
    }

    private static SparseModel RandomModel(Random r,int n,int m,bool fractional,out long[] feasible)
    {
        var b=new SparseBuilder("rand",r.Next(2)==0);
        feasible=new long[n];
        for(int j=0;j<n;j++)
        {
            long lo=r.Next(-5,3),hi=lo+r.Next(0,8);feasible[j]=r.NextInt64(lo,hi+1);
            Q cost=fractional&&r.Next(3)==0?Q.Of(r.Next(-9,10),r.Next(1,7)):r.Next(-9,10);
            b.AddColumn(r.Next(8)==0?null:lo,r.Next(8)==0?null:hi,r.Next(3)!=0,cost);
        }
        for(int i=0;i<m;i++)
        {
            int k=r.Next(1,Math.Min(n,6)+1);
            var cols=Enumerable.Range(0,k).Select(_=>r.Next(n)).ToArray();
            var vals=cols.Select(_=>fractional&&r.Next(4)==0?Q.Of(r.Next(-7,8),r.Next(1,5)):Q.Of(r.Next(-6,7))).ToArray();
            var act=Q.Zero;for(int t=0;t<k;t++)act+=vals[t]*feasible[cols[t]];
            int kind=r.Next(3);
            b.AddRow(cols,vals,kind==1?null:act-r.Next(0,4),kind==2?null:act+r.Next(0,4));
        }
        b.SetObjectiveOffset(r.Next(-3,4));
        return b.Build();
    }

    [Fact]
    public void CodecRoundTripsCanonicallyAndRejectsCorruption()
    {
        var r=new Random(11);int corrupted=0,rejected=0;
        for(int c=0;c<Cases("WOWDI_CODEC_CASES",3000);c++)
        {
            var m=RandomModel(r,r.Next(1,30),r.Next(0,40),r.Next(2)==0,out _);
            var (bytes,sha)=SparseCodec.Encode(m);
            var back=SparseCodec.Decode(bytes);
            var (bytes2,sha2)=SparseCodec.Encode(back);
            Assert.Equal(sha,sha2);Assert.Equal(bytes,bytes2);
            Assert.Equal(sha,Convert.ToHexStringLower(SHA256.HashData(bytes)));
            // single-byte corruption: either rejected with a codec/sparse code or decodes to a different digest
            var bad=(byte[])bytes.Clone();var at=r.Next(bad.Length);bad[at]^=(byte)(1<<r.Next(8));corrupted++;
            try{var d=SparseCodec.Decode(bad);Assert.NotEqual(sha,SparseCodec.Encode(d).Sha256);}
            catch(SparseException){rejected++;}
            catch(OverflowException){rejected++;}
            catch(DivideByZeroException){rejected++;}
        }
        output.WriteLine($"corrupted={corrupted} rejected={rejected}");
    }

    [Fact]
    public void DigestIsIndependentOfBuilderValueOrder()
    {
        // Same model, different pool insertion order (offset set before vs after the rows) -> identical bytes and digest.
        SparseModel Build(bool offsetFirst)
        {
            var b=new SparseBuilder("p",false);b.AddColumn(null,null,false,0);
            if(offsetFirst)b.SetObjectiveOffset(7);
            b.AddRow([0],[Q.Of(2)],5,null);b.AddRow([0],[Q.Of(9)],null,99);
            if(!offsetFirst)b.SetObjectiveOffset(7);
            return b.Build();
        }
        var x=Build(true);var y=Build(false);
        Assert.NotEqual(x.Pool,y.Pool);
        Assert.Equal(SparseCodec.Encode(x).Bytes,SparseCodec.Encode(y).Bytes);
    }

    [Fact]
    public void BuilderMergesDuplicatesDropsZerosAndTransposes()
    {
        var b=new SparseBuilder("t",false);
        for(int j=0;j<3;j++)b.AddColumn(0,10,true,1);
        b.AddRow([2,0,2,1],[Q.Of(1),Q.Of(3),Q.Of(-1),Q.Of(4)],null,7);   // x2 terms cancel
        b.AddRow([1,2],[Q.Of(1,2),Q.Of(5)],0,null);
        var m=b.Build();
        Assert.Equal([0,1,1,2],m.ColIndex);
        var (cs,ri,_)=m.Transpose();
        Assert.Equal([0,1,3,4],cs);Assert.Equal([0,0,1,1],ri);
    }

    [Fact]
    public void VerifierFastAndExactPathsAgreeAndParallelismIsDeterministic()
    {
        var r=new Random(5);
        for(int c=0;c<Cases("WOWDI_VERIFY_CASES",3000);c++)
        {
            var m=RandomModel(r,r.Next(1,40),r.Next(0,60),r.Next(2)==0,out var x);
            if(r.Next(2)==0)x[r.Next(x.Length)]+=r.Next(-3,4);
            var fast=SparseVerifier.Check(m,x,1);
            var exact=SparseVerifier.Check(m,x.Select(v=>(Q)v).ToArray(),1);
            var par=SparseVerifier.Check(m,x,4);
            Assert.Equal(exact.ExactFeasible,fast.ExactFeasible);Assert.Equal(exact.Objective,fast.Objective);
            Assert.Equal(exact.MaxRowViolation,fast.MaxRowViolation);Assert.Equal(exact.FirstViolatedRow,fast.FirstViolatedRow);
            Assert.Equal(fast with{FastPathRows=0,ExactPathRows=0},par with{FastPathRows=0,ExactPathRows=0});
        }
    }

    // ------------------------------------------------------------------ three-way differential against the UDIR reference
    private static readonly byte[] Catalog=Encoding.UTF8.GetBytes("""{"units":[{"symbol":"minute","dimension":"time","scale":"1"}],"currencies":[],"calendars":["shift"]}""");
    private static readonly MeasurementSemantics Units=new([new("minute","time","1")],[],["shift"]);
    private static readonly SolverCapabilities Manifest=new(new("ref","1","rid"),["integer.linear"],[ScalarKind.Integer,ScalarKind.Boolean],null,null,1,true,true,true);

    [Fact]
    public void SparsePathsAgreeWithUdirOriginalVerifier()
    {
        var r=new Random(2026);int models=0,checks=0,feasible=0,domain=0;
        for(int c=0;c<Cases("WOWDI_DIFF_MODELS",2000);c++)
        {
            int n=r.Next(1,7),m=r.Next(0,5);
            var lo=Enumerable.Range(0,n).Select(_=>r.Next(-3,3)).ToArray();var hi=lo.Select(l=>l+r.Next(0,5)).ToArray();
            var rows=Enumerable.Range(0,m).Select(_=>Enumerable.Range(0,n).Select(_=>r.Next(3)==0?0:r.Next(-4,6)).ToArray()).ToArray();
            var rel=Enumerable.Range(0,m).Select(_=>r.Next(2)==0).ToArray(); // true: <=, false: >=
            var rhs=Enumerable.Range(0,m).Select(_=>r.Next(-6,12)).ToArray();
            var cost=Enumerable.Range(0,n).Select(_=>r.Next(-5,6)).ToArray();var max=r.Next(2)==0;
            string Lin(int[] a){var t=a.Select((v,j)=>(v,j)).Where(z=>z.v!=0).Select(z=>z.v<0?$"(0 - {-z.v}*x{z.j})":$"{z.v}*x{z.j}").ToArray();return t.Length==0?"0":string.Join(" + ",t);}
            string Num(int v)=>v<0?$"0 - {-v}":v.ToString(CultureInfo.InvariantCulture);
            var text=new StringBuilder("problem P { ");
            for(int j=0;j<n;j++)text.Append(CultureInfo.InvariantCulture,$"decision x{j} : Int in [{Num(lo[j])},{Num(hi[j])}]; ");
            for(int i=0;i<m;i++)if(rows[i].Any(v=>v!=0))text.Append(CultureInfo.InvariantCulture,$"constraint r{i} : {Lin(rows[i])} {(rel[i]?"<=":">=")} {Num(rhs[i])}; ");
            text.Append(max?"maximize":"minimize").Append(" goal priority 1 : ").Append(cost.Any(v=>v!=0)?Lin(cost):"1*x0").Append("; }");
            if(!cost.Any(v=>v!=0))cost[0]=1;
            var bound=DmlBinder.Bind("s",Encoding.UTF8.GetBytes(text.ToString()),new BindingContext());
            Assert.True(bound.Accepted,bound.Diagnostic?.Message+" "+text);
            var comp=DmlCompiler.Compile(bound.Document!,new("t","m","r",new("shift","UTC","2026-01-01T00:00:00Z","minute"),Units,
                [Convert.ToHexStringLower(SHA256.HashData(Catalog))],[],["integer.linear"]));
            Assert.True(comp.Accepted,comp.Diagnostic?.Message+" "+text);
            var cc=comp.Compilation!;var facts=cc.GeneratedInputs[cc.FactsDigest].ToArray();
            var lowered=ExactLowerer.Lower(cc.Normalized,Manifest,NumericEncoding.Int64,facts);
            Assert.True(lowered.Accepted,lowered.Diagnostic?.Code);
            var (fromLowered,ids)=UdirBridge.FromLowered(lowered.Model!,"lowered");
            // independent direct build from the generator arrays
            var db=new SparseBuilder("direct",max);
            for(int j=0;j<n;j++)db.AddColumn(lo[j],hi[j],true,cost[j]);
            for(int i=0;i<m;i++)if(rows[i].Any(v=>v!=0))
            {var cols=Enumerable.Range(0,n).Where(j=>rows[i][j]!=0).ToArray();db.AddRow(cols,cols.Select(j=>(long)rows[i][j]).ToArray(),rel[i]?null:rhs[i],rel[i]?rhs[i]:null);}
            var direct=db.Build();
            var doc=cc.Original;var model=doc.Model;
            var scope=new VerificationScope(model.TenantId,model.ModelId,model.RevisionId,"run",Convert.ToHexStringLower(SHA256.HashData(doc.Write())));
            var varIds=Enumerable.Range(0,n).Select(j=>model.Symbols.Single(s=>s.Role==SymbolRole.Variable&&(s.Id=="x"+j||s.Id.EndsWith(".x"+j,StringComparison.Ordinal))).Id).ToArray();
            models++;
            for(int a=0;a<6;a++)
            {
                var x=Enumerable.Range(0,n).Select(j=>(long)r.Next(lo[j]-(a==0?1:0),hi[j]+1+(a==1?1:0))).ToArray();
                var res=new SolverResult(model.TenantId,model.ModelId,model.RevisionId,"run",Manifest.Solver,MathematicalStatus.Feasible,Termination.Completed,
                    Enumerable.Range(0,n).ToImmutableDictionary(j=>varIds[j],j=>x[j].ToString(CultureInfo.InvariantCulture),StringComparer.Ordinal),
                    [new(model.Objectives[0].Id,"0",null,"0","0")],null,new(null,null,null));
                var rep=OriginalVerifier.Check(doc,Units,scope,res,facts);
                var udirDomainOk=!rep.Diagnostics.Any(d=>d.Code=="verify.domain");
                var dCheck=SparseVerifier.Check(direct,x);
                var xl=new long[n];for(int k=0;k<ids.Length;k++)xl[k]=x[Array.IndexOf(varIds,ids[k])];
                var lCheck=SparseVerifier.Check(fromLowered,xl);
                Assert.Equal(udirDomainOk,dCheck.ViolatedColumns==0);
                Assert.Equal(udirDomainOk,lCheck.ViolatedColumns==0);
                checks++;
                if(!udirDomainOk){domain++;continue;}
                var udirRows=rep.Constraints.All(k=>k.Satisfied);
                Assert.Equal(udirRows,dCheck.ViolatedRows==0);
                Assert.Equal(udirRows,lCheck.ViolatedRows==0);
                var udirObj=Q.Parse(rep.Objectives.Single().ExactValue);
                Assert.Equal(udirObj,dCheck.Objective);Assert.Equal(udirObj,lCheck.Objective);
                if(udirRows)feasible++;
            }
        }
        output.WriteLine($"models={models} assignments={checks} feasible={feasible} domainViolations={domain}");
    }

    // ------------------------------------------------------------------ certificates
    [Fact]
    public void SafeBoundHoldsForArbitraryMultipliers_WeakDualityProperty()
    {
        var r=new Random(99);long cases=0,finite=0;
        for(int c=0;c<Cases("WOWDI_BOUND_CASES",20000);c++)
        {
            var m=RandomModel(r,r.Next(1,25),r.Next(0,30),r.Next(2)==0,out var x);
            var y=new double[m.Rows];
            for(int i=0;i<y.Length;i++)y[i]=r.Next(4) switch{0=>0,1=>r.NextDouble()*10-5,2=>Math.Round(r.NextDouble()*6-3),_=>(r.NextDouble()-0.5)*1e-3};
            var cert=SafeBound.Compute(m,y);
            cases++;
            if(cert.Bound is not {} b)continue;
            finite++;
            var v=SparseVerifier.Check(m,x).Objective; // x is feasible by construction
            Assert.True(SparseVerifier.Check(m,x).ExactFeasible);
            Assert.True(m.Maximize?b>=v:b<=v,$"weak duality violated case {c}: bound {b} value {v}");
        }
        output.WriteLine($"cases={cases} finiteBounds={finite}");
        Assert.True(finite>cases/4);
    }

    [Fact]
    public void BoundNeverExceedsTrueIntegerOptimum()
    {
        var r=new Random(3);int proven=0;
        for(int c=0;c<Cases("WOWDI_OPT_CASES",3000);c++)
        {
            var b=new SparseBuilder("e",r.Next(2)==0);int n=r.Next(1,4);
            for(int j=0;j<n;j++)b.AddColumn(0,r.Next(1,4),true,r.Next(-5,6));
            for(int i=0;i<r.Next(0,3);i++){var cols=Enumerable.Range(0,n).ToArray();b.AddRow(cols,cols.Select(_=>(long)r.Next(-3,5)).ToArray(),null,r.Next(0,8));}
            var m=b.Build();
            Q? best=null;
            foreach(var x in Enumerate(m))
            {var ch=SparseVerifier.Check(m,x);if(ch.ExactFeasible&&(best is null||(m.Maximize?ch.Objective>best.Value:ch.Objective<best.Value)))best=ch.Objective;}
            if(best is null)continue;
            for(int t=0;t<5;t++)
            {
                var y=Enumerable.Range(0,m.Rows).Select(_=>r.NextDouble()*4-2).ToArray();
                var cert=SafeBound.Compute(m,y);
                if(cert.Bound is not {} bd)continue;
                Assert.True(m.Maximize?bd>=best.Value:bd<=best.Value);
                if(SafeBound.Assess(m,best.Value,cert).Status==CertifiedStatus.ProvenOptimal)proven++;
            }
        }
        output.WriteLine($"provenOptimalByRandomMultipliers={proven}");
    }

    private static IEnumerable<long[]> Enumerate(SparseModel m)
    {
        var lo=m.ColLower.Select(i=>(long)m.Pool[i].N).ToArray();var hi=m.ColUpper.Select(i=>(long)m.Pool[i].N).ToArray();
        var x=(long[])lo.Clone();
        while(true){yield return (long[])x.Clone();int k=0;while(k<x.Length&&x[k]==hi[k]){x[k]=lo[k];k++;}if(k==x.Length)yield break;x[k]++;}
    }

    [Fact]
    public void ExactDualsProveOptimalityAndContradictionsAreDetected()
    {
        // min x0 + 2 x1  s.t. x0 + x1 >= 3, x in [0,10]^2  -> optimum 3 at (3,0); dual y = 1 proves it.
        var b=new SparseBuilder("lp",false);b.AddColumn(0,10,false,1);b.AddColumn(0,10,false,2);b.AddRow([0,1],[Q.One,Q.One],3,null);
        var m=b.Build();
        var proof=SafeBound.Assess(m,3,SafeBound.Compute(m,[1.0]));
        Assert.Equal(CertifiedStatus.ProvenOptimal,proof.Status);
        Assert.Equal(CertifiedStatus.CertifiedGap,SafeBound.Assess(m,4,SafeBound.Compute(m,[1.0])).Status);
        Assert.Equal(CertifiedStatus.Contradiction,SafeBound.Assess(m,2,SafeBound.Compute(m,[1.0])).Status); // "2" is not attainable
        var weak=SafeBound.Compute(m,[-1.0]);                    // wrong sign: still valid, just weak
        Assert.True(weak.Bound!.Value<=3);
        var free=new SparseBuilder("free",false);free.AddColumn(null,null,false,1);var fm=free.Build();
        Assert.Null(SafeBound.Compute(fm,Array.Empty<Q>()).Bound);              // unbounded column: no finite certificate
    }
}
