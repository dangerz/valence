using System.Text.RegularExpressions;
using Valence.Schema;

namespace Valence.Core;

/// <summary>For probe logs: "12 network, 30 file-read, …" over every member's effects.</summary>
public static class EffectSummary
{
    public static string Describe(IEnumerable<NodeInfo> nodes)
    {
        var counts = nodes
            .SelectMany(n => (n.Effects ?? Enumerable.Empty<EffectInfo>()).Concat(n.Members.SelectMany(m => m.Effects ?? Enumerable.Empty<EffectInfo>())))
            .GroupBy(e => e.Kind)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {g.Key}")
            .ToList();
        return counts.Count == 0 ? "none" : string.Join(", ", counts);
    }
}

/// <summary>
/// What a call into code outside the repo does to the world (<see cref="EffectKinds"/>): the Boundary
/// lens's catalog, shared by the probes. .NET calls are matched by declaring type and member name
/// (constructors are "new"), Python calls by the dotted path they resolve to through imports
/// ("requests.Session().get", "builtins.open"). Patterns are globs: <c>*</c> matches within one name,
/// <c>**</c> matches anything. First match wins, so specific rules come before broad ones. A rule with
/// no kind says "nothing outside" (<c>new FileInfo(path)</c> doesn't touch the disk).
/// </summary>
public static class EffectCatalog
{
    /// <summary>Read or write, depending on the open mode.</summary>
    private const string FileOpen = "file-open";

    private const string Read = EffectKinds.FileRead;
    private const string Write = EffectKinds.FileWrite;
    private const string Net = EffectKinds.Network;
    private const string Db = EffectKinds.Database;
    private const string Proc = EffectKinds.Process;
    private const string Env = EffectKinds.Environment;
    private const string Native = EffectKinds.Native;
    private const string Dynamic = EffectKinds.Dynamic;

    /// <summary>At most this many distinct effects are kept per member.</summary>
    private const int MaxPerMember = 32;

    private static readonly Regex WriteMode = new(@"Write|Create|Append|Truncate", RegexOptions.Compiled);
    private static readonly Regex PythonWriteMode = new(@"[wax+]", RegexOptions.Compiled);

    /// <summary>Raising requests.exceptions.HTTPError isn't talking to the network.</summary>
    private static readonly Regex PythonExceptionName = new(@"(?:Error|Exception|Warning)$", RegexOptions.Compiled);

    private sealed class Rule
    {
        public Rule(string pattern, string? kind)
        {
            Kind = kind;
            Pattern = Glob(pattern);
        }

        public string? Kind { get; }

        public Regex Pattern { get; }
    }

    /// <summary>.NET rules over "Namespace.Type.Member", and the type's short name for code whose references didn't load.</summary>
    private sealed record DotNetRule(Rule Full, Rule? Short);

    private static readonly List<DotNetRule> DotNetRules = new();
    private static readonly List<Rule> PythonRules = new();
    private static readonly List<Rule> PythonValueRules = new();

