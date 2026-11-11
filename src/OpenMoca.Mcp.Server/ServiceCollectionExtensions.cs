using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace OpenMoca.Mcp;

/// <summary>Registers the MOCA MCP server with dependency injection.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the MOCA MCP server: its options, a package source (by default <see cref="FolderPackageSource"/> over
    /// <paramref name="folders"/>), and a <see cref="MocaMcpServerFactory"/> that creates the server.
    /// </summary>
    public static MocaMcpBuilder AddMocaMcp(this IServiceCollection services, MocaMcpOptions? options = null, FolderPackageSourceOptions? folders = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(options ?? new MocaMcpOptions());
        services.TryAddSingleton<IPackageSource>(sp => new FolderPackageSource(folders ?? new FolderPackageSourceOptions(), sp.GetService<ILoggerFactory>()?.CreateLogger<FolderPackageSource>()));
        services.TryAddSingleton<MocaMcpServerFactory>();
        return new MocaMcpBuilder(services);
    }
}

/// <summary>Chooses the MOCA MCP server's extension points.</summary>
public sealed class MocaMcpBuilder(IServiceCollection services)
{
    /// <summary>The service collection.</summary>
    public IServiceCollection Services { get; } = services;

    /// <summary>Serves packages from <typeparamref name="TSource"/> instead of the folder source.</summary>
    public MocaMcpBuilder WithPackageSource<TSource>()
        where TSource : class, IPackageSource
    {
        Services.Replace(ServiceDescriptor.Singleton<IPackageSource, TSource>());
        return this;
    }
}

/// <summary>Creates the MOCA MCP server from the registered options, package source and logging.</summary>
public sealed class MocaMcpServerFactory(MocaMcpOptions options, IPackageSource source, ILoggerFactory? loggerFactory = null)
{
    /// <summary>Creates the server; see <see cref="MocaMcpServer.CreateAsync"/>.</summary>
    public Task<MocaMcpServer> CreateAsync(CancellationToken cancellationToken = default) =>
        MocaMcpServer.CreateAsync(options, source, loggerFactory?.CreateLogger<MocaMcpServer>(), cancellationToken);
}
