using System.Text.Json.Serialization;

namespace Valence.Schema;

/// <summary>
/// Versioned format identifiers. Bump the number on breaking changes;
/// consumers must reject versions they don't understand. Additive, optional fields
/// (like members, memberEdges and entryPoints) don't bump the version.
/// </summary>
public static class SchemaIds
{
    public const string ProbeManifest = "valence.probe-manifest/1";
    public const string ProbeOutput = "valence.probe/1";
    public const string Graph = "valence.graph/1";
}

/// <summary>Well-known module kinds. Probes may emit others; viewers treat unknown kinds generically.</summary>
public static class ModuleKinds
{
    public const string Project = "project";
    public const string Namespace = "namespace";
    public const string Folder = "folder";
}

/// <summary>Well-known node kinds.</summary>
public static class NodeKinds
{
    public const string Class = "class";
    public const string Struct = "struct";
    public const string Interface = "interface";
    public const string Enum = "enum";
    public const string Record = "record";
    public const string RecordStruct = "record-struct";
    public const string Delegate = "delegate";

    /// <summary>A file/module that holds free functions (Python, PHP, JS). C# never emits it.</summary>
    public const string Module = "module";
}

/// <summary>Well-known edge kinds, roughly from strongest to weakest coupling.</summary>
public static class EdgeKinds
{
    public const string Inherits = "inherits";
    public const string Implements = "implements";
    public const string Contains = "contains";
    public const string Has = "has";
    public const string Creates = "creates";
    public const string Calls = "calls";
    public const string Uses = "uses";
    public const string References = "references";
}

/// <summary>Members of a node: the things data flows between.</summary>
public static class MemberKinds
{
    public const string Method = "method";
    public const string Constructor = "constructor";
    public const string Property = "property";
    public const string Field = "field";
    public const string Event = "event";
    public const string Function = "function";
}

/// <summary>Member-level edge kinds.</summary>
public static class MemberEdgeKinds
{
    /// <summary>Source method invokes target method/constructor.</summary>
    public const string Calls = "calls";

    /// <summary>Source method constructs an instance via target constructor.</summary>
    public const string Creates = "creates";

    /// <summary>Source method reads target field/property.</summary>
    public const string Reads = "reads";

    /// <summary>Source method assigns target field/property.</summary>
    public const string Writes = "writes";

    /// <summary>Source method raises/emits target event or signal.</summary>
    public const string Raises = "raises";

    /// <summary>Source event/signal, when raised, runs target handler.</summary>
    public const string Triggers = "triggers";
}

/// <summary>How sure the probe is that an edge exists.</summary>
public static class Confidence
{
    /// <summary>Resolved by a compiler/type checker.</summary>
    public const string Certain = "certain";

    /// <summary>Resolved from type hints, imports, virtual dispatch or framework conventions.</summary>
    public const string Likely = "likely";

    /// <summary>Matched by name only.</summary>
    public const string Guess = "guess";

    public static int Rank(string? confidence) => confidence switch
    {
        null or Certain => 2,
        Likely => 1,
        _ => 0,
    };

    /// <summary>The stronger of two confidences (null means certain).</summary>
    public static string? Strongest(string? a, string? b) => Rank(a) >= Rank(b) ? a : b;
}

/// <summary>What starts an entry point running. Simulation plays one trigger at a time.</summary>
public static class Triggers
{
    public const string Frame = "frame";
    public const string Physics = "physics";
    public const string Input = "input";
    public const string Draw = "draw";
    public const string Startup = "startup";
    public const string Shutdown = "shutdown";
    public const string Event = "event";
    public const string Request = "request";
    public const string Job = "job";
}

/// <summary>Ways code reaches outside the program (the Boundary lens), from calls into code outside the repo.</summary>
public static class EffectKinds
{
    /// <summary>HTTP, sockets, websockets, mail, cloud SDKs.</summary>
    public const string Network = "network";

    /// <summary>Reads files or lists folders.</summary>
    public const string FileRead = "file-read";

    /// <summary>Creates, writes, moves or deletes files or folders.</summary>
    public const string FileWrite = "file-write";

    /// <summary>Starts or stops processes, opens things with the OS shell.</summary>
    public const string Process = "process";

    /// <summary>Environment variables, command-line arguments, the registry.</summary>
    public const string Environment = "environment";

    /// <summary>SQL and NoSQL clients, ORMs.</summary>
    public const string Database = "database";

    /// <summary>Native code: P/Invoke, marshalling, ctypes.</summary>
    public const string Native = "native";

    /// <summary>Code chosen at run time (reflection, eval, call by name): what runs can't be seen statically.</summary>
    public const string Dynamic = "dynamic";

