namespace DiskCleaner.Models;

public enum AppLanguage
{
    Czech = 0,
    English = 1
}

/// <summary>Minimal runtime language switch shared by every view model.</summary>
public static class Localization
{
    public static AppLanguage Language { get; set; } = AppLanguage.Czech;

    public static bool IsCzech => Language == AppLanguage.Czech;

    public static string Pick(string czech, string english) => IsCzech ? czech : english;
}
