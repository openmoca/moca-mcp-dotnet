// moca-mcp for .NET: serves MOCA packages to any MCP client over stdio. Logs go to stderr; stdout is the protocol.
using System.Reflection;
using ModelContextProtocol.Server;
using OpenMoca.Mcp;

var version = typeof(CommandLine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
void Log(string line) => Console.Error.WriteLine($"moca-mcp: {line}");

if (args.Length > 0 && args[0] == "models")
{
    return await ModelsCommand.RunAsync(args[1..], Log);
}

CommandLine options;
try
{
    options = CommandLine.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(CommandLine.Usage);
    return 2;
}

if (options.Help)
{
    Console.WriteLine(CommandLine.Usage);
    return 0;
}

if (options.Version)
{
    Console.WriteLine(version);
    return 0;
}

// The standard models installed in the store; each loads, and its files are checked, on first use.
var store = new OpenMoca.Reader.Onnx.ModelStore(options.Models);
var embedders = store.Installed().Select(m => new OpenMoca.Reader.Onnx.LazyEmbedder(store, m)).ToList();
if (embedders.Count > 0)
{
    Log($"models: {string.Join(", ", embedders.Select(e => e.Model.Id))}");
}

MocaMcpServer moca;
using var source = new FolderPackageSource(new FolderPackageSourceOptions
{
    Packages = [.. options.Packages.Select(p => (p.Package, p.Sidecar))],
    Folders = options.Library,
    MaxPackages = options.MaxPackages,
    MaxDepth = options.MaxDepth,
    Watch = options.Watch,
}, StderrLogger.Instance);
try
{
    moca = await MocaMcpServer.CreateAsync(new MocaMcpOptions
    {
        TrustRoot = options.TrustRoot is null ? null : OpenMoca.Reader.Attestations.TrustRoot.LoadFile(options.TrustRoot),
        Audiences = options.Audiences,
        AllowUnlabelled = options.AllowUnlabelled,
        IncludeExcluded = options.IncludeExcluded,
        AllowUnsignedRelations = options.AllowUnsignedRelations,
        Overlays = [.. options.Overlays.Select(o => new OpenMoca.Reader.Structure.StructureOverlay(o.Id, o.Layer, File.ReadAllText(o.Path)))],
        MemberFolders = options.Members,
        MaxLoadedPages = options.MaxLoadedPages,
        Preload = options.Preload,
        Embedders = embedders,
        ServerVersion = version,
    }, source, StderrLogger.Instance);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
{
    // A trust root or overlay the host named could not be read: the host's mistake, so say so and stop.
    Log(ex.Message);
    return 1;
}

if (moca.Packages.Count == 0)
{
    Log(options.Library.Count > 0 ? "no packages to serve: none valid, and none in the library" : "no valid packages to serve");
    return 1;
}

if (options.Library.Count > 0)
{
    Log($"library: {moca.Packages.Count} package(s); each loads on first use (--max-loaded-pages {options.MaxLoadedPages})");
}

var server = McpServer.Create(new StdioServerTransport("moca-mcp"), moca.ServerOptions);
moca.Attach(server);
await server.RunAsync();
return 0;
