using System.Globalization;
namespace WowDi.Studio.DesignSystem.Localization;

public enum StudioLanguage { English, Arabic }
public static class StudioLanguages
{
    public static string Tag(this StudioLanguage language)=>language switch {StudioLanguage.English=>"en",StudioLanguage.Arabic=>"ar",_=>throw new ArgumentOutOfRangeException(nameof(language))};
    public static string Direction(this StudioLanguage language)=>language switch {StudioLanguage.English=>"ltr",StudioLanguage.Arabic=>"rtl",_=>throw new ArgumentOutOfRangeException(nameof(language))};
    public static CultureInfo Culture(this StudioLanguage language)=>CultureInfo.GetCultureInfo(language.Tag());
    public static string Number(this StudioLanguage language,decimal value)=>value.ToString("G",language.Culture());
}