    static EffectCatalog()
    {
        // ---------------------------------------------------------------- .NET: files
        Type("System.IO.File", ("Open", FileOpen), ("OpenHandle", FileOpen), ("OpenRead", Read), ("OpenText", Read),
            ("Read*", Read), ("Exists", Read), ("Get*", Read), ("*", Write));
        Type("System.IO.Directory", ("*CurrentDirectory", Env), ("Get*", Read), ("Enumerate*", Read), ("Exists", Read),
            ("*", Write));
        foreach (var type in new[] { "System.IO.FileInfo", "System.IO.DirectoryInfo", "System.IO.FileSystemInfo" })
        {
            Type(type, ("new", null), ("Open", FileOpen), ("OpenRead", Read), ("OpenText", Read), ("Get*", Read),
                ("Enumerate*", Read), ("Refresh", Read), ("Create*", Write), ("Delete", Write), ("MoveTo", Write),
                ("CopyTo", Write), ("Replace", Write), ("Append*", Write), ("Encrypt", Write), ("Decrypt", Write));
        }
        Type("System.IO.FileStream", ("new", FileOpen));
        Type("System.IO.StreamReader", ("new", Read));
        Type("System.IO.StreamWriter", ("new", Write));
        Type("System.IO.FileSystemWatcher", ("new", Read));
        Type("System.IO.Path", ("GetTempFileName", Write));
        Type("System.IO.Compression.ZipFile", ("Open", FileOpen), ("OpenRead", Read), ("*", Write));
        Type("System.IO.Compression.ZipFileExtensions", ("ExtractToFile", Write), ("ExtractToDirectory", Write), ("CreateEntryFromFile", Read));
        Type("System.IO.MemoryMappedFiles.MemoryMappedFile", ("CreateFromFile", FileOpen));

        // ---------------------------------------------------------------- .NET: network
        foreach (var ns in new[]
                 {
                     "System.Net.Http", "System.Net.Sockets", "System.Net.WebSockets", "System.Net.Mail",
                     "System.Net.NetworkInformation", "System.Net.Quic", "System.Net.Security", "Grpc.Net.Client",
                     "Grpc.Core", "RestSharp", "Flurl.Http", "MQTTnet", "RabbitMQ.Client", "Confluent.Kafka",
                     "Amazon", "Azure", "Google.Cloud", "Google.Apis", "Microsoft.AspNetCore.SignalR.Client",
                     "Octokit", "Refit", "MailKit", "Renci.SshNet",
                 })
        {
            Namespace(ns, Net);
        }
        foreach (var type in new[] { "WebClient", "WebRequest", "HttpWebRequest", "FtpWebRequest", "Dns", "HttpListener" })
        {
            Type("System.Net." + type, ("*", Net));
        }

        // ---------------------------------------------------------------- .NET: databases
        foreach (var ns in new[]
                 {
                     "System.Data.Common", "System.Data.SqlClient", "Microsoft.Data.SqlClient", "Microsoft.Data.Sqlite",
                     "System.Data.SQLite", "System.Data.Odbc", "System.Data.OleDb", "Npgsql", "MySql.Data",
                     "MySqlConnector", "Oracle.ManagedDataAccess", "MongoDB.Driver", "StackExchange.Redis", "LiteDB",
                     "Dapper", "Cassandra", "Elastic.Clients", "Nest", "Neo4j.Driver", "Couchbase", "Marten",
                     "Raven.Client", "SQLite",
                 })
        {
            Namespace(ns, Db);
        }
        Add(DotNetRules, "System.Data.IDb*.*", Db, null);
        Type("Microsoft.EntityFrameworkCore.DbContext", ("SaveChanges*", Db), ("Find*", Db));
        Type("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions", ("*", Db));
        Type("Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions", ("*", Db));
        Type("Microsoft.EntityFrameworkCore.RelationalQueryableExtensions", ("Execute*", Db), ("FromSql*", Db));
        Type("Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade", ("*", Db));

        // ---------------------------------------------------------------- .NET: processes, environment
        Type("System.Diagnostics.Process", ("GetCurrentProcess", null), ("*", Proc));
        Type("System.Environment", ("Exit", Proc), ("FailFast", Proc), ("GetLogicalDrives", Read),
            ("GetEnvironmentVariable*", Env), ("SetEnvironmentVariable", Env), ("ExpandEnvironmentVariables", Env),
            ("GetCommandLineArgs", Env), ("GetFolderPath", Env));
        Type("Microsoft.Win32.Registry", ("*", Env));
        Type("Microsoft.Win32.RegistryKey", ("*", Env));

        // ---------------------------------------------------------------- .NET: native code, dynamic code
        Type("System.Runtime.InteropServices.Marshal", ("*", Native));
        Type("System.Runtime.InteropServices.NativeLibrary", ("*", Native));
        Type("System.Runtime.InteropServices.NativeMemory", ("*", Native));
        foreach (var type in new[] { "MethodBase", "MethodInfo", "ConstructorInfo" })
        {
            Type("System.Reflection." + type, ("Invoke", Dynamic));
        }
        Type("System.Reflection.Assembly", ("Load*", Dynamic));
        Type("System.Activator", ("CreateInstance*", Dynamic));
        Type("System.Type", ("InvokeMember", Dynamic), ("GetType", Dynamic));
        Type("System.Delegate", ("DynamicInvoke", Dynamic));
        Namespace("System.Reflection.Emit", Dynamic);

        // ---------------------------------------------------------------- Godot
        Type("Godot.FileAccess", ("Open", FileOpen), ("OpenCompressed", FileOpen), ("OpenEncrypted*", FileOpen),
            ("Store*", Write), ("Resize", Write), ("Flush", Write), ("Close", null), ("*", Read));
        Type("Godot.DirAccess", ("Make*", Write), ("Remove*", Write), ("Rename*", Write), ("Copy*", Write),
            ("CreateLink", Write), ("*", Read));
        Type("Godot.ConfigFile", ("Load*", Read), ("Save*", Write));
        Type("Godot.ResourceSaver", ("Save", Write));
        Type("Godot.Image", ("Save*", Write), ("Load", Read), ("LoadFromFile", Read));
        Type("Godot.ProjectSettings", ("Save*", Write));
        Type("Godot.OS", ("Execute*", Proc), ("CreateProcess", Proc), ("CreateInstance", Proc), ("Kill", Proc),
            ("Shell*", Proc), ("MoveToTrash", Write), ("GetEnvironment", Env), ("SetEnvironment", Env),
            ("HasEnvironment", Env), ("UnsetEnvironment", Env), ("GetCmdline*", Env));
        foreach (var type in new[]
                 {
                     "HttpRequest", "HttpClient", "StreamPeerTcp", "TcpServer", "PacketPeerUdp", "UdpServer",
                     "WebSocketPeer", "WebSocketMultiplayerPeer", "ENetConnection", "ENetMultiplayerPeer",
                     "WebRtcPeerConnection", "WebRtcMultiplayerPeer", "StreamPeerTls", "DtlsServer", "Upnp",
                 })
        {
            Type("Godot." + type, ("*", Net));
        }
        Type("Godot.GodotObject", ("Call", Dynamic), ("Callv", Dynamic), ("CallDeferred", Dynamic));
        Type("Godot.ClassDB", ("Instantiate", Dynamic));
        Type("Godot.Expression", ("Execute", Dynamic));

        // ---------------------------------------------------------------- Python: files
        foreach (var opener in new[]
                 {
                     "builtins.open", "io.open", "codecs.open", "gzip.open", "bz2.open", "lzma.open", "tarfile.open",
                     "zipfile.ZipFile", "shelve.open", "aiofiles.open", "h5py.File",
                 })
        {
            Add(PythonRules, opener, FileOpen);
        }
        // A Path instance: Path(...), Path.cwd(), Path.home(), a parameter hinted as Path; then any chain.
        foreach (var path in new[] { "pathlib.Path()**.", "pathlib.Path.*()**." })
        {
            Add(PythonRules, path + "open", FileOpen);
            Add(PythonRules, path + "read_*", Read);
            Add(PythonRules, path + "write_*", Write);
            foreach (var method in new[] { "exists", "is_file", "is_dir", "iterdir", "glob", "rglob", "stat", "lstat", "samefile", "readlink", "walk" })
            {
                Add(PythonRules, path + method, Read);
            }
            foreach (var method in new[] { "mkdir", "touch", "unlink", "rmdir", "rename", "replace", "chmod", "lchmod", "symlink_to", "hardlink_to" })
            {
                Add(PythonRules, path + method, Write);
            }
        }
        foreach (var name in new[]
                 {
                     "listdir", "scandir", "walk", "fwalk", "stat", "lstat", "access", "readlink", "path.exists", "path.lexists",
                     "path.isfile", "path.isdir", "path.islink", "path.getsize", "path.getmtime", "path.getctime",
                     "path.getatime", "path.samefile", "open",
                 })
        {
            Add(PythonRules, "os." + name, Read);
        }
        foreach (var name in new[]
                 {
                     "remove", "unlink", "rmdir", "removedirs", "mkdir", "makedirs", "rename", "renames", "replace",
                     "chmod", "chown", "lchown", "symlink", "link", "truncate", "utime", "mkfifo",
                 })
        {
            Add(PythonRules, "os." + name, Write);
        }
        Add(PythonRules, "shutil.disk_usage", Read);
        Add(PythonRules, "shutil.which", Env);
        Add(PythonRules, "shutil.**", Write);
        Add(PythonRules, "glob.**", Read);
        Add(PythonRules, "fileinput.**", Read);
        Add(PythonRules, "filecmp.**", Read);
        Add(PythonRules, "tempfile.**", Write);
        Add(PythonRules, "pandas.read_sql*", Db);
        Add(PythonRules, "pandas.read_*", Read);
        Add(PythonRules, "numpy.load*", Read);
        Add(PythonRules, "numpy.genfromtxt", Read);
        Add(PythonRules, "numpy.fromfile", Read);
        Add(PythonRules, "numpy.save*", Write);
        Add(PythonRules, "PIL.Image.open", Read);
        Add(PythonRules, "cv2.imread*", Read);
        Add(PythonRules, "cv2.imwrite", Write);
        Add(PythonRules, "imageio.imread", Read);
        Add(PythonRules, "imageio.imwrite", Write);
        Add(PythonRules, "torch.load", Read);
        Add(PythonRules, "torch.save", Write);
        Add(PythonRules, "joblib.load", Read);
        Add(PythonRules, "joblib.dump", Write);

        // ---------------------------------------------------------------- Python: processes, environment
        foreach (var name in new[] { "system", "popen", "exec*", "spawn*", "posix_spawn*", "fork", "forkpty", "kill", "killpg", "startfile", "_exit" })
        {
            Add(PythonRules, "os." + name, Proc);
        }
        Add(PythonRules, "os.environ**", Env);
        Add(PythonRules, "os.getenv*", Env);
        Add(PythonRules, "os.putenv", Env);
        Add(PythonRules, "os.unsetenv", Env);
        Add(PythonRules, "os.chdir", Env);
        Add(PythonRules, "os.getcwd*", Env);
        Add(PythonRules, "sys.exit", Proc);
        Add(PythonRules, "subprocess.**", Proc);
        Add(PythonRules, "asyncio.create_subprocess_*", Proc);
        Add(PythonRules, "webbrowser.**", Proc);
        Add(PythonRules, "pty.**", Proc);
        Add(PythonRules, "multiprocessing.Process", Proc);
        Add(PythonValueRules, "os.environ**", Env);
        Add(PythonValueRules, "sys.argv**", Env);

        // ---------------------------------------------------------------- Python: databases (before network: some are cloud SDKs)
        foreach (var module in new[]
                 {
                     "sqlite3", "psycopg2", "psycopg", "pymysql", "MySQLdb", "mysql.connector", "pymongo", "motor",
                     "redis", "aioredis", "asyncpg", "aiosqlite", "aiomysql", "cx_Oracle", "oracledb", "pyodbc",
                     "pymssql", "cassandra", "elasticsearch", "duckdb", "neo4j", "clickhouse_driver",
                     "snowflake.connector", "google.cloud.bigquery", "google.cloud.firestore", "google.cloud.spanner",
                 })
        {
            Add(PythonRules, module + ".**", Db);
        }
        Add(PythonRules, "sqlalchemy.create_engine", Db);
        Add(PythonRules, "sqlalchemy.ext.asyncio.create_async_engine", Db);
        Add(PythonRules, "sqlalchemy.orm.Session**", Db);
        Add(PythonRules, "sqlalchemy.orm.sessionmaker**", Db);

        // ---------------------------------------------------------------- Python: network
        foreach (var module in new[]
                 {
                     "requests", "httpx", "aiohttp", "urllib.request", "urllib3", "http.client", "http.server", "socket",
                     "socketserver", "ssl", "websocket", "websockets", "ftplib", "smtplib", "aiosmtplib", "poplib",
                     "imaplib", "telnetlib", "xmlrpc", "paramiko", "grpc", "boto3", "botocore", "google.cloud",
                     "googleapiclient", "azure", "openai", "anthropic", "httplib2", "pycurl", "tornado.httpclient",
                     "zmq", "pika", "kafka", "confluent_kafka", "paho", "scapy", "dns.resolver", "slack_sdk", "stripe",
                     "twilio", "github", "gql",
                 })
        {
            Add(PythonRules, module + ".**", Net);
        }
        Add(PythonRules, "asyncio.open_connection", Net);
        Add(PythonRules, "asyncio.open_unix_connection", Net);
        Add(PythonRules, "asyncio.start_server", Net);
        Add(PythonRules, "asyncio.start_unix_server", Net);

        // ---------------------------------------------------------------- Python: native code, dynamic code
        Add(PythonRules, "ctypes.**", Native);
        Add(PythonRules, "cffi.**", Native);
        foreach (var name in new[]
                 {
                     "builtins.eval", "builtins.exec", "builtins.compile", "builtins.__import__", "importlib.import_module",
                     "importlib.reload", "runpy.**", "pickle.load", "pickle.loads", "marshal.load", "marshal.loads",
                     "dill.load*", "cloudpickle.load*",
                 })
        {
            Add(PythonRules, name, Dynamic);
        }
    }

