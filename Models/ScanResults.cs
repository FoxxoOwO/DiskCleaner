namespace DiskCleaner.Models;

/// <summary>A drive shown in the picker.</summary>
public sealed class DriveItem
{
    public DriveItem(string rootPath, string label, long freeBytes, long totalBytes)
    {
        RootPath = rootPath;
        Label = label;
        FreeBytes = freeBytes;
        TotalBytes = totalBytes;
    }

    public string RootPath { get; }
    public string Label { get; }
    public long FreeBytes { get; }
    public long TotalBytes { get; }

    public string Display => Localization.IsCzech
        ? $"{RootPath} ({Label}) - {Format.Bytes(FreeBytes)} volných z {Format.Bytes(TotalBytes)}"
        : $"{RootPath} ({Label}) - {Format.Bytes(FreeBytes)} free of {Format.Bytes(TotalBytes)}";
}

/// <summary>One found path, before it becomes a CleanItemViewModel.</summary>
public sealed class ScanItem
{
    public string TitleCs { get; init; } = string.Empty;
    public string TitleEn { get; init; } = string.Empty;
    public string ImpactCs { get; init; } = string.Empty;
    public string ImpactEn { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public long Bytes { get; init; }
    public bool DefaultChecked { get; init; } = true;
    public int MinAgeHours { get; init; }
    public string Kind { get; init; } = "folder";
    public string[] ProcessNames { get; init; } = Array.Empty<string>();
}

public sealed class ScanCategory
{
    public ScanCategory(CategoryDefinition definition) => Definition = definition;

    public CategoryDefinition Definition { get; }
    public List<ScanItem> Items { get; } = new();
    public long TotalBytes { get; private set; }

    public void Add(ScanItem item)
    {
        Items.Add(item);
        TotalBytes += item.Bytes;
    }
}

/// <summary>An item streamed to the UI as soon as it has been measured.</summary>
public sealed record ScanItemFound(string CategoryId, ScanItem Item);

/// <summary>A checked item handed to the cleaner.</summary>
public sealed record CleanTarget(string Path, string Kind, int MinAgeHours, long Bytes);
