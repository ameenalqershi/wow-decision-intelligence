using System.Globalization;
using System.Numerics;

namespace WowDi.Dml.Semantics;

// Private compiler arithmetic. Original-model verification remains independent.
internal readonly record struct Rational : IComparable<Rational>
{
    internal BigInteger N {get;}
    internal BigInteger D {get;}
    internal Rational(BigInteger n,BigInteger d)
    {
        if(d.IsZero)throw new DivideByZeroException();
        if(d.Sign<0){n=-n;d=-d;}
        var gcd=BigInteger.GreatestCommonDivisor(BigInteger.Abs(n),d);
        N=n/gcd;D=d/gcd;
        if(BigInteger.Abs(N).ToString(CultureInfo.InvariantCulture).Length>256||
            D.ToString(CultureInfo.InvariantCulture).Length>256)throw new OverflowException("Exact coefficient exceeds supported profile.");
    }
    internal bool Integral=>D.IsOne;
    internal bool Zero=>N.IsZero;
    internal static Rational Parse(string text)
    {
        var parts=text.Split('/');
        return new(BigInteger.Parse(parts[0],CultureInfo.InvariantCulture),parts.Length==1?BigInteger.One:BigInteger.Parse(parts[1],CultureInfo.InvariantCulture));
    }
    internal static Rational Decimal(string text)
    {
        int point=text.IndexOf('.');
        return point<0?Parse(text):new(BigInteger.Parse(text.Remove(point,1),CultureInfo.InvariantCulture),BigInteger.Pow(10,text.Length-point-1));
    }
    public override string ToString()=>D.IsOne?N.ToString(CultureInfo.InvariantCulture):FormattableString.Invariant($"{N}/{D}");
    public int CompareTo(Rational other)=>(N*other.D).CompareTo(other.N*D);
    public static Rational operator +(Rational a,Rational b)=>new(a.N*b.D+b.N*a.D,a.D*b.D);
    public static Rational operator -(Rational a,Rational b)=>new(a.N*b.D-b.N*a.D,a.D*b.D);
    public static Rational operator -(Rational a)=>new(-a.N,a.D);
    public static Rational operator *(Rational a,Rational b)=>new(a.N*b.N,a.D*b.D);
    public static Rational operator /(Rational a,Rational b)=>new(a.N*b.D,a.D*b.N);
    internal static Rational One=>new(1,1);
    internal static Rational Origin=>new(0,1);
}
