using System.Reflection;
using System.Runtime.InteropServices;
using TreeSitter;

namespace Valence.Syntax;

/// <summary>
/// Loads tree-sitter and its grammars in-process. The TreeSitter.DotNet package ships them as native
/// libraries under runtimes/&lt;rid&gt;/native. Its own loader looks under the app's base directory,
/// which a host like Godot sets elsewhere, so both the core library and the grammars are also looked
/// up next to the bindings assembly.
/// </summary>
public static class TreeSitterRuntime
{
    private const string CoreLibrary = "tree-sitter";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Language> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static bool _resolverInstalled;

    /// <summary>
    /// The grammar for a language id as the bindings spell it ("Python", "PHP", "C#"), loaded once
    /// and kept for the life of the process. Throws when a native library can't be loaded.
    /// </summary>
    public static Language Load(string id, Action<string>? log = null)
    {
        lock (Gate)
        {
            InstallResolver();
            if (Loaded.TryGetValue(id, out var cached)) return cached;

            var libraryId = LibraryId(id);
            var path = Find("tree-sitter-" + libraryId);
            var language = path != null
                ? new Language(path, "tree_sitter_" + libraryId.Replace('-', '_'))
                : new Language(id);
            // Reading the ABI version is the first call into the core library, so this proves both load.
            log?.Invoke($"tree-sitter: {id} grammar (ABI {language.AbiVersion}) from {path ?? "the default search path"}; " +
                        $"core from {Find(CoreLibrary) ?? "the default search path"}");
            Loaded[id] = language;
            return language;
        }
    }

    /// <summary>The full path of one of the package's native libraries, or null if it isn't found.</summary>
    public static string? Find(string library)
    {
        var file = NativeFileName(library);
        return SearchDirectories().Select(dir => Path.Combine(dir, file)).FirstOrDefault(File.Exists);
    }

    /// <summary>Where native libraries are looked for, most specific first (for diagnostics).</summary>
    public static IEnumerable<string> SearchDirectories()
    {
        var bases = new[]
            {
                Path.GetDirectoryName(typeof(Parser).Assembly.Location),
                Path.GetDirectoryName(typeof(TreeSitterRuntime).Assembly.Location),
                AppContext.BaseDirectory,
            }
            .Where(dir => !string.IsNullOrEmpty(dir))
            .Select(dir => dir!)
            .Distinct(StringComparer.Ordinal);
        foreach (var dir in bases)
        {
            yield return Path.Combine(dir, "runtimes", RuntimeId(), "native");
            yield return dir;
        }
    }

    /// <summary>Resolves the bindings' [DllImport("tree-sitter")]; anything else falls through to the runtime.</summary>
    private static void InstallResolver()
    {
        if (_resolverInstalled) return;
        _resolverInstalled = true;
        try
        {
            System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(Parser).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // A resolver is already set for the bindings; the runtime's own lookup still applies.
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != CoreLibrary || Find(CoreLibrary) is not { } path) return IntPtr.Zero;
        return System.Runtime.InteropServices.NativeLibrary.TryLoad(path, out var handle) ? handle : IntPtr.Zero;
    }

    /// <summary>Same mapping as the bindings: "C#" → c-sharp, "C++" → cpp, otherwise lower case.</summary>
    private static string LibraryId(string id) => id switch
    {
        "C++" => "cpp",
        "C#" => "c-sharp",
        _ => id.ToLowerInvariant(),
    };

    private static string NativeFileName(string library)
    {
        if (OperatingSystem.IsWindows()) return library + ".dll";
        if (OperatingSystem.IsMacOS()) return "lib" + library + ".dylib";
        return "lib" + library + ".so";
    }

    private static string RuntimeId()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        return $"{os}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
    }
}
