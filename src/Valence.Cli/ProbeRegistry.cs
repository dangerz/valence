using Valence.Schema;

namespace Valence.Cli;

internal sealed record DiscoveredProbe(ProbeManifest Manifest, string Directory);

/// <summary>Finds probe.json manifests. A probe directory holds probe.json plus whatever it needs to run.</summary>
internal static class ProbeRegistry
{
    public const string ManifestFileName = "probe.json";
    public const string EnvironmentVariable = "VALENCE_PROBES";

    /// <summary>Search order: explicit --probes dirs, $VALENCE_PROBES, then probes/ next to the CLI.</summary>
    public static IEnumerable<string> SearchPaths(IEnumerable<string> explicitDirs)
    {
        foreach (var dir in explicitDirs) yield return Path.GetFullPath(dir);

        var fromEnv = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            foreach (var dir in fromEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                yield return Path.GetFullPath(dir);
            }
        }

        yield return Path.Combine(AppContext.BaseDirectory, "probes");
    }

    public static List<DiscoveredProbe> Discover(IEnumerable<string> searchPaths, Action<string> warn)
    {
        var found = new List<DiscoveredProbe>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in searchPaths)
        {
            if (!Directory.Exists(dir)) continue;

            // A search path may itself be a probe directory, or a folder of probe directories.
            var candidates = File.Exists(Path.Combine(dir, ManifestFileName))
                ? new[] { dir }
                : Directory.GetDirectories(dir);

            foreach (var candidate in candidates.OrderBy(c => c, StringComparer.Ordinal))
            {
                var manifestPath = Path.Combine(candidate, ManifestFileName);
                if (!File.Exists(manifestPath)) continue;
                try
                {
                    var manifest = ValenceJson.Read<ProbeManifest>(manifestPath);
                    if (manifest.Schema != SchemaIds.ProbeManifest)
                    {
                        warn($"skipping {manifestPath}: unsupported schema '{manifest.Schema}'");
                        continue;
                    }
                    if (manifest.Command.Count == 0)
                    {
                        warn($"skipping {manifestPath}: empty command");
                        continue;
                    }
                    // Earlier search paths win, so a --probes dir can override a bundled probe.
                    if (seenNames.Add(manifest.Name)) found.Add(new DiscoveredProbe(manifest, candidate));
                }
                catch (Exception e)
                {
                    warn($"skipping {manifestPath}: {e.Message}");
                }
            }
        }
        return found;
    }
}
