namespace Valence.Core;

public static class RepoFiles
{
    /// <summary>Build output and dependency folders no probe should look inside.</summary>
    public static readonly IReadOnlySet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "packages", "dist", "build", "artifacts",
    };

    /// <summary>
    /// True when the folder (recursively, skipping hidden, build and symlinked folders)
    /// has at least one file with one of the given extensions (with leading dot).
    /// </summary>
    public static bool ContainsAny(string root, IEnumerable<string> extensions)
    {
        var wanted = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                if (Directory.EnumerateFiles(dir).Any(f => wanted.Contains(Path.GetExtension(f)))) return true;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith('.') || SkipDirs.Contains(name)) continue;
                    if (new DirectoryInfo(sub).LinkTarget != null) continue;
                    pending.Push(sub);
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // Unreadable folder: skip it.
            }
        }
        return false;
    }
}
