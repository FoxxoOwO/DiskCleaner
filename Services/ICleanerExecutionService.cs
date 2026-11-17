using DiskCleaner.Models;

namespace DiskCleaner.Services;

public interface ICleanerExecutionService
{
    Task<CleanResult> CleanAsync(
        string driveRoot,
        IReadOnlyList<CleanTarget> targets,
        bool dryRun,
        bool useRecycleBin,
        IProgress<string> progress,
        CancellationToken cancellationToken);
}