    // ---------------------------------------------------------------- lookups

    /// <summary>
    /// A .NET call: <paramref name="type"/> is the declaring type's full name, <paramref name="member"/> the
    /// member name ("new" for constructors), <paramref name="mode"/> the arguments' text for calls that open files.
    /// </summary>
    public static string? DotNet(string type, string member, string? mode = null)
    {
        var key = type + "." + member;
        foreach (var rule in DotNetRules)
        {
            if (rule.Full.Pattern.IsMatch(key)) return Resolve(rule.Full.Kind, mode, WriteMode);
        }
        return null;
    }

    /// <summary>A .NET call whose type didn't resolve (references not restored): matched by the type's short name.</summary>
    public static string? DotNetByShortName(string type, string member, string? mode = null)
    {
        var key = type + "." + member;
        foreach (var rule in DotNetRules)
        {
            if (rule.Short != null && rule.Short.Pattern.IsMatch(key)) return Resolve(rule.Short.Kind, mode, WriteMode);
        }
        return null;
    }

    /// <summary>StreamReader/StreamWriter only touch the disk when given a path (not when wrapping a stream).</summary>
    public static bool NeedsPathArgument(string type) => type is "System.IO.StreamReader" or "System.IO.StreamWriter";

    /// <summary>
    /// A Python call by the dotted path it resolves to ("requests.get", "pathlib.Path().write_text");
    /// <paramref name="mode"/> is an open mode string ("w", "rb") when the call has one.
    /// </summary>
    public static string? Python(string api, string? mode = null)
    {
        var last = api.LastIndexOf('.');
        if (PythonExceptionName.IsMatch(last >= 0 ? api[(last + 1)..] : api)) return null;
        foreach (var rule in PythonRules)
        {
            if (rule.Pattern.IsMatch(api)) return Resolve(rule.Kind, mode, PythonWriteMode);
        }
        return null;
    }

