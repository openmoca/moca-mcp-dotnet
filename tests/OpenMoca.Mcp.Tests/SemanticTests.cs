using System.Text.Json.Nodes;
using OpenMoca.Reader.Onnx;
using OpenMoca.Reader.Search;
using Xunit;

namespace OpenMoca.Mcp.Tests;

/// <summary>
/// Search by meaning, with a real standard model. These run when <c>MOCA_TEST_MODELS</c> names a model store holding
/// bge-small-en-v1.5 (CI installs it with <c>moca-mcp models add</c>); otherwise they are skipped.
/// </summary>
public sealed class SemanticTests
{
    private static readonly string? Models = Environment.GetEnvironmentVariable("MOCA_TEST_MODELS");

    private static StandardModel Bge => StandardModel.Find("bge-small-en-v1.5")!;

    private static void RequireModel()
    {
        if (string.IsNullOrEmpty(Models) || !new ModelStore(Models).Installed().Contains(Bge))
        {
            Assert.Skip("MOCA_TEST_MODELS does not name a model store holding bge-small-en-v1.5");
        }
    }

    // A hybrid index for the support knowledge base: the example's lexical chunks, embedded with bge-small, written
    // as the sidecar specification describes (storage.vectors, model with prefixes).
    private static async Task<string> HybridIndexAsync()
    {
        var target = Directory.CreateTempSubdirectory("moca-hybrid-").FullName;
        var source = Path.Combine(MocaSpec.Root, "examples", "sidecars", "support-kb.moca.idx");
        Directory.CreateDirectory(Path.Combine(target, "payload"));
        File.Copy(Path.Combine(source, "payload", "items.jsonl"), Path.Combine(target, "payload", "items.jsonl"));
        var texts = File.ReadLines(Path.Combine(target, "payload", "items.jsonl")).Select(l => (string)JsonNode.Parse(l)!["text"]!).ToList();
        using var embedder = new ModelStore(Models!).Load(Bge);
        var vectors = await embedder.EmbedAsync(texts, EmbeddingRole.Document, TestContext.Current.CancellationToken);
        await using (var output = File.Create(Path.Combine(target, "payload", "vectors.f32")))
        {
            foreach (var v in vectors)
            {
                output.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(v.AsSpan()));
            }
        }

        var index = JsonNode.Parse(File.ReadAllText(Path.Combine(source, "index.json")))!.AsObject();
        index["indexType"] = "hybrid";
        index["model"] = new JsonObject
        {
            ["name"] = Bge.Name,
            ["version"] = Bge.Version,
            ["dimensions"] = Bge.Dimensions,
            ["queryPrefix"] = Bge.QueryPrefix,
            ["documentPrefix"] = Bge.DocumentPrefix,
        };
        index["storage"]!["vectors"] = new JsonObject { ["file"] = "payload/vectors.f32", ["dtype"] = "float32", ["dimensions"] = Bge.Dimensions };
        File.WriteAllText(Path.Combine(target, "index.json"), index.ToJsonString());
        return target;
    }

    [Fact]
    public async Task With_the_model_installed_search_is_hybrid_and_can_be_semantic()
    {
        RequireModel();
        var index = await HybridIndexAsync();
        try
        {
            await using var client = await ServerTests.StartAsync(Path.Combine(MocaSpec.Root, "examples", "support-kb") + "=" + index, "--models", Models!);

            var list = (await ServerTests.CallAsync(client, "moca_list_packages", [])).AsArray();
            Assert.Equal(true, (bool?)list[0]!["semantic"]!["available"]);
            Assert.Equal(Bge.Name, (string?)list[0]!["semantic"]!["model"]);

            var hybrid = await ServerTests.CallAsync(client, "moca_search", new() { ["query"] = "how long until I get my money back" });
            Assert.Equal("hybrid", (string?)hybrid["mode"]);
            Assert.NotEmpty(hybrid["results"]!.AsArray());

            // No word of the question is in the refund page; meaning finds it.
            var semantic = await ServerTests.CallAsync(client, "moca_search", new() { ["query"] = "money back after buying", ["mode"] = "semantic" });
            Assert.Equal("semantic", (string?)semantic["mode"]);
            Assert.EndsWith("#refund-window.md", (string)semantic["results"]![0]!["ref"]!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(index, recursive: true);
        }
    }

    [Fact]
    public async Task Without_the_model_it_searches_by_words_and_says_how_to_install_it()
    {
        RequireModel();
        var index = await HybridIndexAsync();
        try
        {
            await using var client = await ServerTests.StartAsync(Path.Combine(MocaSpec.Root, "examples", "support-kb") + "=" + index);

            var list = (await ServerTests.CallAsync(client, "moca_list_packages", [])).AsArray();
            Assert.Equal(false, (bool?)list[0]!["semantic"]!["available"]);
            Assert.Equal("moca-mcp models add bge-small-en-v1.5", (string?)list[0]!["semantic"]!["install"]);

            var search = await ServerTests.CallAsync(client, "moca_search", new() { ["query"] = "refund", ["mode"] = "semantic" });
            Assert.Equal("lexical", (string?)search["mode"]);
            Assert.Contains("moca-mcp models add bge-small-en-v1.5", (string)search["modeNote"]!, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(index, recursive: true);
        }
    }
}
