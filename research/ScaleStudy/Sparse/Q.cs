using System.Globalization;
using System.Numerics;

namespace WowDi.Research.Sparse;

/// <summary>Exact rational number in canonical form (denominator &gt; 0, gcd = 1). Every finite double is exactly
/// representable (dyadic), so solver outputs convert without rounding. Bit-length is bounded to keep work predictable.</summary>
public readonly struct Q:IEquatable<Q>,IComparable<Q>
{
    public const int MaxBits=4096;
    public BigInteger N {get;}
    public BigInteger D {get;}
    public static readonly Q Zero=new(BigInteger.Zero,BigInteger.One,true);
    public static readonly Q One=new(BigInteger.One,BigInteger.One,true);

    private Q(BigInteger n,BigInteger d,bool canonical){N=n;D=d;}

    public static Q Of(BigInteger n,BigInteger d)
    {
        if(d.IsZero)throw new DivideByZeroException();
        if(d.Sign<0){n=-n;d=-d;}
        var g=BigInteger.GreatestCommonDivisor(n,d);
        if(!g.IsOne&&!g.IsZero){n/=g;d/=g;}
        if(n.IsZero)d=BigInteger.One;
        if(n.GetBitLength()>MaxBits||d.GetBitLength()>MaxBits)throw new OverflowException("Rational exceeds the bounded bit length.");
        return new(n,d,true);
    }
    public static Q Of(long n)=>new(n,BigInteger.One,true);
    public static implicit operator Q(long n)=>Of(n);

    /// <summary>Exact value of a finite double (no rounding).</summary>
    public static Q FromDouble(double v)
    {
        if(!double.IsFinite(v))throw new ArgumentException("Non-finite value.");
        if(v==0)return Zero;
        long bits=BitConverter.DoubleToInt64Bits(v);
        bool neg=bits<0;int exp=(int)((bits>>52)&0x7FF);long man=bits&0xFFFFFFFFFFFFFL;
        if(exp==0)exp++;else man|=1L<<52;
        exp-=1075;
        BigInteger n=man;if(neg)n=-n;
        return exp>=0?Of(n<<exp,BigInteger.One):Of(n,BigInteger.One<<-exp);
    }

    public bool IsInteger=>D.IsOne;
    public int Sign=>N.Sign;
    public static Q operator+(Q a,Q b)=>a.D.IsOne&&b.D.IsOne?new(a.N+b.N,BigInteger.One,true):Of(a.N*b.D+b.N*a.D,a.D*b.D);
    public static Q operator-(Q a,Q b)=>a.D.IsOne&&b.D.IsOne?new(a.N-b.N,BigInteger.One,true):Of(a.N*b.D-b.N*a.D,a.D*b.D);
    public static Q operator*(Q a,Q b)=>a.D.IsOne&&b.D.IsOne?new(a.N*b.N,BigInteger.One,true):Of(a.N*b.N,a.D*b.D);
    public static Q operator/(Q a,Q b)=>Of(a.N*b.D,a.D*b.N);
    public static Q operator-(Q a)=>new(-a.N,a.D,true);
    public static bool operator<(Q a,Q b)=>a.CompareTo(b)<0;
    public static bool operator>(Q a,Q b)=>a.CompareTo(b)>0;
    public static bool operator<=(Q a,Q b)=>a.CompareTo(b)<=0;
    public static bool operator>=(Q a,Q b)=>a.CompareTo(b)>=0;
    public static bool operator==(Q a,Q b)=>a.Equals(b);
    public static bool operator!=(Q a,Q b)=>!a.Equals(b);
    public int CompareTo(Q o)=>D.IsOne&&o.D.IsOne?N.CompareTo(o.N):(N*o.D).CompareTo(o.N*D);
    public bool Equals(Q o)=>N==o.N&&D==o.D;
    public override bool Equals(object? obj)=>obj is Q q&&Equals(q);
    public override int GetHashCode()=>HashCode.Combine(N,D);
    public static Q Abs(Q a)=>a.N.Sign<0?-a:a;
    public static Q Min(Q a,Q b)=>a<=b?a:b;
    public static Q Max(Q a,Q b)=>a>=b?a:b;
    public BigInteger Floor()=>BigInteger.Divide(N-(N.Sign<0?D-1:0),D);
    public BigInteger Ceiling()=>-(-this).Floor();
    public double ToDouble()=>(double)N/(double)D;

    /// <summary>Canonical text "n" or "n/d" (same canonical form as UDIR rationals).</summary>
    public override string ToString()=>D.IsOne?N.ToString(CultureInfo.InvariantCulture):N.ToString(CultureInfo.InvariantCulture)+"/"+D.ToString(CultureInfo.InvariantCulture);

    /// <summary>Parses "n", "n/d" and exact decimals such as "-1.25e3" (as written in MPS files) without rounding.</summary>
    public static Q Parse(string s)
    {
        s=s.Trim();
        var slash=s.IndexOf('/');
        if(slash>0)return Of(BigInteger.Parse(s[..slash],CultureInfo.InvariantCulture),BigInteger.Parse(s[(slash+1)..],CultureInfo.InvariantCulture));
        int e=s.IndexOfAny(['e','E']);int exp=0;
        if(e>=0){exp=int.Parse(s[(e+1)..],NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture);s=s[..e];}
        if(Math.Abs(exp)>1000)throw new OverflowException("Exponent out of range.");
        var dot=s.IndexOf('.');
        if(dot>=0){exp-=s.Length-dot-1;s=s.Remove(dot,1);}
        if(s.Length==0||s=="-"||s=="+")throw new FormatException("Invalid number.");
        var n=BigInteger.Parse(s,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture);
        return exp>=0?Of(n*BigInteger.Pow(10,exp),BigInteger.One):Of(n,BigInteger.Pow(10,-exp));
    }
}
