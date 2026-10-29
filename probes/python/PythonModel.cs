using Valence.Schema;

namespace Valence.Probe.Python;

// What pass 1 (PythonExtractor) records from the syntax trees and pass 2 (PythonResolver) resolves.
// Nothing here holds tree-sitter nodes, so trees are freed as soon as each file is extracted.

/// <summary>A name chain as written: <c>a.b.c</c>, <c>self.x</c>, <c>Foo()</c>, <c>make().run</c>.</summary>
internal sealed record Ref(Ref? Receiver, string Name, bool Call)
{
    public override string ToString() => (Receiver != null ? Receiver + "." : "") + Name + (Call ? "()" : "");
}

internal enum UseKind
{
    /// <summary><c>Foo()</c>, <c>obj.run()</c>: the callee becomes creates/calls.</summary>
    Call,

    /// <summary>Any other mention in code: references (classes, functions) or uses (attributes).</summary>
    Mention,

    /// <summary>A base class.</summary>
    Inherit,

    /// <summary>A parameter, return or local type hint.</summary>
    Annotation,

    /// <summary>The type hint of a field (class-level or <c>self.x: T</c>): composition.</summary>
    FieldType,

    /// <summary>The value stored in a field (<c>self.x = Foo()</c>): composition when it's an instance.</summary>
    Assigned,

    /// <summary>An attribute being assigned (<c>self.x = …</c>, <c>obj.count += 1</c>).</summary>
    Write,

    /// <summary>Control enters <see cref="Use.Member"/> here (a game loop's body): a member link only.</summary>
    Enter,
}

/// <summary>Framework wiring found by call shape, resolved into entry points in pass 2.</summary>
internal enum WiringKind
{
    /// <summary>Django <c>path("x/", view)</c>, <c>re_path</c>, <c>url</c>: a function or class-based view.</summary>
    Route,

    /// <summary>Django REST framework <c>router.register("x", ViewSet)</c>.</summary>
    ViewSet,

    /// <summary><c>signal.connect(handler)</c>: Django signals, blinker, Qt signals.</summary>
    Connect,
}

internal sealed record Wiring(WiringKind Kind, Ref Target, Scope Scope, string Label);

/// <summary>
/// One name use, with where it appears: the scope decides how names resolve and, unless
/// <paramref name="Source"/> is given (a class's own bases), which nucleon the bond starts from.
/// <paramref name="Member"/> is the target of an <see cref="UseKind.Enter"/>. <paramref name="Mode"/> is
/// the mode string of a call that opens a file (<c>open(path, "w")</c>), for the Boundary lens.
/// </summary>
internal sealed record Use(UseKind Kind, Ref Ref, Scope Scope, int Line, NodeInfo? Source = null, MemberInfo? Member = null,
    string? Mode = null);

/// <summary>
/// Where code is: a file's top level, a class body, or a function (nested functions fold into theirs;
/// a file's <c>if __name__ == "__main__":</c> block is a function of its own).
/// </summary>
internal sealed class Scope
{
    public Scope(PyFile file, PyClass? cls, PyFunction? function)
    {
        File = file;
        Class = cls;
        Function = function;
    }

    public PyFile File { get; }

    public PyClass? Class { get; }

    public PyFunction? Function { get; }

    /// <summary>The nucleon bonds start from: the class, else the file's own node (if it has one).</summary>
    public NodeInfo? Owner => Class?.Node ?? File.Node;
}

/// <summary>A name bound by assignment, parameter or annotation; evaluated lazily in its own scope.</summary>
internal sealed class Binding
{
    public Binding(Ref? value, Ref? type, Scope scope)
    {
        Value = value;
        Type = type;
        Scope = scope;
    }

    /// <summary>The assigned expression, when it is a name chain.</summary>
    public Ref? Value { get; }

    /// <summary>The annotated type, when it names one class (Optional[X] and X | None count).</summary>
    public Ref? Type { get; }

    public Scope Scope { get; }
}

/// <summary>
/// <c>import a.b as x</c> → (a.b, null); <c>from a.b import c</c> → (a.b, c). Relative imports are
/// stored as repo paths (<see cref="IsRepoPath"/>), since they're relative to the file's folder.
/// </summary>
internal sealed record ImportBinding(string Module, string? Name, bool IsRepoPath, int Line);

