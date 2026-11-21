using OpenMoca.Reader.Attestations;
using OpenMoca.Reader.Search;
using OpenMoca.Reader.Structure;

namespace OpenMoca.Mcp;

/// <summary>How the server reads, trusts and serves packages. Every setting has a default for one person's machine.</summary>
public sealed record MocaMcpOptions
{
    /// <summary>The trust root that verifies publisher and review attestations. Without one, nothing is signed or
    /// reviewed.</summary>
    public TrustRoot? TrustRoot { get; init; }

    /// <summary>The audiences this host may serve, applied to every call; callers cannot widen it. <c>null</c> serves
    /// every audience.</summary>
    public IReadOnlyList<string>? Audiences { get; init; }

    /// <summary>Reader contract §8 (moca-spec ADR-0016): with an audience set, also serve content that has no audience label. A
    /// malformed label is never served under an audience set.</summary>
    public bool AllowUnlabelled { get; init; }

    /// <summary>Also serve out-of-force, superseded and deprecated content, flagged as such. Host configuration only:
    /// a tool argument can never switch it on (Reader interface §7).</summary>
    public bool IncludeExcluded { get; init; }

    /// <summary>Apply <c>supersedes</c> and <c>conflictsWith</c> from unsigned or unscoped packages (package spec
    /// §7.2). Safe only for a library of the host's own packages.</summary>
    public bool AllowUnsignedRelations { get; init; }

    /// <summary>Structure overlays the host loads over the packages (Reader contract §10).</summary>
    public IReadOnlyList<StructureOverlay> Overlays { get; init; } = [];

    /// <summary>Folders to find a composed package's members in. When empty, members are found among the source's
    /// packages.</summary>
    public IReadOnlyList<string> MemberFolders { get; init; } = [];

    /// <summary>
    /// The creator's terms applied to everything the server hands a client (Reader contract §15.4): search results,
    /// nodes, the nodes bound to a concept, and resources. A client of an MCP server is a model, by default a third
    /// party's, so the default is <see cref="TermsRequest.Provider"/>. Set the destination to <c>device</c> when the
    /// model runs on the person's own device, or <c>host</c> when it is your own service, and list the conditions
    /// (<c>noTraining</c>, <c>noRetention</c>) the model's provider meets.
    /// </summary>
    public TermsRequest Terms { get; init; } = TermsRequest.Provider;

    /// <summary>
    /// Attestations §8: serve only packages with a valid package attestation under <see cref="TrustRoot"/>. A package
    /// without one, or whose signature fails, is not loaded (A009).
    /// </summary>
    public bool RequireSignature { get; init; }

    /// <summary>The memory budget, in pages kept loaded at once; the least recently used package is unloaded beyond
    /// it. A package that supersedes a loaded one always loads.</summary>
    public int MaxLoadedPages { get; init; } = 25_000;

    /// <summary>
    /// The memory budget in bytes, beside <see cref="MaxLoadedPages"/>. A loaded package counts the bytes of its files
    /// plus about 30 KB a page for what the Reader builds from them (bench/README.md). Beyond it the least recently used
    /// package is unloaded, and a call loads no more packages than fit, apart from the first it needs.
    /// </summary>
    public long MaxLoadedBytes { get; init; } = 1_000_000_000;

    /// <summary>
    /// The most bytes of text (UTF-8) one record carries: a longer text is cut at a character boundary and the record
    /// is marked <c>truncated</c>.
    /// </summary>
    public int MaxTextBytes { get; init; } = 100_000;

    /// <summary>
    /// The most bytes of records one answer carries: records past it are left out, and the answer says so with
    /// <c>truncated</c>. At least one record is always returned.
    /// </summary>
    public int MaxResponseBytes { get; init; } = 400_000;

    /// <summary>Load every package when the server starts, within the budget, rather than on first use.</summary>
    public bool Preload { get; init; }

    /// <summary>
    /// Embedders for semantic and hybrid search, one per model (Reader interface §5.3). A package is searched by
    /// meaning when its index was built with one of them. The moca-mcp program supplies the standard models installed
    /// on the machine; a host may supply any <see cref="OpenMoca.Reader.Search.IEmbedder"/>.
    /// </summary>
    public IReadOnlyList<OpenMoca.Reader.Search.IEmbedder> Embedders { get; init; } = [];

    /// <summary>Search hooks (Reader interface §5.4): may narrow or rewrite a search, never widen it.</summary>
    public SearchHooks? SearchHooks { get; init; }

    /// <summary>The name the server reports to clients.</summary>
    public string ServerName { get; init; } = "moca";

    /// <summary>The version the server reports to clients.</summary>
    public string ServerVersion { get; init; } = "0.6.0";
}
