using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Godot;
using Valence.Schema;
using FileAccess = Godot.FileAccess;

namespace Valence.Viewer;

public static class GraphIO
{
    /// <summary>Loads a graph from a filesystem path or a res:// / user:// path.</summary>
    public static Graph Load(string path)
    {
        string text;
        if (path.StartsWith("res://", StringComparison.Ordinal) || path.StartsWith("user://", StringComparison.Ordinal))
        {
            if (!FileAccess.FileExists(path)) throw new FileNotFoundException("Graph not found", path);
            text = FileAccess.GetFileAsString(path);
        }
        else
        {
            text = File.ReadAllText(path);
        }

        var graph = JsonSerializer.Deserialize<Graph>(text, ValenceJson.Options)
                    ?? throw new InvalidDataException("Empty graph file");
        if (!graph.Schema.StartsWith("valence.graph/", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Not a Valence graph (schema '{graph.Schema}')");
        }
        return graph;
    }
}

/// <summary>
/// Positions of systems the user dragged, per repo, in user://layouts. Only X/Z are stored:
/// height always comes from the dependency layer.
/// </summary>
public static class LayoutStore
{
    public static Dictionary<string, Vector2> Load(string repoRoot)
    {
        var result = new Dictionary<string, Vector2>(StringComparer.Ordinal);
        try
        {
            var path = PathFor(repoRoot);
            if (!File.Exists(path)) return result;
            var stored = JsonSerializer.Deserialize<Dictionary<string, float[]>>(File.ReadAllText(path));
            if (stored == null) return result;
            foreach (var (id, xz) in stored)
            {
                if (xz.Length == 2) result[id] = new Vector2(xz[0], xz[1]);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            GD.PushWarning($"Could not read saved layout: {e.Message}");
        }
        return result;
    }

    public static void Save(string repoRoot, Dictionary<string, Vector2> pins)
    {
        try
        {
            var path = PathFor(repoRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stored = new Dictionary<string, float[]>();
            foreach (var (id, xz) in pins) stored[id] = new[] { xz.X, xz.Y };
            File.WriteAllText(path, JsonSerializer.Serialize(stored));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GD.PushWarning($"Could not save layout: {e.Message}");
        }
    }

    private static string PathFor(string repoRoot)
    {
        var key = string.IsNullOrEmpty(repoRoot) ? "(unknown)" : repoRoot;
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        return Path.Combine(ProjectSettings.GlobalizePath("user://layouts"), hash + ".json");
    }
}

public enum Visibility
{
    Shown = 0,
    Quiet = 1,
    Hidden = 2,
}

/// <summary>
/// Hidden/quiet items per repo, in user://visibility. Keys are "molecule:&lt;id&gt;", "atom:&lt;id&gt;"
/// or "node:&lt;id&gt;"; anything not listed is shown.
/// </summary>
public static class VisibilityStore
{
    public static Dictionary<string, Visibility> Load(string repoRoot)
    {
        var result = new Dictionary<string, Visibility>(StringComparer.Ordinal);
        try
        {
            var path = PathFor(repoRoot);
            if (!File.Exists(path)) return result;
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (stored == null) return result;
            foreach (var (key, value) in stored)
            {
                if (Enum.TryParse<Visibility>(value, ignoreCase: true, out var state) && state != Visibility.Shown) result[key] = state;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            GD.PushWarning($"Could not read saved visibility: {e.Message}");
        }
        return result;
    }

    public static void Save(string repoRoot, Dictionary<string, Visibility> states)
    {
        try
        {
            var path = PathFor(repoRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stored = new Dictionary<string, string>();
            foreach (var (key, state) in states) stored[key] = state.ToString().ToLowerInvariant();
            File.WriteAllText(path, JsonSerializer.Serialize(stored));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GD.PushWarning($"Could not save visibility: {e.Message}");
        }
    }

    private static string PathFor(string repoRoot)
    {
        var key = string.IsNullOrEmpty(repoRoot) ? "(unknown)" : repoRoot;
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        return Path.Combine(ProjectSettings.GlobalizePath("user://visibility"), hash + ".json");
    }
}

/// <summary>Viewer preferences in user://viewer.json: last graph, recent folders, panel visibility.</summary>
public sealed class ViewerSettings
{
    private const int MaxRecent = 10;

    public string? LastGraph { get; set; }
    public List<string> RecentFolders { get; set; } = new();
    public bool ShowOutline { get; set; } = true;
    public bool ShowMethods { get; set; } = true;
    public bool ShowInspector { get; set; } = true;
    public bool ShowLegend { get; set; } = true;

    /// <summary>How visible "quiet" items are, 0..1.</summary>
    public float QuietFade { get; set; } = 0.15f;

    private static string SettingsPath => ProjectSettings.GlobalizePath("user://viewer.json");

    public static ViewerSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<ViewerSettings>(File.ReadAllText(SettingsPath), ValenceJson.Options)
                       ?? new ViewerSettings();
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            GD.PushWarning($"Could not read viewer settings: {e.Message}");
        }
        return new ViewerSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, ValenceJson.Indented));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            GD.PushWarning($"Could not save viewer settings: {e.Message}");
        }
    }

    public void AddRecentFolder(string folder)
    {
        RecentFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.Ordinal));
        RecentFolders.Insert(0, folder);
        if (RecentFolders.Count > MaxRecent) RecentFolders.RemoveRange(MaxRecent, RecentFolders.Count - MaxRecent);
    }
}
