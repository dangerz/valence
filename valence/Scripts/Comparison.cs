using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

/// <summary>One saved snapshot: its file, when it was taken, the commit, and an optional name.</summary>
public sealed record SnapshotEntry(string Path, string Stamp, string? Commit, string? Name, long Bytes)
{
    /// <summary>The name with its date ("Before refactor · 2026-10-26 14:03"), or the date and commit.</summary>
    public string Label => Name != null
        ? $"{Name} · {(Stamp.Length >= 16 ? Stamp[..16] : Stamp)}"
        : Commit != null ? $"{Stamp} · {Commit}" : Stamp;
}

/// <summary>
/// Saved copies of a repo's graph in ~/.valence/snapshots/&lt;repo&gt;, newest first. Names given in
/// the Snapshots dialog live in names.json beside them, so file names (and their order) never change.
/// </summary>
public static class SnapshotStore
{
    private const string NamesFile = "names.json";

    public static string Save(Graph graph)
    {
        var commit = string.IsNullOrEmpty(graph.Repo.Commit) ? "" : "_" + graph.Repo.Commit[..Math.Min(8, graph.Repo.Commit.Length)];
        var path = Path.Combine(DirectoryFor(graph.Repo.Root), $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}{commit}.valence.json");
        ValenceJson.Write(path, graph);
        return path;
    }

    /// <summary>Every snapshot of the repo, newest first.</summary>
    public static List<SnapshotEntry> Entries(string repoRoot)
    {
        var dir = DirectoryFor(repoRoot);
        if (!Directory.Exists(dir)) return new List<SnapshotEntry>();
        var names = ReadNames(dir);
        return Directory.GetFiles(dir, "*.valence.json")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .Select(f => EntryFor(f, names))
            .ToList();
    }

    /// <summary>(menu label, file path), newest first.</summary>
    public static List<(string Label, string Path)> List(string repoRoot) =>
        Entries(repoRoot).Select(e => (e.Label, e.Path)).ToList();

    public static string LabelFor(string path) =>
        EntryFor(path, ReadNames(System.IO.Path.GetDirectoryName(path) ?? "")).Label;

    /// <summary>Names a snapshot; an empty name goes back to the date.</summary>
    public static void Rename(string path, string name)
    {
        var dir = System.IO.Path.GetDirectoryName(path) ?? "";
        var names = ReadNames(dir);
        var file = System.IO.Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name)) names.Remove(file);
        else names[file] = name.Trim();
        WriteNames(dir, names);
    }

    /// <summary>Moves a snapshot to the system trash (deletes it if there's no trash). Null on success.</summary>
    public static string? Delete(string path)
    {
        var error = OS.MoveToTrash(path);
        if (error != Error.Ok)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return e.Message;
            }
        }
        var dir = System.IO.Path.GetDirectoryName(path) ?? "";
        var names = ReadNames(dir);
        if (names.Remove(System.IO.Path.GetFileName(path))) WriteNames(dir, names);
        return null;
    }

    /// <summary>"2026-10-26_14-03-12_ab12cd34.valence.json" → stamp "2026-10-26 14:03:12", commit "ab12cd34".</summary>
    private static SnapshotEntry EntryFor(string path, Dictionary<string, string> names)
    {
        var file = System.IO.Path.GetFileName(path);
        var parts = file.Replace(".valence.json", "").Split('_');
        var stamp = parts.Length >= 2 ? $"{parts[0]} {parts[1].Replace('-', ':')}" : parts[0];
        var commit = parts.Length > 2 ? parts[2] : null;
        long bytes;
        try
        {
            bytes = new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            bytes = 0;
        }
        return new SnapshotEntry(path, stamp, commit, names.GetValueOrDefault(file), bytes);
    }

    private static Dictionary<string, string> ReadNames(string dir)
    {
        try
        {
            var path = System.IO.Path.Combine(dir, NamesFile);
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                       ?? new Dictionary<string, string>();
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            GD.PushWarning($"Could not read snapshot names: {e.Message}");
        }
        return new Dictionary<string, string>();
    }

    private static void WriteNames(string dir, Dictionary<string, string> names)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(System.IO.Path.Combine(dir, NamesFile), JsonSerializer.Serialize(names));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GD.PushWarning($"Could not save snapshot names: {e.Message}");
        }
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
