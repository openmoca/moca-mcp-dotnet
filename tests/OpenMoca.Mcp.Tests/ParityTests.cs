using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace OpenMoca.Mcp.Tests;

/// <summary>
/// The expected answers in tests/parity: for each scenario, the server started with its arguments must list the same
/// tools and give the same answer to every call (numbers within 1e-9), and refuse what it should refuse.
/// </summary>
public sealed class ParityTests
{
    private static readonly JsonNode Cases = Load("cases.json");
    private static readonly JsonNode Expected = Load("expected.json");

    public static TheoryData<int> Scenarios() => new(Enumerable.Range(0, Cases["scenarios"]!.AsArray().Count));

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Answers_as_recorded(int index)
    {
        var scenario = Cases["scenarios"]![index]!;
        var expected = Expected["scenarios"]![index]!;
        Assert.Equal((string?)scenario["name"], (string?)expected["name"]);

        await using var client = await StartAsync(Arguments(scenario["args"]!.AsArray()));
        var problems = new List<string>();

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var listed = new JsonArray([.. tools.Select(t => Definition(t.ProtocolTool))]);
        Compare(expected["tools"], listed, "tools", problems);

        var calls = scenario["calls"]!.AsArray();
        for (var i = 0; i < calls.Count; i++)
        {
            var call = calls[i]!;
            var want = expected["answers"]![i]!;
            var label = (string?)call["tool"] ?? $"resources {(string?)call["resources"]}";
            var (refused, value) = await AnswerAsync(client, call);
            var wantRefused = (bool?)want["refused"] ?? false;
            if (refused != wantRefused)
            {
                problems.Add($"{label} [{i}]: refused {refused}, expected {wantRefused}");
            }
            else if (want["value"] is { } wantValue)
            {
                Compare(wantValue, value, $"{label} [{i}]", problems);
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(20)));
    }

