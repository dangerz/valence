using Valence.Schema;

namespace Valence.Core;

/// <summary>Merges probe outputs into one graph and derives cross-language metrics.</summary>
internal static class GraphBuilder
{
    public static Graph Build(string root, IReadOnlyList<ProbeOutput> outputs, GitHistory? git, int? churnDays)
    {
        var graph = new Graph
        {
            Generator = new ToolInfo
            {
                Name = "valence",
                Version = typeof(GraphBuilder).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            },
            Repo = new RepoInfo
            {
                Root = root,
                Name = Path.GetFileName(root),
                Commit = git?.Commit,
                Branch = git?.Branch,
                GeneratedAt = DateTimeOffset.UtcNow,
                ChurnWindowDays = git != null ? churnDays : null,
            },
        };

        var modules = new Dictionary<string, ModuleInfo>(StringComparer.Ordinal);
        var nodes = new Dictionary<string, NodeInfo>(StringComparer.Ordinal);
        var edges = new Dictionary<(string, string, string), EdgeInfo>();

        foreach (var output in outputs)
        {
            graph.Probes.Add(output.Probe);
            graph.Diagnostics.AddRange(output.Diagnostics);

            foreach (var module in output.Modules) modules.TryAdd(module.Id, module);
            foreach (var node in output.Nodes)
            {
                if (!nodes.TryAdd(node.Id, node))
                {
                    graph.Diagnostics.Add(new DiagnosticInfo
                    {
                        Level = "warning",
                        Message = $"duplicate node id {node.Id} from probe {output.Probe.Name}; keeping the first",
                    });
                }
            }
            foreach (var edge in output.Edges)
            {
                var key = (edge.Source, edge.Target, edge.Kind);
                if (edges.TryGetValue(key, out var existing)) existing.Weight += edge.Weight;
                else edges[key] = edge;
            }
        }

        // Probes should only emit edges between their own nodes; drop anything dangling.
        var validEdges = edges.Values
            .Where(e => nodes.ContainsKey(e.Source) && nodes.ContainsKey(e.Target) && e.Source != e.Target)
            .ToList();

        AddFanMetrics(nodes, validEdges);
        if (git != null) AddGitMetrics(nodes.Values, git);

        graph.Modules = modules.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
        graph.Nodes = nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal).ToList();
        graph.Edges = validEdges;
        graph.ModuleEdges = AggregateModuleEdges(nodes, validEdges);
        return graph;
    }

    /// <summary>Distinct dependency counterparts; "contains" (nesting) isn't a dependency.</summary>
    private static void AddFanMetrics(Dictionary<string, NodeInfo> nodes, List<EdgeInfo> edges)
    {
        var fanIn = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var fanOut = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var edge in edges.Where(e => e.Kind != EdgeKinds.Contains))
        {
            Bucket(fanOut, edge.Source).Add(edge.Target);
            Bucket(fanIn, edge.Target).Add(edge.Source);
        }
        foreach (var node in nodes.Values)
        {
            node.Metrics[MetricKeys.FanIn] = fanIn.TryGetValue(node.Id, out var i) ? i.Count : 0;
            node.Metrics[MetricKeys.FanOut] = fanOut.TryGetValue(node.Id, out var o) ? o.Count : 0;
        }
    }

    private static void AddGitMetrics(IEnumerable<NodeInfo> nodes, GitHistory git)
    {
        foreach (var node in nodes)
        {
            var commits = new HashSet<int>();
            long churn = 0, lastChanged = 0;
            var files = node.Files.Count > 0 ? node.Files : new List<string> { node.File };
            foreach (var file in files)
            {
                if (!git.Files.TryGetValue(file, out var history)) continue;
                commits.UnionWith(history.Commits);
                churn += history.Churn;
                lastChanged = Math.Max(lastChanged, history.LastChanged);
            }
            node.Metrics[MetricKeys.Commits] = commits.Count;
            node.Metrics[MetricKeys.Churn] = churn;
            if (lastChanged > 0) node.Metrics[MetricKeys.LastChanged] = lastChanged;
        }
    }

    private static List<ModuleEdgeInfo> AggregateModuleEdges(Dictionary<string, NodeInfo> nodes, List<EdgeInfo> edges)
    {
        var result = new Dictionary<(string, string), ModuleEdgeInfo>();
        foreach (var edge in edges)
        {
            if (edge.Kind == EdgeKinds.Contains) continue;
            var source = nodes[edge.Source].Module;
            var target = nodes[edge.Target].Module;
            if (source == target) continue;

            if (!result.TryGetValue((source, target), out var moduleEdge))
            {
                moduleEdge = new ModuleEdgeInfo { Source = source, Target = target };
                result[(source, target)] = moduleEdge;
            }
            moduleEdge.Weight += edge.Weight;
            moduleEdge.Kinds[edge.Kind] = moduleEdge.Kinds.GetValueOrDefault(edge.Kind) + edge.Weight;
        }
        return result.Values
            .OrderBy(e => e.Source, StringComparer.Ordinal)
            .ThenBy(e => e.Target, StringComparer.Ordinal)
            .ToList();
    }

    private static HashSet<string> Bucket(Dictionary<string, HashSet<string>> map, string key)
    {
        if (!map.TryGetValue(key, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            map[key] = set;
        }
        return set;
    }
}
