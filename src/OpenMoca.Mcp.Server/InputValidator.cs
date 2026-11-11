using System.Globalization;
using System.Text.Json;

namespace OpenMoca.Mcp;

/// <summary>
/// Checks a tool call's arguments against the tool's input schema before anything runs. Tool input comes from a model
/// or a client, not the host: an oversized or malformed request is refused rather than processed. It covers the
/// keywords the definitions use: <c>type</c>, <c>required</c>, <c>enum</c>, <c>minLength</c>, <c>maxLength</c>,
/// <c>minimum</c>, <c>maximum</c>, <c>maxItems</c> and <c>items</c>. Unknown properties are ignored, as zod's object
/// schemas do.
/// </summary>
internal static class InputValidator
{
    /// <summary>The problems found; empty when the arguments are valid.</summary>
    public static IReadOnlyList<string> Validate(JsonElement schema, IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        var problems = new List<string>();
        var args = arguments ?? new Dictionary<string, JsonElement>();
        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
            {
                if (!args.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
                {
                    problems.Add($"{name}: required");
                }
            }
        }

        if (schema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (args.TryGetValue(property.Name, out var value) && value.ValueKind != JsonValueKind.Undefined)
                {
                    Check(property.Value, value, property.Name, problems);
                }
            }
        }

        return problems;
    }

    private static void Check(JsonElement schema, JsonElement value, string path, List<string> problems)
    {
        var type = schema.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "string":
                if (value.ValueKind != JsonValueKind.String)
                {
                    problems.Add($"{path}: expected string");
                    return;
                }

                // Lengths in UTF-16 code units, as JavaScript counts them.
                var length = value.GetString()!.Length;
                if (schema.TryGetProperty("minLength", out var min) && length < min.GetInt32())
                {
                    problems.Add($"{path}: shorter than {min.GetInt32()} characters");
                }

                if (schema.TryGetProperty("maxLength", out var max) && length > max.GetInt32())
                {
                    problems.Add($"{path}: longer than {max.GetInt32()} characters");
                }

                if (schema.TryGetProperty("enum", out var values) && !values.EnumerateArray().Any(v => v.GetString() == value.GetString()))
                {
                    problems.Add($"{path}: must be one of {string.Join(", ", values.EnumerateArray().Select(v => v.GetString()))}");
                }

                break;
            case "integer":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || number != Math.Floor(number))
                {
                    problems.Add($"{path}: expected integer");
                    return;
                }

                if (schema.TryGetProperty("minimum", out var minimum) && number < minimum.GetDouble())
                {
                    problems.Add($"{path}: less than {minimum.GetDouble().ToString(CultureInfo.InvariantCulture)}");
                }

                if (schema.TryGetProperty("maximum", out var maximum) && number > maximum.GetDouble())
                {
                    problems.Add($"{path}: greater than {maximum.GetDouble().ToString(CultureInfo.InvariantCulture)}");
                }

                break;
            case "boolean":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    problems.Add($"{path}: expected boolean");
                }

                break;
            case "array":
                if (value.ValueKind != JsonValueKind.Array)
                {
                    problems.Add($"{path}: expected array");
                    return;
                }

                if (schema.TryGetProperty("maxItems", out var maxItems) && value.GetArrayLength() > maxItems.GetInt32())
                {
                    problems.Add($"{path}: more than {maxItems.GetInt32()} items");
                    return;
                }

                if (schema.TryGetProperty("items", out var items))
                {
                    var i = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        Check(items, item, $"{path}[{i++}]", problems);
                    }
                }

                break;
        }
    }
}
