using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenMoca.Reader.Search;
using Xunit;

namespace OpenMoca.Mcp.Tests;

/// <summary>
/// The canary (Reader contract §15.4; moca-spec ADR-0041): three packages whose text is unique marker strings, one open,
/// one closed to a third party's model and named, one closed and hidden. Every tool and resource is called, with
/// not-found paths, paging, errors and notes. The hidden package's markers appear nowhere; the named one's only in its
/// catalogue entry; and every answer is the same, byte for byte, as from a server that never had the hidden package.
/// </summary>
public sealed class CanaryTests
{
    private sealed record Canary(string Name, string Id, string Iri, string Title, string Description, string Publisher,
        string NodeTitle, string Body, string Label, string Definition, string? Terms)
    {
        public string Key => $"{Id}@1.0.0";

        public string NodeId => $"{Id}#a.md";

        public string NodeRef => $"{Key}#a.md";

        // Every string the package supplies, except its id and title (checked on their own).
        public IEnumerable<string> Text => [Description, Publisher, NodeTitle, Body, Label, Definition];
    }

    private static Canary Make(string name, string? terms)
    {
        var mark = $"Zq{name}";
        return new Canary(name, $"https://example.com/{mark.ToLowerInvariant()}id", $"https://example.org/{mark.ToLowerInvariant()}#Thing",
            $"{mark}Title", $"{mark}Description", $"{mark}Publisher", $"{mark}NodeTitle", $"{mark}Body lantern", $"{mark}Label",
            $"{mark}Definition", terms);
    }

    private static readonly Canary Open = Make("open", null);

    private static readonly Canary Named = Make("named", """{"ai":{"allowed":{"provider":false}}}""");

    private static readonly Canary Hidden = Make("hidden", """{"ai":{"allowed":{"provider":false,"mention":false}}}""");

    /// <summary>A package with one node bound to one concept: every string in it carries the package's marker.</summary>
    private static string Write(string root, Canary c)
    {
        var dir = Path.Combine(root, c.Name);
        Directory.CreateDirectory(Path.Combine(dir, "content"));
        var manifest = new JsonObject
        {
            ["id"] = c.Id,
            ["version"] = "1.0.0",
            ["title"] = c.Title,
            ["description"] = c.Description,
            ["language"] = "en",
            ["publisher"] = new JsonObject { ["name"] = c.Publisher },
            ["tags"] = new JsonArray(c.Name + "-tag-" + c.Label),
        };
        if (c.Terms is not null)
        {
            manifest["terms"] = JsonNode.Parse(c.Terms);
        }

        File.WriteAllText(Path.Combine(dir, "moca.json"), manifest.ToJsonString());
        File.WriteAllText(Path.Combine(dir, "content", "a.md"),
            $"---\ntype: Note\ntitle: {c.NodeTitle}\ndescription: {c.Description}\nmoca:\n  concepts:\n    - iri: {c.Iri}\n      role: primary\n---\n\n{c.Body}\n");
        File.WriteAllText(Path.Combine(dir, "structure.ttl"),
            $"@prefix skos: <http://www.w3.org/2004/02/skos/core#> .\n\n<{c.Iri}> a skos:Concept ;\n  skos:prefLabel \"{c.Label}\"@en ;\n  skos:definition \"{c.Definition}\"@en ;\n  skos:broader <{c.Iri}Parent> .\n\n<{c.Iri}Parent> a skos:Concept ;\n  skos:prefLabel \"{c.Label}Parent\"@en .\n");
        return dir;
    }

    /// <summary>An embedder that records every text it is given.</summary>
    private sealed class RecordingEmbedder : IEmbedder
    {
        public List<string> Texts { get; } = [];

        public string Name => "recording";

        public string? Version => null;

        public int Dimensions => 4;

