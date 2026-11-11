using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenMoca.Reader;

namespace OpenMoca.Mcp;

/// <summary>Settings for <see cref="FolderPackageSource"/>.</summary>
public sealed record FolderPackageSourceOptions
{
    /// <summary>Packages named one by one: a folder or <c>.moca</c> file, with an optional sidecar index. They load
    /// when the server starts.</summary>
    public IReadOnlyList<(string Path, string? Sidecar)> Packages { get; init; } = [];

    /// <summary>Library folders: every unpacked package and <c>.moca</c> file in them, with indexes found by name.</summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    /// <summary>The most packages the library folders may list.</summary>
    public int MaxPackages { get; init; } = 1000;

    /// <summary>How deep to look in a library folder.</summary>
    public int MaxDepth { get; init; } = 3;

    /// <summary>Follow changes to the library folders.</summary>
    public bool Watch { get; init; }
}

/// <summary>
/// Packages on the local disk: packages named one by one, and library folders scanned by reading only manifests
/// (unpacked packages, and the <c>moca.json</c> entry of <c>.moca</c> files). A sidecar beside a package, named
/// <c>&lt;name&gt;.moca.idx</c> or <c>&lt;name&gt;.&lt;model&gt;.moca.idx</c> (or a Zip of one, ending <c>.zip</c>), is bound to it. Links are not followed,
/// and nothing outside the folders is read.
/// </summary>
public sealed class FolderPackageSource : IPackageSource, IDisposable
{
    private readonly FolderPackageSourceOptions _options;
    private readonly ILogger _logger;
    private readonly LibraryWatcher? _watcher;
    private IReadOnlyList<PackageDescriptor> _last = [];

    /// <summary>A source over named packages and library folders.</summary>
    public FolderPackageSource(FolderPackageSourceOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        if (options.Watch && options.Folders.Count > 0)
        {
            _watcher = new LibraryWatcher(options.Folders, OnChanged);
        }
    }

    /// <inheritdoc/>
    public event EventHandler<PackagesChangedEventArgs>? Changed;

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<PackageDescriptor>> ListAsync(CancellationToken cancellationToken)
    {
        var list = new List<PackageDescriptor>();
        foreach (var (path, sidecar) in _options.Packages)
        {
            var isArchive = File.Exists(path);
            var manifest = Catalogue.ReadManifest(path, isArchive, Log);
            list.Add(new PackageDescriptor(path, Catalogue.Text(manifest?["id"]) ?? "", Catalogue.Text(manifest?["version"]) ?? "",
                Catalogue.Text(manifest?["title"]), Catalogue.Text(manifest?["description"]), sidecar)
            {
                Supersedes = Catalogue.SupersedesOf(manifest),
                Sidecars = sidecar is null ? [] : [sidecar],
                LoadAtStart = true,
            });
        }

        if (_options.Folders.Count > 0)
        {
            list.AddRange(Catalogue.Scan(_options.Folders, _options.MaxPackages, _options.MaxDepth, Log));
        }

        _last = list;
        return ValueTask.FromResult<IReadOnlyList<PackageDescriptor>>(list);
    }

    /// <inheritdoc/>
    public ValueTask<PackageTarget> OpenAsync(PackageDescriptor package, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        return ValueTask.FromResult(PackageTarget.FromPath(package.Location));
    }

    /// <inheritdoc/>
    public void Dispose() => _watcher?.Dispose();

    private void Log(string message) => _logger.LogInformation("{Message}", message);

    // A changed path names the packages it is inside, or whose sidecar it is.
    private void OnChanged(IReadOnlyCollection<string> paths)
    {
        static bool Under(string path, string root) =>
            path == root || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        var keys = _last
            .Where(d => paths.Any(p => Under(p, Path.GetFullPath(d.Location)) || (d.Sidecar is { } s && Under(p, Path.GetFullPath(s)))))
            .Select(d => d.Key)
            .ToList();
        Changed?.Invoke(this, new PackagesChangedEventArgs(keys));
    }
}
