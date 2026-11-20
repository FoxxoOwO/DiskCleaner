using System.ComponentModel;
using System.Runtime.InteropServices;
using DiskCleaner.Models;

namespace DiskCleaner.Services;

/// <summary>Deletes folders, empties the Recycle Bin and prunes Docker, tolerating locked files.</summary>
public sealed class CleanerExecutionService : ICleanerExecutionService
{
    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const uint SHERB_NOPROGRESSUI = 0x00000002;
    private const uint SHERB_NOSOUND = 0x00000004;

    private const int DryRunLogLimit = 5000;

    private const ushort FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT = 0x0004;

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false
    };

    private readonly DockerCliService _docker;

    public CleanerExecutionService(DockerCliService docker) => _docker = docker;

    public async Task<CleanResult> CleanAsync(
        string driveRoot,
        IReadOnlyList<CleanTarget> targets,
        bool dryRun,
        bool useRecycleBin,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var result = new CleanResult { DryRun = dryRun };

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(Localization.Pick($"Čistím {target.Path}", $"Cleaning {target.Path}"));

            try
            {
                switch (target.Kind)
                {
                    case "recyclebin":
                        if (dryRun)
                        {
                            result.FreedBytes += target.Bytes;
                            result.Log.Add($"[dry] recycle bin {driveRoot}: {Format.Bytes(target.Bytes)}");
                        }
                        else if (SHEmptyRecycleBin(IntPtr.Zero, driveRoot,
                                     SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND) == 0)
                        {
                            result.FreedBytes += target.Bytes;
                            result.Log.Add($"emptied recycle bin {driveRoot}");
                        }
                        else
                        {
                            result.SkippedFiles++;
                            result.Log.Add($"recycle bin refused {driveRoot}");
                        }

                        break;

                    case DockerCliService.ImagesKind:
                    case DockerCliService.BuildCacheKind:
                    case DockerCliService.ContainersKind:
                        if (dryRun)
                        {
                            result.FreedBytes += target.Bytes;
                            result.Log.Add($"[dry] docker prune {target.Kind}: {Format.Bytes(target.Bytes)}");
                            break;
                        }

                        var output = await _docker.PruneAsync(target.Kind, cancellationToken).ConfigureAwait(false);
                        var reclaimed = DockerCliService.ParseReclaimed(output);
                        result.FreedBytes += reclaimed;
                        result.Log.Add($"docker prune {target.Kind}: {Format.Bytes(reclaimed)} reclaimed");
                        foreach (var line in output)
                        {
                            result.Log.Add($"docker prune {target.Kind}: {line}");
                        }

                        break;

                    default:
                        DeleteFolder(target, dryRun, useRecycleBin, result, cancellationToken);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.SkippedFiles++;
                result.Log.Add($"failed {target.Path}: {ex.Message}");
            }
        }

        result.LogPath = WriteLog(result) ?? string.Empty;
        return result;
    }

    private static void DeleteFolder(
        CleanTarget target,
        bool dryRun,
        bool useRecycleBin,
        CleanResult result,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(target.Path))
        {
            return;
        }

        var cutoff = target.MinAgeHours > 0 ? DateTime.UtcNow.AddHours(-target.MinAgeHours) : DateTime.MinValue;

        if (dryRun)
        {
            foreach (var file in EnumerateFiles(target.Path, cutoff))
            {
                if (result.Log.Count < DryRunLogLimit)
                {
                    result.Log.Add($"[dry] {file.FullName}");
                }

                result.DeletedItems++;
                result.FreedBytes += SafeLength(file);
            }

            if (result.DeletedItems > DryRunLogLimit)
            {
                result.Log.Add($"[dry] … {result.DeletedItems - DryRunLogLimit} more entries not listed");
            }

            return;
        }

        if (useRecycleBin)
        {
            // A folder with a cutoff (Temp) must never be moved wholesale: files inside the cutoff stay.
            var files = cutoff == DateTime.MinValue ? null : EnumerateFiles(target.Path, cutoff).ToList();
            var paths = files is null
                ? new List<string> { target.Path }
                : files.Select(file => file.FullName).ToList();

            if (paths.Count == 0)
            {
                return;
            }

            var code = MoveToRecycleBin(paths);
            if (code == 0)
            {
                if (files is null)
                {
                    result.FreedBytes += target.Bytes;
                    result.DeletedItems++;
                    result.Log.Add($"recycled {target.Path}");
                }
                else
                {
                    foreach (var file in files)
                    {
                        result.FreedBytes += SafeLength(file);
                        result.DeletedItems++;
                    }

                    result.Log.Add($"recycled {files.Count} files in {target.Path}");
                    RemoveEmptyDirectories(target.Path);
                }

                return;
            }

            result.SkippedFiles++;
            result.Log.Add($"recycle failed (0x{code:X}) for {target.Path}, skipped");
            return;
        }

        foreach (var file in EnumerateFiles(target.Path, cutoff))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var length = file.Length;
                file.Delete();
                result.FreedBytes += length;
                result.DeletedItems++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                result.SkippedFiles++;
            }
        }

        RemoveEmptyDirectories(target.Path);
    }

    private static IEnumerable<FileInfo> EnumerateFiles(string path, DateTime cutoff)
    {
        IEnumerable<FileInfo> sequence;
        try
        {
            sequence = new DirectoryInfo(path).EnumerateFiles("*", Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            yield break;
        }

        using var enumerator = sequence.GetEnumerator();
        while (true)
        {
            FileInfo file;
            bool skip;
            try
            {
                if (!enumerator.MoveNext())
                {
                    yield break;
                }

                file = enumerator.Current;
                skip = cutoff != DateTime.MinValue && file.LastWriteTimeUtc > cutoff;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                yield break;
            }

            if (!skip)
            {
                yield return file;
            }
        }
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Removes empty directories deepest-first so a folder that still holds locked files survives.</summary>
    private static void RemoveEmptyDirectories(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                RemoveIfEmpty(new DirectoryInfo(path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Nothing else to do; the summary reports the leftovers.
        }
    }

    /// <summary>Recursive post-order delete: a directory goes only after its now-empty children.</summary>
    private static bool RemoveIfEmpty(DirectoryInfo directory)
    {
        FileSystemInfo[] entries;
        try
        {
            entries = directory.GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }

        var empty = true;
        foreach (var entry in entries)
        {
            if (entry is DirectoryInfo child)
            {
                if (!RemoveIfEmpty(child))
                {
                    empty = false;
                }
            }
            else
            {
                empty = false;
            }
        }

        if (!empty)
        {
            return false;
        }

        try
        {
            directory.Delete(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static int MoveToRecycleBin(IReadOnlyCollection<string> paths)
    {
        var from = string.Join('\0', paths) + '\0' + '\0';
        var operation = new ShFileOpStruct
        {
            wFunc = FO_DELETE,
            pFrom = from,
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
        };

        try
        {
            return SHFileOperation(ref operation);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return -1;
        }
    }

    private static string? WriteLog(CleanResult result)
    {
        if (result.Log.Count == 0)
        {
            return null;
        }

        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DiskCleaner");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"clean-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllLines(file, result.Log);
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
}