    public static readonly string[] All = { Network, Database, FileWrite, FileRead, Process, Environment, Native, Dynamic };
}

/// <summary>One kind of outside effect a member (or a class's top-level code) has, through one API.</summary>
public sealed class EffectInfo
{
    /// <summary>One of <see cref="EffectKinds"/>.</summary>
    public string Kind { get; set; } = EffectKinds.Network;

    /// <summary>What it calls, e.g. "System.IO.File.WriteAllText" or "requests.get".</summary>
    public string Api { get; set; } = "";

    /// <summary>Line of the first call.</summary>
    public int Line { get; set; }

    /// <summary>Number of calls.</summary>
    public int Weight { get; set; } = 1;

    /// <summary>Null means certain.</summary>
    public string? Confidence { get; set; }
}

/// <summary>Well-known metric keys. Probes and the core may add any others.</summary>
public static class MetricKeys
{
    public const string Loc = "loc";
    public const string Methods = "methods";
    public const string Fields = "fields";
    public const string Properties = "properties";
    public const string Events = "events";
    public const string FanIn = "fanIn";
    public const string FanOut = "fanOut";
    public const string Commits = "commits";
    public const string Churn = "churn";
    public const string LastChanged = "lastChanged";

    /// <summary>People who changed it in the churn window.</summary>
    public const string Authors = "authors";

    /// <summary>Share of its changed lines by the person who changed it most (0..1).</summary>
    public const string TopAuthorShare = "topAuthorShare";

    /// <summary>Share of its changed lines from commits by AI agents or bots, as author or co-author (0..1).</summary>
    public const string AgentShare = "agentShare";
}

/// <summary>Well-known node attribute keys. Probes and the core may add any others.</summary>
public static class AttributeKeys
{
    /// <summary>"true" for abstract classes (and protocols / ABCs).</summary>
    public const string Abstract = "abstract";

    /// <summary>Who changed it most in the churn window (git author name).</summary>
    public const string TopAuthor = "topAuthor";

    /// <summary>Comma-separated modules outside the repo that its file imports.</summary>
    public const string ExternalImports = "externalImports";
}

/// <summary>probe.json — describes a language plugin and how to invoke it.</summary>
public sealed class ProbeManifest
{
    public string Schema { get; set; } = SchemaIds.ProbeManifest;
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version { get; set; } = "0.0.0";
    public List<string> Languages { get; set; } = new();

    /// <summary>File extensions (with leading dot) that make this probe relevant to a repo.</summary>
    public List<string> Extensions { get; set; } = new();

    /// <summary>
    /// Executable and leading arguments. "{dir}" is replaced with the manifest's directory.
    /// The core appends: --root &lt;repo&gt; --out &lt;file&gt;
    /// </summary>
    public List<string> Command { get; set; } = new();
}

/// <summary>What a probe writes to its --out file.</summary>
public sealed class ProbeOutput
{
    public string Schema { get; set; } = SchemaIds.ProbeOutput;
    public ToolInfo Probe { get; set; } = new();
    public List<ModuleInfo> Modules { get; set; } = new();
    public List<NodeInfo> Nodes { get; set; } = new();
    public List<EdgeInfo> Edges { get; set; } = new();
    public List<MemberEdgeInfo> MemberEdges { get; set; } = new();
    public List<EntryPointInfo> EntryPoints { get; set; } = new();
    public List<DiagnosticInfo> Diagnostics { get; set; } = new();
}

/// <summary>The merged graph the core writes and the viewer reads.</summary>
public sealed class Graph
{
    public string Schema { get; set; } = SchemaIds.Graph;
    public ToolInfo Generator { get; set; } = new();
    public RepoInfo Repo { get; set; } = new();
    public List<ToolInfo> Probes { get; set; } = new();
    public List<ModuleInfo> Modules { get; set; } = new();
    public List<NodeInfo> Nodes { get; set; } = new();
    public List<EdgeInfo> Edges { get; set; } = new();

    /// <summary>Node edges aggregated to leaf modules (excludes edges within one module).</summary>
    public List<ModuleEdgeInfo> ModuleEdges { get; set; } = new();

    /// <summary>Method/field-level flow: calls, reads, writes, events. Used for simulation.</summary>
    public List<MemberEdgeInfo> MemberEdges { get; set; } = new();
    public List<EntryPointInfo> EntryPoints { get; set; } = new();
    public List<DiagnosticInfo> Diagnostics { get; set; } = new();
}

public sealed class ToolInfo
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}

