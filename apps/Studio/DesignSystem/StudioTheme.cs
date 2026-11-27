namespace WowDi.Studio.DesignSystem;

public enum StudioTheme
{
    Dark,
    Light,
    HighContrast
}

public static class StudioThemeNames
{
    public static string ToAttribute(this StudioTheme theme) => theme switch
    {
        StudioTheme.Dark => "dark",
        StudioTheme.Light => "light",
        StudioTheme.HighContrast => "high-contrast",
        _ => throw new ArgumentOutOfRangeException(nameof(theme))
    };
}
