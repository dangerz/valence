using System.Diagnostics;
using System.Text.RegularExpressions;
using TreeSitter;
using Valence.Core;
using Valence.Schema;
using Valence.Syntax;

namespace Valence.Probe.Python;

/// <summary>
/// Spike: proves tree-sitter runs in-process and is fast enough. Parses every .py file and emits
/// the bare structure (folders as atoms under top-level folders, classes and files with free functions
/// as nucleons, methods and functions as members) with no dependencies yet. Logs what it loaded,
/// counts and timing. Only reads the repo.
/// </summary>
internal sealed class PythonAnalyzer
{
    public const string Version = "0.0.1-spike";

    private const string Lang = "python";
    private const string Phase = "Parsing Python";
    private const long MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>Virtualenvs and caches: other people's code, not the repo's.</summary>
    private static readonly string[] SkipDirs = { "__pycache__", "venv", "site-packages", "node_modules" };

    // Definitions and imports, reported in document order.
    private const string DefinitionsQuery = @"
(class_definition) @class
(function_definition) @function
(import_statement) @import
(import_from_statement) @import
";

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly string _root;
    private readonly string _repoName;
    private readonly AnalysisContext _context;
    private readonly ProbeOutput _output = new() { Probe = new ToolInfo { Name = "python", Version = Version } };
    private readonly Dictionary<string, string> _atoms = new(StringComparer.Ordinal);
    private readonly HashSet<string> _molecules = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    private int _classes;
    private int _functions;
    private int _methods;
    private int _skippedNested;
    private int _imports;
    private readonly List<string> _filesWithErrors = new();

    public PythonAnalyzer(string root, AnalysisContext context)
    {
        _root = root;
        _repoName = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        _context = context;
    }

    public ProbeOutput Run()
    {
        var clock = Stopwatch.StartNew();
        _context.Report(Phase, 0, force: true);

        Language language;
        try
        {
            language = TreeSitterRuntime.Load("Python", _context.Log);
        }
        catch (Exception e)
        {
            _context.Log($"tree-sitter could not be loaded: {e.GetType().Name}: {e.Message}");
            _context.Log("looked in: " + string.Join(" | ", TreeSitterRuntime.SearchDirectories()));
            throw;
        }

        var files = RepoFiles.Find(_root, PythonProbe.Extensions, SkipDirs).OrderBy(f => f, StringComparer.Ordinal).ToList();
        _context.Log($"python: {files.Count} .py files");

        using var parser = new Parser(language);
        using var query = new Query(language, DefinitionsQuery);
        var parseMs = 0L;
        for (var i = 0; i < files.Count; i++)
        {
            _context.Cancellation.ThrowIfCancellationRequested();
            var relative = Relative(files[i]);
            _context.Report(Phase, i, files.Count, relative);

            string source;
            try
            {
                if (new FileInfo(files[i]).Length > MaxFileBytes)
                {
                    Warn($"skipped {relative}: larger than {MaxFileBytes / (1024 * 1024)} MB");
                    continue;
                }
                source = File.ReadAllText(files[i]);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Warn($"could not read {relative}: {e.Message}");
                continue;
            }

            var started = clock.ElapsedMilliseconds;
            using var tree = parser.Parse(source);
            parseMs += clock.ElapsedMilliseconds - started;
            if (tree == null)
            {
                Warn($"could not parse {relative}");
                continue;
            }
            if (tree.RootNode.HasError) _filesWithErrors.Add(relative);
            AddFile(relative, source, tree, query);
        }
        _context.Report(Phase, files.Count, files.Count);

        if (_filesWithErrors.Count > 0)
        {
            Warn($"{_filesWithErrors.Count} files have syntax errors (parsed around them), e.g. " +
                 string.Join(", ", _filesWithErrors.Take(5)));
        }
        _context.Log($"python: parsed {files.Count} files in {clock.ElapsedMilliseconds} ms ({parseMs} ms in tree-sitter); " +
                     $"{_atoms.Count} folders/packages, {_classes} classes, {_functions} functions, {_methods} methods, " +
                     $"{_imports} imports, {_skippedNested} nested definitions skipped, {_filesWithErrors.Count} files with syntax errors");
        return _output;
    }

