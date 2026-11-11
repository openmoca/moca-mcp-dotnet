using System.Text.Json.Nodes;
using OpenMoca.Reader;
using OpenMoca.Reader.Attestations;
using OpenMoca.Reader.Diagnostics;
using OpenMoca.Reader.Search;
using OpenMoca.Reader.Sidecars;
using OpenMoca.Reader.Structure;

namespace OpenMoca.Mcp;

/// <summary>Which packages a call covers, once loaded: the digests to search, and what could not be covered.</summary>
internal sealed record Selection(IReadOnlySet<string> Digests, IReadOnlyList<string> Unknown, IReadOnlyList<string> NotLoaded);

/// <summary>
/// The packages the server serves: what the source lists, and the ones loaded so far. A package is read and verified
/// on first use and kept in one <see cref="Library"/>, so every call sees the same relations (what supersedes what)
/// whichever packages it covers. When the loaded pages pass the budget, the least recently used package is unloaded.
/// All access goes through <see cref="RunAsync{T}"/>, one call at a time.
/// </summary>
internal sealed class PackageSet
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ReadOptions _read;
    private readonly IPackageSource _source;
    private readonly int _maxLoadedPages;
    private readonly Action<string> _log;
    private readonly Dictionary<string, Loaded> _loaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _problems = new(StringComparer.Ordinal);
    private readonly Dictionary<IEmbedder, DenseBackend> _dense = [];
    private List<PackageDescriptor> _entries = [];
    private long _clock;

    private sealed record Loaded(PackageDescriptor Entry, PackageResult Result, IReadOnlySet<string> Digests, int Pages, bool Chunks)
    {
        public long LastUsed { get; set; }
    }

    private PackageSet(Library library, ReadOptions read, IPackageSource source, int maxLoadedPages, IReadOnlyList<IEmbedder> embedders, Action<string> log)
    {
        Library = library;
        Embedders = embedders;
        Backend = new LexicalBackend(library);
        _read = read;
        _source = source;
        _maxLoadedPages = maxLoadedPages;
        _log = log;
    }

    /// <summary>Everything loaded, in one library: relations and structure span all of it.</summary>
    public Library Library { get; }

    /// <summary>The lexical backend over <see cref="Library"/>; it keeps term counts per package.</summary>
    public LexicalBackend Backend { get; }

    /// <summary>The host's embedders, one per model.</summary>
    public IReadOnlyList<IEmbedder> Embedders { get; }

    /// <summary>The dense backend for an embedder, kept across calls: it checks each package's index once.</summary>
    public DenseBackend DenseFor(IEmbedder embedder) =>
        _dense.TryGetValue(embedder, out var dense) ? dense : _dense[embedder] = new DenseBackend(Library, embedder);

    /// <summary>
    /// A loaded package's semantic index: the model that made its vectors, and the embedder that can search them
    /// (null when no installed model matches). Null when its index has no vectors.
    /// </summary>
    public (JsonObject Model, IEmbedder? Embedder)? SemanticOf(string digest)
    {
        var loaded = Library.Packages.FirstOrDefault(p => p.Result.Digest == digest);
        var vector = loaded?.Chunks?.FirstOrDefault(c => c.Vector is not null)?.Vector;
        if (loaded?.Index?["model"] is not JsonObject model || vector is null)
        {
            return null;
        }

        return (model, Embedders.FirstOrDefault(e => DenseBackend.ModelMismatch(e, model, vector.Length) is null));
    }

    /// <summary>Called after the catalogue or the loaded packages change, so the client can list resources again.</summary>
    public Func<Task>? Changed { get; set; }

    /// <summary>Builds a set over a source: loads the packages it marks to load at start, lists the rest.</summary>
    public static async Task<PackageSet> CreateAsync(MocaMcpOptions options, IPackageSource source, Action<string> log, CancellationToken cancellationToken = default)
    {
        var library = new Library(new LibraryOptions { Overlays = options.Overlays, AllowUnsignedRelations = options.AllowUnsignedRelations });
        foreach (var d in library.Diagnostics)
        {
            log($"{d.Code}: {d.Message}");
        }

        // Members of a composed package: from member folders, else from the source's own packages. The Reader checks
        // each member against the digest the composed package pins.
        PackageSet? created = null;
        var read = new ReadOptions
        {
            TrustRoot = options.TrustRoot,
            // Sigstore attestations verify offline against the trust root's sigstore.trustedRoot (attestations §6).
            SigstoreVerifier = new OpenMoca.Reader.Sigstore.SigstoreVerifier(),
            ResolveMember = options.MemberFolders.Count > 0
                ? MemberResolvers.Directories(options.MemberFolders)
                : async (member, ct) =>
                {
                    var match = created!._entries.FirstOrDefault(e => e.Id == member.Id && (member.Version is null || e.Version == member.Version));
                    return match is null ? null : await source.OpenAsync(match, ct).ConfigureAwait(false);
                },
        };
        var set = created = new PackageSet(library, read, source, options.MaxLoadedPages, options.Embedders, log);
        var listed = await source.ListAsync(cancellationToken).ConfigureAwait(false);
        set._entries = [.. listed.Where(e => !e.LoadAtStart)];
        var started = new List<PackageDescriptor>();
        foreach (var entry in listed.Where(e => e.LoadAtStart))
        {
            // Loaded now, as a named package always has been: an invalid one is reported and left out.
            if (await set.LoadAsync(entry, cancellationToken).ConfigureAwait(false) is { } loaded && started.All(e => e.Key != loaded.Entry.Key))
            {
                started.Add(loaded.Entry);
            }
        }

        var keys = started.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        set._entries = [.. started, .. set._entries.Where(e => keys.Add(e.Key))];
        source.Changed += (_, e) => _ = set.OnChangedAsync(e.ChangedKeys);
        if (options.Preload)
        {
            await set.RunAsync(() => set.SelectAsync(null, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        return set;
    }

    /// <summary>Every package the server can serve: the named ones, then the library's.</summary>
    public IReadOnlyList<PackageDescriptor> Entries => _entries;

    /// <summary>The loaded result for a catalogue key, if loaded.</summary>
    public PackageResult? ResultFor(string key) =>
        _loaded.TryGetValue(key, out var l)
            ? l.Result
            : Library.Packages.Select(p => p.Result).FirstOrDefault(r => $"{r.Id}@{r.Version}" == key); // loaded as a member

    /// <summary>Whether a loaded package was bound to sidecar chunks.</summary>
    public bool HasChunks(string key) => _loaded.TryGetValue(key, out var l) && l.Chunks;

    /// <summary>Why a package could not be loaded, if it could not.</summary>
    public string? ProblemFor(string key) => _problems.GetValueOrDefault(key);

    /// <summary>Runs one call with the set to itself.</summary>
    public async Task<T> RunAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await call().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Loads what a call needs. <paramref name="names"/> are <c>id@version</c> keys or package ids (every version);
    /// <c>null</c> means every package, loaded in catalogue order while the budget allows.
    /// </summary>
    public async Task<Selection> SelectAsync(IReadOnlyList<string>? names, CancellationToken cancellationToken)
    {
        var unknown = new List<string>();
        List<PackageDescriptor> wanted;
        if (names is null)
        {
            wanted = [.. _entries];
        }
        else
        {
            wanted = [];
            foreach (var name in names.Distinct(StringComparer.Ordinal))
            {
                var matches = _entries.Where(e => e.Key == name || e.Id == name).ToList();
                if (matches.Count == 0)
                {
                    unknown.Add(name);
                }

                wanted.AddRange(matches.Where(m => !wanted.Contains(m)));
            }
        }

        // Packages that supersede a wanted one load with it, so the gate knows it is superseded (contract §8). They
        // are not searched unless asked for.
        var searched = wanted.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < wanted.Count; i++)
        {
            foreach (var newer in _entries.Where(e => e.Replaces(wanted[i]) && !wanted.Contains(e)))
            {
                wanted.Add(newer);
            }
        }

        var keep = new HashSet<string>(wanted.Select(e => e.Key), StringComparer.Ordinal);
        var digests = new HashSet<string>(StringComparer.Ordinal);
        var notLoaded = new List<string>();
        foreach (var entry in wanted)
        {
            if (_problems.ContainsKey(entry.Key))
            {
                continue;
            }

            if (!_loaded.TryGetValue(entry.Key, out var loaded))
            {
                // Over the budget with nothing left to unload: an "every package" call stops here, except for a
                // package that supersedes one already loaded, which must load or the older one would look current.
                var supersedesLoaded = _loaded.Values.Any(l => entry.Replaces(l.Entry));
                if (names is null && LoadedPages() >= _maxLoadedPages && !supersedesLoaded)
                {
                    notLoaded.Add(entry.Key);
                    continue;
                }

                loaded = await LoadAsync(entry, cancellationToken).ConfigureAwait(false);
                if (loaded is null)
                {
                    continue;
                }

                Evict(keep);
            }

            loaded.LastUsed = ++_clock;
            if (searched.Contains(entry.Key))
            {
                digests.UnionWith(loaded.Digests);
            }
        }

        return new Selection(digests, unknown, notLoaded);
    }

    // The source changed: unload what changed, then list again.
    private async Task OnChangedAsync(IReadOnlyCollection<string> changedKeys)
    {
        try
        {
            await RunAsync(async () =>
            {
                foreach (var key in changedKeys)
                {
                    Unload(key);
                    _problems.Remove(key);
                }

                // Packages loaded at start stay listed first; the rest are as the source now lists them.
                var listed = await _source.ListAsync(CancellationToken.None).ConfigureAwait(false);
                var started = _entries.Where(e => e.LoadAtStart).ToList();
                var known = started.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
                _entries = [.. started, .. listed.Where(e => !e.LoadAtStart && known.Add(e.Key))];
                foreach (var gone in _loaded.Keys.Where(k => _entries.All(e => e.Key != k)).ToList())
                {
                    Unload(gone);
                }

                return true;
            }, CancellationToken.None).ConfigureAwait(false);
            _log($"packages changed; {_entries.Count} package(s)");
            if (Changed is { } changed)
            {
                await changed().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log($"listing packages again failed: {ex.Message}");
        }
    }

    private async Task<Loaded?> LoadAsync(PackageDescriptor entry, CancellationToken cancellationToken)
    {
        var target = await _source.OpenAsync(entry, cancellationToken).ConfigureAwait(false);
        var result = await MocaReader.ReadPackageAsync(target, _read, cancellationToken).ConfigureAwait(false);
        if (!result.Valid)
        {
            var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            _log($"skipping {entry.Location}: not a valid package\n{Format(errors)}");
            if (entry.Id.Length > 0)
            {
                _problems[entry.Key] = $"not a valid package: {string.Join("; ", errors.Take(3).Select(d => $"{d.Code} {d.Message}"))}";
            }

            return null;
        }

        var key = $"{result.Id}@{result.Version}";
        if (_loaded.TryGetValue(key, out var already))
        {
            return already;
        }

        // Of the indexes beside the package, the one an installed model can search; else the first.
        var candidates = entry.Sidecars.Count > 0 ? entry.Sidecars : entry.Sidecar is null ? [] : [entry.Sidecar];
        var sidecar = candidates.FirstOrDefault(c => SidecarModel.Read(c) is { } model
            && Embedders.Any(e => DenseBackend.ModelMismatch(e, model, e.Dimensions) is null)) ?? candidates.FirstOrDefault();
        SidecarBinding? binding = null;
        if (sidecar is not null)
        {
            binding = SidecarBinder.Bind(sidecar, result);
            if (!binding.Usable)
            {
                _log($"ignoring sidecar {sidecar} for {entry.Location}: {string.Join(", ", binding.Diagnostics.Select(d => d.Code))}");
            }
        }

        var before = Library.Packages.Select(p => p.Result.Digest!).ToHashSet(StringComparer.Ordinal);
        Library.Add(result, binding);
        var added = Library.Packages.Select(p => p.Result.Digest!).Where(d => !before.Contains(d)).ToHashSet(StringComparer.Ordinal);
        added.Add(result.Digest!);
        var pages = Library.Packages.Where(p => added.Contains(p.Result.Digest!)).Sum(p => p.Result.Nodes.Count);

        var full = entry with
        {
            Id = result.Id,
            Version = result.Version,
            Title = JsonText(result.Manifest?["title"]),
            Description = JsonText(result.Manifest?["description"]),
            Supersedes = Catalogue.SupersedesOf(result.Manifest),
            LoadAtStart = entry.LoadAtStart,
        };
        var loaded = new Loaded(full, result, added, pages, binding is { Usable: true }) { LastUsed = ++_clock };
        _loaded[key] = loaded;
        _log($"loaded {result.Id}@{result.Version} {result.Digest} [{string.Join(", ", result.Capabilities)}]");
        return loaded;
    }

    private int LoadedPages() => _loaded.Values.Sum(l => l.Pages);

    // Unloads least recently used packages, never one the current call needs, until within the budget.
    private void Evict(IReadOnlySet<string> keep)
    {
        while (LoadedPages() > _maxLoadedPages)
        {
            var victim = _loaded.Values.Where(l => !keep.Contains(l.Entry.Key)).OrderBy(l => l.LastUsed).FirstOrDefault();
            if (victim is null)
            {
                return;
            }

            _log($"unloading {victim.Entry.Key} (memory budget, --max-loaded-pages {_maxLoadedPages})");
            Unload(victim.Entry.Key);
        }
    }

    private void Unload(string key)
    {
        if (!_loaded.Remove(key, out var loaded))
        {
            return;
        }

        var stillNeeded = _loaded.Values.SelectMany(l => l.Digests).ToHashSet(StringComparer.Ordinal);
        foreach (var digest in loaded.Digests.Where(d => !stillNeeded.Contains(d)))
        {
            Library.Remove(digest);
        }
    }

    private static string? JsonText(System.Text.Json.Nodes.JsonNode? node) =>
        node is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Diagnostics grouped by file, as moca-lint prints them.</summary>
    private static string Format(IReadOnlyList<Diagnostic> items)
    {
        var lines = new List<string>();
        foreach (var group in items.GroupBy(d => d.File ?? "(package)"))
        {
            lines.Add(group.Key);
            foreach (var d in group)
            {
                lines.Add($"  {d.Severity.ToString().ToUpperInvariant()} {d.Code}  {(d.Line is { } line ? $"line {line}: " : "")}{d.Message}");
            }
        }

        lines.Add("");
        lines.Add($"{items.Count} error(s), 0 warning(s), 0 info");
        return string.Join('\n', lines);
    }
}

/// <summary>A backend that searches only the packages a call covers; the gate still sees the whole library.</summary>
internal sealed class SelectedPackages(ISearchBackend inner, IReadOnlySet<string> digests) : ISearchBackend
{
    public BackendFeatures Features => inner.Features;

    public IReadOnlyList<Diagnostic> Diagnostics => inner.Diagnostics;

    public ValueTask<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, SearchFilters filters, CancellationToken cancellationToken) =>
        inner.SearchAsync(query, limit, filters with { Digests = [.. filters.Digests.Where(digests.Contains)] }, cancellationToken);
}
