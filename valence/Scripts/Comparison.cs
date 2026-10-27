using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Godot;
using Valence.Schema;

namespace Valence.Viewer;

/// <summary>
/// The current graph compared with a saved snapshot: classes added since, changed (size, members or
/// dependencies), and removed (named only, since they're no longer in the scene).
/// </summary>
public sealed class Comparison
{
    private Comparison(string label)
    {
        Label = label;
    }

    public string Label { get; }
    public HashSet<string> Added { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Changed { get; } = new(StringComparer.Ordinal);
    public List<NodeInfo> Removed { get; } = new();

    public static Comparison Build(Graph snapshot, GraphModel current, string label)
    {
        var comparison = new Comparison(label);
        var before = new Dictionary<string, NodeInfo>(StringComparer.Ordinal);
        foreach (var node in snapshot.Nodes) before.TryAdd(node.Id, node);

        var beforeEdges = snapshot.Edges
            .GroupBy(e => e.Source)
            .ToDictionary(g => g.Key, g => new HashSet<string>(g.Select(e => $"{e.Target}|{e.Kind}")), StringComparer.Ordinal);

        foreach (var node in current.Nodes.Values)
        {
            var id = node.Info.Id;
            if (!before.TryGetValue(id, out var old))
            {
                comparison.Added.Add(id);
                continue;
            }
            var edgesNow = new HashSet<string>(node.Outgoing.Select(e => $"{e.Target}|{e.Kind}"));
            var edgesThen = beforeEdges.GetValueOrDefault(id) ?? new HashSet<string>();
            var changed = Math.Abs(node.Metric(MetricKeys.Loc) - old.Metrics.GetValueOrDefault(MetricKeys.Loc)) > 0.5
                          || node.Info.Members.Count != old.Members.Count
                          || !edgesNow.SetEquals(edgesThen);
            if (changed) comparison.Changed.Add(id);
        }

        foreach (var (id, node) in before)
        {
            if (!current.Nodes.ContainsKey(id)) comparison.Removed.Add(node);
        }
        comparison.Removed.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return comparison;
    }
}

/// <summary>Saved copies of a repo's graph in ~/.valence/snapshots/&lt;repo&gt;, newest first.</summary>
public static class SnapshotStore
{
    public static string Save(Graph graph)
    {
        var commit = string.IsNullOrEmpty(graph.Repo.Commit) ? "" : "_" + graph.Repo.Commit[..Math.Min(8, graph.Repo.Commit.Length)];
        var path = Path.Combine(DirectoryFor(graph.Repo.Root), $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}{commit}.valence.json");
        ValenceJson.Write(path, graph);
        return path;
    }

    /// <summary>(menu label, file path), newest first.</summary>
    public static List<(string Label, string Path)> List(string repoRoot)
    {
        var dir = DirectoryFor(repoRoot);
        if (!Directory.Exists(dir)) return new List<(string, string)>();
        return Directory.GetFiles(dir, "*.valence.json")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .Select(f => (LabelFor(f), f))
            .ToList();
    }

    /// <summary>"2026-10-26_14-03-12_ab12cd34.valence.json" → "2026-10-26 14:03:12 · ab12cd34".</summary>
    public static string LabelFor(string path)
    {
        var name = System.IO.Path.GetFileName(path).Replace(".valence.json", "");
        var parts = name.Split('_');
        if (parts.Length < 2) return name;
        var label = $"{parts[0]} {parts[1].Replace('-', ':')}";
        return parts.Length > 2 ? $"{label} · {parts[2]}" : label;
    }

    private static string DirectoryFor(string repoRoot)
    {
        var key = string.IsNullOrEmpty(repoRoot) ? "(unknown)" : repoRoot;
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..8].ToLowerInvariant();
        var name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(key));
        foreach (var bad in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        return System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            ".valence", "snapshots", $"{name}-{hash}");
    }
}