    private void AddFile(string relative, string source, Tree tree, Query query)
    {
        var dir = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "";
        var atom = AtomFor(dir);
        var dotted = DottedName(relative);
        var fileName = Path.GetFileName(relative);

        // Class definitions by position, so methods can find their class.
        var classes = new Dictionary<int, NodeInfo>();
        NodeInfo? fileNode = null;

        using var cursor = query.Execute(tree.RootNode);
        foreach (var capture in cursor.Captures)
        {
            var node = capture.Node;
            switch (capture.Name)
            {
                case "import":
                    _imports++;
                    break;

                case "class":
                {
                    var name = node.GetChildForField("name")?.Text;
                    if (string.IsNullOrEmpty(name)) break;
                    var owner = Enclosing(node);
                    NodeInfo? container = null;
                    if (owner is { Type: "class_definition" } && !classes.TryGetValue(owner.StartIndex, out container)) break;
                    if (owner is { Type: "function_definition" })
                    {
                        _skippedNested++;
                        break;
                    }

                    var fullName = (container?.FullName ?? dotted) + "." + name;
                    var info = new NodeInfo
                    {
                        Id = UniqueId("py:" + fullName),
                        Name = name,
                        FullName = fullName,
                        Kind = NodeKinds.Class,
                        Module = atom,
                        Visibility = IsPrivate(name) ? "private" : "public",
                        Language = Lang,
                        File = relative,
                        Line = node.StartPosition.Row + 1,
                        Files = { relative },
                        Container = container?.Id,
                        Metrics = { [MetricKeys.Loc] = Lines(node), [MetricKeys.Methods] = 0 },
                    };
                    if (node.GetChildForField("superclasses") is { } bases)
                    {
                        info.Attributes["bases"] = Collapse(bases.Text.Trim('(', ')'));
                    }
                    classes[node.StartIndex] = info;
                    _output.Nodes.Add(info);
                    _classes++;
                    break;
                }

                case "function":
                {
                    var name = node.GetChildForField("name")?.Text;
                    if (string.IsNullOrEmpty(name)) break;
                    var owner = Enclosing(node);
                    NodeInfo? target;
                    if (owner is { Type: "class_definition" })
                    {
                        if (!classes.TryGetValue(owner.StartIndex, out target)) break;
                        _methods++;
                    }
                    else if (owner is { Type: "function_definition" })
                    {
                        _skippedNested++;
                        break;
                    }
                    else
                    {
                        // Free function: lives on a node for the file (kind "module").
                        fileNode ??= AddFileNode(relative, dotted, fileName, atom, source);
                        target = fileNode;
                        _functions++;
                    }
                    target.Members.Add(Member(node, name, target, relative, isMethod: owner != null));
                    target.Metrics[MetricKeys.Methods] = target.Metrics.GetValueOrDefault(MetricKeys.Methods) + 1;
                    break;
                }
            }
        }

        // Scripts with neither classes nor functions still show up; empty __init__.py files don't.
        if (fileNode == null && classes.Count == 0 && fileName != "__init__.py")
        {
            AddFileNode(relative, dotted, fileName, atom, source);
        }
    }

    private NodeInfo AddFileNode(string relative, string dotted, string fileName, string atom, string source)
    {
        var node = new NodeInfo
        {
            Id = UniqueId("py:" + dotted),
            Name = fileName,
            FullName = dotted,
            Kind = NodeKinds.Module,
            Module = atom,
            Visibility = fileName.StartsWith('_') && fileName != "__init__.py" ? "private" : "public",
            Language = Lang,
            File = relative,
            Line = 1,
            Files = { relative },
            Metrics = { [MetricKeys.Loc] = CountLines(source), [MetricKeys.Methods] = 0 },
        };
        _output.Nodes.Add(node);
        return node;
    }

