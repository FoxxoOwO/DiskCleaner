namespace DiskCleaner.Models;

/// <summary>One static scan rule loaded from Assets/rules.json.</summary>
public sealed class ScanRule
{
    /// <summary>Path template. Supports environment variables, {drive} and "*" segments.</summary>
    public string Path { get; set; } = string.Empty;

    public string? TitleCs { get; set; }
    public string? TitleEn { get; set; }
    public string? ImpactCs { get; set; }
    public string? ImpactEn { get; set; }

    public bool DefaultChecked { get; set; } = true;

    /// <summary>Skip files newer than this many hours (used by the Temp category).</summary>
    public int MinAgeHours { get; set; }

    /// <summary>folder | recyclebin | docker-images | docker-buildcache | docker-containers</summary>
    public string Kind { get; set; } = "folder";

    /// <summary>Processes that lock this data, used for the "currently running" warning badge.</summary>
    public string[] ProcessNames { get; set; } = Array.Empty<string>();
}