        public ValueTask<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken cancellationToken)
        {
            lock (Texts)
            {
                Texts.AddRange(texts);
            }

            return ValueTask.FromResult<IReadOnlyList<float[]>>([.. texts.Select(_ => new float[] { 1, 0, 0, 0 })]);
        }
    }

    /// <summary>A server in-process over the packages in a folder: named one by one (loaded at start), or as a library
    /// folder (loaded on first use).</summary>
    private static async Task<McpClient> ConnectAsync(MocaMcpOptions options, string folder, bool library)
    {
        var source = new FolderPackageSource(library
            ? new FolderPackageSourceOptions { Folders = [folder] }
            : new FolderPackageSourceOptions { Packages = [.. Directory.GetDirectories(folder).Order(StringComparer.Ordinal).Select(p => (p, (string?)null))] });
        var moca = await MocaMcpServer.CreateAsync(options, source, cancellationToken: TestContext.Current.CancellationToken);
        var toServer = new Pipe();
        var toClient = new Pipe();
        var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), moca.ServerOptions);
        moca.Attach(server);
        _ = server.RunAsync(TestContext.Current.CancellationToken);
        return await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>One call and its answer, as the client sees it: the text, and whether it was an error.</summary>
    private sealed record Answer(string Label, string Arguments, string Text);

    /// <summary>Every tool and resource, with not-found paths, paging, errors and notes.</summary>
    private static async Task<List<Answer>> CallEverythingAsync(McpClient client)
    {
        var ct = TestContext.Current.CancellationToken;
        var answers = new List<Answer>();
        async Task Call(string tool, Dictionary<string, object?> args)
        {
            var label = $"{tool} {JsonSerializer.Serialize(args)}";
            try
            {
                var result = await client.CallToolAsync(tool, args, cancellationToken: ct);
                answers.Add(new Answer(label, JsonSerializer.Serialize(args), $"{(result.IsError == true ? "error: " : "")}{string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text))}"));
            }
            catch (McpException ex)
            {
                answers.Add(new Answer(label, JsonSerializer.Serialize(args), $"protocol error: {ex.Message}"));
            }
        }

        var tools = await client.ListToolsAsync(cancellationToken: ct);
        answers.Add(new Answer("tools/list", "", JsonSerializer.Serialize(tools.Select(t => t.ProtocolTool))));
        var templates = await client.ListResourceTemplatesAsync(cancellationToken: ct);
        answers.Add(new Answer("resources/templates/list", "", JsonSerializer.Serialize(templates.Select(t => t.ProtocolResourceTemplate))));

        await Call("moca_list_packages", []);
        var unknown = Make("unknown", null);
        var all = new[] { Open, Named, Hidden, unknown };
        foreach (var query in new[] { "lantern", "ZqhiddenBody", "ZqnamedBody", "ZqopenBody" })
        {
            await Call("moca_search", new() { ["query"] = query });
            await Call("moca_search", new() { ["query"] = query, ["detail"] = "full", ["limit"] = 20 });
            foreach (var mode in new[] { "semantic", "hybrid" })
            {
                await Call("moca_search", new() { ["query"] = query, ["mode"] = mode });
            }
        }

        foreach (var c in all)
        {
            foreach (var name in new[] { c.Id, c.Key })
            {
                await Call("moca_search", new() { ["query"] = "lantern", ["packages"] = new[] { name } });
                await Call("moca_search", new() { ["query"] = "lantern", ["packages"] = new[] { name, Open.Id } });
                await Call("moca_package_info", new() { ["package"] = name });
                await Call("moca_structure", new() { ["op"] = "nodes", ["iri"] = c.Iri, ["packages"] = new[] { name } });
            }

            foreach (var node in new[] { c.NodeId, c.NodeRef, c.NodeRef + "-missing" })
            {
                await Call("moca_get_node", new() { ["node_id"] = node });
            }

            foreach (var op in new[] { "concept", "requires", "required_by", "parts", "narrower", "broader", "related", "sequence" })
            {
                await Call("moca_structure", new() { ["op"] = op, ["iri"] = c.Iri });
                await Call("moca_structure", new() { ["op"] = op, ["iri"] = c.Iri + "Parent", ["transitive"] = true });
            }

            for (var offset = 0; offset < 3; offset++)
            {
                await Call("moca_structure", new() { ["op"] = "nodes", ["iri"] = c.Iri, ["limit"] = 1, ["offset"] = offset, ["include"] = "narrower" });
                await Call("moca_structure", new() { ["op"] = "nodes", ["iri"] = c.Iri + "Parent", ["limit"] = 1, ["offset"] = offset, ["include"] = "narrower" });
            }

            foreach (var uri in new[] { MocaServer.NodeUri + MocaServer.EncodeUriComponent(c.NodeRef), MocaServer.NodeUri + MocaServer.EncodeUriComponent(c.NodeId) })
            {
                try
                {
                    var read = await client.ReadResourceAsync(uri, cancellationToken: ct);
                    answers.Add(new Answer($"resources/read {uri}", uri, JsonSerializer.Serialize(read.Contents)));
                }
                catch (McpException ex)
                {
                    answers.Add(new Answer($"resources/read {uri}", uri, $"protocol error: {ex.Message}"));
                }
            }
        }

        // Errors: a missing argument, a wrong type, an unknown tool.
        await Call("moca_search", []);
        await Call("moca_get_node", new() { ["node_id"] = 7 });
        await Call("moca_unknown", []);

        // Every page of resources, a page at a time.
        string? cursor = null;
        do
        {
            var page = await client.ListResourcesAsync(new ListResourcesRequestParams { Cursor = cursor }, cancellationToken: ct);
            answers.Add(new Answer($"resources/list {cursor}", cursor ?? "", JsonSerializer.Serialize(page.Resources) + "|" + page.NextCursor));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        // Last, once everything is loaded.
        await Call("moca_list_packages", []);
        return answers;
    }

    /// <summary>Scores are computed over every loaded package and carry no identity (contract §15.4); they are compared apart.</summary>
    private static string WithoutScores(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, "\"score\": [-0-9.eE+]+", "\"score\": 0");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_hidden_package_is_never_named_and_a_closed_one_only_in_its_catalogue_entry(bool library)
    {
        var root = Directory.CreateTempSubdirectory("moca-canary-").FullName;
        try
        {
            var three = Path.Combine(root, "three");
            var two = Path.Combine(root, "two");
            foreach (var c in new[] { Open, Named, Hidden })
            {
                Write(three, c);
            }

            foreach (var c in new[] { Open, Named })
            {
                Write(two, c);
            }

            var embedder = new RecordingEmbedder();
            await using var withHidden = await ConnectAsync(new MocaMcpOptions { Embedders = [embedder] }, three, library);
            var answers = await CallEverythingAsync(withHidden);
            await using var withoutHidden = await ConnectAsync(new MocaMcpOptions { Embedders = [new RecordingEmbedder()] }, two, library);
            var baseline = await CallEverythingAsync(withoutHidden);

            // The canary is alive: the open package is served in full.
            var all = string.Join("\n", answers.Select(a => a.Text));
            Assert.Contains(Open.Title, all, StringComparison.Ordinal);
            Assert.Contains(Open.Body, all, StringComparison.Ordinal);
            Assert.Contains(Open.Label, all, StringComparison.Ordinal);
            Assert.Contains(Open.Definition, all, StringComparison.Ordinal);

            var problems = new List<string>();

            // Byte for byte as if the hidden package were not there.
            Assert.Equal(baseline.Count, answers.Count);
            for (var i = 0; i < answers.Count; i++)
            {
                if (WithoutScores(answers[i].Text) != WithoutScores(baseline[i].Text))
                {
                    problems.Add($"differs from a server without the hidden package: {answers[i].Label}\n  with:    {answers[i].Text}\n  without: {baseline[i].Text}");
                }
            }

            foreach (var a in answers)
            {
                // The hidden package: nothing, except the caller's own words given back as they are for an unknown name.
                foreach (var marker in Hidden.Text.Append(Hidden.Title).Append("Zqhidden"))
                {
                    var echoed = a.Arguments.Contains(marker, StringComparison.OrdinalIgnoreCase);
                    if (!echoed && a.Text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add($"hidden marker {marker} in {a.Label}: {a.Text}");
                    }
                }

                // The named package: its description, text, labels and definitions nowhere at all.
                foreach (var marker in Named.Text.Where(m => m != Named.Publisher))
                {
                    if (!a.Arguments.Contains(marker, StringComparison.Ordinal) && a.Text.Contains(marker, StringComparison.Ordinal))
                    {
                        problems.Add($"closed marker {marker} in {a.Label}: {a.Text}");
                    }
                }

                // Its title and publisher only in the catalogue: the list and its own package info.
                var catalogue = a.Label.StartsWith("moca_list_packages", StringComparison.Ordinal)
                    || (a.Label.StartsWith("moca_package_info", StringComparison.Ordinal) && a.Arguments.Contains(Named.Id, StringComparison.Ordinal));
                foreach (var marker in new[] { Named.Title, Named.Publisher })
                {
                    if (!catalogue && a.Text.Contains(marker, StringComparison.Ordinal))
                    {
                        problems.Add($"closed title or publisher {marker} outside the catalogue in {a.Label}: {a.Text}");
                    }
                }
            }

            Assert.True(problems.Count == 0, string.Join("\n", problems.Take(20)));

            // In the catalogue, the closed package is exactly the six fields.
            var list = JsonNode.Parse(answers.Last().Text)!.AsArray();
            var entry = list.Single(p => (string?)p!["id"] == Named.Id)!.AsObject();
            Assert.Equal(["closedBy", "id", "publisher", "signed", "title", "version"], entry.Select(p => p.Key).Order(StringComparer.Ordinal));
            Assert.Equal(["ai.allowed.provider"], entry["closedBy"]!.AsArray().Select(x => (string)x!));
            Assert.Equal([Named.Id, Open.Id], list.Select(p => (string)p!["id"]!).Order(StringComparer.Ordinal));
            var info = answers.First(a => a.Label.StartsWith("moca_package_info", StringComparison.Ordinal) && a.Arguments.Contains(Named.Key, StringComparison.Ordinal));
            Assert.Equal(entry.ToJsonString(), JsonNode.Parse(info.Text)!.ToJsonString());

            // The not-found answers: a hidden package's are exactly an unknown package's.
            var unknown = Make("unknown", null);
            string AnswerFor(string tool, string argument) =>
                answers.First(a => a.Label.StartsWith(tool, StringComparison.Ordinal) && a.Arguments.Contains(argument, StringComparison.Ordinal)).Text;
            Assert.Equal(AnswerFor("moca_package_info", unknown.Key).Replace(unknown.Key, "X", StringComparison.Ordinal),
                AnswerFor("moca_package_info", Hidden.Key).Replace(Hidden.Key, "X", StringComparison.Ordinal));
            Assert.Equal(AnswerFor("moca_get_node", unknown.NodeRef).Replace(unknown.NodeRef, "X", StringComparison.Ordinal),
                AnswerFor("moca_get_node", Hidden.NodeRef).Replace(Hidden.NodeRef, "X", StringComparison.Ordinal));

            // Embedding: the server has no ingest; it embeds only the caller's queries, never package text.
            Assert.All(embedder.Texts, t => Assert.True(answers.Any(a => a.Arguments.Contains($"\"query\":\"{t}\"", StringComparison.Ordinal)), $"embedded text that is not a query: {t}"));

        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task On_a_device_every_package_is_served_so_the_canary_can_fail()
    {
        var root = Directory.CreateTempSubdirectory("moca-canary-").FullName;
        try
        {
            foreach (var c in new[] { Open, Named, Hidden })
            {
                Write(root, c);
            }

            var options = new MocaMcpOptions { Terms = new TermsRequest { Ai = new AiRequest(TermsDestinations.Device) } };
            await using var client = await ConnectAsync(options, root, library: true);
            var all = string.Join("\n", (await CallEverythingAsync(client)).Select(a => a.Text));

            foreach (var marker in Hidden.Text.Concat(Named.Text).Append(Hidden.Title).Append(Named.Title))
            {
                Assert.Contains(marker, all, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_terms_request_without_an_ai_part_is_refused()
    {
        var source = new FolderPackageSource(new FolderPackageSourceOptions());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            MocaMcpServer.CreateAsync(new MocaMcpOptions { Terms = new TermsRequest() }, source, cancellationToken: TestContext.Current.CancellationToken));
    }
}
