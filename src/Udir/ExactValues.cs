namespace WowDi.Udir;

/// <summary>Compare two canonical UDIR rational strings without rounding or fixed-width conversion.</summary>
public static class ExactValues
{
    public static int Compare(string left, string right) => ExactNumber.Parse(left).CompareTo(ExactNumber.Parse(right));
}
