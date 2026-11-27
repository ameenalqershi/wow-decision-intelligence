using System.Globalization;
using System.Numerics;
using WowDi.Contracts;

namespace WowDi.Udir;

internal readonly record struct ExactNumber(BigInteger Numerator, BigInteger Denominator) : IComparable<ExactNumber>
{
    public int CompareTo(ExactNumber other) => (Numerator * other.Denominator).CompareTo(other.Numerator * Denominator);

    internal static ExactNumber Parse(string? text)
    {
        if (text is null || text.Length > 514) throw BadNumber();
        var parts = text.Split('/');
        if (parts.Length > 2) throw BadNumber();
        var num = Part(parts[0], signed: true);
        var den = parts.Length == 1 ? BigInteger.One : Part(parts[1], signed: false);
        if (den.Sign <= 0 || parts.Length == 2 && den.IsOne ||
            !BigInteger.GreatestCommonDivisor(BigInteger.Abs(num), den).IsOne) throw BadNumber();
        return new(num, den);
    }

    private static BigInteger Part(string raw, bool signed)
    {
        var digits = signed && raw.StartsWith('-') ? raw[1..] : raw;
        if (digits.Length is < 1 or > 256 || !digits.All(char.IsAsciiDigit) ||
            digits.Length > 1 && digits[0] == '0' || raw == "-0") throw BadNumber();
        return BigInteger.Parse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    }

    private static ContractException BadNumber() => new("udir.rational", "Expected a bounded canonical rational string.");
}
