using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenMoca.Reader;
using OpenMoca.Reader.Citations;
using OpenMoca.Reader.Search;
using OpenMoca.Reader.Structure;

namespace OpenMoca.Mcp;

/// <summary>
/// The MCP server over a library: the five tools and the node resources, with the definitions in definitions.json
/// and the answers tests/parity expects. Every result is a citation record, a structure answer or a catalogue entry;
/// package text is returned as data in JSON fields, never inside the server's own sentences or as instructions (Reader
/// contract §12). Everything goes through the creator's terms for the host's destination (contract §15.4, moca-spec
/// ADR-0041): a package hidden for it gets the not-found answer, and nothing the server says names it.
/// </summary>
internal static class MocaServer
{
    public const string NodeUri = "moca://node/";

    /// <summary>Resources listed per page (resources/list); the client asks for the next with the cursor it is given.</summary>
    internal const int ResourcePage = 100;

    private static readonly JsonSerializerOptions Output = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        // Text as JavaScript's JSON.stringify writes it: non-ASCII characters as they are.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Lazy<(IReadOnlyList<Tool> Tools, IReadOnlyList<ResourceTemplate> Templates)> Definitions = new(LoadDefinitions);

    /// <summary>The tools, as listed to clients.</summary>
    public static IReadOnlyList<Tool> Tools => Definitions.Value.Tools;

