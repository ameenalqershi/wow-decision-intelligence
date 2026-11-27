using System.Globalization;
using System.Numerics;

namespace WowDi.ModelingSdk;

/// <summary>Exact input representation, not a solver or expression evaluator.
/// Domain validity is checked by UDIR when the model is built.</summary>
public readonly struct ExactValue
{
    private readonly BigInteger numerator;
    // default(ExactValue) denotes zero, with implicit denominator one.
    private readonly BigInteger denominator;
    private BigInteger Denominator => denominator.IsZero ? BigInteger.One : denominator;

    public ExactValue(BigInteger numerator, BigInteger denominator)
    {
        if (denominator.Sign <= 0) throw new ArgumentOutOfRangeException(nameof(denominator));
        var gcd = BigInteger.GreatestCommonDivisor(BigInteger.Abs(numerator), denominator);
        this.numerator = numerator / gcd;
        this.denominator = denominator / gcd;
    }

    public static implicit operator ExactValue(long value) => new(value, BigInteger.One);
    public static ExactValue FromDecimal(decimal value)
    {
        var bits = decimal.GetBits(value);
        BigInteger n = (BigInteger)(uint)bits[0] | ((BigInteger)(uint)bits[1] << 32) | ((BigInteger)(uint)bits[2] << 64);
        if (bits[3] < 0) n = -n;
        return new(n, BigInteger.Pow(10, (bits[3] >> 16) & 0xff));
    }
    internal bool IsIntegral => Denominator.IsOne;
    internal bool FitsProfile => BigInteger.Abs(numerator).ToString(CultureInfo.InvariantCulture).Length<=256 &&
        Denominator.ToString(CultureInfo.InvariantCulture).Length<=256;
    internal int CompareTo(ExactValue other) => (numerator*other.Denominator).CompareTo(other.numerator*Denominator);
    // Only for canonical domain endpoints already admitted by UDIR.
    internal static ExactValue FromAdmittedBound(string value)
    {
        var parts=value.Split('/');
        return new(BigInteger.Parse(parts[0],CultureInfo.InvariantCulture),
            parts.Length==1?BigInteger.One:BigInteger.Parse(parts[1],CultureInfo.InvariantCulture));
    }
    public override string ToString() => Denominator.IsOne
        ? numerator.ToString(CultureInfo.InvariantCulture)
        : FormattableString.Invariant($"{numerator}/{Denominator}");
}
