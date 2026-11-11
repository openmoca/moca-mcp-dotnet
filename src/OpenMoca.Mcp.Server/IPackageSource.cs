using OpenMoca.Reader;

namespace OpenMoca.Mcp;

/// <summary>
/// Where the server's packages come from. The default, <see cref="FolderPackageSource"/>, serves named packages and
/// library folders on the local disk. A host can supply its own, for example packages kept in object storage or an
/// internal registry: list what is there, and open one when the server needs it. The server verifies every package
/// it opens, whatever the source.
/// </summary>
public interface IPackageSource
{
    /// <summary>Every package the source can serve, read from manifests only.</summary>
    ValueTask<IReadOnlyList<PackageDescriptor>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Opens a listed package for the Reader: a path, an archive's bytes, or a custom package source.</summary>
    ValueTask<PackageTarget> OpenAsync(PackageDescriptor package, CancellationToken cancellationToken);

    /// <summary>Raised when the packages change. The server lists them again, and unloads the keys given, so they are
    /// verified again when next used.</summary>
    event EventHandler<PackagesChangedEventArgs>? Changed;
}

/// <summary>The packages that changed: their keys (<c>id@version</c>), to unload before listing again.</summary>
public sealed class PackagesChangedEventArgs(IReadOnlyCollection<string> changedKeys) : EventArgs
{
    /// <summary>The keys of packages whose content changed or went.</summary>
    public IReadOnlyCollection<string> ChangedKeys { get; } = changedKeys;
}
