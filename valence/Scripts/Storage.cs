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

/// <summary>Remembers the last opened graph.</summary>
public static class ViewerSettings
{
    private static string SettingsPath => ProjectSettings.GlobalizePath("user://viewer.json");

    public static string? LastGraph
    {
        get
        {
            try
            {
                if (!File.Exists(SettingsPath)) return null;
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsPath));
                return values != null && values.TryGetValue("lastGraph", out var path) ? path : null;
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        set
        {
            try
            {
                var values = new Dictionary<string, string> { ["lastGraph"] = value ?? "" };
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(values));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                GD.PushWarning($"Could not save viewer settings: {e.Message}");
            }
        }
    }
}