    /// <summary>A Python name read as a value, not called (<c>os.environ["HOME"]</c>, <c>sys.argv</c>).</summary>
    public static string? PythonValue(string api)
    {
        foreach (var rule in PythonValueRules)
        {
            if (rule.Pattern.IsMatch(api)) return rule.Kind;
        }
        return null;
    }

    /// <summary>Adds one call to a member's effects, merging repeats of the same API.</summary>
    public static List<EffectInfo> Add(List<EffectInfo>? effects, string kind, string api, int line, string? confidence)
    {
        effects ??= new List<EffectInfo>();
        var existing = effects.Find(e => e.Kind == kind && e.Api == api);
        if (existing != null)
        {
            existing.Weight++;
            existing.Confidence = Confidence.Strongest(existing.Confidence, confidence);
            if (existing.Confidence == Confidence.Certain) existing.Confidence = null;
        }
        else if (effects.Count < MaxPerMember)
        {
            effects.Add(new EffectInfo
            {
                Kind = kind,
                Api = api,
                Line = line,
                Confidence = confidence == Confidence.Certain ? null : confidence,
            });
        }
        return effects;
    }

    // ---------------------------------------------------------------- building the tables

    private static string? Resolve(string? kind, string? mode, Regex writeMode) =>
        kind != FileOpen ? kind
        : mode != null && writeMode.IsMatch(mode) ? Write
        : Read;

    private static void Type(string type, params (string Member, string? Kind)[] members)
    {
        var dot = type.LastIndexOf('.');
        var shortName = dot >= 0 ? type[(dot + 1)..] : type;
        foreach (var (member, kind) in members) Add(DotNetRules, $"{type}.{member}", kind, $"{shortName}.{member}");
    }

    private static void Namespace(string ns, string kind) => Add(DotNetRules, ns + ".**", kind, null);

    private static void Add(List<DotNetRule> rules, string pattern, string? kind, string? shortPattern) =>
        rules.Add(new DotNetRule(new Rule(pattern, kind), shortPattern != null ? new Rule(shortPattern, kind) : null));

    private static void Add(List<Rule> rules, string pattern, string kind) => rules.Add(new Rule(pattern, kind));

    /// <summary><c>**</c> → anything, <c>*</c> → anything within one name; everything else literal.</summary>
    private static Regex Glob(string pattern)
    {
        var escaped = Regex.Escape(pattern)
            .Replace(@"\*\*", "\u0001", StringComparison.Ordinal)
            .Replace(@"\*", @"[^.(]*", StringComparison.Ordinal)
            .Replace("\u0001", ".*", StringComparison.Ordinal);
        return new Regex("^" + escaped + "$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }
}
