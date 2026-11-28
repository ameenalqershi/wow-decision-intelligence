using System.Numerics;
using WowDi.Research.Certificates;
using WowDi.Research.Sparse;
using Xunit;

namespace WowDi.Research.ScaleStudy.Tests;

/// <summary>Targeted tests added after mutation testing exposed untested logic (implied bounds, dual cleaning, guards,
/// overflow fallback, codec edge cases).</summary>
public sealed class HardeningTests
{
    [Fact]
    public void ImpliedBoundsAreExactAndRoundInwardForIntegers()
    {
        // x0 + 2 x1 <= 7, x >= 0 (no upper bounds); -x0 + x2 >= -3 with x2 <= 4  ->  x0 <= 7, x1 <= 3 (int) or 7/2, x0 <= 7 also from row 2
        var b=new SparseBuilder("ib",false);
        b.AddColumn(0,null,false,1);b.AddColumn(0,null,true,1);b.AddColumn(null,4,false,0);
        b.AddRow([0,1],[Q.One,Q.Of(2)],null,7);
        b.AddRow([0,2],[-Q.One,Q.One],-3,null);
        var (lo,hi)=SafeBound.ImpliedBounds(b.Build());
        Assert.Equal(Q.Of(7),hi[0]);
        Assert.Equal(Q.Of(3),hi[1]);             // 7/2 rounded down for an integer column
        Assert.Equal(Q.Zero,lo[0]);              // declared bounds are kept
        Assert.Equal(Q.Of(-3),lo[2]);            // x2 >= -3 + x0 >= -3
        Assert.Equal(Q.Of(4),hi[2]);
    }

    [Fact]
    public void ImpliedBoundsContainEveryFeasiblePoint()
    {
        var r=new Random(17);
        for(int c=0;c<2000;c++)
        {
            int n=r.Next(1,4);var b=new SparseBuilder("p",false);
            var lo=new long[n];var hi=new long[n];
            for(int j=0;j<n;j++){lo[j]=r.Next(-3,2);hi[j]=lo[j]+r.Next(0,4);b.AddColumn(r.Next(3)==0?null:lo[j],r.Next(3)==0?null:hi[j],r.Next(2)==0,1);}
            for(int i=0;i<r.Next(1,4);i++)
            {var cols=Enumerable.Range(0,n).ToArray();var v=cols.Select(_=>Q.Of(r.Next(-3,4)==0?1:r.Next(-3,4))).ToArray();
             long rl=r.Next(-8,3);b.AddRow(cols,v,r.Next(2)==0?null:Q.Of(rl),r.Next(2)==0?null:Q.Of(rl+r.Next(0,9)));}
            var m=b.Build();var (L,U)=SafeBound.ImpliedBounds(m,4);
            // enumerate a box larger than the declared one; every feasible point must lie inside the implied box
            var x=new long[n];for(int j=0;j<n;j++)x[j]=lo[j]-2;
            while(true)
            {
                var ch=SparseVerifier.Check(m,x);
                if(ch.ExactFeasible)for(int j=0;j<n;j++){if(L[j] is {} l)Assert.True(x[j]>=l);if(U[j] is {} u)Assert.True(x[j]<=u);}
                int k=0;while(k<n&&x[k]==hi[k]+2){x[k]=lo[k]-2;k++;}if(k==n)break;x[k]++;
            }
        }
    }

