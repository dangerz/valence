using System.Diagnostics;
using TreeSitter;
using Valence.Core;
using Valence.Schema;
using Valence.Syntax;

namespace Valence.Probe.Python;

/// <summary>
/// Python through tree-sitter, in two passes. Pass 1 (<see cref="PythonExtractor"/>) parses each file
/// and records its nucleons, imports, bindings and name uses; trees are freed right away. Pass 2
/// (<see cref="PythonResolver"/>) resolves the names across the whole repo and emits the bonds and
/// member links; entry points (__main__ blocks, route/task/command decorators) come from pass 1.
/// Only reads the repo.
/// </summary>
internal sealed class PythonAnalyzer
{
    public const string Version = "0.3.0";

    private const string ParsePhase = "Parsing Python";
    private const string ResolvePhase = "Resolving Python names";
    private const long MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>Virtualenvs and caches: other people's code, not the repo's.</summary>
    private static readonly string[] SkipDirs = { "__pycache__", "venv", "site-packages", "node_modules" };

    private readonly string _root;
    private readonly AnalysisContext _context;
    private readonly ProbeOutput _output = new() { Probe = new ToolInfo { Name = "python", Version = Version } };
    private readonly List<string> _filesWithErrors = new();

    public PythonAnalyzer(string root, AnalysisContext context)
    {
        _root = root;
        _context = context;
    }

    public ProbeOutput Run()
    {
        var clock = Stopwatch.StartNew();
        var parsing = _context.Slice(0, 0.75);
        var resolving = _context.Slice(0.75, 0.25);
        parsing.Report(ParsePhase, 0, force: true);

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

        var paths = RepoFiles.Find(_root, PythonProbe.Extensions, SkipDirs).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var project = new PythonProject(_root, _output);
        var stats = new ExtractionStats();

        // Pass 1: parse and extract.
        using (var parser = new Parser(language))
        {
            for (var i = 0; i < paths.Count; i++)
            {
                _context.Cancellation.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(_root, paths[i]).Replace('\\', '/');
                parsing.Report(ParsePhase, i, paths.Count, relative);
                if (ReadSource(paths[i], relative) is not { } source) continue;

                using var tree = parser.Parse(source);
                if (tree == null)
                {
                    Warn($"could not parse {relative}");
                    continue;
                }
                if (tree.RootNode.HasError) _filesWithErrors.Add(relative);

                var dir = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "";
                var file = new PyFile(relative, project.ModuleName(relative), PythonProject.RepoPath(relative),
                    Path.GetFileName(relative) == "__init__.py", project.AtomFor(dir), CountLines(source));
                project.Files.Add(file);
                new PythonExtractor(project, file, stats).Extract(tree.RootNode);
            }
        }
        parsing.Report(ParsePhase, paths.Count, paths.Count);
        var parsedMs = clock.ElapsedMilliseconds;

        // Pass 2: resolve names into bonds.
        project.IndexModules();
        var resolver = new PythonResolver(project);
        resolver.Run(resolving, ResolvePhase);
        _output.Edges.AddRange(project.Edges);
        _output.MemberEdges.AddRange(project.MemberEdges);

        if (_filesWithErrors.Count > 0)
        {
            Warn($"{_filesWithErrors.Count} files have syntax errors (parsed around them), e.g. " +
                 string.Join(", ", _filesWithErrors.Take(5)));
        }
        resolver.MarkExternalImports();
        var (internalImports, externalImports) = resolver.CountImports();
        var byKind = project.Edges
            .GroupBy(e => e.Kind)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {g.Key}");
        var linksByKind = project.MemberEdges
            .GroupBy(e => e.Kind)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {g.Key}");
        var entriesByTrigger = _output.EntryPoints
            .GroupBy(e => e.Trigger)
            .Select(g => $"{g.Count()} {g.Key}");
        _context.Log($"python: {project.Files.Count} files, {project.AtomCount} folders, {project.ModuleCount} modules; " +
                     $"{stats.Classes} classes, {stats.Functions} functions, {stats.Methods} methods, {stats.Fields} fields " +
                     $"({stats.LocalClasses} classes inside functions folded in); {_filesWithErrors.Count} files with syntax errors");
        _context.Log($"python: imports {internalImports} inside the repo, {externalImports} outside; " +
                     $"{stats.Uses} name uses → {resolver.Bonded} bonded, {resolver.WithinClass} within their own class, " +
                     $"{resolver.Unresolved} outside the repo or unknown, {resolver.WithoutOwner} in top-level code of class-only files");
        _context.Log($"python: {project.Edges.Count} bonds ({string.Join(", ", byKind)}); " +
                     $"{project.MemberEdges.Count} member links ({string.Join(", ", linksByKind)}); " +
                     $"{_output.EntryPoints.Count} entry points ({string.Join(", ", entriesByTrigger)}); " +
                     $"parse {parsedMs} ms, resolve {clock.ElapsedMilliseconds - parsedMs} ms");
        return _output;
    }

    private string? ReadSource(string path, string relative)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
            {
                Warn($"skipped {relative}: larger than {MaxFileBytes / (1024 * 1024)} MB");
                return null;
            }
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Warn($"could not read {relative}: {e.Message}");
            return null;
        }
    }

    private static double CountLines(string source) => source.Length == 0 ? 0 : source.Count(c => c == '\n') + 1;

    private void Warn(string message)
    {
        _output.Diagnostics.Add(new DiagnosticInfo { Level = "warning", Message = message, Source = "python" });
        _context.Log("python: " + message);
    }
}
