namespace OpenMoca.Mcp;

/// <summary>
/// A package a source can serve, described from its manifest alone: the server reads and verifies the package itself
/// only when a call first needs it.
/// </summary>
/// <param name="Location">Where the source finds it: a path for the folder source; anything a custom source
/// understands, passed back to <see cref="IPackageSource.OpenAsync"/>.</param>
/// <param name="Id">The manifest's <c>id</c>; empty when the manifest could not be read (loading then reports why).</param>
/// <param name="Version">The manifest's <c>version</c>.</param>
/// <param name="Title">The manifest's <c>title</c>, if any.</param>
/// <param name="Description">The manifest's <c>description</c>, if any.</param>
/// <param name="Sidecar">A local sidecar index to search it through, if any.</param>
public sealed record PackageDescriptor(string Location, string Id, string Version, string? Title, string? Description, string? Sidecar)
{
    /// <summary>The key packages are named by in tool calls: <c>id@version</c>.</summary>
    public string Key => $"{Id}@{Version}";

    /// <summary>The packages this one declares it supersedes (package spec §8): target id, and version when given.</summary>
    public IReadOnlyList<(string Id, string? Version)> Supersedes { get; init; } = [];

    /// <summary>Every sidecar index beside the package, <see cref="Sidecar"/> first; the server binds the one whose model
    /// an installed embedder runs, when there is one.</summary>
    public IReadOnlyList<string> Sidecars { get; init; } = [];

    /// <summary>Load when the server starts, and stop the server if it is invalid, as a package named on the command
    /// line is.</summary>
    public bool LoadAtStart { get; init; }

    /// <summary>Whether this package supersedes <paramref name="other"/>.</summary>
    public bool Replaces(PackageDescriptor other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Supersedes.Any(s => s.Id == other.Id && (s.Version is null || s.Version == other.Version));
    }
}