/// <summary>One .py file.</summary>
internal sealed class PyFile
{
    public PyFile(string path, string module, string repoPath, bool isPackage, string atom, double lines)
    {
        Path = path;
        Module = module;
        RepoPath = repoPath;
        IsPackage = isPackage;
        Atom = atom;
        Lines = lines;
    }

    /// <summary>Repo-relative, forward slashes.</summary>
    public string Path { get; }

    /// <summary>The dotted name it is imported as (from its top-most package).</summary>
    public string Module { get; }

    /// <summary>The dotted path from the repo root (a/b/c.py → a.b.c), for relative and fallback lookups.</summary>
    public string RepoPath { get; }

    /// <summary>An __init__.py.</summary>
    public bool IsPackage { get; }

    public string Atom { get; }

    public double Lines { get; }

    /// <summary>The file's own nucleon, for free functions and top-level code; null when it only has classes.</summary>
    public NodeInfo? Node { get; set; }

    public Dictionary<string, PyClass> Classes { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, PyFunction> Functions { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<Binding>> Globals { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, ImportBinding> Imports { get; } = new(StringComparer.Ordinal);

    public List<ImportBinding> Wildcards { get; } = new();

    public List<Use> Uses { get; } = new();

    public List<Wiring> Wirings { get; } = new();

    /// <summary>The package relative imports start from, as a repo path.</summary>
    public string PackageRepoPath =>
        IsPackage ? RepoPath : RepoPath.LastIndexOf('.') is var dot and >= 0 ? RepoPath[..dot] : "";
}

internal sealed class PyClass
{
    public PyClass(NodeInfo node, PyFile file, PyClass? outer)
    {
        Node = node;
        File = file;
        Outer = outer;
    }

    public NodeInfo Node { get; }

    public PyFile File { get; }

    public PyClass? Outer { get; }

    public Dictionary<string, PyClass> Nested { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, PyFunction> Methods { get; } = new(StringComparer.Ordinal);

    /// <summary>Class-level attributes and <c>self.x</c> assignments.</summary>
    public Dictionary<string, List<Binding>> Fields { get; } = new(StringComparer.Ordinal);

    /// <summary>The member each field is listed as (for reads/writes in simulation).</summary>
    public Dictionary<string, MemberInfo> FieldMembers { get; } = new(StringComparer.Ordinal);

    public List<Ref> Bases { get; } = new();
}

internal sealed class PyFunction
{
    /// <param name="locals">Shared with the enclosing code, for a block that runs inside it (a game loop).</param>
    public PyFunction(MemberInfo member, NodeInfo owner, PyClass? cls, PyFile file,
        Dictionary<string, List<Binding>>? locals = null)
    {
        Member = member;
        Owner = owner;
        Class = cls;
        File = file;
        Locals = locals ?? new Dictionary<string, List<Binding>>(StringComparer.Ordinal);
    }

    public MemberInfo Member { get; }

    /// <summary>The class for methods, the file's node for free functions.</summary>
    public NodeInfo Owner { get; }

    public PyClass? Class { get; }

    public PyFile File { get; }

    /// <summary>The return annotation, when it names one class.</summary>
    public Ref? Returns { get; set; }

    /// <summary>The first parameter of a method (usually self/cls).</summary>
    public string? SelfParam { get; set; }

    /// <summary>The first parameter is the class itself (classmethod, __new__).</summary>
    public bool SelfIsClass { get; set; }

    /// <summary>Parameters and local variables, including those of nested functions.</summary>
    public Dictionary<string, List<Binding>> Locals { get; }
}

/// <summary>A module file or a package folder (with or without __init__.py).</summary>
internal sealed class PyModule
{
    public PyModule(string name, string repoPath, PyFile? file)
    {
        Name = name;
        RepoPath = repoPath;
        File = file;
    }

    public string Name { get; }

    public string RepoPath { get; }

    /// <summary>The module's file, or the package's __init__.py; null for a namespace folder.</summary>
    public PyFile? File { get; }
}