    private static MemberInfo Member(Node function, string name, NodeInfo owner, string relative, bool isMethod)
    {
        var decorators = function.Parent is { Type: "decorated_definition" } decorated
            ? decorated.NamedChildren.Where(c => c.Type == "decorator").Select(c => c.Text.TrimStart('@').Trim()).ToList()
            : new List<string>();

        var kind = !isMethod ? MemberKinds.Function
            : name == "__init__" ? MemberKinds.Constructor
            : decorators.Any(d => d == "property" || d.EndsWith(".setter") || d.EndsWith(".getter")) ? MemberKinds.Property
            : MemberKinds.Method;

        var parameters = function.GetChildForField("parameters")?.Text ?? "()";
        var signature = Collapse($"{(function.Children.Any(c => c.Type == "async") ? "async " : "")}def {name}{parameters}");
        if (signature.Length > 160) signature = signature[..157] + "…";

        var id = owner.Id + "." + name;
        if (owner.Members.Any(m => m.Id == id)) id += "#" + (function.StartPosition.Row + 1);

        return new MemberInfo
        {
            Id = id,
            Name = name,
            Kind = kind,
            Visibility = IsPrivate(name) ? "private" : "public",
            IsStatic = decorators.Contains("staticmethod") || decorators.Contains("classmethod"),
            Signature = signature,
            File = relative,
            Line = function.StartPosition.Row + 1,
        };
    }

    /// <summary>The nearest enclosing class or function definition, or null at module level.</summary>
    private static Node? Enclosing(Node node)
    {
        for (var p = node.Parent; p != null; p = p.Parent)
        {
            if (p.Type is "class_definition" or "function_definition") return p;
            if (p.Type == "module") return null;
        }
        return null;
    }

    /// <summary>One atom per folder of .py files, grouped into molecules by top-level folder.</summary>
    private string AtomFor(string dir)
    {
        if (_atoms.TryGetValue(dir, out var id)) return id;

        var top = dir.Length == 0 ? "" : dir.Split('/')[0];
        var molecule = "py:project:" + (top.Length == 0 ? "." : top);
        if (_molecules.Add(molecule))
        {
            _output.Modules.Add(new ModuleInfo
            {
                Id = molecule,
                Name = top.Length == 0 ? _repoName : top,
                Kind = ModuleKinds.Project,
                Language = Lang,
                Path = top.Length == 0 ? "." : top,
            });
        }

        id = "py:package:" + (dir.Length == 0 ? "." : dir);
        _output.Modules.Add(new ModuleInfo
        {
            Id = id,
            Name = dir.Length == 0 ? _repoName : dir.Replace('/', '.'),
            Kind = File.Exists(Path.Combine(_root, dir, "__init__.py")) ? "package" : ModuleKinds.Folder,
            Parent = molecule,
            Language = Lang,
            Path = dir.Length == 0 ? "." : dir,
        });
        _atoms[dir] = id;
        return id;
    }

    /// <summary>a/b/c.py → a.b.c; a/b/__init__.py → a.b.</summary>
    private string DottedName(string relative)
    {
        var withoutExtension = relative[..^".py".Length].Replace('/', '.');
        if (withoutExtension == "__init__") return _repoName;
        return withoutExtension.EndsWith(".__init__") ? withoutExtension[..^".__init__".Length] : withoutExtension;
    }

    private string UniqueId(string id)
    {
        if (_ids.Add(id)) return id;
        for (var n = 2; ; n++)
        {
            if (_ids.Add($"{id}#{n}")) return $"{id}#{n}";
        }
    }

    /// <summary>_name is private by convention; __dunder__ names are public protocol.</summary>
    private static bool IsPrivate(string name) =>
        name.StartsWith('_') && !(name.StartsWith("__") && name.EndsWith("__"));

    private static double Lines(Node node) => node.EndPosition.Row - node.StartPosition.Row + 1;

    private static double CountLines(string source) => source.Length == 0 ? 0 : source.Count(c => c == '\n') + 1;

    private static string Collapse(string text) => Whitespace.Replace(text, " ").Trim();

    private string Relative(string path) => Path.GetRelativePath(_root, path).Replace('\\', '/');

    private void Warn(string message)
    {
        _output.Diagnostics.Add(new DiagnosticInfo { Level = "warning", Message = message, Source = "python" });
        _context.Log("python: " + message);
    }
}