    /// <summary>
    /// Writes tests/parity/expected.json from this build's answers. Run it only when a tool or parameter changes on
    /// purpose, then read the difference before committing it:
    /// <c>dotnet test --project tests/OpenMoca.Mcp.Tests -- --filter-method "*Record_expected_answers" --explicit only</c>.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Record_expected_answers()
    {
        var scenarios = new JsonArray();
        foreach (var scenario in Cases["scenarios"]!.AsArray())
        {
            await using var client = await StartAsync(Arguments(scenario!["args"]!.AsArray()));
            var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
            var answers = new JsonArray();
            foreach (var call in scenario["calls"]!.AsArray())
            {
                var (refused, value) = await AnswerAsync(client, call!);
                var answer = new JsonObject();
                if (refused)
                {
                    answer["refused"] = true;
                }

                if (!refused || ((bool?)call!["refused"] != true && value is not null))
                {
                    answer["value"] = value;
                }

                answers.Add(answer);
            }

            scenarios.Add(new JsonObject
            {
                ["name"] = (string?)scenario["name"],
                ["tools"] = new JsonArray([.. tools.Select(t => Definition(t.ProtocolTool))]),
                ["answers"] = answers,
            });
        }

        var document = new JsonObject { ["$comment"] = Expected["$comment"]?.DeepClone(), ["scenarios"] = scenarios };
        var target = Path.Combine(MocaSpec.RepositoryRoot, "tests", "parity", "expected.json");
        await File.WriteAllTextAsync(target, document.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentSize = 2,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n", TestContext.Current.CancellationToken);
    }

    private static async Task<(bool Refused, JsonNode? Value)> AnswerAsync(McpClient client, JsonNode call)
    {
        var ct = TestContext.Current.CancellationToken;
        try
        {
            switch ((string?)call["resources"])
            {
                case "list":
                    return (false, JsonSerializer.SerializeToNode((await client.ListResourcesAsync(cancellationToken: ct)).Select(r => r.ProtocolResource), McpJsonUtilities.DefaultOptions));
                case "templates":
                    return (false, JsonSerializer.SerializeToNode((await client.ListResourceTemplatesAsync(cancellationToken: ct)).Select(r => r.ProtocolResourceTemplate), McpJsonUtilities.DefaultOptions));
                case "read":
                    return (false, JsonSerializer.SerializeToNode((await client.ReadResourceAsync((string)call["uri"]!, cancellationToken: ct)).Contents, McpJsonUtilities.DefaultOptions));
            }

            var arguments = call["arguments"]!.AsObject().ToDictionary(p => p.Key, p => (object?)p.Value?.DeepClone());
            var result = await client.CallToolAsync((string)call["tool"]!, arguments, cancellationToken: ct);
            var text = ((TextContentBlock)result.Content[0]).Text;
            JsonNode? value;
            try
            {
                value = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                value = JsonValue.Create(text);
            }

            return (result.IsError == true, value);
        }
        catch (McpException)
        {
            return (true, null);
        }
    }

    private static JsonObject Definition(Tool tool)
    {
        var json = JsonSerializer.SerializeToNode(tool, McpJsonUtilities.DefaultOptions)!.AsObject();
        return new JsonObject(json.Where(p => p.Key is "name" or "title" or "description" or "inputSchema" or "annotations")
            .Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
    }

    // Keys in any order, numbers within 1e-9.
    private static void Compare(JsonNode? a, JsonNode? b, string at, List<string> problems)
    {
        switch (a, b)
        {
            case (JsonObject x, JsonObject y):
                foreach (var key in x.Select(p => p.Key).Union(y.Select(p => p.Key)))
                {
                    if (!x.ContainsKey(key))
                    {
                        problems.Add($"{at}.{key}: not expected");
                    }
                    else if (!y.ContainsKey(key))
                    {
                        problems.Add($"{at}.{key}: missing");
                    }
                    else
                    {
                        Compare(x[key], y[key], $"{at}.{key}", problems);
                    }
                }

                break;
            case (JsonArray x, JsonArray y):
                if (x.Count != y.Count)
                {
                    problems.Add($"{at}: {y.Count} items, expected {x.Count}");
                }

                for (var i = 0; i < Math.Min(x.Count, y.Count); i++)
                {
                    Compare(x[i], y[i], $"{at}[{i}]", problems);
                }

                break;
            case (JsonValue x, JsonValue y) when x.GetValueKind() == JsonValueKind.Number && y.GetValueKind() == JsonValueKind.Number:
                var (p, q) = (x.GetValue<double>(), y.GetValue<double>());
                if (Math.Abs(p - q) > 1e-9 * Math.Max(1, Math.Max(Math.Abs(p), Math.Abs(q))))
                {
                    problems.Add($"{at}: {q}, expected {p}");
                }

                break;
            default:
                if (!JsonNode.DeepEquals(a, b))
                {
                    problems.Add($"{at}: {Short(b)}, expected {Short(a)}");
                }

                break;
        }
    }

    private static string Short(JsonNode? n)
    {
        var s = n?.ToJsonString() ?? "null";
        return s.Length > 120 ? s[..120] + "…" : s;
    }

    // Paths in the cases are relative to moca-spec: every argument that names a file, and the part after "=".
    private static string[] Arguments(JsonArray args)
    {
        string Path(string p) => System.IO.Path.IsPathRooted(p) ? p : System.IO.Path.Combine(MocaSpec.Root, p);
        var list = args.Select(a => (string)a!).ToList();
        var output = new List<string>();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                output.Add(a);
                if (a is "--audience" or "--max-packages" or "--max-depth" or "--max-loaded-pages")
                {
                    output.Add(list[++i]);
                }
                else if (a == "--trust-root")
                {
                    output.Add(Path(list[++i]));
                }

                continue;
            }

            var at = a.IndexOf('=', StringComparison.Ordinal);
            if (at > 0 && (a.StartsWith("organisation=", StringComparison.Ordinal) || a.StartsWith("application=", StringComparison.Ordinal)))
            {
                output.Add($"{a[..at]}={Path(a[(at + 1)..])}");
            }
            else if (at > 0)
            {
                output.Add($"{Path(a[..at])}={Path(a[(at + 1)..])}");
            }
            else
            {
                output.Add(Path(a));
            }
        }

        return [.. output];
    }

    private static Task<McpClient> StartAsync(string[] args) => McpClient.CreateAsync(
        new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "moca-mcp",
            Command = "dotnet",
            Arguments = [Path.Combine(AppContext.BaseDirectory, "moca-mcp.dll"), .. args],
            EnvironmentVariables = new Dictionary<string, string?> { ["MOCA_MODELS"] = ServerTests.NoModels },
        }),
        cancellationToken: TestContext.Current.CancellationToken);

    private static JsonNode Load(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "parity", name)))!;
}
