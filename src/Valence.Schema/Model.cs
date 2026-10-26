using System.Text.Json.Serialization;

namespace Valence.Schema;

/// <summary>
/// Versioned format identifiers. Bump the number on breaking changes;
/// consumers must reject versions they don't understand.
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

/// <summary>A type-level unit of code: class, interface, struct, ...</summary>
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

    public Dictionary<string, double> Metrics { get; set; } = new();

    /// <summary>Free-form string facts, e.g. "externalBase": "Node3D".</summary>
    public Dictionary<string, string> Attributes { get; set; } = new();
}

/// <summary>A directed dependency: Source depends on Target.</summary>
public sealed class EdgeInfo
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string Kind { get; set; } = EdgeKinds.References;

    /// <summary>Number of occurrences in source code.</summary>
    public int Weight { get; set; } = 1;
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
