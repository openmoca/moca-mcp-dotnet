using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenMoca.Mcp;

/// <summary>Reads only a sidecar's <c>index.json</c>, to learn which model made its vectors before binding it.</summary>
internal static class SidecarModel
{
    /// <summary>The <c>model</c> object of a sidecar folder or <c>.zip</c>, or null.</summary>
    public static JsonObject? Read(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var file = Path.Combine(path, "index.json");
                return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file))?["model"] as JsonObject : null;
            }

            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("index.json");
            if (entry is null || entry.Length > 1_000_000)
            {
                return null;
            }

            using var stream = entry.Open();
            return JsonNode.Parse(stream)?["model"] as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return null;
        }
    }
}
