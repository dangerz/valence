using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Valence.Schema;

namespace Valence.Viewer;

/// <summary>A class/interface/struct: one nucleon.</summary>
public sealed class NodeData
{
    public NodeData(NodeInfo info, AtomData atom)
    {
        Info = info;
        Atom = atom;
    }

    public NodeInfo Info { get; }
    public AtomData Atom { get; }

    /// <summary>Instance index in the nucleon MultiMesh.</summary>
    public int Index { get; set; }

    /// <summary>Where the nucleon sits in the packed nucleus.</summary>
    public Vector3 PackedOffset { get; set; }

    /// <summary>Where it sits when the atom is opened (readable, layered by internal dependencies).</summary>
    public Vector3 ExpandedOffset { get; set; }

    /// <summary>Current offset from the atom centre (animates between packed and expanded).</summary>
    public Vector3 LocalOffset { get; set; }
    public float Radius { get; set; }
    public int FanIn { get; set; }
    public int FanOut { get; set; }
    public List<EdgeInfo> Outgoing { get; } = new();
    public List<EdgeInfo> Incoming { get; } = new();

    /// <summary>What's drawn: <see cref="UserOpacity"/>, or 0 while "hide untouched" hides it in a simulation.</summary>
    public float Opacity { get; set; } = 1f;

    /// <summary>From the user's hide/quiet choices: 1 shown, the quiet fade when quiet, 0 when hidden.</summary>
    public float UserOpacity { get; set; } = 1f;
    public bool IsHidden => Opacity <= 0f;

    public Vector3 WorldPosition => Atom.Position + LocalOffset;
    public bool IsPublic => Info.Visibility == "public";
    public double Metric(string key) => Info.Metrics.TryGetValue(key, out var value) ? value : 0;
}

/// <summary>A system (leaf module, e.g. a namespace): one atom.</summary>
public sealed class AtomData
{
    public AtomData(string id, string name, MoleculeData molecule)
    {
        Id = id;
        Name = name;
        Molecule = molecule;
    }

    public string Id { get; }
    public string Name { get; }
    public MoleculeData Molecule { get; }
    public List<NodeData> Nodes { get; } = new();
    public HashSet<AtomData> Neighbors { get; } = new();

    public Vector3 Position { get; set; }
    public float CoreRadius { get; set; }
    public float Radius { get; set; }

    /// <summary>Radius currently drawn: <see cref="Radius"/>, or larger while the atom is opened.</summary>
    public float DisplayRadius { get; set; }

    /// <summary>Dependency depth: 0 = depends on no other system. Higher layers sit higher in the scene.</summary>
    public int Layer { get; set; }

    /// <summary>True when this system is part of a dependency cycle with other systems.</summary>
    public bool InCycle { get; set; }
    public bool Pinned { get; set; }
    public AtomView? View { get; set; }

    /// <summary>What's drawn: <see cref="UserOpacity"/>, or 0 while "hide untouched" hides it in a simulation.</summary>
    public float Opacity { get; set; } = 1f;

    /// <summary>From the user's hide/quiet choices (itself or its project).</summary>
    public float UserOpacity { get; set; } = 1f;
    public bool IsHidden => Opacity <= 0f;

    /// <summary>How many other systems this one bonds with.</summary>
    public int Valence => Neighbors.Count;

    public string ShortName
    {
        get
        {
            var dot = Name.LastIndexOf('.');
            return dot >= 0 && dot < Name.Length - 1 ? Name[(dot + 1)..] : Name;
        }
    }
}

/// <summary>A top-level module (e.g. a project): a molecule of atoms.</summary>
public sealed class MoleculeData
{
    public MoleculeData(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }
    public List<AtomData> Atoms { get; } = new();
    public Label3D? Label { get; set; }
    public float Opacity { get; set; } = 1f;
}

/// <summary>Aggregated dependencies between two systems. A and B are ordered by id.</summary>
public sealed class BondData
{
    public BondData(AtomData a, AtomData b)
    {
        A = a;
        B = b;
    }

    public AtomData A { get; }
    public AtomData B { get; }

