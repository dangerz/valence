namespace Valence.Probe.CSharp;

/// <summary>Read-only file discovery that skips build output, VCS and tool folders.</summary>
internal static class RepoScanner
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "packages", "dist", "build", "artifacts",
    };

    public static IEnumerable<string> Files(string root, string extension)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] files;
            string[] subdirs;
            try
            {
                files = Directory.GetFiles(dir, "*" + extension);
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                // The pattern also matches longer extensions on some platforms (*.cs → *.csx).
                if (file.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) yield return file;
            }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                // Hidden folders cover .git, .godot, .vs, .idea, .mono, etc.
                if (name.StartsWith('.') || SkipDirs.Contains(name)) continue;
                // Don't follow symlinks: avoids loops and wandering outside the repo.
                if (new DirectoryInfo(sub).LinkTarget != null) continue;
                pending.Push(sub);
            }
        }
    }

    public static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    public static bool IsUnder(string path, string dir)
    {
        var d = dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
        return path.StartsWith(d, StringComparison.Ordinal);
    }
}
