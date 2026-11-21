using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace OpenMoca.Mcp.Tests;

/// <summary>The server end to end: the built moca-mcp over stdio, as an MCP client starts it.</summary>
public sealed class ServerTests
{
    /// <summary>A model store with nothing in it.</summary>
    internal static readonly string NoModels = Path.Combine(AppContext.BaseDirectory, "no-models");

    private static string Spec(string path) => Path.Combine(MocaSpec.Root, path);

    internal static async Task<McpClient> StartAsync(params string[] args)
    {
        var server = Path.Combine(AppContext.BaseDirectory, "moca-mcp.dll");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "moca-mcp",
            Command = "dotnet",
            Arguments = [server, .. args],
            // No models unless a test names a store: results never depend on what this machine has installed.
            EnvironmentVariables = new Dictionary<string, string?> { ["MOCA_MODELS"] = NoModels },
        });
        return await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    internal static async Task<JsonNode> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);
        return JsonNode.Parse(((TextContentBlock)result.Content[0]).Text)!;
    }

    private static Task<McpClient> SupportKbAsync(params string[] extra) => StartAsync(
        [Spec("examples/support-kb") + "=" + Spec("examples/sidecars/support-kb.moca.idx"),
         "--trust-root", Spec("fixtures/signing-keys/trust-root.json"), .. extra]);

    [Fact]
    public async Task Every_tool_is_read_only_idempotent_and_closed_world_with_a_title()
    {
        await using var client = await SupportKbAsync();
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["moca_get_node", "moca_list_packages", "moca_package_info", "moca_search", "moca_structure"], tools.Select(t => t.Name).Order());
        foreach (var tool in tools.Select(t => t.ProtocolTool))
        {
            Assert.False(string.IsNullOrEmpty(tool.Title));
            Assert.Equal(tool.Title, tool.Annotations?.Title);
            Assert.True(tool.Annotations?.ReadOnlyHint);
            Assert.False(tool.Annotations?.DestructiveHint);
            Assert.True(tool.Annotations?.IdempotentHint);
            Assert.False(tool.Annotations?.OpenWorldHint);
        }
    }

    [Fact]
    public async Task Lists_loaded_packages_with_digest_signature_and_search_mode()
    {
        await using var client = await SupportKbAsync();
        var list = (await CallAsync(client, "moca_list_packages", [])).AsArray();
        var kb = list.Single(p => (string?)p!["id"] == "https://example.com/moca/support-kb")!;

        Assert.True((bool)kb["signed"]!);
        Assert.Equal("sidecar", (string?)kb["searchMode"]);
        Assert.StartsWith("sha256:", (string?)kb["digest"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_returns_compact_records_with_matched_evidence()
    {
        await using var client = await SupportKbAsync();
        var results = (await CallAsync(client, "moca_search", new() { ["query"] = "refund window" }))["results"]!.AsArray();
        var r = results.Single(x => ((string)x!["ref"]!).EndsWith("#refund-window.md", StringComparison.Ordinal))!.AsObject();

        Assert.True((bool)r["evidenceMatched"]!);
        Assert.True((bool)r["signed"]!);
        Assert.Equal(["evidenceMatched", "inForce", "ref", "score", "signed", "stale", "status", "superseded", "text", "title"],
            r.Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_host_audience_filter_cannot_be_widened()
    {
        await using var client = await SupportKbAsync("--audience", "public");
        // A caller cannot opt in to excluded content either: include_all is ignored, the host decides.
        var results = (await CallAsync(client, "moca_search", new() { ["query"] = "delete account", ["include_all"] = true }))["results"]!.AsArray();
        Assert.DoesNotContain(results, r => ((string)r!["ref"]!).EndsWith("#account-deletion.md", StringComparison.Ordinal));

        var result = await client.CallToolAsync("moca_get_node", new Dictionary<string, object?> { ["node_id"] = "https://example.com/moca/support-kb#account-deletion.md" },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);

        var resources = await client.ListResourcesAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(resources, r => r.Name.EndsWith("#account-deletion.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nodes_are_resources_read_by_their_versioned_reference()
    {
        await using var client = await SupportKbAsync();
        var resources = await client.ListResourcesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var refund = resources.Single(r => r.Name.EndsWith("#refund-window.md", StringComparison.Ordinal));
        Assert.StartsWith("moca://node/https%3A%2F%2Fexample.com%2Fmoca%2Fsupport-kb%40", refund.Uri, StringComparison.Ordinal);

        var read = await client.ReadResourceAsync(refund.Uri, cancellationToken: TestContext.Current.CancellationToken);
        var text = Assert.IsType<TextResourceContents>(read.Contents[0]);
        Assert.Equal("text/markdown", text.MimeType);
        Assert.Contains("refund", text.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Structure_answers_name_their_layer()
    {
        const string IR = "https://example.org/handbook/incident#";
        await using var client = await StartAsync(Spec("examples/handbook/incident-response"),
            "--overlay", "organisation=" + Spec("conformance/fixtures/overlays/organisation-review.ttl"));

        var sequence = await CallAsync(client, "moca_structure", new() { ["op"] = "sequence", ["iri"] = IR + "ResponseSteps" });
        Assert.Equal(["Assess severity", "Declare the incident", "Notify customers", "Resolve and review"],
            sequence["result"]!.AsArray().Select(x => (string)x!["label"]!));

        var requires = await CallAsync(client, "moca_structure", new() { ["op"] = "requires", ["iri"] = IR + "ResolveAndReview", ["transitive"] = true });
        Assert.Equal([("AssessSeverity", "package"), ("DeclareIncident", "organisation")],
            requires["result"]!.AsArray().Select(x => (((string)x!["iri"]!).Split('#')[1], (string)x["layer"]!)));
    }

    [Fact]
    public async Task Oversized_input_is_refused_before_any_search_runs()
    {
        await using var client = await SupportKbAsync();
        async Task<bool> Refused(string tool, Dictionary<string, object?> args)
        {
            try
            {
                return (await client.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken)).IsError == true;
            }
            catch (ModelContextProtocol.McpException)
            {
                return true;
            }
        }

        string[] Concepts(int n) => [.. Enumerable.Range(0, n).Select(i => $"https://example.org/concept#{i}")];
        Assert.True(await Refused("moca_search", new() { ["query"] = "refund", ["concepts"] = Concepts(51) }));
        Assert.True(await Refused("moca_search", new() { ["query"] = new string('x', 1001) }));
        Assert.True(await Refused("moca_search", new() { ["query"] = "refund", ["locale"] = new string('x', 36) }));
        Assert.True(await Refused("moca_get_node", new() { ["node_id"] = "https://example.com/" + new string('x', 2048) }));
        Assert.True(await Refused("moca_structure", new() { ["op"] = "concept", ["iri"] = "https://example.org/" + new string('x', 2048) }));
        Assert.False(await Refused("moca_search", new() { ["query"] = new string('x', 1000), ["concepts"] = Concepts(50) }));
    }

    [Fact]
    public async Task The_server_reports_its_version()
    {
        await using var client = await SupportKbAsync();
        Assert.Equal("moca", client.ServerInfo?.Name);
        Assert.Equal(typeof(MocaServer).Assembly.GetName().Version!.ToString(3), client.ServerInfo?.Version);
    }

    /// <summary>Writes a package with one node into a folder: its id, version and optional terms.</summary>
    private static string Package(string root, string name, string version, string? terms = null, string text = "The harbour lantern is lit at dusk.")
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(dir, "content"));
        File.WriteAllText(Path.Combine(dir, "moca.json"),
            $"{{\"id\":\"https://example.com/{name.Split('-')[0]}\",\"version\":\"{version}\",\"title\":\"T\"{(terms is null ? "" : $",\"terms\":{terms}")}}}");
        File.WriteAllText(Path.Combine(dir, "content", "a.md"), $"---\ntype: Note\ntitle: Lantern\n---\n\n{text}\n");
        return dir;
    }

    [Fact]
    public async Task The_creators_terms_apply_to_everything_returned_as_a_third_partys_model_by_default()
    {
        var root = Directory.CreateTempSubdirectory("moca-terms-").FullName;
        try
        {
            var dir = Package(root, "closed", "1.0.0", """{"ai":{"allowed":{"provider":false}}}""");
            await using var provider = await StartAsync(dir);
            await using var device = await StartAsync(dir, "--destination", "device");
            var ct = TestContext.Current.CancellationToken;

            var search = await CallAsync(provider, "moca_search", new() { ["query"] = "lantern" });
            var node = await provider.CallToolAsync("moca_get_node", new Dictionary<string, object?> { ["node_id"] = "https://example.com/closed#a.md" }, cancellationToken: ct);
            var resources = await provider.ListResourcesAsync(cancellationToken: ct);
            var onDevice = await CallAsync(device, "moca_search", new() { ["query"] = "lantern" });

            Assert.Empty(search["results"]!.AsArray());
            Assert.Contains("creators' terms", (string?)search["termsNote"], StringComparison.Ordinal);
            Assert.True(node.IsError);
            Assert.Contains("creator's terms", ((TextContentBlock)node.Content[0]).Text, StringComparison.Ordinal);
            Assert.Empty(resources);
            Assert.Single(onDevice["results"]!.AsArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Require_signature_serves_only_signed_packages()
    {
        await using var client = await StartAsync(Spec("examples/support-kb"), Spec("examples/minimal"),
            "--trust-root", Spec("fixtures/signing-keys/trust-root.json"), "--require-signature");
        var list = (await CallAsync(client, "moca_list_packages", [])).AsArray();

        Assert.Equal(["https://example.com/moca/support-kb"], list.Select(p => (string)p!["id"]!));
    }

    [Fact]
    public async Task A_node_id_without_a_version_gives_the_newest_version_and_names_the_others()
    {
        var root = Directory.CreateTempSubdirectory("moca-versions-").FullName;
        try
        {
            var older = Package(root, "lamp-1", "1.10.0", text: "Older text.");
            var newer = Package(root, "lamp-2", "1.9.0-beta.1", text: "Pre-release text.");
            var newest = Package(root, "lamp-3", "2.0.0", text: "Newest text.");
            await using var client = await StartAsync(older, newer, newest);

            var node = await CallAsync(client, "moca_get_node", new() { ["node_id"] = "https://example.com/lamp#a.md" });
            var pinned = await CallAsync(client, "moca_get_node", new() { ["node_id"] = "https://example.com/lamp@1.10.0#a.md" });

            Assert.Equal("2.0.0", (string?)node["package"]!["version"]);
            Assert.Equal(["https://example.com/lamp@1.10.0#a.md", "https://example.com/lamp@1.9.0-beta.1#a.md"], node["otherVersions"]!.AsArray().Select(v => (string)v!));
            Assert.Equal("1.10.0", (string?)pinned["package"]!["version"]);
            Assert.Null(pinned["otherVersions"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_watched_library_picks_up_new_packages()
    {
        var root = Directory.CreateTempSubdirectory("moca-watch-").FullName;
        try
        {
            CopyFolder(Spec("examples/minimal"), Path.Combine(root, "minimal"));
            await using var client = await StartAsync("--library", root, "--watch");
            async Task<int> Count() => (await CallAsync(client, "moca_list_packages", [])).AsArray().Count;
            Assert.Equal(1, await Count());

            CopyFolder(Spec("examples/service-catalogue"), Path.Combine(root, "service-catalogue"));
            var count = 1;
            for (var i = 0; i < 40 && count < 2; i++)
            {
                await Task.Delay(250, TestContext.Current.CancellationToken);
                count = await Count();
            }

            Assert.Equal(2, count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_neighbours_index_matched_by_name_is_passed_over_for_the_next()
    {
        // kb.aaa.mocai matches kb's <stem>.*.mocai pattern but is built for another package, so it fails the digest
        // check; the server moves on to kb.zzz.mocai, which binds.
        var root = Directory.CreateTempSubdirectory("moca-sidecars-").FullName;
        try
        {
            CopyFolder(Spec("examples/support-kb"), Path.Combine(root, "kb"));
            CopyFolder(Spec("examples/sidecars/support-kb.moca.idx"), Path.Combine(root, "kb.aaa.mocai"));
            CopyFolder(Spec("examples/sidecars/support-kb.moca.idx"), Path.Combine(root, "kb.zzz.mocai"));
            var neighbour = Path.Combine(root, "kb.aaa.mocai", "index.json");
            var index = JsonNode.Parse(File.ReadAllText(neighbour))!;
            index["target"]!["digest"] = "sha256:" + new string('0', 64);
            File.WriteAllText(neighbour, index.ToJsonString());

            await using var client = await StartAsync("--library", root, "--trust-root", Spec("fixtures/signing-keys/trust-root.json"));
            await CallAsync(client, "moca_search", new() { ["query"] = "refund" }); // a package loads when a tool first uses it
            var list = (await CallAsync(client, "moca_list_packages", [])).AsArray();
            Assert.Equal("sidecar", (string?)list.Single()!["searchMode"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CopyFolder(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    [Fact]
    public void Tool_definitions_parse()
    {
        Assert.Equal(5, MocaServer.Tools.Count);
        Assert.All(MocaServer.Tools, t => Assert.Equal(JsonValueKind.Object, t.InputSchema.ValueKind));
    }
}
