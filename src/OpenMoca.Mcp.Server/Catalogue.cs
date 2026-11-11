using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenMoca.Mcp;

/// <summary>
/// Finds packages in library folders, reading only each manifest: unpacked packages (a folder holding
/// <c>moca.json</c>, which is not looked inside further) and <c>.moca</c> files (their <c>moca.json</c> entry only).
/// Beside each, a sidecar index named <c>&lt;name&gt;.moca.idx</c>, or <c>&lt;name&gt;.&lt;model&gt;.moca.idx</c>, is
/// bound to it (a Zip of one ends <c>.moca.idx.zip</c>). Nothing outside the folders is read, and links are not followed.
/// </summary>
internal static class Catalogue
{
    public static IReadOnlyList<PackageDescriptor> Scan(IReadOnlyList<string> folders, int maxPackages, int maxDepth, Action<string> log)
    {
        var found = new List<PackageDescriptor>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var full = false;
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                log($"library folder not found: {folder}");
                continue;
            }

            Walk(Path.GetFullPath(folder), 0);
        }

        return found;

        void Walk(string dir, int depth)
        {
            if (File.Exists(Path.Combine(dir, "moca.json")))
            {
                Add(dir, isArchive: false);
                return;
            }

            IEnumerable<string> files, subfolders;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.moca").Order(StringComparer.Ordinal).ToList();
                subfolders = depth < maxDepth
                    ? Directory.EnumerateDirectories(dir).Where(d => !new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint) && !Path.GetFileName(d).StartsWith('.')).Order(StringComparer.Ordinal).ToList()
                    : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"skipping {dir}: {ex.Message}");
                return;
            }

            foreach (var file in files)
            {
                Add(file, isArchive: true);
            }

            foreach (var sub in subfolders)
            {
                Walk(sub, depth + 1);
            }
        }

        void Add(string path, bool isArchive)
        {
            if (found.Count >= maxPackages)
            {
                if (!full)
                {
                    log($"library has more than {maxPackages} packages; skipping the rest (--max-packages)");
                    full = true;
                }

                return;
            }

            var manifest = ReadManifest(path, isArchive, log);
            if (manifest is null)
            {
                return;
            }

            var id = (string?)manifest["id"];
            var version = (string?)manifest["version"];
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version))
            {
                log($"skipping {path}: its manifest has no id or version");
                return;
            }

            var sidecars = SidecarsFor(path);
            var entry = new PackageDescriptor(path, id, version, Text(manifest["title"]), Text(manifest["description"]), sidecars.FirstOrDefault())
            {
                Sidecars = sidecars,
                Supersedes = SupersedesOf(manifest),
            };
            if (!keys.Add(entry.Key))
            {
                log($"skipping {path}: {entry.Key} is already in the library");
                return;
            }

            found.Add(entry);
        }
    }

    /// <summary>The <c>supersedes</c> relations of a manifest (package spec §8).</summary>
    internal static IReadOnlyList<(string Id, string? Version)> SupersedesOf(JsonNode? manifest) =>
        manifest?["relations"] is JsonArray relations
            ? [.. relations.OfType<JsonObject>()
                .Where(r => Text(r["type"]) == "supersedes" && Text(r["target"]) is not null)
                .Select(r => (Text(r["target"])!, Text(r["version"])))]
            : [];

    internal static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static JsonNode? ReadManifest(string path, bool isArchive, Action<string> log)
    {
        try
        {
            if (!isArchive)
            {
                return JsonNode.Parse(File.ReadAllText(Path.Combine(path, "moca.json")));
            }

            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("moca.json");
            if (entry is null || entry.Length > 1_000_000)
            {
                log($"skipping {path}: no moca.json in the archive");
                return null;
            }

            using var stream = entry.Open();
            return JsonNode.Parse(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            log($"skipping {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The sidecars beside a package: <c>&lt;name&gt;.moca.idx</c> first, then one per model, by name. Each may be a
    /// folder, or a Zip of one named with <c>.zip</c> on the end (<c>&lt;name&gt;.&lt;model&gt;.moca.idx.zip</c>), as
    /// release downloads are.
    /// </summary>
    internal static IReadOnlyList<string> SidecarsFor(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileName(path);
        var stem = name.EndsWith(".moca", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
        var found = new List<string>();
        foreach (var plain in new[] { $"{stem}.moca.idx", $"{stem}.moca.idx.zip" }.Select(n => Path.Combine(dir, n)))
        {
            if (File.Exists(plain) || Directory.Exists(plain))
            {
                found.Add(plain);
            }
        }

        found.AddRange(Directory.EnumerateFileSystemEntries(dir, $"{stem}.*.moca.idx")
            .Concat(Directory.EnumerateFiles(dir, $"{stem}.*.moca.idx.zip"))
            .Order(StringComparer.Ordinal));
        return found;
    }
}
