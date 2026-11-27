namespace WowDi.Studio.DesignSystem;

public enum StudioDensity
{
    Comfortable,
    Compact
}

public static class StudioDensityNames
{
    public static string ToAttribute(this StudioDensity density) => density switch
    {
        StudioDensity.Comfortable => "comfortable",
        StudioDensity.Compact => "compact",
        _ => throw new ArgumentOutOfRangeException(nameof(density))
    };
}
