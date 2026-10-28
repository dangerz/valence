using System.Collections.Generic;
using Godot;
using Valence.Schema;

namespace Valence.Viewer;

public enum ColorMode
{
    Visibility,
    Kind,
    Churn,
    Coupling,

    /// <summary>Churn × dependents × size, as percentiles (<see cref="RiskScores"/>).</summary>
    Risk,

    /// <summary>Hot: no entry point reaches it (<see cref="Insights.ComputeReach"/>).</summary>
    Reach,

    /// <summary>Hot: changed mostly by AI agents or bots in the churn window.</summary>
    Authors,

    /// <summary>Hot: most of its links are inferred, not certain.</summary>
    Trust,
}

public static class Palette
{
    /// <summary>The 3D world behind the atoms: pure black so they stand out against the blue-gray panels.</summary>
    public static readonly Color Background = Hex(0x000000);
    public static readonly Color Proton = Hex(0xff5a5f);
    public static readonly Color Neutron = Hex(0x7f9cc0);
    public static readonly Color Ionic = Hex(0x4cc9f0);
    public static readonly Color Covalent = Hex(0xff9f1c);
    public static readonly Color Selected = Hex(0xffffff);
    public static readonly Color MutedText = Hex(0x8b95a7);
    public static readonly Color Accent = Hex(0x7cc4ff);
    public static readonly Color PanelBorder = Hex(0x3a5a85);

    /// <summary>Simulation: a class the flow just reached.</summary>
    public static readonly Color Gold = Hex(0xffc83d);

    /// <summary>The opened system's bonds, ports and connected systems.</summary>
    public static readonly Color Connection = Hex(0xffe066);

    /// <summary>Compare / timeline: added since the snapshot or previous commit.</summary>
    public static readonly Color Added = Hex(0x51cf66);

    /// <summary>Compare: changed since the snapshot.</summary>
    public static readonly Color Changed = Hex(0xfcc419);

    private static readonly Color HeatCold = Hex(0x2a4b8d);
    private static readonly Color HeatMid = Hex(0xd6336c);
    private static readonly Color HeatHot = Hex(0xffd43b);

    /// <summary>Display order for edge kinds, strongest coupling first.</summary>
    public static readonly string[] EdgeKindOrder =
    {
        EdgeKinds.Inherits, EdgeKinds.Implements, EdgeKinds.Contains, EdgeKinds.Has,
        EdgeKinds.Creates, EdgeKinds.Calls, EdgeKinds.Uses, EdgeKinds.References,
    };

    private static readonly Dictionary<string, Color> EdgeColors = new()
    {
        [EdgeKinds.Inherits] = Hex(0xff6b6b),
        [EdgeKinds.Implements] = Hex(0xffa94d),
        [EdgeKinds.Contains] = Hex(0x868e96),
        [EdgeKinds.Has] = Hex(0xffd43b),
        [EdgeKinds.Creates] = Hex(0x69db7c),
        [EdgeKinds.Calls] = Hex(0x4dabf7),
        [EdgeKinds.Uses] = Hex(0x9775fa),
        [EdgeKinds.References] = Hex(0xadb5bd),
    };

    private static readonly Dictionary<string, Color> NodeKindColors = new()
    {
        [NodeKinds.Class] = Hex(0x4dabf7),
        [NodeKinds.Interface] = Hex(0xffd43b),
        [NodeKinds.Struct] = Hex(0x69db7c),
        [NodeKinds.Enum] = Hex(0xda77f2),
        [NodeKinds.Record] = Hex(0x38d9a9),
        [NodeKinds.RecordStruct] = Hex(0x38d9a9),
        [NodeKinds.Delegate] = Hex(0xff922b),
        [NodeKinds.Module] = Hex(0xa9e34b),
    };

    public static Color EdgeKind(string kind) =>
        EdgeColors.TryGetValue(kind, out var color) ? color : Hex(0xadb5bd);

    private static readonly Dictionary<string, Color> FlowColors = new()
    {
        [MemberEdgeKinds.Calls] = Hex(0x4dabf7),
        [MemberEdgeKinds.Creates] = Hex(0x69db7c),
        [MemberEdgeKinds.Raises] = Hex(0xff922b),
        [MemberEdgeKinds.Triggers] = Hex(0xf783ac),
        [MemberEdgeKinds.Reads] = Hex(0x9775fa),
        [MemberEdgeKinds.Writes] = Hex(0xffd43b),
    };

    /// <summary>Simulation pulse colors by member-edge kind.</summary>
    public static Color FlowKind(string kind) =>
        FlowColors.TryGetValue(kind, out var color) ? color : Hex(0xadb5bd);

    public static IEnumerable<KeyValuePair<string, Color>> FlowLegend => FlowColors;

    public static Color NodeKind(string kind) =>
        NodeKindColors.TryGetValue(kind, out var color) ? color : Hex(0xced4da);

    public static IEnumerable<KeyValuePair<string, Color>> NodeKindLegend => NodeKindColors;

    /// <summary>0 → cold blue, 0.5 → magenta, 1 → hot yellow.</summary>
    public static Color Heat(float t)
    {
        t = Mathf.Clamp(t, 0f, 1f);
        return t < 0.5f ? HeatCold.Lerp(HeatMid, t * 2f) : HeatMid.Lerp(HeatHot, (t - 0.5f) * 2f);
    }

    public static Color MoleculeHue(string id) => Color.FromHsv(Hash01(id), 0.55f, 0.95f);

    public static string Html(Color color) => "#" + color.ToHtml(false);

    /// <summary>FNV-1a. string.GetHashCode is randomized per process; layouts must be stable.</summary>
    public static uint Hash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return hash;
    }

    public static float Hash01(string text) => Hash(text) / (float)uint.MaxValue;

    private static Color Hex(uint rgb) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f);
}
