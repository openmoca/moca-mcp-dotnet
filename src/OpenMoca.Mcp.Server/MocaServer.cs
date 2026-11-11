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
/// The MCP server over a library: the four tools and the node resources, with the definitions in definitions.json
/// and the answers tests/parity expects. Every result is a citation record or built from them; package text is returned
/// as data inside records, never as instructions (Reader contract §12).
/// </summary>
internal static class MocaServer
{
    public const string NodeUri = "moca://node/";

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
                        "moca_list_packages" => Text(ListPackages(set)),
                        "moca_search" => Text(await SearchAsync(set, options, args, cancellationToken).ConfigureAwait(false)),
                        "moca_structure" => Text(await StructureOfAsync(set, args, Allowed, cancellationToken).ConfigureAwait(false)),
                        "moca_get_node" => await GetNodeAsync(set, args, Allowed, cancellationToken).ConfigureAwait(false),
                        "moca_package_info" => await PackageInfoAsync(set, args, cancellationToken).ConfigureAwait(false),
                        _ => throw new McpProtocolException($"Tool {call.Name} not found", McpErrorCode.InvalidParams),
                    }, cancellationToken).ConfigureAwait(false);
                },
                ListResourceTemplatesHandler = (_, _) =>
                    ValueTask.FromResult(new ListResourceTemplatesResult { ResourceTemplates = [.. Definitions.Value.Templates] }),
                // The pages of the loaded packages; a package loads when a tool first uses it, or with --preload.
                ListResourcesHandler = async (_, cancellationToken) => await set.RunAsync(() => Task.FromResult(new ListResourcesResult
                {
                    Resources = [.. set.Library.Citations().Where(Allowed).Select(r => new Resource
                    {
                        Uri = NodeUri + EncodeUriComponent(r.Node.Ref),
                        Name = r.Node.Ref,
                        Title = r.Node.Title,
                        // A node without a description takes the template's, so every listed resource has one.
                        Description = r.Node.Description ?? Definitions.Value.Templates[0].Description,
                        MimeType = "text/markdown",
                    })],
                }), cancellationToken).ConfigureAwait(false),
                ReadResourceHandler = async (request, cancellationToken) =>
                {
                    var uri = request.Params!.Uri;
                    var isNode = uri.StartsWith(NodeUri, StringComparison.Ordinal);
                    var nodeRef = isNode ? Uri.UnescapeDataString(uri[NodeUri.Length..]) : uri;
                    var record = isNode
                        ? await set.RunAsync(async () =>
                        {
                            await set.SelectAsync([PackageOf(nodeRef)], cancellationToken).ConfigureAwait(false);
                            return set.Library.Get(nodeRef);
                        }, cancellationToken).ConfigureAwait(false)
                        : null;
                    if (record is null || !Allowed(record))
                    {
                        throw new McpProtocolException($"no node {nodeRef}", McpErrorCode.InvalidParams);
                    }

                    return new ReadResourceResult
                    {
                        Contents = [new TextResourceContents { Uri = uri, MimeType = "text/markdown", Text = record.Text ?? "" }],
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

    private static JsonArray ListPackages(PackageSet set) => new([.. set.Entries.Select(e =>
    {
        var package = new JsonObject { ["id"] = e.Id, ["version"] = e.Version };
        if (e.Title is not null)
        {
            package["title"] = e.Title;
        }

        if (e.Description is not null)
        {
            package["description"] = e.Description;
        }

        var result = set.ResultFor(e.Key);
        package["loaded"] = result is not null;
        if (result is not null)
        {
            package["digest"] = result.Digest;
            package["signed"] = result.Signers.Count > 0;
            package["capabilities"] = new JsonArray([.. result.Capabilities.Select(c => (JsonNode)c)]);
            package["nodes"] = result.Nodes.Count;
            package["searchMode"] = set.HasChunks(e.Key) ? "sidecar" : "lexical";
            AddSemantic(package, set, result.Digest!);
            var members = result.Members.Where(m => m.Result is { Valid: true }).Select(m => (JsonNode)$"{m.Result!.Id}@{m.Result.Version}").ToList();
            if (members.Count > 0)
            {
                package["members"] = new JsonArray([.. members]);
            }
        }
        else if (set.ProblemFor(e.Key) is { } problem)
        {
            package["problem"] = problem;
        }

        return (JsonNode)package;
    })]);

    private static async Task<JsonObject> SearchAsync(PackageSet set, MocaMcpOptions mcp, Dictionary<string, JsonElement> args, CancellationToken cancellationToken)
    {
        var selection = await set.SelectAsync(Strings(args, "packages"), cancellationToken).ConfigureAwait(false);
        var (backend, mode, note) = Backend(set, selection, String(args, "mode"));
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
        };
        var records = await search.SearchAsync(query, options, cancellationToken).ConfigureAwait(false);
        var full = String(args, "detail") == "full";
        var result = new JsonObject
        {
            ["query"] = query,
            ["results"] = new JsonArray([.. records.Select(r => full ? CitationJson.ToJson(r) : Compact(r))]),
        };
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
    /// When meaning search is asked for but unavailable, it says why and searches by words.
    /// </summary>
    private static (ISearchBackend Backend, string Mode, string? Note) Backend(PackageSet set, Selection selection, string? requested)
    {
        var lexical = new SelectedPackages(set.Backend, selection.Digests);
        var semantic = selection.Digests.Select(d => (Digest: d, Info: set.SemanticOf(d))).Where(x => x.Info is not null).ToList();
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

    // What a call could not cover, said in the answer so the model can act on it.
    private static void AddSelectionNotes(JsonObject result, Selection selection)
    {
        if (selection.Unknown.Count > 0)
        {
            result["unknownPackages"] = new JsonArray([.. selection.Unknown.Select(u => (JsonNode)u)]);
        }

        if (selection.NotLoaded.Count > 0)
        {
            result["notSearched"] = new JsonArray([.. selection.NotLoaded.Select(u => (JsonNode)u)]);
            result["note"] = "Some packages were not searched: the server's memory budget is full. Name the packages to search with the packages argument.";
        }
    }

    private static async Task<JsonObject> StructureOfAsync(PackageSet set, Dictionary<string, JsonElement> args, Func<CitationRecord, bool> allowed, CancellationToken cancellationToken)
    {
        var selection = await set.SelectAsync(Strings(args, "packages"), cancellationToken).ConfigureAwait(false);
        var op = args["op"].GetString()!;
        var iri = args["iri"].GetString()!;
        var transitive = Bool(args, "transitive") ?? false;
        var s = set.Library.Structure;
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
            "nodes" => new JsonArray([.. s.Nodes(iri, String(args, "include") ?? "none")
                .Where(r => allowed(r) && selection.Digests.Contains(r.Package.Digest))
                .Select(r => String(args, "detail") == "full" ? CitationJson.ToJson(r) : Compact(r))]),
            _ => throw new McpProtocolException($"unknown operation {op}", McpErrorCode.InvalidParams),
        };
        var result = new JsonObject { ["op"] = op, ["iri"] = iri, ["result"] = answer };
        AddSelectionNotes(result, selection);
        return result;
    }

    private static async Task<CallToolResult> GetNodeAsync(PackageSet set, Dictionary<string, JsonElement> args, Func<CitationRecord, bool> allowed, CancellationToken cancellationToken)
    {
        var nodeId = args["node_id"].GetString()!;
        await set.SelectAsync([PackageOf(nodeId)], cancellationToken).ConfigureAwait(false);
        var record = set.Library.Get(nodeId, String(args, "locale"));
        if (record is null || !allowed(record))
        {
            return Error(new JsonObject { ["error"] = $"no node {nodeId}" });
        }

        return Text(CitationJson.ToJson(record));
    }

    private static async Task<CallToolResult> PackageInfoAsync(PackageSet set, Dictionary<string, JsonElement> args, CancellationToken cancellationToken)
    {
        var name = args["package"].GetString()!;
        var selection = await set.SelectAsync([name], cancellationToken).ConfigureAwait(false);
        var entry = set.Entries.FirstOrDefault(e => e.Key == name) ?? set.Entries.LastOrDefault(e => e.Id == name);
        if (entry is null)
        {
            return Error(new JsonObject { ["error"] = $"no package {name}" });
        }

        if (set.ResultFor(entry.Key) is not { } result)
        {
            return Error(new JsonObject { ["error"] = $"package {entry.Key} could not be loaded", ["problem"] = set.ProblemFor(entry.Key) });
        }

        var info = new JsonObject
        {
            ["id"] = result.Id,
            ["version"] = result.Version,
            ["manifest"] = result.Manifest?.DeepClone(),
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
            ["members"] = new JsonArray([.. result.Members.Select(m => (JsonNode)new JsonObject
            {
                ["id"] = m.Reference.Id,
                ["version"] = m.Reference.Version,
                ["digest"] = m.Reference.Digest,
                ["status"] = m.Status,
            })]),
            ["searchMode"] = set.HasChunks(entry.Key) ? "sidecar" : "lexical",
            ["findings"] = new JsonArray([.. result.Diagnostics.Select(d => JsonSerializer.SerializeToNode(d, CitationJson.Options))]),
        };
        AddSemantic(info, set, result.Digest!);
        AddSelectionNotes(info, selection);
        return Text(info);
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
