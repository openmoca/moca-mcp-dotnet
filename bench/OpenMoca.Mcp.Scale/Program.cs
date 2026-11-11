// The scale benchmark behind the server's default limits. It generates synthetic packages (unsigned, one Markdown page
// per node, about 300 words each) under a working folder, then measures:
//   1. the library scan: reading only moca.json from each of N packages;
//   2. a first load: read and verify one package, add it to a library, run a first search (builds the lexical index);
//   3. memory held per loaded package;
//   4. search time once loaded.
//
//   dotnet run -c Release --project bench/OpenMoca.Mcp.Scale -- [work folder]
using System.Diagnostics;
using System.Text;
using OpenMoca.Reader;
using OpenMoca.Reader.Search;

var work = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "moca-scale");
Directory.CreateDirectory(work);

string[] words = [.. "refund delivery parcel account password invoice warranty return order payment carrier policy review signed digest evidence source page section module lesson procedure step check safety engine landing approach clause regulation version supersede audience internal public citation record structure concept topic requires part sequence index lexical semantic hybrid model embedding vector chunk heading language translation french english customer support agent knowledge package reader verify trust root publisher reviewer".Split(' ')];

string Generate(string name, int pages, int seed)
{
    var dir = Path.Combine(work, name);
    if (File.Exists(Path.Combine(dir, "moca.json")))
    {
        return dir;
    }

    var rng = new Random(seed);
    Directory.CreateDirectory(Path.Combine(dir, "content"));
    File.WriteAllText(Path.Combine(dir, "moca.json"), $$"""
        { "mocaVersion": "0.5", "id": "https://example.com/scale/{{name}}", "version": "1.0.0", "title": "Scale {{name}}", "description": "{{pages}} generated pages" }
        """);
    for (var p = 0; p < pages; p++)
    {
        var sb = new StringBuilder($"---\ntype: Guide\ntitle: Page {p} {words[rng.Next(words.Length)]}\n---\n\n# Page {p}\n\n");
        for (var w = 0; w < 300; w++)
        {
            sb.Append(words[rng.Next(words.Length)]).Append(w % 15 == 14 ? ".\n" : " ");
        }

        var folder = Path.Combine(dir, "content", $"part-{p / 100}");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, $"page-{p}.md"), sb.ToString());
    }

    return dir;
}

static long Heap() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); return GC.GetTotalMemory(true); }

Console.WriteLine($"Work folder: {work}");
Console.WriteLine($".NET {Environment.Version}, {Environment.ProcessorCount} cores, {Environment.OSVersion}\n");

// 1. The library scan: manifests only.
foreach (var count in new[] { 100, 1000 })
{
    var dirs = Enumerable.Range(0, count).Select(i => Generate($"lib{count}-{i:D4}", 5, i)).ToList();
    var sw = Stopwatch.StartNew();
    var titles = 0;
    foreach (var d in Directory.EnumerateDirectories(work, $"lib{count}-*"))
    {
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(d, "moca.json")));
        titles += manifest?["title"] is null ? 0 : 1;
    }

    Console.WriteLine($"Scan {count,5} packages (manifests only): {sw.ElapsedMilliseconds,6} ms ({titles} titles)");
}

Console.WriteLine();

// 2–4. First load, memory and search, by package size.
string[] queries = ["refund policy", "engine landing check", "trust root publisher", "french translation page", "carrier parcel delivery"];
foreach (var pages in new[] { 100, 1000, 5000 })
{
    var dir = Generate($"pkg-{pages}", pages, pages);
    var before = Heap();
    var sw = Stopwatch.StartNew();
    var result = await MocaReader.ReadPackageAsync(PackageTarget.FromPath(dir));
    var read = sw.ElapsedMilliseconds;
    var library = new Library().Add(result);
    var search = new MocaSearch(library, new LexicalBackend(library));
    sw.Restart();
    await search.SearchAsync("refund policy", new SearchOptions { Limit = 5 });
    var firstSearch = sw.ElapsedMilliseconds;
    var held = Heap() - before;

    sw.Restart();
    const int runs = 20;
    for (var i = 0; i < runs; i++)
    {
        await search.SearchAsync(queries[i % queries.Length], new SearchOptions { Limit = 5 });
    }

    var perSearch = sw.Elapsed.TotalMilliseconds / runs;
    Console.WriteLine($"{pages,5} pages: valid {result.Valid}, read and verify {read,6} ms, first search {firstSearch,5} ms, " +
        $"then {perSearch,7:F1} ms a search; memory held {held / 1024 / 1024,5} MB ({(double)held / pages / 1024:F1} KB a page)");
    GC.KeepAlive(search);
}
