using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace Valence.Probe.CSharp;

internal sealed class ProjectInfo
{
    public required string Name { get; init; }

    /// <summary>Full path of the .csproj, or null for the synthetic "loose files" project.</summary>
    public string? ProjectFile { get; init; }
    public required string Dir { get; init; }
    public required string ModuleId { get; init; }
    public List<string> ProjectReferences { get; } = new();
    public List<string> DefineConstants { get; } = new();
    public List<string> MetadataReferencePaths { get; } = new();
    public List<string> SourceFiles { get; } = new();

    /// <summary>
    /// SDK projects with ImplicitUsings get their global usings from a generated file in obj/,
    /// which we never create; the analyzer synthesizes the same usings in memory instead.
    /// </summary>
    public bool ImplicitUsings { get; set; }
}

/// <summary>
/// Reads .csproj files as plain XML. We deliberately don't evaluate them with MSBuild:
/// that would run a restore and write obj/ folders into the repo we promised not to touch.
/// </summary>
internal static class ProjectDiscovery
{
    private static readonly string[] PreferredFrameworks =
    {
        "net9.0", "net8.0", "net7.0", "net6.0", "net5.0", "netcoreapp3.1", "netstandard2.1", "netstandard2.0",
    };

    public static List<ProjectInfo> Discover(string root, Action<string> log)
    {
        var projects = new List<ProjectInfo>();
        foreach (var file in RepoScanner.Files(root, ".csproj"))
        {
            var rel = RepoScanner.Relative(root, file);
            var info = new ProjectInfo
            {
                Name = Path.GetFileNameWithoutExtension(file),
                ProjectFile = file,
                Dir = Path.GetDirectoryName(file)!,
                ModuleId = "cs:project:" + rel,
            };
            try
            {
                ReadProjectFile(info, log);
            }
            catch (Exception e)
            {
                log($"warning: could not read {rel}: {e.Message}");
            }
            projects.Add(info);
        }
        return projects;
    }

    private static void ReadProjectFile(ProjectInfo info, Action<string> log)
    {
        var doc = XDocument.Load(info.ProjectFile!);
        var rootEl = doc.Root!;

        foreach (var el in rootEl.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            var include = el.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include)) continue;
            var path = Path.GetFullPath(Path.Combine(info.Dir, include.Replace('\\', Path.DirectorySeparatorChar)));
            info.ProjectReferences.Add(path);
        }

        info.ImplicitUsings = rootEl.Descendants()
            .Any(e => e.Name.LocalName == "ImplicitUsings" && e.Value.Trim() is "enable" or "true");

        foreach (var el in rootEl.Descendants().Where(e => e.Name.LocalName == "DefineConstants"))
        {
            foreach (var c in el.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!c.StartsWith("$(")) info.DefineConstants.Add(c);
            }
        }

        // Resolve SDK-provided and package assemblies from the local NuGet cache (read-only).
        var sdk = rootEl.Attribute("Sdk")?.Value;
        if (sdk != null && sdk.StartsWith("Godot.NET.Sdk/", StringComparison.OrdinalIgnoreCase))
        {
            var version = sdk["Godot.NET.Sdk/".Length..];
            AddPackageAssemblies(info, "GodotSharp", version, log);
            info.DefineConstants.AddRange(new[] { "GODOT", "TOOLS" });
        }

        foreach (var el in rootEl.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
        {
            var id = el.Attribute("Include")?.Value;
            var version = el.Attribute("Version")?.Value ?? el.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
            if (id != null && version != null) AddPackageAssemblies(info, id, version, log);
        }
    }

    private static void AddPackageAssemblies(ProjectInfo info, string id, string version, Action<string> log)
    {
        var cache = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var libDir = Path.Combine(cache, id.ToLowerInvariant(), version.Trim('[', ']', ' '), "lib");
        if (!Directory.Exists(libDir))
        {
            log($"note: package {id} {version} not in local NuGet cache; its types will be unresolved");
            return;
        }

        var tfm = PreferredFrameworks.FirstOrDefault(f => Directory.Exists(Path.Combine(libDir, f)))
                  ?? Directory.GetDirectories(libDir).Select(d => Path.GetFileName(d)).FirstOrDefault();
        if (tfm == null) return;
        info.MetadataReferencePaths.AddRange(Directory.GetFiles(Path.Combine(libDir, tfm), "*.dll"));
    }

    /// <summary>Assigns each file to the deepest project folder containing it.</summary>
    public static void AssignFiles(string root, List<ProjectInfo> projects, IEnumerable<string> files, out ProjectInfo? loose)
    {
        loose = null;
        var byDepth = projects.OrderByDescending(p => p.Dir.Length).ToList();
        foreach (var file in files)
        {
            var owner = byDepth.FirstOrDefault(p => RepoScanner.IsUnder(file, p.Dir));
            if (owner == null)
            {
                loose ??= new ProjectInfo { Name = "(loose files)", Dir = root, ModuleId = "cs:project:(loose)" };
                owner = loose;
            }
            owner.SourceFiles.Add(file);
        }
    }

    public static IEnumerable<MetadataReference> PlatformReferences()
    {
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "";
        return tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => Path.GetFileName(p).StartsWith("System", StringComparison.Ordinal)
                        || Path.GetFileName(p) is "mscorlib.dll" or "netstandard.dll")
            .Select(p => MetadataReference.CreateFromFile(p));
    }
}
