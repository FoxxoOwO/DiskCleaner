namespace DiskCleaner.Models;

/// <summary>One expandable category loaded from Assets/rules.json.</summary>
public sealed class CategoryDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Glyph { get; set; } = "\uE8B7";

    public string NameCs { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string DescriptionCs { get; set; } = string.Empty;
    public string DescriptionEn { get; set; } = string.Empty;

    /// <summary>True when the category is filled dynamically (deep artifact scan / Docker).</summary>
    public bool Dynamic { get; set; }

    public List<ScanRule> Rules { get; set; } = new();
}

public sealed class RulesFile
{
    public List<CategoryDefinition> Categories { get; set; } = new();
}
