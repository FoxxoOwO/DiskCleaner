using System.Globalization;
using DiskCleaner.Models;
using DiskCleaner.Services;

// Small dependency-free check runner: no test framework, no NuGet restore. Exit code 1 on failure.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var failures = 0;

void Check(bool condition, string message)
{
    if (condition)
    {
        return;
    }

    failures++;
    Console.WriteLine($"FAIL {message}");
}

void Equal<T>(T expected, T actual, string message) =>
    Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected '{expected}', got '{actual}'");

// ----- Format -------------------------------------------------------------
Equal("0 B", Format.Bytes(0), "zero bytes");
Equal("1023 B", Format.Bytes(1023), "bytes below 1 KB");
Equal("1 KB", Format.Bytes(1024), "1 KB boundary");
Equal("1.0 MB", Format.Bytes(1024 * 1024), "1 MB boundary");
Equal("1.50 GB", Format.Bytes(3L * 1024 * 1024 * 1024 / 2), "GB keeps two decimals");

// ----- Docker output parsers ---------------------------------------------
Equal(1288490188L, DockerCliService.ParseReclaimable("1.2GB (20%)"), "reclaimable with share");
Equal(536870912L, DockerCliService.ParseReclaimable("512MB"), "reclaimable MB");
Equal(536870912L, DockerCliService.ParseReclaimable("512 MB"), "reclaimable with space");
Equal(12595L, DockerCliService.ParseReclaimable("12.3kB"), "reclaimable kB");
Equal(0L, DockerCliService.ParseReclaimable("0B"), "reclaimable zero");
Equal(0L, DockerCliService.ParseReclaimable(string.Empty), "reclaimable empty");
Equal(0L, DockerCliService.ParseReclaimable("garbage"), "reclaimable garbage");
Equal(10737418240L, DockerCliService.ParseReclaimable("10GiB"), "reclaimable GiB");
Equal(1610612736L, DockerCliService.ParseReclaimed(new[] { "TYPE SIZE", "Total reclaimed space: 1.5GB" }), "prune total");
Equal(0L, DockerCliService.ParseReclaimed(Array.Empty<string>()), "prune total empty");

// ----- Localization -------------------------------------------------------
Localization.Language = AppLanguage.Czech;
Equal("Ano", Localization.Pick("Ano", "Yes"), "czech pick");
Localization.Language = AppLanguage.English;
Equal("Yes", Localization.Pick("Ano", "Yes"), "english pick");
Localization.Language = AppLanguage.Czech;

// ----- rules.json ---------------------------------------------------------
// The scanner reads Assets/rules.json next to the executable, exactly like the app does.
var scanner = new DiskScannerService(new DockerCliService());
var definitions = scanner.Definitions;

Equal(6, definitions.Count, "category count");
Check(definitions.Select(d => d.Id).Distinct().Count() == definitions.Count, "category ids are unique");

var expectedIds = new[]
{
    "dev-cache", "build-artifacts", "app-cache", "docker", "installers-logs", "temp"
};
foreach (var id in expectedIds)
{
    Check(definitions.Any(d => d.Id == id), $"category '{id}' is missing");
}

foreach (var definition in definitions)
{
    Check(!string.IsNullOrWhiteSpace(definition.NameCs), $"{definition.Id}: empty nameCs");
    Check(!string.IsNullOrWhiteSpace(definition.NameEn), $"{definition.Id}: empty nameEn");
    Check(!string.IsNullOrWhiteSpace(definition.DescriptionCs), $"{definition.Id}: empty descriptionCs");
    Check(!string.IsNullOrWhiteSpace(definition.DescriptionEn), $"{definition.Id}: empty descriptionEn");
    Check(!string.IsNullOrWhiteSpace(definition.Glyph), $"{definition.Id}: empty glyph");
    Check(definition.Dynamic == (definition.Id is "build-artifacts" or "docker"), $"{definition.Id}: dynamic flag");
}

var rules = definitions.SelectMany(d => d.Rules).ToList();
Check(rules.Count >= 40, $"rule count looks low: {rules.Count}");
Check(definitions.Sum(d => d.Rules.Count) == rules.Count, "rule totals match");

