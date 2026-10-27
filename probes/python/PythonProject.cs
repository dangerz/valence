using Valence.Schema;

namespace Valence.Probe.Python;

/// <summary>
/// State shared by both passes: the probe output being built, module naming (Python import names
/// come from __init__.py packages, not folders), the atoms, and the module index used to resolve imports.
/// </summary>
internal sealed class PythonProject
{
    public const string Lang = "python";

    private readonly Dictionary<string, bool> _hasInit = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _atoms = new(StringComparer.Ordinal);
    private readonly HashSet<string> _molecules = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string, string), EdgeInfo> _edges = new();
    private readonly Dictionary<string, List<PyModule>> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PyModule> _byRepoPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PyModule>> _suffixCache = new(StringComparer.Ordinal);

    public PythonProject(string root, ProbeOutput output)
    {
        Root = root;
        RepoName = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(root));
        Output = output;
    }

    public string Root { get; }

    public string RepoName { get; }

    public ProbeOutput Output { get; }

    public List<PyFile> Files { get; } = new();

    public int AtomCount => _atoms.Count;

    public int ModuleCount => _byRepoPath.Count;

    public IReadOnlyCollection<EdgeInfo> Edges => _edges.Values;

    // ------------------------------------------------------------------ naming

    /// <summary>
    /// The name a file is imported as: from the top-most folder of an unbroken chain of __init__.py
    /// packages (src/pkg/sub/x.py with pkg and sub packages → pkg.sub.x).
    /// </summary>
    public string ModuleName(string relative)
    {
        var parts = relative[..^".py".Length].Split('/');
        var dirs = parts.Length - 1;
        var start = dirs;
        while (start > 0 && HasInit(string.Join('/', parts, 0, start))) start--;
        var name = parts.Skip(start).ToList();
        if (name[^1] == "__init__") name.RemoveAt(name.Count - 1);
        return name.Count == 0 ? RepoName : string.Join('.', name);
    }

    /// <summary>a/b/c.py → a.b.c; a/b/__init__.py → a.b; the root __init__.py → "".</summary>
    public static string RepoPath(string relative)
    {
        var path = relative[..^".py".Length].Replace('/', '.');
        if (path == "__init__") return "";
        return path.EndsWith(".__init__", StringComparison.Ordinal) ? path[..^".__init__".Length] : path;
    }

    private bool HasInit(string dir)
    {
        if (!_hasInit.TryGetValue(dir, out var has))
        {
            has = File.Exists(System.IO.Path.Combine(Root, dir, "__init__.py"));
            _hasInit[dir] = has;
        }
        return has;
    }

    public string UniqueId(string id)
    {
        if (_ids.Add(id)) return id;
        for (var n = 2; ; n++)
        {
            if (_ids.Add($"{id}#{n}")) return $"{id}#{n}";
        }
    }

    /// <summary>One atom per folder of .py files, grouped into molecules by top-level folder.</summary>
    public string AtomFor(string dir)
    {
        if (_atoms.TryGetValue(dir, out var id)) return id;

        var top = dir.Length == 0 ? "" : dir.Split('/')[0];
        var molecule = "py:project:" + (top.Length == 0 ? "." : top);
        if (_molecules.Add(molecule))
        {
            Output.Modules.Add(new ModuleInfo
            {
                Id = molecule,
                Name = top.Length == 0 ? RepoName : top,
                Kind = ModuleKinds.Project,
                Language = Lang,
                Path = top.Length == 0 ? "." : top,
            });
        }

        id = "py:package:" + (dir.Length == 0 ? "." : dir);
        Output.Modules.Add(new ModuleInfo
        {
            Id = id,
            Name = dir.Length == 0 ? RepoName : dir.Replace('/', '.'),
            Kind = HasInit(dir) ? "package" : ModuleKinds.Folder,
            Parent = molecule,
            Language = Lang,
            Path = dir.Length == 0 ? "." : dir,
        });
        _atoms[dir] = id;
        return id;
    }

    /// <summary>The file's own nucleon (kind "module"), created the first time something needs it.</summary>
    public NodeInfo FileNode(PyFile file)
    {
        if (file.Node != null) return file.Node;
        var fileName = System.IO.Path.GetFileName(file.Path);
        file.Node = new NodeInfo
        {
            Id = UniqueId("py:" + file.Module),
            Name = fileName,
            FullName = file.Module,
            Kind = NodeKinds.Module,
            Module = file.Atom,
            Visibility = fileName.StartsWith('_') && !file.IsPackage ? "private" : "public",
            Language = Lang,
            File = file.Path,
            Line = 1,
            Files = { file.Path },
            Metrics = { [MetricKeys.Loc] = file.Lines, [MetricKeys.Methods] = 0 },
        };
        Output.Nodes.Add(file.Node);
        return file.Node;
    }

    // ------------------------------------------------------------------ edges

    /// <summary>One edge per (source, target, kind), weighted; keeps the strongest confidence.</summary>
    public void AddEdge(NodeInfo source, NodeInfo target, string kind, string? confidence)
    {
        if (ReferenceEquals(source, target)) return;
        var key = (source.Id, target.Id, kind);
        if (_edges.TryGetValue(key, out var edge))
        {
            edge.Weight++;
            edge.Confidence = Confidence.Strongest(edge.Confidence, confidence);
        }
        else
        {
            _edges[key] = new EdgeInfo { Source = source.Id, Target = target.Id, Kind = kind, Confidence = confidence };
        }
    }

    // ------------------------------------------------------------------ module index

    /// <summary>Indexes every file and every package folder above it. Call after pass 1.</summary>
    public void IndexModules()
    {
        foreach (var file in Files) Add(new PyModule(file.Module, file.RepoPath, file));

        // Folders without __init__.py (namespace packages, plain folders) are importable too.
        foreach (var file in Files)
        {
            // a.b.c → folders a and a.b (for a package a.b, a.b is the __init__.py file itself).
            var parts = file.RepoPath.Split('.');
            for (var i = 1; i < parts.Length; i++)
            {
                var repoPath = string.Join('.', parts, 0, i);
                if (!_byRepoPath.ContainsKey(repoPath)) Add(new PyModule(parts[i - 1], repoPath, null));
            }
        }

        void Add(PyModule module)
        {
            if (!_byRepoPath.TryAdd(module.RepoPath, module)) return;
            if (!_byName.TryGetValue(module.Name, out var list)) _byName[module.Name] = list = new List<PyModule>();
            list.Add(module);
        }
    }

    public PyModule? ByRepoPath(string repoPath) => _byRepoPath.GetValueOrDefault(repoPath);

    /// <summary>
    /// An absolute import: by import name, then by repo path, then by repo-path suffix (for repos whose
    /// import roots don't follow __init__.py). Approximate matches are reported so bonds can say "likely".
    /// </summary>
    public (PyModule? Module, bool Approximate) FindModule(string name, PyFile from)
    {
        if (_byName.TryGetValue(name, out var exact)) return (Nearest(exact, from), false);
        if (_byRepoPath.TryGetValue(name, out var byPath)) return (byPath, true);

        if (!_suffixCache.TryGetValue(name, out var matches))
        {
            var suffix = "." + name;
            matches = _byRepoPath.Values.Where(m => m.RepoPath.EndsWith(suffix, StringComparison.Ordinal)).ToList();
            _suffixCache[name] = matches;
        }
        return matches.Count == 0 ? (null, false) : (Nearest(matches, from), true);
    }

    /// <summary>The candidate sharing the longest repo-path prefix with the importing file.</summary>
    private static PyModule Nearest(List<PyModule> candidates, PyFile from)
    {
        if (candidates.Count == 1) return candidates[0];
        return candidates.OrderByDescending(m => SharedPrefix(m.RepoPath, from.RepoPath)).First();
    }

    private static int SharedPrefix(string a, string b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && a[n] == b[n]) n++;
        return n;
    }
}
