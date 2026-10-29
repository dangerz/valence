using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Valence.Schema;

namespace Valence.Viewer;

/// <summary>The ways code reaches outside the program (<see cref="EffectKinds"/>), as bits.</summary>
[Flags]
public enum EffectFlags
{
    None = 0,
    Network = 1,
    Database = 2,
    FileWrite = 4,
    FileRead = 8,
    Process = 16,
    Environment = 32,
    Native = 64,
    Dynamic = 128,
}

/// <summary>One effect kind's look: label, icon, color and a short tag for exports.</summary>
public sealed record EffectKind(EffectFlags Flag, string Key, string Label, string Icon, Color Color, string Tag);

/// <summary>
/// The Boundary lens: which code reaches outside the program. Probes record each member's direct
/// effects (calls into network, file, process, environment, database, native and dynamic APIs); here
/// they spread back up the control links (calls, creates, events), so a button handler that calls
/// <c>Storage.Save</c> "writes files, through Storage". For each spread effect we keep the next step
/// toward it, so the inspector can show the path.
/// </summary>
public sealed class Boundary
{
    private const int MaxTrace = 24;

    /// <summary>Every kind: "anything outside the program".</summary>
    public const EffectFlags AllKinds = (EffectFlags)255;

    private static readonly HashSet<string> ControlKinds = new(StringComparer.Ordinal)
    {
        MemberEdgeKinds.Calls, MemberEdgeKinds.Creates, MemberEdgeKinds.Raises, MemberEdgeKinds.Triggers,
    };

    /// <summary>Most notable first: a class with several effects is colored by the first.</summary>
    public static readonly EffectKind[] Kinds =
    {
        new(EffectFlags.Network, EffectKinds.Network, "Network", "globe", Hex(0x4dabf7), "net"),
        new(EffectFlags.Database, EffectKinds.Database, "Database", "database", Hex(0xb197fc), "db"),
        new(EffectFlags.FileWrite, EffectKinds.FileWrite, "Writes files", "file-write", Hex(0xff922b), "write"),
        new(EffectFlags.Process, EffectKinds.Process, "Processes", "terminal", Hex(0xff6b6b), "proc"),
        new(EffectFlags.FileRead, EffectKinds.FileRead, "Reads files", "file-read", Hex(0x69db7c), "read"),
        new(EffectFlags.Environment, EffectKinds.Environment, "Environment", "sliders", Hex(0xffd43b), "env"),
        new(EffectFlags.Native, EffectKinds.Native, "Native code", "chip", Hex(0xf783ac), "native"),
        new(EffectFlags.Dynamic, EffectKinds.Dynamic, "Dynamic code", "code", Hex(0xdee2e6), "dyn"),
    };

