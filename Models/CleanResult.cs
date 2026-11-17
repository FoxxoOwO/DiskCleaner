namespace DiskCleaner.Models;

public sealed class CleanResult
{
    public long FreedBytes { get; set; }
    public int DeletedItems { get; set; }
    public int SkippedFiles { get; set; }
    public bool DryRun { get; set; }
    public List<string> Log { get; } = new();

    public string LogPath { get; set; } = string.Empty;
}
