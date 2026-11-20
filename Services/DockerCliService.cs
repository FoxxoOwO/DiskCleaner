using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DiskCleaner.Models;

namespace DiskCleaner.Services;

/// <summary>Talks to the Docker CLI. Every call fails soft: no CLI or no daemon simply yields no items.</summary>
public sealed class DockerCliService
{
    public const string ImagesKind = "docker-images";
    public const string BuildCacheKind = "docker-buildcache";
    public const string ContainersKind = "docker-containers";

    public async Task<IReadOnlyList<ScanItem>> GetItemsAsync(CancellationToken cancellationToken)
    {
        var output = await RunAsync("system df --format \"{{json .}}\"", cancellationToken).ConfigureAwait(false);
        var items = new List<ScanItem>();
        if (output is null)
        {
            return items;
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = GetString(root, "Type");
                var bytes = ParseReclaimable(GetString(root, "Reclaimable"));
                var kind = type switch
                {
                    "Images" => ImagesKind,
                    "Build Cache" => BuildCacheKind,
                    "Containers" => ContainersKind,
                    _ => string.Empty
                };
                if (bytes <= 0 || kind.Length == 0)
                {
                    continue;
                }

                var (impactCs, impactEn) = kind switch
                {
                    ImagesKind => ("Odstraní image, které nepoužívá žádný kontejner. Znovu je stáhne až příští docker pull/build.",
                        "Removes images not used by any container. The next docker pull/build downloads them again."),
                    BuildCacheKind => ("Odstraní build cache. Příští docker build bude pomalejší.",
                        "Removes build cache. The next docker build is slower."),
                    _ => ("Odstraní zastavené kontejnery. Data ve volumes zůstanou.",
                        "Removes stopped containers. Data in volumes is kept.")
                };

                items.Add(new ScanItem
                {
                    TitleCs = $"Docker – {type}",
                    TitleEn = $"Docker – {type}",
                    ImpactCs = impactCs,
                    ImpactEn = impactEn,
                    Path = type,
                    Bytes = bytes,
                    Kind = kind,
                    ProcessNames = new[] { "com.docker.backend", "Docker Desktop" }
                });
            }
            catch (JsonException)
            {
                // Non-JSON warning lines from the Docker CLI carry no reclaimable size.
            }
        }

        return items;
    }

    /// <summary>Runs the prune command for a kind and returns its raw output lines.</summary>
    public async Task<IReadOnlyList<string>> PruneAsync(string kind, CancellationToken cancellationToken)
    {
        var args = kind switch
        {
            ImagesKind => "image prune -a -f",
            BuildCacheKind => "builder prune -f",
            ContainersKind => "container prune -f",
            _ => string.Empty
        };
        if (args.Length == 0)
        {
            return Array.Empty<string>();
        }

        var output = await RunAsync(args, cancellationToken).ConfigureAwait(false);
        return output is null
            ? Array.Empty<string>()
            : output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    /// <summary>Parses "1.2GB (20%)" / "512MB" / "0B" into bytes.</summary>
    public static long ParseReclaimable(string reclaimable)
    {
        if (string.IsNullOrWhiteSpace(reclaimable))
        {
            return 0;
        }

        var cut = reclaimable.IndexOf('(');
        var size = (cut > 0 ? reclaimable[..cut] : reclaimable).Trim();
        var split = 0;
        while (split < size.Length && (char.IsDigit(size[split]) || size[split] == '.' || size[split] == ','))
        {
            split++;
        }

        if (!double.TryParse(size[..split].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return 0;
        }

        var factor = size[split..].Trim().ToUpperInvariant() switch
        {
            "B" or "" => 1L,
            "KB" or "KIB" => 1024L,
            "MB" or "MIB" => 1024L * 1024,
            "GB" or "GIB" => 1024L * 1024 * 1024,
            "TB" or "TIB" => 1024L * 1024 * 1024 * 1024,
            _ => 0L
        };

        return factor == 0 ? 0 : (long)(number * factor);
    }

    /// <summary>Reads the "Total reclaimed space" line that <c>docker … prune</c> prints last.</summary>
    public static long ParseReclaimed(IReadOnlyList<string> output)
    {
        const string marker = "Total reclaimed space:";
        foreach (var line in output)
        {
            var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return ParseReclaimable(line[(index + marker.Length)..]);
            }
        }

        return 0;
    }

    private static async Task<string?> RunAsync(string args, CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo("docker", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));

            // Both pipes must be drained concurrently, otherwise a chatty stderr deadlocks the read.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            var stdout = await stdoutTask.ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? stdout : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException
                                       or InvalidOperationException or IOException)
        {
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(true);
                }
            }
            catch (Exception killEx) when (killEx is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone; nothing to clean up.
            }

            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }
}
