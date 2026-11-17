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
                            result.Log.Add($"[dry] recycle bin {driveRoot}");
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
                            result.Log.Add($"[dry] docker prune {target.Kind}");
                            break;
                        }

                        var output = await _docker.PruneAsync(target.Kind, cancellationToken).ConfigureAwait(false);
                        result.FreedBytes += target.Bytes;
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
                result.Log.Add($"[dry] {file.FullName}");
                result.DeletedItems++;
                result.FreedBytes += file.Length;
            }

            return;
        }

        if (useRecycleBin)
        {
            var code = MoveToRecycleBin(target.Path);
            if (code == 0)
            {
                result.FreedBytes += target.Bytes;
                result.DeletedItems++;
                result.Log.Add($"recycled {target.Path}");
                return;
            }

            result.Log.Add($"recycle failed (0x{code:X}) for {target.Path}, deleting permanently");
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
        FileInfo[] files;
        try
        {
            files = new DirectoryInfo(path).GetFiles("*", Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            bool skip;
            try
            {
                skip = cutoff != DateTime.MinValue && file.LastWriteTimeUtc > cutoff;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skip = true;
            }

            if (!skip)
            {
                yield return file;
            }
        }
    }

    /// <summary>Removes empty directories deepest-first so a folder that still holds locked files survives.</summary>
    private static void RemoveEmptyDirectories(string path)
    {
        DirectoryInfo[] directories;
        try
        {
            directories = new DirectoryInfo(path).GetDirectories("*", Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            directories = Array.Empty<DirectoryInfo>();
        }

        foreach (var directory in directories.OrderByDescending(d => d.FullName.Length))
        {
            try
            {
                if (!directory.EnumerateFileSystemInfos().Any())
                {
                    directory.Delete(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A locked or non-empty directory stays behind.
            }
        }

        try
        {
            var root = new DirectoryInfo(path);
            if (root.Exists && !root.EnumerateFileSystemInfos().Any())
            {
                root.Delete(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Nothing else to do; the summary reports the leftovers.
        }
    }

    private static int MoveToRecycleBin(string path)
    {
        var from = path + '\0' + '\0';
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
