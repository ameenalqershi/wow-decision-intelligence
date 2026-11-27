using System.Globalization;
using System.Numerics;

namespace WowDi.Verification.Independent;

internal sealed class VerificationFailure(string code):Exception(code)
{
    internal string Code{get;}=code;
}

// Independent arithmetic implementation: no lowering, solver or presolve arithmetic.
internal readonly record struct Rational(BigInteger N,BigInteger D):IComparable<Rational>
{
    internal static Rational Zero=>new(BigInteger.Zero,BigInteger.One);
    internal bool Integral=>D.IsOne;
    internal static Rational Parse(string? value)
    {
        if(value is null||value.Length>514)throw new VerificationFailure("verify.number");
        var parts=value.Split('/');
        if(parts.Length is <1 or >2)throw new VerificationFailure("verify.number");
        static BigInteger Part(string raw,bool signed)
        {
            var digits=signed&&raw.StartsWith('-')?raw[1..]:raw;
            if(digits.Length is <1 or >256||!digits.All(char.IsAsciiDigit)||digits.Length>1&&digits[0]=='0'||raw=="-0")
                throw new VerificationFailure("verify.number");
            return BigInteger.Parse(raw,CultureInfo.InvariantCulture);
        }
        var n=Part(parts[0],true);
        var d=parts.Length==1?BigInteger.One:Part(parts[1],false);
        if(d<=0||parts.Length==2&&d.IsOne||BigInteger.GreatestCommonDivisor(BigInteger.Abs(n),d)!=BigInteger.One)
            throw new VerificationFailure("verify.number");
        return new(n,d);
    }
    internal static Rational From(long value)=>new(value,BigInteger.One);
    private static Rational Reduce(BigInteger n,BigInteger d)
    {
        if(d.IsZero)throw new VerificationFailure("verify.divisionByZero");
        if(d.Sign<0){n=-n;d=-d;}
        var gcd=BigInteger.GreatestCommonDivisor(BigInteger.Abs(n),d);
        n/=gcd;d/=gcd;
        // Bounds the intermediate arithmetic and the public exact representation.
        if(BigInteger.Abs(n).ToString(CultureInfo.InvariantCulture).Length>256||d.ToString(CultureInfo.InvariantCulture).Length>256)
            throw new VerificationFailure("verify.numericLimit");
        return new(n,d);
    }
    internal Rational Add(Rational b)=>Reduce(N*b.D+b.N*D,D*b.D);
    internal Rational Subtract(Rational b)=>Reduce(N*b.D-b.N*D,D*b.D);
    internal Rational Multiply(Rational b)=>Reduce(N*b.N,D*b.D);
    internal Rational Divide(Rational b)=>Reduce(N*b.D,D*b.N);
    internal Rational Negate()=>new(-N,D);
    public int CompareTo(Rational b)=>(N*b.D).CompareTo(b.N*D);
    public override string ToString()=>D.IsOne?N.ToString(CultureInfo.InvariantCulture):FormattableString.Invariant($"{N}/{D}");
}