foreach (var definition in definitions)
{
    var duplicates = definition.Rules
        .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Count() > 1)
        .Select(g => g.Key)
        .ToList();
    Check(duplicates.Count == 0, $"{definition.Id}: duplicate rule paths {string.Join(", ", duplicates)}");
}

foreach (var rule in rules)
{
    Check(!string.IsNullOrWhiteSpace(rule.Path), "rule with empty path");
    Check(!string.IsNullOrWhiteSpace(rule.TitleCs), $"{rule.Path}: empty titleCs");
    Check(!string.IsNullOrWhiteSpace(rule.TitleEn), $"{rule.Path}: empty titleEn");
    Check(!string.IsNullOrWhiteSpace(rule.ImpactCs), $"{rule.Path}: empty impactCs");
    Check(!string.IsNullOrWhiteSpace(rule.ImpactEn), $"{rule.Path}: empty impactEn");
    Check(rule.MinAgeHours >= 0, $"{rule.Path}: negative MinAgeHours");
    Check(rule.ProcessNames.All(n => !string.IsNullOrWhiteSpace(n)), $"{rule.Path}: blank process name");
}

// Environment variables must be known names, otherwise the rule silently never matches a drive.
var allowedVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "LOCALAPPDATA", "APPDATA", "USERPROFILE", "TEMP", "WINDIR", "PROGRAMDATA", "PROGRAMFILES",
    "PROGRAMFILES(X86)", "SYSTEMDRIVE", "SYSTEMROOT", "PUBLIC"
};
foreach (var rule in rules)
{
    foreach (var variable in rule.Path.Split('%').Where((_, index) => index % 2 == 1))
    {
        Check(allowedVariables.Contains(variable), $"{rule.Path}: unknown environment variable %{variable}%");
    }
}

// The 24 hour Temp safety rule and the recycle bin rule are part of the contract.
Check(
    definitions.SelectMany(d => d.Rules).Any(r => r.Path.Contains("%TEMP%", StringComparison.OrdinalIgnoreCase) && r.MinAgeHours == 24),
    "%TEMP% rule must carry the 24 hour cutoff");
Check(
    definitions.SelectMany(d => d.Rules).Any(r => r.Path.Contains("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)),
    "recycle bin rule is missing");

// ----- dry run and the 24 hour cutoff ------------------------------------
var sandbox = Path.Combine(Path.GetTempPath(), "dc-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);
var stale = Path.Combine(sandbox, "stale.tmp");
var fresh = Path.Combine(sandbox, "fresh.tmp");
File.WriteAllText(stale, new string('x', 5000));
File.WriteAllText(fresh, new string('x', 1000));
File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-3));

var cleaner = new CleanerExecutionService(new DockerCliService());
var quiet = new Progress<string>(_ => { });
var sandboxRoot = Path.GetPathRoot(sandbox) ?? "C:\\";

var dryRun = await cleaner.CleanAsync(
    sandboxRoot,
    new[] { new CleanTarget(sandbox, "folder", 24, 6000) },
    dryRun: true,
    useRecycleBin: false,
    quiet,
    CancellationToken.None);

Equal(5000L, dryRun.FreedBytes, "dry run honours the 24 h cutoff");
Equal(1, dryRun.DeletedItems, "dry run counts only the eligible file");
Check(File.Exists(stale) && File.Exists(fresh), "dry run must not delete anything");

// A real run must remove the folder completely.
await cleaner.CleanAsync(
    sandboxRoot,
    new[] { new CleanTarget(sandbox, "folder", 0, 6000) },
    dryRun: false,
    useRecycleBin: false,
    quiet,
    CancellationToken.None);

Check(!Directory.Exists(sandbox), "a real clean removes the folder");

// ----- process badge snapshot --------------------------------------------
var processes = RunningProcesses.Capture();
Check(!processes.IsRunning(new[] { "definitely-not-a-real-process-xyz" }), "unknown process must not match");

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;
