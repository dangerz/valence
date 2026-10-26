using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Valence.Schema;

namespace Valence.Probe.CSharp;

/// <summary>
/// Builds one Roslyn compilation per project (wired together by ProjectReference) and extracts
/// type-level nodes and dependency edges. Edges only point at types declared inside the repo;
/// external dependencies (BCL, Godot, NuGet) are recorded as node attributes instead.
/// </summary>
internal sealed class CSharpAnalyzer
{
    private const string Prefix = "cs:";
    private const string Language = "csharp";

    private static readonly SymbolDisplayFormat IdFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    /// <summary>Same set Microsoft.NET.Sdk generates for ImplicitUsings.</summary>
    private const string ImplicitUsingsSource = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;

    private readonly string _root;
    private readonly Action<string> _log;
    private readonly Dictionary<string, NodeInfo> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModuleInfo> _modules = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Source, string Target, string Kind), int> _edges = new();
    private readonly HashSet<string> _structuralDone = new(StringComparer.Ordinal);
    private readonly Dictionary<SyntaxTree, SemanticModel> _models = new();
    private readonly List<DiagnosticInfo> _diagnostics = new();

    public CSharpAnalyzer(string root, Action<string> log)
    {
        _root = root;
        _log = log;
    }

    public static string Version =>
        typeof(CSharpAnalyzer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public ProbeOutput Run()
    {
        var sources = RepoScanner.Files(_root, ".cs").ToList();
        var projects = ProjectDiscovery.Discover(_root, Warn);
        ProjectDiscovery.AssignFiles(_root, projects, sources, out var loose);
        if (loose != null) projects.Add(loose);
        projects.RemoveAll(p => p.SourceFiles.Count == 0);
        _log($"{sources.Count} C# files in {projects.Count} project(s)");

        var compilations = BuildCompilations(projects);

        // Pass 1 declares every type so pass 2 can tell repo types from external ones.
        foreach (var (project, compilation) in compilations) DeclareTypes(project, compilation);
        foreach (var (_, compilation) in compilations) CollectEdges(compilation);

        return new ProbeOutput
        {
            Probe = new ToolInfo { Name = "csharp", Version = Version },
            Modules = _modules.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToList(),
            Nodes = _nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal).ToList(),
            Edges = _edges
                .Select(kv => new EdgeInfo { Source = kv.Key.Source, Target = kv.Key.Target, Kind = kv.Key.Kind, Weight = kv.Value })
                .OrderBy(e => e.Source, StringComparer.Ordinal)
                .ThenBy(e => e.Target, StringComparer.Ordinal)
                .ThenBy(e => e.Kind, StringComparer.Ordinal)
                .ToList(),
            Diagnostics = _diagnostics,
        };
    }

    private void Warn(string message)
    {
        _log(message);
        _diagnostics.Add(new DiagnosticInfo { Level = "warning", Message = message, Source = "csharp" });
    }

    // ---------------------------------------------------------------- compilations

    private List<(ProjectInfo Project, CSharpCompilation Compilation)> BuildCompilations(List<ProjectInfo> projects)
    {
        var platform = ProjectDiscovery.PlatformReferences().ToList();
        var byFile = projects
            .Where(p => p.ProjectFile != null)
            .ToDictionary(p => p.ProjectFile!, StringComparer.Ordinal);
        var built = new Dictionary<ProjectInfo, CSharpCompilation>();
        var visiting = new HashSet<ProjectInfo>();
        var order = new List<(ProjectInfo, CSharpCompilation)>();

        CSharpCompilation? Build(ProjectInfo project)
        {
            if (built.TryGetValue(project, out var existing)) return existing;
            if (!visiting.Add(project))
            {
                Warn($"project reference cycle through {project.Name}");
                return null;
            }

            var references = new List<MetadataReference>(platform);
            foreach (var path in project.MetadataReferencePaths)
            {
                try
                {
                    references.Add(MetadataReference.CreateFromFile(path));
                }
                catch (Exception e) when (e is IOException or BadImageFormatException)
                {
                    Warn($"skipping reference {path}: {e.Message}");
                }
            }
            foreach (var referencePath in project.ProjectReferences)
            {
                if (byFile.TryGetValue(referencePath, out var dependency) && Build(dependency) is { } dependencyCompilation)
                {
                    references.Add(dependencyCompilation.ToMetadataReference());
                }
            }

            var parseOptions = CSharpParseOptions.Default
                .WithLanguageVersion(LanguageVersion.Preview)
                .WithDocumentationMode(DocumentationMode.None)
                .WithPreprocessorSymbols(project.DefineConstants.Concat(new[] { "DEBUG", "TRACE" }).Distinct());

            var trees = project.SourceFiles
                .AsParallel().AsOrdered()
                .Select(file => Parse(file, parseOptions))
                .Where(tree => tree != null)
                .Cast<SyntaxTree>()
                .ToList();
            if (project.ImplicitUsings)
            {
                // Not under _root, so the declare/edge passes skip it.
                trees.Add(CSharpSyntaxTree.ParseText(ImplicitUsingsSource, parseOptions, "<implicit-usings>.cs"));
            }

            var compilation = CSharpCompilation.Create(
                project.Name,
                trees,
                references,
                new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    allowUnsafe: true,
                    nullableContextOptions: NullableContextOptions.Enable));

            built[project] = compilation;
            visiting.Remove(project);
            order.Add((project, compilation));
            return compilation;
        }

        foreach (var project in projects) Build(project);
        return order;
    }

    private SyntaxTree? Parse(string file, CSharpParseOptions options)
    {
        try
        {
            return CSharpSyntaxTree.ParseText(File.ReadAllText(file), options, file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            lock (_diagnostics) Warn($"could not read {RepoScanner.Relative(_root, file)}: {e.Message}");
            return null;
        }
    }

    private bool IsRepoFile(SyntaxTree tree) => RepoScanner.IsUnder(tree.FilePath, _root);

    private SemanticModel Model(Compilation compilation, SyntaxTree tree)
    {
        if (!_models.TryGetValue(tree, out var model))
        {
            model = compilation.GetSemanticModel(tree);
            _models[tree] = model;
        }
        return model;
    }

    // ---------------------------------------------------------------- pass 1: types

    private void DeclareTypes(ProjectInfo project, CSharpCompilation compilation)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!IsRepoFile(tree)) continue;
            var model = Model(compilation, tree);
            var file = RepoScanner.Relative(_root, tree.FilePath);
            foreach (var decl in TypeDeclarations(tree.GetRoot()))
            {
                if (model.GetDeclaredSymbol(decl) is not INamedTypeSymbol type) continue;

                var id = IdOf(type);
                if (!_nodes.TryGetValue(id, out var node))
                {
                    node = new NodeInfo
                    {
                        Id = id,
                        Name = DisplayName(type),
                        FullName = type.ToDisplayString(IdFormat),
                        Kind = KindOf(type),
                        Module = EnsureNamespaceModule(project, type),
                        Visibility = VisibilityOf(type.DeclaredAccessibility),
                        Language = Language,
                        File = file,
                        Line = decl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        Container = type.ContainingType is { } outer ? IdOf(outer) : null,
                    };
                    CountMembers(type, node.Metrics);
                    _nodes[id] = node;
                }

                if (!node.Files.Contains(file)) node.Files.Add(file);
                node.Metrics[MetricKeys.Loc] = node.Metrics.GetValueOrDefault(MetricKeys.Loc) + OwnLines(decl);
            }
        }
    }

    private string EnsureProjectModule(ProjectInfo project)
    {
        if (!_modules.ContainsKey(project.ModuleId))
        {
            _modules[project.ModuleId] = new ModuleInfo
            {
                Id = project.ModuleId,
                Name = project.Name,
                Kind = ModuleKinds.Project,
                Language = Language,
                Path = project.ProjectFile != null ? RepoScanner.Relative(_root, project.Dir) : null,
            };
        }
        return project.ModuleId;
    }

    private string EnsureNamespaceModule(ProjectInfo project, INamedTypeSymbol type)
    {
        var parent = EnsureProjectModule(project);
        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : "(global)";
        var id = $"{parent}#{ns}";
        if (!_modules.ContainsKey(id))
        {
            _modules[id] = new ModuleInfo
            {
                Id = id,
                Name = ns,
                Kind = ModuleKinds.Namespace,
                Parent = parent,
                Language = Language,
            };
        }
        return id;
    }

    private static void CountMembers(INamedTypeSymbol type, Dictionary<string, double> metrics)
    {
        int methods = 0, fields = 0, properties = 0, events = 0;
        foreach (var member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared) continue;
            switch (member)
            {
                case IMethodSymbol
                {
                    MethodKind: MethodKind.Ordinary or MethodKind.Constructor or MethodKind.UserDefinedOperator
                    or MethodKind.Conversion or MethodKind.Destructor
                }:
                    methods++;
                    break;
                case IPropertySymbol:
                    properties++;
                    break;
                case IFieldSymbol:
                    fields++;
                    break;
                case IEventSymbol:
                    events++;
                    break;
            }
        }
        metrics[MetricKeys.Methods] = methods;
        metrics[MetricKeys.Fields] = fields;
        metrics[MetricKeys.Properties] = properties;
        metrics[MetricKeys.Events] = events;
    }

    // ---------------------------------------------------------------- pass 2: edges

    private void CollectEdges(CSharpCompilation compilation)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!IsRepoFile(tree)) continue;
            var model = Model(compilation, tree);
            foreach (var decl in TypeDeclarations(tree.GetRoot()))
            {
                if (model.GetDeclaredSymbol(decl) is not INamedTypeSymbol type) continue;
                var self = IdOf(type);
                if (!_nodes.ContainsKey(self)) continue;

                // Partial types have several declarations; structural edges come from the symbol, once.
                if (_structuralDone.Add(self)) AddStructuralEdges(type, self);

                // Walk this declaration's body, but not nested types (they're walked on their own)
                // or the base list (already covered by inherits/implements).
                var body = decl.DescendantNodes(child =>
                    child == decl || (!IsTypeDeclaration(child) && child is not BaseListSyntax));
                foreach (var node in body) Visit(model, self, node);
            }
        }
    }

    private void AddStructuralEdges(INamedTypeSymbol type, string self)
    {
        if (type.BaseType is { } baseType && !IsImplicitBase(baseType))
        {
            var baseId = IdOf(baseType);
            if (_nodes.ContainsKey(baseId)) AddEdge(self, baseId, EdgeKinds.Inherits);
            else _nodes[self].Attributes["externalBase"] = baseType.ToDisplayString(IdFormat);
        }

        var externalInterfaces = new List<string>();
        foreach (var iface in type.Interfaces)
        {
            var ifaceId = IdOf(iface);
            if (_nodes.ContainsKey(ifaceId)) AddEdge(self, ifaceId, EdgeKinds.Implements);
            else externalInterfaces.Add(iface.ToDisplayString(IdFormat));
        }
        if (externalInterfaces.Count > 0)
        {
            _nodes[self].Attributes["externalInterfaces"] = string.Join(", ", externalInterfaces);
        }

        if (type.ContainingType is { } outer) AddEdge(IdOf(outer), self, EdgeKinds.Contains);
    }

    private void Visit(SemanticModel model, string self, SyntaxNode node)
    {
        switch (node)
        {
            case BaseObjectCreationExpressionSyntax creation:
            {
                var created = (model.GetSymbolInfo(creation).Symbol as IMethodSymbol)?.ContainingType
                              ?? model.GetTypeInfo(creation).Type;
                AddEdge(self, created, EdgeKinds.Creates);
                break;
            }
            case IdentifierNameSyntax { IsVar: true }:
                break;
            case SimpleNameSyntax name:
            {
                if (IsCreationTypeName(name)) break;
                var info = model.GetSymbolInfo(name);
                var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                switch (symbol)
                {
                    case INamedTypeSymbol named:
                        AddEdge(self, named, IsMemberTypeSyntax(name) ? EdgeKinds.Has : EdgeKinds.References);
                        break;
                    case IMethodSymbol { MethodKind: MethodKind.Constructor }:
                        break;
                    case IMethodSymbol method:
                        AddEdge(self, (method.ReducedFrom ?? method).ContainingType, EdgeKinds.Calls);
                        break;
                    case IPropertySymbol or IFieldSymbol or IEventSymbol:
                        AddEdge(self, symbol.ContainingType, EdgeKinds.Uses);
                        break;
                }
                break;
            }
        }
    }

    private void AddEdge(string source, ITypeSymbol? target, string kind)
    {
        if (target is INamedTypeSymbol named) AddEdge(source, IdOf(named), kind);
    }

    private void AddEdge(string source, string target, string kind)
    {
        if (source == target || !_nodes.ContainsKey(target) || !_nodes.ContainsKey(source)) return;
        var key = (source, target, kind);
        _edges[key] = _edges.GetValueOrDefault(key) + 1;
    }

    // ---------------------------------------------------------------- syntax helpers

    private static bool IsTypeDeclaration(SyntaxNode node) =>
        node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax;

    private static IEnumerable<MemberDeclarationSyntax> TypeDeclarations(SyntaxNode root) =>
        root.DescendantNodes().Where(IsTypeDeclaration).Cast<MemberDeclarationSyntax>();

    /// <summary>True for the type name in <c>new Foo()</c>; that use is recorded as "creates".</summary>
    private static bool IsCreationTypeName(SimpleNameSyntax name)
    {
        SyntaxNode current = name;
        while (current.Parent is QualifiedNameSyntax qualified && qualified.Right == current) current = qualified;
        return current.Parent is ObjectCreationExpressionSyntax creation && creation.Type == current;
    }

    /// <summary>True when the name is part of a field, event field or property's declared type.</summary>
    private static bool IsMemberTypeSyntax(SimpleNameSyntax name)
    {
        foreach (var ancestor in name.Ancestors())
        {
            switch (ancestor)
            {
                case VariableDeclarationSyntax variable when variable.Parent is FieldDeclarationSyntax or EventFieldDeclarationSyntax:
                    return variable.Type.Span.Contains(name.Span);
                case PropertyDeclarationSyntax property:
                    return property.Type.Span.Contains(name.Span);
                case StatementSyntax:
                case MemberDeclarationSyntax:
                case ExpressionSyntax and not TypeSyntax:
                    return false;
            }
        }
        return false;
    }

    private static int Lines(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
    }

    /// <summary>Lines of a declaration minus its nested type declarations (those count for themselves).</summary>
    private static int OwnLines(MemberDeclarationSyntax decl)
    {
        var total = Lines(decl);
        if (decl is TypeDeclarationSyntax type)
        {
            foreach (var member in type.Members)
            {
                if (IsTypeDeclaration(member)) total -= Lines(member);
            }
        }
        return Math.Max(1, total);
    }

    // ---------------------------------------------------------------- symbol helpers

    private static string IdOf(INamedTypeSymbol type) => Prefix + type.OriginalDefinition.ToDisplayString(IdFormat);

    private static bool IsImplicitBase(INamedTypeSymbol type) => type.SpecialType is
        SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum
        or SpecialType.System_Delegate or SpecialType.System_MulticastDelegate;

    private static string DisplayName(INamedTypeSymbol type) =>
        type.TypeParameters.Length == 0
            ? type.Name
            : $"{type.Name}<{string.Join(",", type.TypeParameters.Select(p => p.Name))}>";

    private static string KindOf(INamedTypeSymbol type) => type.TypeKind switch
    {
        TypeKind.Interface => NodeKinds.Interface,
        TypeKind.Enum => NodeKinds.Enum,
        TypeKind.Delegate => NodeKinds.Delegate,
        TypeKind.Struct => type.IsRecord ? NodeKinds.RecordStruct : NodeKinds.Struct,
        _ => type.IsRecord ? NodeKinds.Record : NodeKinds.Class,
    };

    private static string VisibilityOf(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Protected => "protected",
        Accessibility.Private => "private",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => "internal",
    };
}