public sealed class RepoInfo
{
    public string Root { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Commit { get; set; }
    public string? Branch { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }

    /// <summary>Size of the git history window used for churn metrics, in days.</summary>
    public int? ChurnWindowDays { get; set; }
}

/// <summary>A grouping of nodes: project, namespace, package, folder. Forms a tree via Parent.</summary>
public sealed class ModuleInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = ModuleKinds.Folder;
    public string? Parent { get; set; }
    public string Language { get; set; } = "";

    /// <summary>Repo-relative path, forward slashes, when the module maps to a location.</summary>
    public string? Path { get; set; }
}

/// <summary>A type-level unit of code: class, interface, struct, module of functions, ...</summary>
public sealed class NodeInfo
{
    /// <summary>Globally unique; probes prefix with their language (e.g. "cs:").</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Kind { get; set; } = NodeKinds.Class;

    /// <summary>Id of the leaf module this node belongs to.</summary>
    public string Module { get; set; } = "";

    /// <summary>public | internal | protected | private | ... (language-specific strings allowed).</summary>
    public string Visibility { get; set; } = "public";
    public string Language { get; set; } = "";

    /// <summary>Primary declaration file (repo-relative, forward slashes).</summary>
    public string File { get; set; } = "";
    public int Line { get; set; }

    /// <summary>All files the node is declared in (partial classes span several).</summary>
    public List<string> Files { get; set; } = new();

    /// <summary>Id of the enclosing node for nested types.</summary>
    public string? Container { get; set; }

    /// <summary>Methods, constructors, properties, fields and events declared on this node.</summary>
    public List<MemberInfo> Members { get; set; } = new();

    public Dictionary<string, double> Metrics { get; set; } = new();

    /// <summary>Free-form string facts, e.g. "externalBase": "Node3D".</summary>
    public Dictionary<string, string> Attributes { get; set; } = new();

    /// <summary>Outside effects of code that isn't in any member (a script's top level, a class body); null when none.</summary>
    public List<EffectInfo>? Effects { get; set; }
}

/// <summary>A method, constructor, property, field, event/signal or free function.</summary>
public sealed class MemberInfo
{
    /// <summary>Globally unique, language-prefixed (e.g. "cs:Shop.OrderService.Place(Order)").</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = MemberKinds.Method;
    public string Visibility { get; set; } = "public";
    public bool IsStatic { get; set; }

    /// <summary>Human-readable signature, e.g. "void Place(Order order)".</summary>
    public string? Signature { get; set; }
    public string File { get; set; } = "";
    public int Line { get; set; }

    /// <summary>Free-form facts, e.g. "signal": "true", "override": "true".</summary>
    public Dictionary<string, string> Attributes { get; set; } = new();

    /// <summary>What it does outside the program itself (network, files, processes…); null when nothing.</summary>
    public List<EffectInfo>? Effects { get; set; }
}

/// <summary>A directed dependency: Source depends on Target.</summary>
public sealed class EdgeInfo
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string Kind { get; set; } = EdgeKinds.References;

    /// <summary>Number of occurrences in source code.</summary>
    public int Weight { get; set; } = 1;

    /// <summary>Null means certain.</summary>
    public string? Confidence { get; set; }
}

/// <summary>A member-level flow edge between two members (ids from <see cref="MemberInfo.Id"/>).</summary>
public sealed class MemberEdgeInfo
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string Kind { get; set; } = MemberEdgeKinds.Calls;

    /// <summary>Number of occurrences.</summary>
    public int Weight { get; set; } = 1;

    /// <summary>Position of the first occurrence among the source member's edges (0-based, source order).</summary>
    public int Order { get; set; }

    /// <summary>Line of the first occurrence.</summary>
    public int Line { get; set; }

    /// <summary>Null means certain.</summary>
    public string? Confidence { get; set; }
}

/// <summary>A member something outside the code calls: engine callbacks, routes, Main, event handlers.</summary>
public sealed class EntryPointInfo
{
    public string Member { get; set; } = "";

    /// <summary>One of <see cref="Triggers"/> (others allowed).</summary>
    public string Trigger { get; set; } = Triggers.Startup;

    /// <summary>Shown in menus, e.g. "Player._Process" or "POST /orders".</summary>
    public string Label { get; set; } = "";

    /// <summary>What recognized it: "godot", "dotnet", "laravel", ...</summary>
    public string Source { get; set; } = "";
}

public sealed class ModuleEdgeInfo
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public int Weight { get; set; }
    public Dictionary<string, int> Kinds { get; set; } = new();
}

public sealed class DiagnosticInfo
{
    public string Level { get; set; } = "info";
    public string Message { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; set; }
}
