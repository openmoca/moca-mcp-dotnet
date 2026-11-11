using System.Text.Json;
using Xunit;

namespace OpenMoca.Mcp.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void Reads_packages_sidecars_and_options()
    {
        var c = CommandLine.Parse(["kb", "other.moca=other.idx", "--trust-root", "t.json", "--audience", "public, internal",
            "--members", "a", "b", "--overlay", "organisation=o.ttl", "application=p.ttl"]);

        Assert.Equal([new PackageEntry("kb", null), new PackageEntry("other.moca", "other.idx")], c.Packages);
        Assert.Equal("t.json", c.TrustRoot);
        Assert.Equal(["public", "internal"], c.Audiences!);
        Assert.Equal(["a", "b"], c.Members);
        Assert.Equal([new OverlayEntry("organisation-1", "organisation", "o.ttl"), new OverlayEntry("application-2", "application", "p.ttl")], c.Overlays);
    }

    [Fact]
    public void Reads_library_options()
    {
        var c = CommandLine.Parse(["--library", "a", "b", "--watch", "--preload", "--max-packages", "50", "--max-depth", "2", "--max-loaded-pages", "900"]);

        Assert.Empty(c.Packages);
        Assert.Equal(["a", "b"], c.Library);
        Assert.True(c.Watch);
        Assert.True(c.Preload);
        Assert.Equal((50, 2, 900), (c.MaxPackages, c.MaxDepth, c.MaxLoadedPages));
    }

    [Fact]
    public void No_audience_means_no_filter() => Assert.Null(CommandLine.Parse(["kb"]).Audiences);

    public static TheoryData<string> BadArguments => new() { "", "kb --trust-root", "kb --overlay team=x.ttl", "kb --embedder e.mjs", "kb --nope", "kb --watch", "--library lib --max-depth 0", "--library lib --max-loaded-pages lots" };

    [Theory]
    [MemberData(nameof(BadArguments))]
    public void Refuses_bad_arguments(string args) =>
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(args.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    [Fact]
    public void Help_and_version()
    {
        Assert.True(CommandLine.Parse(["--help"]).Help);
        Assert.True(CommandLine.Parse(["-V"]).Version);
    }
}

public sealed class InputValidatorTests
{
    private static readonly JsonElement Search = MocaServer.Tools.Single(t => t.Name == "moca_search").InputSchema;

    private static IReadOnlyList<string> Check(string json) =>
        InputValidator.Validate(Search, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

    [Theory]
    [InlineData("""{"query":"refund"}""")]
    // include_all is no longer a tool argument (Reader interface §7): like any unknown key, it is ignored.
    [InlineData("""{"query":"refund","limit":20,"locale":"fr","include_all":true,"detail":"full","unknown":1}""")]
    public void Accepts_valid_input(string json) => Assert.Empty(Check(json));

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"query":""}""")]
    [InlineData("""{"query":3}""")]
    [InlineData("""{"query":"refund","limit":0}""")]
    [InlineData("""{"query":"refund","limit":2.5}""")]
    [InlineData("""{"query":"refund","detail":"everything"}""")]
    [InlineData("""{"query":"refund","locale":3}""")]
    [InlineData("""{"query":"refund","concepts":[""]}""")]
    public void Refuses_invalid_input(string json) => Assert.NotEmpty(Check(json));
}

public sealed class EncodingTests
{
    [Theory]
    [InlineData("https://example.com/kb@4.2.0#refund.md", "https%3A%2F%2Fexample.com%2Fkb%404.2.0%23refund.md")]
    [InlineData("a b!~*'()", "a%20b!~*'()")]
    [InlineData("délai", "d%C3%A9lai")]
    public void Encodes_as_javascript_encodeURIComponent(string value, string expected) =>
        Assert.Equal(expected, MocaServer.EncodeUriComponent(value));
}

public sealed class CatalogueTests
{
    [Fact]
    public void Finds_unpacked_and_packed_packages_and_their_indexes()
    {
        var root = Directory.CreateTempSubdirectory("moca-catalogue-").FullName;
        try
        {
            void Manifest(string dir, string id, string extra = "") =>
                File.WriteAllText(Path.Combine(Directory.CreateDirectory(dir).FullName, "moca.json"), $$"""{ "id": "{{id}}", "version": "1.0.0", "title": "T"{{extra}} }""");
            Manifest(Path.Combine(root, "kb"), "https://e.x/kb");
            File.WriteAllText(Path.Combine(root, "kb.moca.idx"), "");
            Manifest(Path.Combine(root, "deep", "a", "b", "c", "too-deep"), "https://e.x/deep");
            Manifest(Path.Combine(root, "new"), "https://e.x/new", """, "relations": [{ "type": "supersedes", "target": "https://e.x/kb", "version": "1.0.0" }]""");
            Manifest(Path.Combine(root, ".hidden"), "https://e.x/hidden");
            using (var zip = System.IO.Compression.ZipFile.Open(Path.Combine(root, "packed.moca"), System.IO.Compression.ZipArchiveMode.Create))
            {
                using var w = new StreamWriter(zip.CreateEntry("moca.json").Open());
                w.Write("""{ "id": "https://e.x/packed", "version": "2.0.0", "description": "D" }""");
            }

            File.WriteAllText(Path.Combine(root, "packed.bge-small.moca.idx"), "");
            File.WriteAllText(Path.Combine(root, "packed.all-minilm.moca.idx.zip"), "");
            File.WriteAllText(Path.Combine(root, "packed.moca.idx.zip"), "");
            var log = new List<string>();
            var entries = Catalogue.Scan([root], 1000, 3, log.Add);

            Assert.Equal(["https://e.x/packed@2.0.0", "https://e.x/kb@1.0.0", "https://e.x/new@1.0.0"], entries.Select(e => e.Key));
            Assert.EndsWith("kb.moca.idx", entries[1].Sidecar);
            Assert.Equal(["packed.moca.idx.zip", "packed.all-minilm.moca.idx.zip", "packed.bge-small.moca.idx"], entries[0].Sidecars.Select(Path.GetFileName));
            Assert.Equal("D", entries[0].Description);
            Assert.True(entries[2].Replaces(entries[1]));
            Assert.False(entries[1].Replaces(entries[2]));

            Assert.Single(Catalogue.Scan([root], 1, 3, log.Add));
            Assert.Contains(log, l => l.Contains("--max-packages", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
