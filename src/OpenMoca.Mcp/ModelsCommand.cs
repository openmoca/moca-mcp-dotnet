using OpenMoca.Reader.Onnx;

namespace OpenMoca.Mcp;

/// <summary>
/// <c>moca-mcp models list | add &lt;model...&gt; [--models folder]</c>: the one command that downloads. It fetches a
/// standard model's files from Hugging Face at their pinned commit and checks each SHA-256 before installing.
/// </summary>
internal static class ModelsCommand
{
    public static async Task<int> RunAsync(string[] args, Action<string> log)
    {
        var folder = CommandLine.DefaultModels;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--models" && i + 1 < args.Length)
            {
                folder = args[++i];
            }
            else
            {
                rest.Add(args[i]);
            }
        }

        var store = new ModelStore(folder);
        switch (rest.FirstOrDefault())
        {
            case "list":
                var installed = store.Installed().Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
                Console.WriteLine($"Standard models (store: {store.Folder}):");
                foreach (var m in StandardModel.All)
                {
                    var size = m.Files.Keys.Sum(f => File.Exists(Path.Combine(store.Folder, m.Id, f)) ? new FileInfo(Path.Combine(store.Folder, m.Id, f)).Length : 0);
                    Console.WriteLine($"  {(installed.Contains(m.Id) ? "installed" : "         ")}  {m.Id,-24} {m.Name} ({m.Dimensions} dimensions, {m.Licence}){(size > 0 ? $", {size / 1024 / 1024} MB" : "")}");
                }

                return 0;
            case "add" when rest.Count > 1:
                var unknown = rest.Skip(1).Where(n => StandardModel.Find(n) is null).ToList();
                if (unknown.Count > 0)
                {
                    log($"not a standard model: {string.Join(", ", unknown)}. Run `moca-mcp models list`.");
                    return 2;
                }

                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
                {
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("moca-mcp");
                    foreach (var name in rest.Skip(1))
                    {
                        var model = StandardModel.Find(name)!;
                        try
                        {
                            await store.AddAsync(model, http, log);
                            log($"installed {model.Id} in {Path.Combine(store.Folder, model.Id)} (checked)");
                        }
                        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or TaskCanceledException)
                        {
                            log($"{model.Id}: {ex.Message}");
                            return 1;
                        }
                    }
                }

                return 0;
            default:
                Console.Error.WriteLine("usage: moca-mcp models list | models add <model...> [--models folder]");
                return 2;
        }
    }
}