    private readonly Dictionary<string, EffectFlags> _direct = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EffectFlags> _reaches = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Member, EffectFlags Kind), string> _next = new();
    private readonly Dictionary<string, MemberInfo> _members = new(StringComparer.Ordinal);

    /// <summary>The graph has effects at all (older graphs and other probes may not).</summary>
    public bool Any { get; private set; }

    /// <summary>Classes with each effect directly / directly or through other code.</summary>
    public Dictionary<EffectFlags, int> DirectCounts { get; } = new();

    public static EffectKind? Info(EffectFlags flag) => Kinds.FirstOrDefault(k => k.Flag == flag);

    public static EffectFlags FlagOf(string kind) => Kinds.FirstOrDefault(k => k.Key == kind)?.Flag ?? EffectFlags.None;

    public static IEnumerable<EffectKind> In(EffectFlags flags) => Kinds.Where(k => (flags & k.Flag) != 0);

    /// <summary>The most notable effect among these, for the class's color.</summary>
    public static EffectKind? Strongest(EffectFlags flags) => Kinds.FirstOrDefault(k => (flags & k.Flag) != 0);

    public EffectFlags MemberDirect(string member) => _direct.GetValueOrDefault(member);

    /// <summary>What a member does outside, itself or through what it calls.</summary>
    public EffectFlags MemberReaches(string member) => _reaches.GetValueOrDefault(member);

    public MemberInfo? Member(string id) => _members.GetValueOrDefault(id);

    public void Compute(GraphModel model)
    {
        _direct.Clear();
        _reaches.Clear();
        _next.Clear();
        _members.Clear();
        DirectCounts.Clear();
        Any = false;

        foreach (var node in model.Nodes.Values)
        {
            var direct = Flags(node.Info.Effects);
            foreach (var member in node.Info.Members)
            {
                _members.TryAdd(member.Id, member);
                var flags = Flags(member.Effects);
                if (flags == EffectFlags.None) continue;
                _direct[member.Id] = flags;
                direct |= flags;
            }
            node.DirectEffects = direct;
            node.Effects = direct;
            Any |= direct != EffectFlags.None;
        }
        if (!Any) return;

        // Spread each kind back up the control links: whoever calls something that touches the network does too.
        var callers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in model.Graph.MemberEdges)
        {
            if (!ControlKinds.Contains(edge.Kind) || edge.Source == edge.Target) continue;
            if (!callers.TryGetValue(edge.Target, out var list)) callers[edge.Target] = list = new List<string>();
            list.Add(edge.Source);
        }
        foreach (var kind in Kinds)
        {
            var queue = new Queue<string>();
            foreach (var (member, flags) in _direct)
            {
                if ((flags & kind.Flag) == 0) continue;
                _reaches[member] = _reaches.GetValueOrDefault(member) | kind.Flag;
                queue.Enqueue(member);
            }
            while (queue.Count > 0)
            {
                var member = queue.Dequeue();
                if (!callers.TryGetValue(member, out var list)) continue;
                foreach (var caller in list)
                {
                    var flags = _reaches.GetValueOrDefault(caller);
                    if ((flags & kind.Flag) != 0) continue;
                    _reaches[caller] = flags | kind.Flag;
                    _next[(caller, kind.Flag)] = member;
                    queue.Enqueue(caller);
                }
            }
        }

        foreach (var node in model.Nodes.Values)
        {
            foreach (var member in node.Info.Members) node.Effects |= _reaches.GetValueOrDefault(member.Id);
            foreach (var kind in In(node.DirectEffects)) DirectCounts[kind.Flag] = DirectCounts.GetValueOrDefault(kind.Flag) + 1;
        }
    }

    /// <summary>
    /// How a member reaches an effect it doesn't have itself: the members on the way (starting with
    /// it, ending with the one that makes the call) and that call.
    /// </summary>
    public (List<string> Path, EffectInfo? Effect) Trace(string member, EffectFlags kind)
    {
        var path = new List<string> { member };
        var current = member;
        for (var i = 0; i < MaxTrace && (MemberDirect(current) & kind) == 0; i++)
        {
            if (!_next.TryGetValue((current, kind), out var next)) break;
            path.Add(next);
            current = next;
        }
        var effect = Member(current)?.Effects?.FirstOrDefault(e => FlagOf(e.Kind) == kind);
        return (path, effect);
    }

    /// <summary>Every direct effect of a class (its members' and its top-level code's), with the member making it.</summary>
    public static IEnumerable<(MemberInfo? Member, EffectInfo Effect)> DirectEffects(NodeData node)
    {
        foreach (var effect in node.Info.Effects ?? Enumerable.Empty<EffectInfo>()) yield return (null, effect);
        foreach (var member in node.Info.Members)
        {
            foreach (var effect in member.Effects ?? Enumerable.Empty<EffectInfo>()) yield return (member, effect);
        }
    }

    /// <summary>A class's effect kinds as "Network, Writes files".</summary>
    public static string Describe(EffectFlags flags) => string.Join(", ", In(flags).Select(k => k.Label));

    private static EffectFlags Flags(List<EffectInfo>? effects)
    {
        if (effects == null) return EffectFlags.None;
        var flags = EffectFlags.None;
        foreach (var effect in effects) flags |= FlagOf(effect.Kind);
        return flags;
    }

    private static Color Hex(uint rgb) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);
}
