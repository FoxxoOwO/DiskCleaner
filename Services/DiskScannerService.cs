using System.Collections.Concurrent;
using System.Text.Json;
using DiskCleaner.Models;

namespace DiskCleaner.Services;

/// <summary>Fills the six categories: static rules from rules.json, a deep artifact walk and Docker.</summary>
public sealed class DiskScannerService : IDiskScannerService
{
    private static readonly EnumerationOptions DeepOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false
    };

    private static readonly HashSet<string> SystemRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "windows", "program files", "program files (x86)", "programdata", "$recycle.bin", "system volume information"
    };

    private readonly DockerCliService _docker;
    private RulesFile? _rules;

    public DiskScannerService(DockerCliService docker) => _docker = docker;

    public IReadOnlyList<CategoryDefinition> Definitions => LoadRules().Categories;

    public async Task<IReadOnlyList<ScanCategory>> ScanAsync(
        string driveRoot,
        bool includeRecentFiles,
        IProgress<string> progress,
        IProgress<ScanItemFound> itemProgress,
        CancellationToken cancellationToken)
    {
        var rules = LoadRules();
        var categories = new List<ScanCategory>();

        foreach (var definition in rules.Categories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var category = new ScanCategory(definition);

            void Add(ScanItem item)
            {
                category.Add(item);
                itemProgress.Report(new ScanItemFound(definition.Id, item));
            }

            if (definition.Id == "build-artifacts")
            {
                ScanArtifacts(driveRoot, progress, Add, cancellationToken);
            }
            else if (definition.Id == "docker")
            {
                progress.Report(Localization.Pick("Zjišťuji stav Dockeru…", "Checking Docker…"));
                foreach (var item in await _docker.GetItemsAsync(cancellationToken).ConfigureAwait(false))
                {
                    Add(item);
                }
            }
            else
            {
                foreach (var rule in definition.Rules)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var label = Localization.IsCzech
                        ? rule.TitleCs ?? rule.Path
                        : rule.TitleEn ?? rule.Path;

                    foreach (var path in ResolvePaths(rule, driveRoot))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var minAge = includeRecentFiles ? 0 : rule.MinAgeHours;
                        progress.Report(Localization.Pick($"Počítám {label}…", $"Measuring {label}…"));
                        var bytes = Measure(path, minAge, cancellationToken, progress, label);
                        if (bytes <= 0)
                        {
                            continue;
                        }

                        Add(new ScanItem
                        {
                            TitleCs = rule.TitleCs ?? System.IO.Path.GetFileName(path),
                            TitleEn = rule.TitleEn ?? System.IO.Path.GetFileName(path),
                            ImpactCs = rule.ImpactCs ?? string.Empty,
                            ImpactEn = rule.ImpactEn ?? string.Empty,
                            Path = path,
                            Bytes = bytes,
                            DefaultChecked = rule.DefaultChecked,
                            MinAgeHours = minAge,
                            Kind = rule.Kind,
                            ProcessNames = rule.ProcessNames
                        });
                    }
                }
            }

            categories.Add(category);
        }

        return categories;
    }

    private RulesFile LoadRules()
    {
        if (_rules is not null)
        {
            return _rules;
        }

        var file = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "rules.json");
        if (!File.Exists(file))
        {
            _rules = new RulesFile();
            return _rules;
        }

        var json = File.ReadAllText(file);
        _rules = JsonSerializer.Deserialize<RulesFile>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new RulesFile();
        return _rules;
    }

    /// <summary>Expands {drive}, environment variables and "*" segments, then keeps only paths on the selected drive.</summary>
    private static IEnumerable<string> ResolvePaths(ScanRule rule, string driveRoot)
    {
        var driveRelative = rule.Path.Contains("{drive}", StringComparison.OrdinalIgnoreCase);
        var template = driveRelative ? rule.Path.Replace("{drive}", driveRoot, StringComparison.OrdinalIgnoreCase) : rule.Path;
        template = Environment.ExpandEnvironmentVariables(template);

        foreach (var candidate in ExpandWildcards(template))
        {
            if (!driveRelative && !candidate.StartsWith(driveRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return candidate;
        }
    }

    private static IEnumerable<string> ExpandWildcards(string template)
    {
        var current = new List<string> { template };
        for (var guard = 0; guard < 8; guard++)
        {
            var wildcard = current.FirstOrDefault(p => p.Contains('*', StringComparison.Ordinal));
            if (wildcard is null)
            {
                return current;
            }

            var expanded = new List<string>();
            foreach (var path in current)
            {
                if (!path.Contains('*', StringComparison.Ordinal))
                {
                    expanded.Add(path);
                    continue;
                }

                var separator = path.IndexOf('*', StringComparison.Ordinal) is var index
                    ? path.LastIndexOf(System.IO.Path.DirectorySeparatorChar, index)
                    : -1;
                if (separator < 0)
                {
                    expanded.Add(path);
                    continue;
                }

                var parent = path[..separator];
                var remainder = path[(separator + 1)..];
                var cut = remainder.IndexOf(System.IO.Path.DirectorySeparatorChar);
                var pattern = cut < 0 ? remainder : remainder[..cut];
                var rest = cut < 0 ? string.Empty : remainder[cut..];

                if (!Directory.Exists(parent))
                {
                    continue;
                }

                try
                {
                    foreach (var match in Directory.EnumerateDirectories(parent, pattern))
                    {
                        expanded.Add(match + rest);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An unreadable parent simply yields no matches.
                }
            }

            if (expanded.Count == 0)
            {
                return Array.Empty<string>();
            }

            current = expanded;
        }

        return current.Where(Directory.Exists);
    }

    /// <summary>
    /// Total size of a directory; files newer than <paramref name="minAgeHours"/> are ignored when it is non-zero.
    /// Reports throttled progress and honours cancellation so a multi-million-file cache can be interrupted.
    /// </summary>
    private static long Measure(
        string path,
        int minAgeHours,
        CancellationToken cancellationToken,
        IProgress<string>? progress = null,
        string? label = null)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        var cutoff = DateTime.UtcNow.AddHours(-minAgeHours);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long total = 0;
        long files = 0;
        var lastReport = 0L;

        try
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", DeepOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                files++;

                var elapsed = clock.ElapsedMilliseconds;
                if (progress is not null && elapsed - lastReport >= 250)
                {
                    lastReport = elapsed;
                    var name = label ?? System.IO.Path.GetFileName(path);
                    progress.Report(Localization.Pick(
                        $"Počítám {name} – {files} souborů, {Format.Bytes(total)}…",
                        $"Measuring {name} – {files} files, {Format.Bytes(total)}…"));
                }

                try
                {
                    if (minAgeHours > 0 && file.LastWriteTimeUtc > cutoff)
                    {
                        continue;
                    }

                    total += file.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // A file vanishing mid-scan is simply not counted.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return total;
        }

        return total;
    }

    /// <summary>
    /// Walks the whole drive in parallel looking for build artifacts. Every worker pulls directories from a
    /// shared queue; a directory that is recognised as an artifact is measured instead of being descended
    /// into, so a huge <c>node_modules</c> is never walked twice.
    /// </summary>
    private void ScanArtifacts(
        string driveRoot,
        IProgress<string> progress,
        Action<ScanItem> add,
        CancellationToken cancellationToken)
    {
        var root = new DirectoryInfo(driveRoot);
        if (!root.Exists)
        {
            return;
        }

        var queue = new ConcurrentQueue<DirectoryInfo>();
        var found = new ConcurrentBag<ScanItem>();
        queue.Enqueue(root);

        // Queued plus currently processed directories; the walk is finished once it drops to zero.
        var outstanding = 1;
        long visited = 0;
        long artifacts = 0;
        var workers = Math.Clamp(Environment.ProcessorCount, 2, 8);

        void Walk()
        {
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (!queue.TryDequeue(out var directory))
                {
                    if (Volatile.Read(ref outstanding) <= 0)
                    {
                        return;
                    }

                    Thread.Sleep(1);
                    continue;
                }

                Visit(directory);
                Interlocked.Decrement(ref outstanding);
            }
        }

        void Visit(DirectoryInfo directory)
        {
            var visit = Interlocked.Increment(ref visited);
            if (visit % 32 == 0)
            {
                progress.Report(Localization.Pick(
                    $"Procházím {TrimForStatus(directory.FullName)} – {Interlocked.Read(ref artifacts)} artefaktů, {visit} složek",
                    $"Scanning {TrimForStatus(directory.FullName)} – {Interlocked.Read(ref artifacts)} artifacts, {visit} folders"));
            }

            DirectoryInfo[] children;
            try
            {
                children = directory.GetDirectories();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return;
            }

            var isRoot = ReferenceEquals(directory, root);
            foreach (var child in children)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (string.Equals(child.Name, ".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (isRoot && SystemRoots.Contains(child.Name))
                {
                    continue;
                }

                var artifact = Classify(child);
                if (artifact is null)
                {
                    Interlocked.Increment(ref outstanding);
                    queue.Enqueue(child);
                    continue;
                }

                var bytes = Measure(child.FullName, 0, cancellationToken);
                if (bytes <= 0)
                {
                    continue;
                }

                Interlocked.Increment(ref artifacts);
                found.Add(new ScanItem
                {
                    TitleCs = artifact.TitleCs,
                    TitleEn = artifact.TitleEn,
                    ImpactCs = artifact.ImpactCs,
                    ImpactEn = artifact.ImpactEn,
                    Path = child.FullName,
                    Bytes = bytes,
                    DefaultChecked = artifact.Checked,
                    Kind = "folder"
                });
            }
        }

        var tasks = new Task[workers];
        for (var i = 0; i < workers; i++)
        {
            tasks[i] = Task.Run(Walk, CancellationToken.None);
        }

        Task.WaitAll(tasks);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var item in found.OrderByDescending(i => i.Bytes).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase))
        {
            add(item);
        }
    }

    /// <summary>Keeps the status line readable for very deep paths.</summary>
    private static string TrimForStatus(string path)
    {
        const int max = 90;
        return path.Length <= max ? path : $"{path[..40]}…{path[(path.Length - (max - 41))..]}";
    }

    private sealed record Artifact(string TitleCs, string TitleEn, string ImpactCs, string ImpactEn, bool Checked);

    private static Artifact? Classify(DirectoryInfo directory)
    {
        var name = directory.Name.ToLowerInvariant();
        var parent = directory.Parent;

        switch (name)
        {
            case "node_modules" when parent is not null && File.Exists(System.IO.Path.Combine(parent.FullName, "package.json")):
                return new Artifact(
                    "node_modules",
                    "node_modules",
                    "Lokální závislosti projektu. Příští npm/pnpm/yarn install je znovu stáhne.",
                    "Project-local dependencies. The next npm/pnpm/yarn install downloads them again.",
                    false);

            case "target" when parent is not null && File.Exists(System.IO.Path.Combine(parent.FullName, "Cargo.toml")):
                return new Artifact(
                    "target (Rust)",
                    "target (Rust)",
                    "Zkompilované artefakty Rustu. Příští cargo build je znovu vytvoří.",
                    "Compiled Rust artifacts. The next cargo build recreates them.",
                    true);

            case "bin" or "obj" when parent is not null && HasProjectFile(parent):
                return new Artifact(
                    $"{name} (.NET)",
                    $"{name} (.NET)",
                    "Mezivýsledky a binárky buildu. Příští dotnet build je znovu vytvoří.",
                    "Build intermediates and binaries. The next dotnet build recreates them.",
                    true);

            case ".venv" or "venv" when File.Exists(System.IO.Path.Combine(directory.FullName, "pyvenv.cfg")):
                return new Artifact(
                    "Python virtualenv",
                    "Python virtualenv",
                    "Virtuální prostředí Pythonu. Znovu jej vytvoříš pomocí python -m venv a pip install.",
                    "Python virtual environment. Recreate it with python -m venv and pip install.",
                    false);

            case "__pycache__":
                return new Artifact(
                    "__pycache__",
                    "__pycache__",
                    "Předkompilované .pyc soubory. Python je při příštím spuštění znovu vytvoří.",
                    "Precompiled .pyc files. Python recreates them on the next run.",
                    true);

            default:
                return null;
        }
    }

    private static bool HasProjectFile(DirectoryInfo directory)
    {
        try
        {
            return directory.EnumerateFiles("*.csproj").Any() || directory.EnumerateFiles("*.fsproj").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
