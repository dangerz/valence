using System;
using System.Collections.Generic;
using System.Linq;
using Valence.Schema;

namespace Valence.Viewer;

public enum FlowDirection
{
    /// <summary>What the start reaches: follow calls, creates, raises and triggers forward.</summary>
    Downstream,

    /// <summary>What reaches the start: follow the same links backwards.</summary>
    Upstream,
}

/// <summary>One link out of (or into) a flow point.</summary>
public readonly record struct FlowLink(string Other, string Kind, int Order, bool Likely);

/// <summary>
/// The graph the simulation walks. Member level (methods, fields, events) when the probe recorded
/// it; otherwise it falls back to class level so older graphs can still be simulated roughly.
/// </summary>
public sealed class FlowGraph
{
    private static readonly IReadOnlyList<FlowLink> None = Array.Empty<FlowLink>();

    private readonly Dictionary<string, List<FlowLink>> _out = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<FlowLink>> _in = new(StringComparer.Ordinal);
    private readonly Func<string, NodeData?> _owner;

    private FlowGraph(bool memberLevel, Func<string, NodeData?> owner)
    {
        MemberLevel = memberLevel;
        _owner = owner;
    }

    public bool MemberLevel { get; }

    public static FlowGraph For(GraphModel model)
    {
        if (model.Graph.MemberEdges.Count > 0)
        {
            var graph = new FlowGraph(true, id => model.MemberOwners.GetValueOrDefault(id));
            foreach (var edge in model.Graph.MemberEdges)
            {
                graph.Add(edge.Source, edge.Target, edge.Kind, edge.Order, edge.Confidence != null);
            }
            graph.Sort();
            return graph;
        }

        // Class level: calls/creates as control flow, field/property use as reads.
        var fallback = new FlowGraph(false, id => model.Nodes.GetValueOrDefault(id));
        foreach (var node in model.Nodes.Values)
        {
            var order = 0;
            foreach (var edge in node.Outgoing.OrderByDescending(e => e.Weight))
            {
                var kind = edge.Kind switch
                {
                    EdgeKinds.Calls => MemberEdgeKinds.Calls,
                    EdgeKinds.Creates => MemberEdgeKinds.Creates,
                    EdgeKinds.Uses => MemberEdgeKinds.Reads,
                    _ => null,
                };
                if (kind != null) fallback.Add(edge.Source, edge.Target, kind, order++, edge.Confidence != null);
            }
        }
        fallback.Sort();
        return fallback;
    }

    public IReadOnlyList<FlowLink> Out(string id) => _out.TryGetValue(id, out var list) ? list : None;

    public IReadOnlyList<FlowLink> In(string id) => _in.TryGetValue(id, out var list) ? list : None;

    /// <summary>The class a flow point (member or class id) belongs to.</summary>
    public NodeData? OwnerOf(string id) => _owner(id);

    /// <summary>Where a flow "from this class" starts: its members that take part in any flow.</summary>
    public List<string> StartPointsFor(NodeData node) => MemberLevel
        ? node.Info.Members.Select(m => m.Id).Where(id => _out.ContainsKey(id) || _in.ContainsKey(id)).ToList()
        : new List<string> { node.Info.Id };

    private void Add(string source, string target, string kind, int order, bool likely)
    {
        Bucket(_out, source).Add(new FlowLink(target, kind, order, likely));
        Bucket(_in, target).Add(new FlowLink(source, kind, order, likely));
    }

    private void Sort()
    {
        foreach (var list in _out.Values) list.Sort((a, b) => a.Order.CompareTo(b.Order));
        foreach (var list in _in.Values) list.Sort((a, b) => a.Order.CompareTo(b.Order));
    }

    private static List<FlowLink> Bucket(Dictionary<string, List<FlowLink>> map, string key)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<FlowLink>();
            map[key] = list;
        }
        return list;
    }
}

/// <summary>One pulse: data/control moving From → To between Start and Arrive (seconds at 1×).</summary>
public sealed record FlowEvent(string From, string To, string Kind, float Start, float Arrive, bool Likely);

public sealed class FlowPlan
{
    public FlowPlan(string title, List<FlowEvent> events, int reached, bool truncated)
    {
        Title = title;
        Events = events;
        Reached = reached;
        Truncated = truncated;
        Duration = events.Count == 0 ? 0f : events.Max(e => e.Arrive);
    }

    public string Title { get; }

    /// <summary>Sorted by <see cref="FlowEvent.Start"/> (and so by Arrive, since travel time is constant).</summary>
    public List<FlowEvent> Events { get; }

    /// <summary>Distinct flow points (members or classes) reached.</summary>
    public int Reached { get; }
    public bool Truncated { get; }
    public float Duration { get; }
}

/// <summary>
/// Plans a static "what would happen" wave: from the start points, follow control links hop by
/// hop. Within a member, links fire in source order a beat apart, so a method's calls play in
/// the order they're written. Each point expands once per plan. Nothing is executed.
/// </summary>
public static class FlowSimulator
{
    public const float Travel = 0.5f;
    public const float SiblingGap = 0.12f;
    public const float StartStagger = 0.2f;
    private const int MaxDepth = 18;
    private const int MaxEvents = 5000;

    private static readonly HashSet<string> ControlKinds = new(StringComparer.Ordinal)
    {
        MemberEdgeKinds.Calls, MemberEdgeKinds.Creates, MemberEdgeKinds.Raises, MemberEdgeKinds.Triggers,
    };

    private static readonly HashSet<string> DataKinds = new(StringComparer.Ordinal)
    {
        MemberEdgeKinds.Reads, MemberEdgeKinds.Writes,
    };

    public static FlowPlan Build(FlowGraph graph, IReadOnlyList<string> starts, FlowDirection direction, bool includeData, string title)
    {
        var events = new List<FlowEvent>();
        var visited = new HashSet<string>(starts, StringComparer.Ordinal);
        var queue = new Queue<(string Id, float Time, int Depth)>();
        for (var i = 0; i < starts.Count; i++) queue.Enqueue((starts[i], i * StartStagger, 0));

        var truncated = false;
        while (queue.Count > 0 && !truncated)
        {
            var (id, time, depth) = queue.Dequeue();
            var links = direction == FlowDirection.Downstream ? graph.Out(id) : graph.In(id);
            var slot = 0;
            foreach (var link in links)
            {
                var control = ControlKinds.Contains(link.Kind);
                if (!control && !(includeData && DataKinds.Contains(link.Kind))) continue;
                if (events.Count >= MaxEvents)
                {
                    truncated = true;
                    break;
                }

                var start = time + slot++ * SiblingGap;
                var arrive = start + Travel;
                // Pulses always move the way the call/data actually goes, even when the wave
                // expands upstream. A read moves data from the field to the reader.
                var (from, to) = direction == FlowDirection.Downstream ? (id, link.Other) : (link.Other, id);
                if (link.Kind == MemberEdgeKinds.Reads) (from, to) = (to, from);
                events.Add(new FlowEvent(from, to, link.Kind, start, arrive, link.Likely));

                if (control && depth < MaxDepth && visited.Add(link.Other)) queue.Enqueue((link.Other, arrive, depth + 1));
            }
        }

        events.Sort((a, b) => a.Start.CompareTo(b.Start));
        return new FlowPlan(title, events, visited.Count, truncated);
    }
}