    [Fact]
    public void CleaningRecoversABoundDestroyedByDualNoiseAndStaysValid()
    {
        // min x0  s.t.  x0 - x1 >= 2 (y1),  x1 >= 0 as a ROW (y2), x0 in [0,10], x1 free.  Optimum 2 at (2,0).
        // True duals y1 = y2 = 1 make the reduced cost of the free column x1 exactly 0; one ulp of noise makes it negative.
        var b=new SparseBuilder("noise",false);b.AddColumn(0,10,false,1);b.AddColumn(null,null,false,0);
        b.AddRow([0,1],[Q.One,-Q.One],2,null);b.AddRow([1],[Q.One],0,null);
        var m=b.Build();
        double[] noisy=[1.0,Math.BitIncrement(1.0)];
        Assert.Null(SafeBound.Compute(m,noisy).Bound);           // raw multipliers: no finite certificate
        Assert.Equal(1,SafeBound.Compute(m,noisy).UnboundedColumn);
        var c=SafeBound.Cleaned(m,noisy);
        Assert.Equal(Q.Of(2),c.Bound);Assert.EndsWith("+cleaned",c.Method);
        Assert.Equal(CertifiedStatus.ProvenOptimal,SafeBound.Assess(m,2,c).Status);
        // validity under adversarial noise on random models
        var r=new Random(5);
        for(int t=0;t<3000;t++)
        {
            var bb=new SparseBuilder("v",r.Next(2)==0);int n=r.Next(1,6);var x=new long[n];
            for(int j=0;j<n;j++){x[j]=r.Next(0,5);bb.AddColumn(0,r.Next(3)==0?null:x[j]+r.Next(0,3),r.Next(2)==0,r.Next(-4,5));}
            for(int i=0;i<r.Next(0,5);i++){var cols=Enumerable.Range(0,n).ToArray();var v=cols.Select(_=>Q.Of(r.Next(-3,4))).ToArray();var act=Q.Zero;for(int k=0;k<n;k++)act+=v[k]*x[k];
                bb.AddRow(cols,v,r.Next(2)==0?null:act-r.Next(0,3),r.Next(2)==0?null:act+r.Next(0,3));}
            var mm=bb.Build();var y=Enumerable.Range(0,mm.Rows).Select(_=>r.NextDouble()*6-3).ToArray();
            var cert=SafeBound.Cleaned(mm,y);
            if(cert.Bound is not {} bd)continue;
            var val=SparseVerifier.Check(mm,x).Objective;
            Assert.True(mm.Maximize?bd>=val:bd<=val);
        }
    }

    [Fact]
    public void BestSkipsOverflowingCandidatesAndReportsNoFiniteBound()
    {
        var b=new SparseBuilder("o",false);b.AddColumn(null,null,false,1);var m=b.Build();
        var none=SafeBound.Best(m,Array.Empty<Q[]>());
        Assert.Null(none.Bound);
        Assert.Throws<ArgumentException>(()=>SafeBound.Compute(m,new Q[1]));
        var huge=Q.Of(BigInteger.One<<(Q.MaxBits-1),BigInteger.One);
        var b2=new SparseBuilder("h",false);b2.AddColumn(0,huge,false,huge);b2.AddRow([0],[huge],0,huge);var m2=b2.Build();
        var c=SafeBound.Best(m2,[huge],[Q.One]);              // first candidate overflows -> skipped, second certifies
        Assert.NotNull(c.Bound);
    }