    /// <summary>The options that make a server over a package set.</summary>
    public static McpServerOptions Options(PackageSet set, MocaMcpOptions options)
    {
        var audiences = options.Audiences;

        // Everything that hands a record to the client passes the audience rule and, unless the host opted in, the
        // default retrieval policy (Reader contract §8, Reader interface §7).
        bool Allowed(CitationRecord? record) =>
            record is not null
            && Audience.Allowed(record, audiences, options.AllowUnlabelled)
            && (options.IncludeExcluded || OpenMoca.Reader.Citations.Citations.PassesDefaultPolicy(record));

        // Reader contract §15.4: the creator's terms apply to everything handed to the client, a model.
        IReadOnlyList<CitationRecord> Permitted(IEnumerable<CitationRecord> records) =>
            new MocaSearch(set.Library, set.Backend, audiences) { AllowUnlabelled = options.AllowUnlabelled }.ApplyTerms(records, options.Terms);

        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = options.ServerName, Version = options.ServerVersion },
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = true },
                Resources = new ResourcesCapability { ListChanged = true },
            },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [.. Tools] }),
                CallToolHandler = async (request, cancellationToken) =>
                {
                    var call = request.Params!;
                    var tool = Tools.FirstOrDefault(t => t.Name == call.Name)
                        ?? throw new McpProtocolException($"Tool {call.Name} not found", McpErrorCode.InvalidParams);
                    var args = call.Arguments is null ? new Dictionary<string, JsonElement>() : new Dictionary<string, JsonElement>(call.Arguments);
                    var problems = InputValidator.Validate(tool.InputSchema, args);
                    if (problems.Count > 0)
                    {
                        return Error($"Input validation error: Invalid arguments for tool {call.Name}: {string.Join("; ", problems)}");
                    }

                    return await set.RunAsync(async () => call.Name switch
                    {
                        "moca_list_packages" => Text(await ListPackagesAsync(set, options, cancellationToken).ConfigureAwait(false)),
                        "moca_search" => Text(await SearchAsync(set, options, args, cancellationToken).ConfigureAwait(false)),
                        "moca_structure" => Text(await StructureOfAsync(set, options, args, Allowed, Permitted, cancellationToken).ConfigureAwait(false)),
                        "moca_get_node" => await GetNodeAsync(set, options, args, Allowed, Permitted, cancellationToken).ConfigureAwait(false),
                        "moca_package_info" => await PackageInfoAsync(set, options, args, cancellationToken).ConfigureAwait(false),
                        _ => throw new McpProtocolException($"Tool {call.Name} not found", McpErrorCode.InvalidParams),
                    }, cancellationToken).ConfigureAwait(false);
                },
                ListResourceTemplatesHandler = (_, _) =>
                    ValueTask.FromResult(new ListResourceTemplatesResult { ResourceTemplates = [.. Definitions.Value.Templates] }),
                // The pages of the loaded packages, a page of them at a time; a package loads when a tool first uses it,
                // or with --preload.
                ListResourcesHandler = async (request, cancellationToken) => await set.RunAsync(() =>
                {
                    var from = int.TryParse(request.Params?.Cursor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var c) ? c : 0;
                    var all = Permitted(set.Library.Citations(includeText: false).Where(Allowed));
                    return Task.FromResult(new ListResourcesResult
                    {
                        Resources = [.. all.Skip(from).Take(ResourcePage).Select(r => new Resource
                        {
                            Uri = NodeUri + EncodeUriComponent(r.Node.Ref),
                            Name = r.Node.Ref,
                            Title = r.Node.Title,
                            // A node without a description takes the template's, so every listed resource has one.
                            Description = r.Node.Description ?? Definitions.Value.Templates[0].Description,
                            MimeType = "text/markdown",
                        })],
                        NextCursor = from + ResourcePage < all.Count ? (from + ResourcePage).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                    });
                }, cancellationToken).ConfigureAwait(false),
                ReadResourceHandler = async (request, cancellationToken) =>
                {
                    var uri = request.Params!.Uri;
                    var isNode = uri.StartsWith(NodeUri, StringComparison.Ordinal);
                    var nodeRef = isNode ? Uri.UnescapeDataString(uri[NodeUri.Length..]) : uri;
                    // As moca_get_node: the newest version that may be served; a hidden package's node is not there.
                    var record = isNode
                        ? await set.RunAsync(async () =>
                        {
                            await set.SelectAsync([PackageOf(nodeRef)], cancellationToken).ConfigureAwait(false);
                            var hidden = Hidden(set, options);
                            return Permitted(Versions(set, nodeRef, null).OfType<CitationRecord>().Where(r => Allowed(r) && !hidden.Contains(r.Package.Digest)))
                                .OrderByDescending(r => r.Package.Version, VersionOrder.Instance).FirstOrDefault();
                        }, cancellationToken).ConfigureAwait(false)
                        : null;
                    if (record is null)
                    {
                        throw new McpProtocolException($"no node {nodeRef}", McpErrorCode.InvalidParams);
                    }

                    var text = Cut(record.Text ?? "", options.MaxTextBytes, out var cut);
                    return new ReadResourceResult
                    {
                        Contents = [new TextResourceContents
                        {
                            Uri = uri, MimeType = "text/markdown", Text = text,
                            Meta = cut ? new JsonObject { ["truncated"] = true } : null,
                        }],
                    };
                },
            },
        };
    }

    /// <summary>The package a node id (<c>id#path</c>) or ref (<c>id@version#path</c>) names: its key or its id.</summary>
    internal static string PackageOf(string node)
    {
        var hash = node.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? node : node[..hash];
    }

    /// <summary>
    /// The catalogue (Reader contract §15.4, "The catalogue rule"): every package the server can serve is loaded, within
    /// the memory budget, so its terms, audience and findings are known; then each gets the entry the Reader gives it.
    /// An open package's entry adds what the server knows of it; a closed package's is exactly its catalogue entry; a
    /// hidden package, or one that could not be loaded, has none. Ordered by id, then version.
    /// </summary>
    private static async Task<JsonArray> ListPackagesAsync(PackageSet set, MocaMcpOptions mcp, CancellationToken cancellationToken)
    {
        await set.SelectAsync(null, cancellationToken).ConfigureAwait(false);
        var served = set.Entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        return new([.. set.Library.Catalogue(CatalogueOptions(mcp))
            .Where(c => served.Contains($"{c.Id}@{c.Version}"))
            .Select(c =>
            {
                var package = Entry(c);
                if (!c.Closed && set.ResultFor($"{c.Id}@{c.Version}") is { } result)
                {
                    package["digest"] = result.Digest;
                    package["capabilities"] = new JsonArray([.. result.Capabilities.Select(x => (JsonNode)x)]);
                    package["nodes"] = result.Nodes.Count;
                    package["searchMode"] = set.HasChunks($"{c.Id}@{c.Version}") ? "sidecar" : "lexical";
                    AddSemantic(package, set, result.Digest!);
                }

                return (JsonNode)package;
            })]);
    }

    /// <summary>The catalogue options for the host's settings: its terms request, audience set and opt-ins.</summary>
    private static CatalogueOptions CatalogueOptions(MocaMcpOptions mcp) => new()
    {
        Terms = mcp.Terms,
        Audiences = mcp.Audiences,
        AllowUnlabelled = mcp.AllowUnlabelled,
        IncludeAll = mcp.IncludeExcluded,
    };

    /// <summary>A catalogue entry as JSON: package-supplied text in its own fields (Reader contract §12).</summary>
    private static JsonObject Entry(CatalogueEntry entry) => JsonSerializer.SerializeToNode(entry, CitationJson.Options)!.AsObject();

    /// <summary>The loaded packages hidden for the host's terms request (Reader contract §15.4, "Closed and hidden").</summary>
    internal static HashSet<string> Hidden(PackageSet set, MocaMcpOptions mcp) =>
        set.Library.Packages.Select(p => p.Result.Digest!)
            .Where(d => set.Library.ClosureOf(d, mcp.Terms)?.Hidden == true)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The loaded packages closed for the host's terms request, hidden or not.</summary>
    private static HashSet<string> Closed(PackageSet set, MocaMcpOptions mcp) =>
        set.Library.Packages.Select(p => p.Result.Digest!)
            .Where(d => set.Library.ClosureOf(d, mcp.Terms)?.Closed == true)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// What a call covers, as the model may be told it (Reader contract §15.4, "Nothing names a hidden package"): the
    /// digests to use, without hidden packages; the names the caller gave that match no package it may be told of
    /// (hidden, or not loadable, as for an unknown name); and the names the caller gave that were not loaded for the
    /// memory budget. Packages the caller did not name are never named.
    /// </summary>
    private static Covered Visible(PackageSet set, Selection selection, IReadOnlyList<string>? names, IReadOnlySet<string> hidden)
    {
        var unknown = new List<string>();
        var notSearched = new List<string>();
        foreach (var name in (names ?? []).Distinct(StringComparer.Ordinal))
        {
            var keys = set.Entries.Where(e => e.Key == name || e.Id == name).Select(e => e.Key).ToList();
            if (keys.Any(k => set.ResultFor(k)?.Digest is { } d && !hidden.Contains(d)))
            {
                continue;
            }

            if (keys.Any(selection.NotLoaded.Contains))
            {
                notSearched.Add(name);
            }
            else
            {
                unknown.Add(name);
            }
        }

        return new Covered(selection.Digests.Where(d => !hidden.Contains(d)).ToHashSet(StringComparer.Ordinal), unknown, notSearched,
            selection.NotLoaded.Count > 0);
    }

    private static async Task<JsonObject> SearchAsync(PackageSet set, MocaMcpOptions mcp, Dictionary<string, JsonElement> args, CancellationToken cancellationToken)
    {
        var names = Strings(args, "packages");
        var selection = Visible(set, await set.SelectAsync(names, cancellationToken).ConfigureAwait(false), names, Hidden(set, mcp));
        var (backend, mode, note) = Backend(set, selection, Closed(set, mcp), String(args, "mode"));
        var search = new MocaSearch(set.Library, backend, mcp.Audiences, mcp.SearchHooks) { AllowUnlabelled = mcp.AllowUnlabelled };
        var query = args["query"].GetString()!;
        var options = new SearchOptions
        {
            Limit = Int(args, "limit") ?? 5,
            Locale = String(args, "locale"),
            // The opt-in to excluded content is host configuration, never a tool argument.
            IncludeAll = mcp.IncludeExcluded,
            Concepts = Strings(args, "concepts"),
            Scope = String(args, "scope"),
            // Reader contract §15.4: the creator's terms, for the model this server hands records to.
            Terms = mcp.Terms,
        };
        var records = await search.SearchAsync(query, options, cancellationToken).ConfigureAwait(false);
        var result = new JsonObject
        {
            ["query"] = query,
            ["results"] = Bounded(records, String(args, "detail") == "full", mcp, out var truncated),
        };
        // Hidden packages are not searched, so U004 counts none of their records; a note never counts them (§15.4).
        AddBoundNotes(result, truncated, search.Diagnostics.Any(d => d.Code == OpenMoca.Reader.Diagnostics.DiagnosticCodes.U004TermsExcluded && (d.Hidden ?? 0) == 0), mcp);
        if (mode != "lexical" || String(args, "mode") is not null)
        {
            result["mode"] = mode;
        }

        if (note is not null)
        {
            result["modeNote"] = note;
        }

        AddSelectionNotes(result, selection);
        return result;
    }

    /// <summary>
    /// The backend for a search: lexical over every selected package; semantic over those whose index an installed
    /// model can search (one dense search per model, fused by rank when there are several); hybrid fuses the two.
    /// When meaning search is asked for but unavailable, it says why and searches by words. A closed package's index is
    /// not looked at: nothing it returns may be handed on, and the note says nothing about it.
    /// </summary>
    private static (ISearchBackend Backend, string Mode, string? Note) Backend(PackageSet set, Covered selection, IReadOnlySet<string> closed, string? requested)
    {
        var lexical = new SelectedPackages(set.Backend, selection.Digests);
        var semantic = selection.Digests.Where(d => !closed.Contains(d)).Select(d => (Digest: d, Info: set.SemanticOf(d))).Where(x => x.Info is not null).ToList();
        var groups = semantic.Where(x => x.Info!.Value.Embedder is not null)
            .GroupBy(x => x.Info!.Value.Embedder!)
            .Select(g => (ISearchBackend)new SelectedPackages(set.DenseFor(g.Key), g.Select(x => x.Digest).ToHashSet(StringComparer.Ordinal)))
            .ToList();
        var missing = semantic.Where(x => x.Info!.Value.Embedder is null)
            .Select(x => (string?)x.Info!.Value.Model["name"]).OfType<string>().Distinct().ToList();
        string? note = null;
        if (missing.Count > 0)
        {
            var install = missing.Select(n => OpenMoca.Reader.Onnx.StandardModel.Find(n)?.Id).OfType<string>().ToList();
            note = $"Some packages' indexes need a model that is not installed ({string.Join(", ", missing)}); they were searched by words."
                + (install.Count > 0 ? $" To search them by meaning, run: moca-mcp models add {string.Join(' ', install)}" : "");
        }

        var mode = requested ?? (groups.Count > 0 ? "hybrid" : "lexical");
        if (mode != "lexical" && groups.Count == 0)
        {
            note ??= "None of these packages has an index built with an installed model, so they were searched by words.";
            return (lexical, "lexical", note);
        }

        var dense = groups.Count == 1 ? groups[0] : new FusedBackend(groups);
        return mode switch
        {
            "semantic" => (dense, mode, note),
            "hybrid" => (new HybridBackend(lexical, dense), mode, note),
            _ => (lexical, "lexical", note),
        };
    }

    // A package with a semantic index: the model that made it, and whether it can be searched by meaning here.
    private static void AddSemantic(JsonObject package, PackageSet set, string digest)
    {
        if (set.SemanticOf(digest) is not { } info)
        {
            return;
        }

        var name = (string?)info.Model["name"];
        var semantic = new JsonObject { ["model"] = name, ["available"] = info.Embedder is not null };
        if (info.Embedder is null && name is not null && OpenMoca.Reader.Onnx.StandardModel.Find(name) is { } standard)
        {
            semantic["install"] = $"moca-mcp models add {standard.Id}";
        }

        package["semantic"] = semantic;
    }

    // What a call left out on purpose, said in the answer so the model can act on it.
    private static void AddBoundNotes(JsonObject result, bool truncated, bool termsExcluded, MocaMcpOptions mcp)
    {
        if (truncated)
        {
            result["truncated"] = true;
        }

        if (termsExcluded)
        {
            result["termsNote"] = $"Some results were left out: their creators' terms do not allow them to be sent to {Destination(mcp)}.";
        }
    }

    private static string Destination(MocaMcpOptions mcp) => mcp.Terms.Ai?.Destination switch
    {
        TermsDestinations.Device => "a model on the person's own device",
        TermsDestinations.Host => "this host's own model",
        _ => "a third party's model",
    };

    /// <summary>
    /// Records as the answer carries them, within the host's bounds: each text cut to <see cref="MocaMcpOptions.MaxTextBytes"/>
    /// (the record marked <c>truncated</c>), and records left out once the answer passes
    /// <see cref="MocaMcpOptions.MaxResponseBytes"/>, always keeping the first.
    /// </summary>
    internal static JsonArray Bounded(IEnumerable<CitationRecord> records, bool full, MocaMcpOptions mcp, out bool truncated)
    {
        truncated = false;
        var output = new JsonArray();
        long size = 0;
        foreach (var r in records)
        {
            var record = r.Text is { } text && Cut(text, mcp.MaxTextBytes, out var cut) is var shorter && cut ? r with { Text = shorter } : r;
            var json = full ? CitationJson.ToJson(record) : Compact(record);
            if (!ReferenceEquals(record, r))
            {
                json["truncated"] = true;
            }

            size += Encoding.UTF8.GetByteCount(json.ToJsonString());
            if (output.Count > 0 && size > mcp.MaxResponseBytes)
            {
                truncated = true;
                break;
            }

            output.Add(json);
        }

        return output;
    }

    /// <summary>Text cut to at most <paramref name="maxBytes"/> of UTF-8, at a character boundary.</summary>
    internal static string Cut(string text, int maxBytes, out bool cut)
    {
        cut = Encoding.UTF8.GetByteCount(text) > maxBytes;
        if (!cut)
        {
            return text;
        }

        var bytes = 0;
        var end = 0;
        while (end < text.Length)
        {
            var step = char.IsHighSurrogate(text[end]) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
            var width = Encoding.UTF8.GetByteCount(text.AsSpan(end, step));
            if (bytes + width > maxBytes)
            {
                break;
            }

            bytes += width;
            end += step;
        }

        return text[..end];
    }

    // What a call could not cover, said in the answer so the model can act on it. Only names the caller gave.
    private static void AddSelectionNotes(JsonObject result, Covered selection)
    {
        if (selection.Unknown.Count > 0)
        {
            result["unknownPackages"] = new JsonArray([.. selection.Unknown.Select(u => (JsonNode)u)]);
        }

        if (selection.NotSearched.Count > 0)
        {
            result["notSearched"] = new JsonArray([.. selection.NotSearched.Select(u => (JsonNode)u)]);
        }

        if (selection.Partial)
        {
            result["note"] = "Some packages were not searched: the server's memory budget is full. Name fewer packages with the packages argument, or ask again for the ones not searched.";
        }
    }

    /// <summary>The most nodes one <c>nodes</c> answer gives, and the default.</summary>
    internal const int MaxNodes = 100;

    private const int DefaultNodes = 20;

    private static async Task<JsonObject> StructureOfAsync(PackageSet set, MocaMcpOptions mcp, Dictionary<string, JsonElement> args, Func<CitationRecord, bool> allowed,
        Func<IEnumerable<CitationRecord>, IReadOnlyList<CitationRecord>> permitted, CancellationToken cancellationToken)
    {
        var names = Strings(args, "packages");
        var selection = Visible(set, await set.SelectAsync(names, cancellationToken).ConfigureAwait(false), names, Hidden(set, mcp));
        var op = args["op"].GetString()!;
        var iri = args["iri"].GetString()!;
        var transitive = Bool(args, "transitive") ?? false;
        // Reader contract §10 and §15.4: the structure of the packages not closed for the host's request, and the
        // overlays. Labels and definitions only a closed package states are not there.
        var s = set.Library.StructureFor(mcp.Terms);
        var bound = false;
        int? next = null;
        JsonNode? Nodes()
        {
            // Reader contract §15.4: only what the creator's terms allow; a page at a time.
            var all = permitted(s.Nodes(iri, String(args, "include") ?? "none").Where(r => allowed(r) && selection.Digests.Contains(r.Package.Digest)));
            var offset = Int(args, "offset") ?? 0;
            var page = all.Skip(offset).Take(Math.Clamp(Int(args, "limit") ?? DefaultNodes, 1, MaxNodes)).ToList();
            var output = Bounded(page, String(args, "detail") == "full", mcp, out bound);
            if (offset + output.Count < all.Count)
            {
                next = offset + output.Count;
            }

            return output;
        }

        JsonNode? answer = op switch
        {
            "concept" => StructureJson.Concept(s.Concept(iri)),
            "requires" => StructureJson.Items(s.Requires(iri, transitive)),
            "required_by" => StructureJson.Items(s.RequiredBy(iri)),
            "parts" => StructureJson.Items(s.Parts(iri)),
            "narrower" => StructureJson.Items(s.Narrower(iri, transitive)),
            "broader" => StructureJson.Items(s.Broader(iri)),
            "related" => StructureJson.Items(s.Related(iri)),
            "sequence" => s.Sequence(iri) is { } sequence ? StructureJson.Items(sequence) : null,
            "nodes" => Nodes(),
            _ => throw new McpProtocolException($"unknown operation {op}", McpErrorCode.InvalidParams),
        };
        var result = new JsonObject { ["op"] = op, ["iri"] = iri, ["result"] = answer };
        if (next is { } n)
        {
            result["nextOffset"] = n;
        }

        if (bound)
        {
            result["truncated"] = true;
        }

        AddSelectionNotes(result, selection);
        return result;
    }

    /// <summary>
    /// The loaded records a node id or ref names: a ref (<c>id@version#path</c>) names that version; an id without a
    /// version (<c>id#path</c>) names every loaded version, so the caller can serve the newest one that may be served.
    /// </summary>
    private static List<CitationRecord?> Versions(PackageSet set, string nodeId, string? locale)
    {
        var package = PackageOf(nodeId);
        var hash = nodeId.LastIndexOf('#');
        return hash < 0 || package.Contains('@', StringComparison.Ordinal)
            ? [set.Library.Get(nodeId, locale)]
            : [.. set.Library.Packages.Where(p => p.Result.Id == package)
                .Select(p => set.Library.Get($"{package}@{p.Result.Version}{nodeId[hash..]}", locale))];
    }

    private static async Task<CallToolResult> GetNodeAsync(PackageSet set, MocaMcpOptions mcp, Dictionary<string, JsonElement> args, Func<CitationRecord, bool> allowed,
        Func<IEnumerable<CitationRecord>, IReadOnlyList<CitationRecord>> permitted, CancellationToken cancellationToken)
    {
        var nodeId = args["node_id"].GetString()!;
        var locale = String(args, "locale");
        await set.SelectAsync([PackageOf(nodeId)], cancellationToken).ConfigureAwait(false);
        var versions = Versions(set, nodeId, locale);
        // A node in a hidden package is not there (Reader contract §15.4): the answer is the one for no such node.
        var hidden = Hidden(set, mcp);
        var found = versions.OfType<CitationRecord>().Where(r => allowed(r) && !hidden.Contains(r.Package.Digest)).ToList();
        var served = permitted(found).OrderByDescending(r => r.Package.Version, VersionOrder.Instance).ToList();
        if (served.Count == 0)
        {
            var why = found.Count > 0 ? $"; its creator's terms do not allow it to be sent to {Destination(mcp)}" : "";
            return Error(new JsonObject { ["error"] = $"no node {nodeId}{why}" });
        }

        var json = Bounded([served[0]], full: true, mcp, out _)[0]!.AsObject();
        if (served.Count > 1)
        {
            json["otherVersions"] = new JsonArray([.. served.Skip(1).Select(r => (JsonNode)r.Node.Ref)]);
        }

        return Text(json);
    }

    /// <summary>
    /// One package's details (Reader contract §15.4, "The catalogue rule"). A package with no catalogue entry (hidden for
    /// the request, outside the audience, or not loadable) gets exactly the answer for an unknown package. A closed
    /// package gets exactly its catalogue entry. An open package gets what the Reader found, with every member or
    /// relation that names a package without an entry left out, and no finding about a closed or hidden package.
    /// </summary>
    private static async Task<CallToolResult> PackageInfoAsync(PackageSet set, MocaMcpOptions mcp, Dictionary<string, JsonElement> args, CancellationToken cancellationToken)
    {
        var name = args["package"].GetString()!;
        await set.SelectAsync([name], cancellationToken).ConfigureAwait(false);
        var catalogue = set.Library.Catalogue(CatalogueOptions(mcp));
        var listed = catalogue.ToDictionary(c => $"{c.Id}@{c.Version}", StringComparer.Ordinal);
        // As before, a key names that version and an id the last version listed; now only among packages with an entry.
        var entry = set.Entries.FirstOrDefault(e => e.Key == name && listed.ContainsKey(e.Key))
            ?? set.Entries.LastOrDefault(e => e.Id == name && listed.ContainsKey(e.Key));
        if (entry is null || set.ResultFor(entry.Key) is not { } result)
        {
            return Error(new JsonObject { ["error"] = $"no package {name}" });
        }

        var own = listed[entry.Key];
        if (own.Closed)
        {
            return Text(Entry(own));
        }

        // Packages a model may be told of in full: open, with an entry. Closed ones by id and version only.
        var open = catalogue.Where(c => !c.Closed).Select(c => $"{c.Id}@{c.Version}").ToHashSet(StringComparer.Ordinal);
        var hidden = Hidden(set, mcp);
        var hiddenIds = set.Library.Packages.Where(p => hidden.Contains(p.Result.Digest!)).Select(p => p.Result.Id).ToHashSet(StringComparer.Ordinal);
        var notOpen = result.Members.Where(m => !open.Contains($"{m.Reference.Id}@{m.Reference.Version}")).Select(m => m.Reference.Id)
            .Concat(hiddenIds).Where(id => id != result.Id).ToHashSet(StringComparer.Ordinal);
        bool Named(string key) => listed.ContainsKey(key);

        var info = new JsonObject
        {
            ["id"] = result.Id,
            ["version"] = result.Version,
            ["manifest"] = Manifest(result.Manifest, Named, hiddenIds),
            ["digest"] = result.Digest,
            ["signed"] = result.Signers.Count > 0,
            ["signers"] = new JsonArray([.. result.Signers.Select(x => (JsonNode)x)]),
            ["attestations"] = new JsonArray([.. result.Attestations.Select(a => (JsonNode)new JsonObject
            {
                ["file"] = a.File,
                ["role"] = a.Role,
                ["outcome"] = a.Outcome,
                ["signer"] = a.Signer,
                ["reason"] = a.Reason,
                ["pagesCurrent"] = a.Current.Count,
                ["pagesOutdated"] = a.Outdated.Count,
            })]),
            ["capabilities"] = new JsonArray([.. result.Capabilities.Select(c => (JsonNode)c)]),
            ["profiles"] = new JsonArray([.. result.Profiles.Select(c => (JsonNode)c)]),
            ["nodes"] = result.Nodes.Count,
            ["members"] = new JsonArray([.. result.Members
                .Where(m => Named($"{m.Reference.Id}@{m.Reference.Version}"))
                .Select(m => (JsonNode)(open.Contains($"{m.Reference.Id}@{m.Reference.Version}")
                    ? new JsonObject
                    {
                        ["id"] = m.Reference.Id,
                        ["version"] = m.Reference.Version,
                        ["digest"] = m.Reference.Digest,
                        ["status"] = m.Status,
                    }
                    : new JsonObject { ["id"] = m.Reference.Id, ["version"] = m.Reference.Version }))]),
            ["searchMode"] = set.HasChunks(entry.Key) ? "sidecar" : "lexical",
            // Reader contract §15.1: the creator's terms as written, their problems and who stated them.
            ["terms"] = result.Terms is { } terms ? JsonSerializer.SerializeToNode(terms, CitationJson.Options) : null,
            // Contract §15.4: no finding about a closed or hidden package, such as a member's.
            ["findings"] = new JsonArray([.. result.Diagnostics
                .Where(d => !notOpen.Any(id => d.Message.Contains(id, StringComparison.Ordinal) || (d.File?.Contains(id, StringComparison.Ordinal) ?? false)))
                .Select(d => JsonSerializer.SerializeToNode(d, CitationJson.Options))]),
        };
        AddSemantic(info, set, result.Digest!);
        return Text(info);
    }

    /// <summary>
    /// An open package's manifest as a model may see it (Reader contract §15.4): members without a catalogue entry left
    /// out, and relations to a package hidden for the request left out.
    /// </summary>
    private static JsonNode? Manifest(JsonNode? manifest, Func<string, bool> named, IReadOnlySet<string> hiddenIds)
    {
        if (manifest?.DeepClone() is not JsonObject copy)
        {
            return manifest?.DeepClone();
        }

        if (copy["members"] is JsonArray members)
        {
            foreach (var m in members.ToList())
            {
                if (m is not JsonObject member || !named($"{Catalogue.Text(member["id"])}@{Catalogue.Text(member["version"])}"))
                {
                    members.Remove(m);
                }
            }
        }

        if (copy["relations"] is JsonArray relations)
        {
            foreach (var r in relations.ToList())
            {
                if (r is JsonObject relation && Catalogue.Text(relation["target"]) is { } target && hiddenIds.Contains(target))
                {
                    relations.Remove(r);
                }
            }
        }

        return copy;
    }

    /// <summary>A citation record reduced to what a model needs in context, as moca-mcp's <c>compactRecord</c>.</summary>
    internal static JsonObject Compact(CitationRecord r)
    {
        var checkedEvidence = r.Evidence.Where(e => e.Matched is not null).ToList();
        var compact = new JsonObject { ["ref"] = r.Node.Ref, ["title"] = r.Node.Title };
        if (r.Text is not null)
        {
            compact["text"] = r.Text;
        }

        if (r.Score is not null)
        {
            compact["score"] = r.Score;
        }

        compact["status"] = r.Trust.Status;
        compact["stale"] = r.Trust.Stale;
        compact["inForce"] = r.Trust.InForce;
        compact["superseded"] = r.Trust.Superseded;
        if (r.Trust.Contested)
        {
            compact["contested"] = true;
        }

        compact["signed"] = r.Package.Signed;
        compact["evidenceMatched"] = checkedEvidence.Count == 0 ? null : checkedEvidence.All(e => e.Matched == true);
        if (r.Retrieval is not null)
        {
            compact["retrieval"] = JsonSerializer.SerializeToNode(r.Retrieval, CitationJson.Options);
        }

        return compact;
    }

    private static CallToolResult Text(JsonNode? value) => new()
    {
        Content = [new TextContentBlock { Text = value?.ToJsonString(Output) ?? "null" }],
    };

    private static CallToolResult Error(JsonNode value) => Error(value.ToJsonString(Output));

    private static CallToolResult Error(string text) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = true,
    };

    private static string? String(Dictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string>? Strings(Dictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Array ? [.. v.EnumerateArray().Select(x => x.GetString()!)] : null;

    private static int? Int(Dictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static bool? Bool(Dictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    /// <summary>JavaScript's <c>encodeURIComponent</c>: everything but <c>A–Z a–z 0–9 - _ . ! ~ * ' ( )</c> escaped.</summary>
    internal static string EncodeUriComponent(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || "-_.!~*'()".Contains(c, StringComparison.Ordinal)))
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    private static (IReadOnlyList<Tool>, IReadOnlyList<ResourceTemplate>) LoadDefinitions()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("OpenMoca.Mcp.definitions.json")
            ?? throw new InvalidOperationException("definitions.json is missing from the assembly");
        var document = JsonNode.Parse(stream)!;
        var tools = document["tools"].Deserialize<List<Tool>>(McpJsonUtilities.DefaultOptions)!;
        var templates = document["resourceTemplates"].Deserialize<List<ResourceTemplate>>(McpJsonUtilities.DefaultOptions)!;
        return (tools, templates);
    }
}

/// <summary>What a call covers, as the model may be told it: see <c>MocaServer.Visible</c>.</summary>
internal sealed record Covered(IReadOnlySet<string> Digests, IReadOnlyList<string> Unknown, IReadOnlyList<string> NotSearched, bool Partial);