    /// <summary>Edge weight where a class in A depends on a class in B.</summary>
    public int WeightAB { get; set; }
    public int WeightBA { get; set; }
    public Dictionary<string, int> Kinds { get; } = new();
    public MeshInstance3D? Mesh { get; set; }

    /// <summary>Both systems depend on each other: a cycle, usually worth a look.</summary>
    public bool Covalent => WeightAB > 0 && WeightBA > 0;
    public int Weight => WeightAB + WeightBA;
    public AtomData Other(AtomData atom) => atom == A ? B : A;
    public int WeightFrom(AtomData atom) => atom == A ? WeightAB : WeightBA;
}

public sealed class GraphModel
{
    private GraphModel(Graph graph)
    {
        Graph = graph;
    }

    public Graph Graph { get; }
    public Dictionary<string, NodeData> Nodes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, AtomData> Atoms { get; } = new(StringComparer.Ordinal);
    public List<AtomData> AtomList { get; } = new();
    public List<MoleculeData> Molecules { get; } = new();
    public List<BondData> Bonds { get; } = new();

    /// <summary>Member id → the node (class) declaring it.</summary>
    public Dictionary<string, NodeData> MemberOwners { get; } = new(StringComparer.Ordinal);

    /// <summary>Member id → entry points that start at it.</summary>
    public Dictionary<string, List<EntryPointInfo>> EntryPointsByMember { get; } = new(StringComparer.Ordinal);

    public int MaxLayer { get; private set; }
    public double MaxChurn { get; private set; }
    public int MaxFanIn { get; private set; }

    public bool HasGit => Graph.Repo.ChurnWindowDays != null;

    /// <summary>When the graph was generated (unix seconds); recency is measured against this, not "now".</summary>
    public long SnapshotTime => Graph.Repo.GeneratedAt == default
        ? DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        : Graph.Repo.GeneratedAt.ToUnixTimeSeconds();

    public static GraphModel Build(Graph graph)
    {
        var model = new GraphModel(graph);
        var modules = new Dictionary<string, ModuleInfo>(StringComparer.Ordinal);
        foreach (var module in graph.Modules) modules.TryAdd(module.Id, module);

        string RootOf(string id)
        {
            var current = id;
            for (var guard = 0; guard < 64; guard++)
            {
                if (!modules.TryGetValue(current, out var module)
                    || string.IsNullOrEmpty(module.Parent)
                    || !modules.ContainsKey(module.Parent))
                {
                    break;
                }
                current = module.Parent;
            }
            return current;
        }

        string NameOf(string id) => modules.TryGetValue(id, out var module) ? module.Name : id;

        var molecules = new Dictionary<string, MoleculeData>(StringComparer.Ordinal);
        foreach (var info in graph.Nodes)
        {
            var moduleId = string.IsNullOrEmpty(info.Module) ? "(unassigned)" : info.Module;
            if (!model.Atoms.TryGetValue(moduleId, out var atom))
            {
                var moleculeId = RootOf(moduleId);
                if (!molecules.TryGetValue(moleculeId, out var molecule))
                {
                    molecule = new MoleculeData(moleculeId, NameOf(moleculeId));
                    molecules[moleculeId] = molecule;
                }
                atom = new AtomData(moduleId, NameOf(moduleId), molecule);
                model.Atoms[moduleId] = atom;
                molecule.Atoms.Add(atom);
            }
            if (model.Nodes.ContainsKey(info.Id)) continue;
            var node = new NodeData(info, atom);
            atom.Nodes.Add(node);
            model.Nodes[info.Id] = node;
            foreach (var member in info.Members) model.MemberOwners.TryAdd(member.Id, node);
        }

        foreach (var entry in graph.EntryPoints)
        {
            if (!model.EntryPointsByMember.TryGetValue(entry.Member, out var list))
            {
                list = new List<EntryPointInfo>();
                model.EntryPointsByMember[entry.Member] = list;
            }
            list.Add(entry);
        }

        model.Molecules.AddRange(molecules.Values.OrderBy(m => m.Id, StringComparer.Ordinal));
        foreach (var molecule in model.Molecules) molecule.Atoms.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
        model.AtomList.AddRange(model.Molecules.SelectMany(m => m.Atoms));

        model.ConnectEdges(graph.Edges);
        model.ComputeLayers();
        model.MaxChurn = model.Nodes.Values.Select(n => n.Metric(MetricKeys.Churn)).DefaultIfEmpty(0).Max();
        model.MaxFanIn = model.Nodes.Values.Select(n => n.FanIn).DefaultIfEmpty(0).Max();
        return model;
    }

