using DiskCleaner.Models;

namespace DiskCleaner.Services;

public interface IDiskScannerService
{
    /// <summary>The six category definitions, so the window can lay out its expanders before scanning.</summary>
    IReadOnlyList<CategoryDefinition> Definitions { get; }

    /// <summary>
    /// Scans <paramref name="driveRoot"/> and returns every category, including the empty ones.
    /// <paramref name="progress"/> reports the current path, <paramref name="itemProgress"/> is
    /// called for every measured path so the window fills up while the scan is still running.
    /// </summary>
    Task<IReadOnlyList<ScanCategory>> ScanAsync(
        string driveRoot,
        bool includeRecentFiles,
        IProgress<string> progress,
        IProgress<ScanItemFound> itemProgress,
        CancellationToken cancellationToken);
}
