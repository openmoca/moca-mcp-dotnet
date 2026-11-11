using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace OpenMoca.Mcp;

/// <summary>
/// The MOCA MCP server, for any host: the tools (<c>moca_list_packages</c>, <c>moca_search</c>,
/// <c>moca_structure</c>, <c>moca_get_node</c>, <c>moca_package_info</c>) and the node resources, over the packages a
/// source lists. Give <see cref="ServerOptions"/> to the MCP SDK with the transport you choose, and
/// <see cref="Attach"/> the server so clients hear when the packages change.
/// </summary>
/// <example>
/// <code>
/// var moca = await MocaMcpServer.CreateAsync(new MocaMcpOptions { Audiences = ["public"] },
///     new FolderPackageSource(new FolderPackageSourceOptions { Folders = ["/srv/moca"] }));
/// var server = McpServer.Create(new StdioServerTransport("moca"), moca.ServerOptions);
/// moca.Attach(server);
/// await server.RunAsync();
/// </code>
/// </example>
public sealed class MocaMcpServer
{
    private readonly PackageSet _set;

    private MocaMcpServer(PackageSet set, McpServerOptions serverOptions)
    {
        _set = set;
        ServerOptions = serverOptions;
    }

    /// <summary>The MCP server options: server information, capabilities and handlers.</summary>
    public McpServerOptions ServerOptions { get; }

    /// <summary>The packages the server can serve, as the source listed them.</summary>
    public IReadOnlyList<PackageDescriptor> Packages => _set.Entries;

    /// <summary>
    /// Creates the server: lists the source's packages and loads those marked to load at start (and, with
    /// <see cref="MocaMcpOptions.Preload"/>, the rest within the budget).
    /// </summary>
    public static async Task<MocaMcpServer> CreateAsync(MocaMcpOptions options, IPackageSource source, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(source);
        var log = logger ?? NullLogger.Instance;
        var set = await PackageSet.CreateAsync(options, source, m => log.LogInformation("{Message}", m), cancellationToken).ConfigureAwait(false);
        return new MocaMcpServer(set, MocaServer.Options(set, options));
    }

    /// <summary>Sends <c>notifications/resources/list_changed</c> to the server's client when the packages change.</summary>
    public void Attach(McpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        _set.Changed = () => server.SendNotificationAsync(NotificationMethods.ResourceListChangedNotification);
    }
}