    [Fact]
    public void GuardsLimitsAndCanonicalFormsAreEnforced()
    {
        Assert.Throws<DivideByZeroException>(()=>Q.Of(1,0));
        Assert.Equal("-1/2",Q.Of(BigInteger.One,new BigInteger(-2)).ToString());
        Assert.Equal("0",Q.Of(BigInteger.Zero,new BigInteger(-7)).ToString());
        Assert.Equal(Q.Of(1,1),Q.Parse("+1"));Assert.Equal(Q.Of(3,2),Q.Parse("1.5E0"));Assert.Equal(Q.Of(2,7),Q.Parse("2/7"));
        Assert.Throws<FormatException>(()=>Q.Parse("-"));Assert.Throws<OverflowException>(()=>Q.Parse("1e2000"));
        Assert.Throws<ArgumentException>(()=>Q.FromDouble(double.NaN));
        Assert.Equal(Q.Zero,Q.FromDouble(-0.0));Assert.Equal(Q.Of(BigInteger.One,BigInteger.One<<1074),Q.FromDouble(double.Epsilon));
        Assert.True(Q.Of(1,3)<Q.Of(1,2)&&Q.Of(-1,3)>Q.Of(-1,2)&&Q.Min(1,2)==Q.One&&Q.Max(1,2)==Q.Of(2));
        var limits=new SparseLimits(MaxColumns:2,MaxRows:1,MaxNonzeros:2,MaxDistinctValues:3);
        var b=new SparseBuilder("l",false,limits);b.AddColumn(0,1,true,1);b.AddColumn(0,1,true,1);
        Assert.Throws<SparseException>(()=>b.AddColumn(0,1,true,1));
        b.AddRow([0,1],[Q.One,Q.One],null,1);
        Assert.Throws<SparseException>(()=>b.AddRow([0],[Q.One],null,1));
        Assert.Throws<SparseException>(()=>new SparseBuilder("x",false).AddColumn(5,1,false,0));
        var bad=new SparseBuilder("x",false);bad.AddColumn(0,1,false,0);
        Assert.Throws<SparseException>(()=>bad.AddRow([0],[Q.One],3,1));
        Assert.Throws<SparseException>(()=>bad.AddRow([4],[Q.One],null,1));
        Assert.Throws<ArgumentException>(()=>bad.AddRow([0],Array.Empty<Q>(),null,1));
        var m=b.Build();
        Assert.Equal("sparse.limit",Assert.Throws<SparseException>(()=>m.Validate(new SparseLimits(MaxColumns:1))).Code);
        Assert.Equal("sparse.limit",Assert.Throws<SparseException>(()=>m.Validate(new SparseLimits(MaxRows:0))).Code);
        Assert.Equal("sparse.limit",Assert.Throws<SparseException>(()=>m.Validate(new SparseLimits(MaxNonzeros:1))).Code);
        Assert.Equal("sparse.limit",Assert.Throws<SparseException>(()=>m.Validate(new SparseLimits(MaxDistinctValues:1))).Code);
        var (bytes,_)=SparseCodec.Encode(m);
        Assert.Equal("codec.limit",Assert.Throws<SparseException>(()=>SparseCodec.Decode(bytes,new SparseLimits(MaxColumns:1))).Code);
        Assert.Equal("codec.header",Assert.Throws<SparseException>(()=>SparseCodec.Decode(bytes.AsSpan(0,4).ToArray().Concat(new byte[]{9,9}).ToArray())).Code);
        Assert.Equal("codec.truncated",Assert.Throws<SparseException>(()=>SparseCodec.Decode(bytes.AsSpan(0,bytes.Length-1))).Code);
        Assert.Equal("codec.trailing",Assert.Throws<SparseException>(()=>SparseCodec.Decode(bytes.Concat(new byte[]{0}).ToArray())).Code);
        Assert.Throws<ArgumentException>(()=>SparseVerifier.Check(m,new long[1]));
        Assert.Throws<ArgumentException>(()=>SparseVerifier.Check(m,new Q[3]));
        Assert.Throws<ArgumentException>(()=>SparseVerifier.Check(m,new double[1]));
    }

    [Fact]
    public void Int128OverflowFallsBackToExactArithmeticWithTheSameVerdict()
    {
        var big=long.MaxValue;
        var b=new SparseBuilder("ovf",false);
        for(int j=0;j<4;j++)b.AddColumn(null,null,true,1);
        var limit=Q.Of((BigInteger)big*big*3,BigInteger.One);
        b.AddRow([0,1,2,3],[Q.Of(big),Q.Of(big),Q.Of(big),Q.Of(big)],null,limit);   // sum overflows Int128 for x = big
        var m=b.Build();
        var x=new[]{big,big,big,big};                                                // activity = 4 big^2 > 3 big^2
        var c=SparseVerifier.Check(m,x);
        Assert.False(c.ExactFeasible);Assert.Equal(1,c.ExactPathRows);Assert.Equal(0,c.FastPathRows);
        Assert.Equal((BigInteger)big*big,c.MaxRowViolation.N);
        var ok=SparseVerifier.Check(m,new[]{big,big,big,0L});
        Assert.True(ok.ExactFeasible);
        // fractional data and non-integer points go to the exact path and integrality is never relaxed
        var f=new SparseBuilder("frac",false);f.AddColumn(0,1,true,Q.Of(1,3));f.AddRow([0],[Q.Of(1,2)],null,Q.Of(1,2));var fm=f.Build();
        var half=SparseVerifier.Check(fm,[0.5]);
        Assert.False(half.ExactFeasible);Assert.Equal(1,half.ViolatedColumns);
        Assert.False(SparseVerifier.WithinTolerance(half,Q.Of(1,10)));
        Assert.True(SparseVerifier.WithinTolerance(SparseVerifier.Check(fm,[1.0]),Q.Zero));
        Assert.Equal(Q.Of(1,3),SparseVerifier.Check(fm,[1.0]).Objective);
    }
}