    /// <summary>Classes directly linked to this one (either direction), optionally filtered by edge kind.</summary>
    public IEnumerable<NodeData> Neighbors(NodeData node, ISet<string>? kinds = null)
    {
        foreach (var edge in node.Outgoing)
        {
            if (kinds != null && !kinds.Contains(edge.Kind)) continue;
            if (Nodes.TryGetValue(edge.Target, out var target) && !target.IsHidden) yield return target;
        }
        foreach (var edge in node.Incoming)
        {
            if (kinds != null && !kinds.Contains(edge.Kind)) continue;
            if (Nodes.TryGetValue(edge.Source, out var source) && !source.IsHidden) yield return source;
        }
    }

    private void ConnectEdges(IEnumerable<EdgeInfo> edges)
    {
        var bonds = new Dictionary<(string, string), BondData>();
        var fanIn = new Dictionary<NodeData, HashSet<NodeData>>();
        var fanOut = new Dictionary<NodeData, HashSet<NodeData>>();

        foreach (var edge in edges)
        {
            if (!Nodes.TryGetValue(edge.Source, out var source) || !Nodes.TryGetValue(edge.Target, out var target)) continue;
            if (source == target) continue;
            source.Outgoing.Add(edge);
            target.Incoming.Add(edge);
            if (edge.Kind == EdgeKinds.Contains) continue;

            Bucket(fanOut, source).Add(target);
            Bucket(fanIn, target).Add(source);

            if (source.Atom == target.Atom) continue;
            var forward = string.CompareOrdinal(source.Atom.Id, target.Atom.Id) < 0;
            var a = forward ? source.Atom : target.Atom;
            var b = forward ? target.Atom : source.Atom;
            if (!bonds.TryGetValue((a.Id, b.Id), out var bond))
            {
                bond = new BondData(a, b);
                bonds[(a.Id, b.Id)] = bond;
                a.Neighbors.Add(b);
                b.Neighbors.Add(a);
            }
            if (forward) bond.WeightAB += edge.Weight;
            else bond.WeightBA += edge.Weight;
            bond.Kinds[edge.Kind] = bond.Kinds.GetValueOrDefault(edge.Kind) + edge.Weight;
        }

        foreach (var node in Nodes.Values)
        {
            node.FanIn = fanIn.TryGetValue(node, out var i) ? i.Count : 0;
            node.FanOut = fanOut.TryGetValue(node, out var o) ? o.Count : 0;
        }
        Bonds.AddRange(bonds.Values.OrderBy(b => b.A.Id, StringComparer.Ordinal).ThenBy(b => b.B.Id, StringComparer.Ordinal));
    }

    private void ComputeLayers()
    {
        var dependsOn = AtomList.ToDictionary(a => a, _ => new List<AtomData>());
        foreach (var bond in Bonds)
        {
            if (bond.WeightAB > 0) dependsOn[bond.A].Add(bond.B);
            if (bond.WeightBA > 0) dependsOn[bond.B].Add(bond.A);
        }

        var (layers, inCycle) = DependencyLayers.Compute(AtomList, atom => dependsOn[atom]);
        MaxLayer = 0;
        foreach (var atom in AtomList)
        {
            atom.Layer = layers[atom];
            atom.InCycle = inCycle.Contains(atom);
            MaxLayer = Math.Max(MaxLayer, atom.Layer);
        }
    }

    private static HashSet<NodeData> Bucket(Dictionary<NodeData, HashSet<NodeData>> map, NodeData key)
    {
        if (!map.TryGetValue(key, out var set))
        {
            set = new HashSet<NodeData>();
            map[key] = set;
        }
        return set;
    }
}
