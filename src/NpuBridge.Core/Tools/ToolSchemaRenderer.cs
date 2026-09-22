using System.Text;
using System.Text.Json;
using NpuBridge.Configuration;

namespace NpuBridge.Tools;

/// <summary>
/// Renders the tool instruction block that is appended to the system section, after the client's own
/// system text, in the exact wording
/// docs/PLAN.md section 2.6 item 1 fixes. The envelope line is a contract with the parser on the other
/// side of the round trip: change it here and the tolerant parser's first branch stops matching what
/// the model was told to produce.
///
/// Compact rendering (the default) is why this exists rather than a <c>JsonSerializer.Serialize</c>
/// call. Full JSON Schema for OpenCode's ~15 tools is 2–3K tokens, which is most of Phi Silica's ~3.5K
/// usable window, so a full-schema block leaves no room for the conversation it is supposed to serve.
/// The signature form keeps what the model needs to fill the arguments in — names, required versus
/// optional, enums, nesting, descriptions — and drops the JSON Schema scaffolding around them.
///
/// Output is deterministic: property order follows the schema document, tool order follows the
/// request, and nothing is sorted or deduplicated. That matters beyond tidiness, because the block
/// lands in the system text and the system text is part of the context-cache key (D71); two renderings
/// of the same tools that differ by a byte are two different conversations to the cache.
/// </summary>
internal static class ToolSchemaRenderer
{
    private const string Preamble = "You can call tools. Available tools:";
    private const string CallInstruction = "To call one or more tools, reply with ONLY this JSON in a ```json fence and nothing else:";
    private const string Envelope = """{"tool_calls":[{"name":"<tool>","arguments":{...}}]}""";
    private const string OptionalClosing = "If no tool is needed, answer normally in plain text. Never reply with an empty tool_calls list.";
    private const string RequiredClosing = "A tool call is required for this message: reply with only that JSON, never plain text.";

    /// <summary>An em dash, written as an escape so the separator survives any re-encoding of this file.</summary>
    private const string DescriptionSeparator = " \u2014 ";

    /// <summary>The two union keywords, hoisted so the union branch does not allocate the array per parameter.</summary>
    private static readonly string[] UnionKeywords = ["anyOf", "oneOf"];

    /// <summary>
    /// How deep a parameter schema is rendered before the shape is elided as <c>any</c>. A signature
    /// line stops carrying information long before this, and the cap is also what keeps a pathological
    /// client schema from recursing as deeply as System.Text.Json's own 64-level parse limit allows.
    /// </summary>
    private const int MaxSchemaDepth = 4;

    /// <summary>
    /// The instruction block, with no trailing newline — the caller joins it to whatever system text
    /// the request already had. Empty when <paramref name="choice"/> is
    /// <see cref="ToolChoiceKind.None"/>: the client asked for no tool call, and the cheapest way to
    /// honour that on an emulated path is to tell the model nothing about tools at all, which also
    /// leaves the request keying exactly as the same conversation without tools.
    /// </summary>
    internal static string Render(ToolCatalog catalog, ToolSchemaMode mode, ToolChoice choice)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (choice.Kind == ToolChoiceKind.None)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.Append(Preamble);

        foreach (var tool in catalog.Tools)
        {
            sb.Append('\n');

            if (mode == ToolSchemaMode.Full)
            {
                AppendFull(sb, tool);
            }
            else
            {
                AppendCompact(sb, tool);
            }
        }

        sb.Append('\n').Append(CallInstruction);
        sb.Append('\n').Append(Envelope);
        sb.Append('\n').Append(Closing(catalog, choice));

        return sb.ToString();
    }

    /// <summary>
    /// <c>required</c> and a named choice are best effort — nothing here can stop the model answering
    /// in prose — but the block never says so. Telling a 3B model that an instruction is optional is
    /// the surest way to have it skipped; the caller is the one that has to accept plain text back
    /// (PLAN section 2.6, "Honest expectation").
    ///
    /// A name the request never offered falls back to the plain requirement rather than naming it, so
    /// a client whose <c>tool_choice</c> and <c>tools</c> disagree cannot make the block advertise a
    /// tool that has no signature above it.
    /// </summary>
    private static string Closing(ToolCatalog catalog, ToolChoice choice) => choice.Kind switch
    {
        ToolChoiceKind.Named when choice.Name is { } name && catalog.Contains(name) =>
            $"A call to the tool \"{name}\" is required for this message: reply with only that JSON, never plain text.",
        ToolChoiceKind.Named or ToolChoiceKind.Required => RequiredClosing,
        _ => OptionalClosing,
    };

    /// <summary><c>- get_weather(location: string, unit?: "c"|"f") — Get current weather</c>.</summary>
    private static void AppendCompact(StringBuilder sb, ToolDefinition tool)
    {
        sb.Append("- ").Append(tool.Name).Append('(');
        AppendParameters(sb, tool.Parameters, depth: 1);
        sb.Append(')');
        AppendToolDescription(sb, tool.Description);
    }

    /// <summary>
    /// The schema as the client sent it, re-emitted indented and pushed in two spaces so the list
    /// structure survives, and nothing else. A tool with no parameters is one line, same as in compact.
    /// </summary>
    private static void AppendFull(StringBuilder sb, ToolDefinition tool)
    {
        sb.Append("- ").Append(tool.Name);
        AppendToolDescription(sb, tool.Description);

        if (tool.Parameters is not { } parameters)
        {
            return;
        }

        foreach (var line in Indented(parameters).Split('\n'))
        {
            sb.Append("\n  ").Append(line);
        }
    }

    /// <summary>
    /// <see cref="JsonWriterOptions.NewLine"/> defaults to <see cref="Environment.NewLine"/>, which
    /// would make this rendering — and therefore the cache key it feeds — differ between a Windows host
    /// and a Linux CI run. Pinned to <c>\n</c> for the same reason the rest of the block is built from
    /// explicit <c>\n</c>.
    /// </summary>
    private static string Indented(JsonElement schema)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            schema.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// The comma-separated parameter list, from the schema's <c>properties</c> in document order.
    /// Writes nothing at all when there is no usable <c>properties</c> object, which is how a tool that
    /// takes no arguments renders as <c>name()</c>.
    /// </summary>
    private static void AppendParameters(StringBuilder sb, JsonElement? schema, int depth)
    {
        if (schema is not { } parameters ||
            !parameters.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var required = RequiredNames(parameters);
        var first = true;

        foreach (var property in properties.EnumerateObject())
        {
            if (!first)
            {
                sb.Append(", ");
            }

            first = false;

            sb.Append(property.Name);

            // Optional is marked, required is bare: the marked case is the rarer one in agent tool
            // schemas, and marking the common case would cost a character on every parameter.
            if (!required.Contains(property.Name))
            {
                sb.Append('?');
            }

            sb.Append(": ").Append(RenderType(property.Value, depth));
            AppendParameterDescription(sb, Description(property.Value));
        }
    }

    private static HashSet<string> RequiredNames(JsonElement schema)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        if (!schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array)
        {
            return names;
        }

        foreach (var entry in required.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { } name)
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// One parameter's type as a signature fragment. <c>enum</c> outranks <c>type</c> because the
    /// literal values tell the model more than <c>string</c> does, and each value is emitted as its own
    /// raw JSON, so string values keep their quotes and numbers do not gain any.
    /// </summary>
    private static string RenderType(JsonElement schema, int depth)
    {
        if (depth > MaxSchemaDepth || schema.ValueKind != JsonValueKind.Object)
        {
            return "any";
        }

        if (schema.TryGetProperty("enum", out var enumValues) && enumValues.ValueKind == JsonValueKind.Array)
        {
            var values = enumValues.EnumerateArray().Select(v => v.GetRawText()).ToList();

            if (values.Count > 0)
            {
                return string.Join("|", values);
            }
        }

        if (schema.TryGetProperty("type", out var type))
        {
            if (type.ValueKind == JsonValueKind.String)
            {
                return RenderNamedType(type.GetString(), schema, depth);
            }

            // JSON Schema's ["string","null"] spelling of a nullable field.
            if (type.ValueKind == JsonValueKind.Array)
            {
                var names = type.EnumerateArray()
                    .Where(t => t.ValueKind == JsonValueKind.String)
                    .Select(t => RenderNamedType(t.GetString(), schema, depth))
                    .ToList();

                if (names.Count > 0)
                {
                    return string.Join("|", names);
                }
            }
        }

        // anyOf/oneOf is how most schema generators spell a union, including the nullable case above.
        // Rendering the alternatives one level deeper is what bounds the recursion: a union of unions
        // otherwise never increments the depth.
        foreach (var keyword in UnionKeywords)
        {
            if (!schema.TryGetProperty(keyword, out var alternatives) || alternatives.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var rendered = alternatives.EnumerateArray()
                .Select(a => RenderType(a, depth + 1))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (rendered.Count > 0)
            {
                return string.Join("|", rendered);
            }
        }

        return "any";
    }

    /// <summary>
    /// <c>integer</c> renders as <c>int</c> and <c>boolean</c> as <c>bool</c>, which is the spelling
    /// PLAN section 2.6's own example uses. An unrecognised type name is passed through rather than
    /// flattened to <c>any</c>: a custom vocabulary the model may understand is worth more than a
    /// placeholder that says nothing.
    /// </summary>
    private static string RenderNamedType(string? name, JsonElement schema, int depth) => name switch
    {
        "string" => "string",
        "integer" => "int",
        "number" => "number",
        "boolean" => "bool",
        "null" => "null",
        "array" => RenderArray(schema, depth),
        "object" => RenderObject(schema, depth),
        null or "" => "any",
        _ => name,
    };

    private static string RenderArray(JsonElement schema, int depth)
    {
        var item = schema.TryGetProperty("items", out var items)
            ? RenderType(items, depth + 1)
            : "any";

        // string|null[] would read as an array of null, so a union item type is parenthesised.
        return item.Contains('|', StringComparison.Ordinal) ? $"({item})[]" : item + "[]";
    }

    private static string RenderObject(JsonElement schema, int depth)
    {
        var nested = new StringBuilder();
        AppendParameters(nested, schema, depth + 1);

        // A bare "object" with no properties: the schema says nothing about its shape, and "{}" would
        // claim it has none.
        return nested.Length == 0 ? "object" : "{" + nested + "}";
    }

    private static string? Description(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object &&
        schema.TryGetProperty("description", out var description) &&
        description.ValueKind == JsonValueKind.String
            ? description.GetString()
            : null;

    private static void AppendToolDescription(StringBuilder sb, string? description)
    {
        if (!string.IsNullOrWhiteSpace(description))
        {
            sb.Append(DescriptionSeparator).Append(Collapse(description));
        }
    }

    private static void AppendParameterDescription(StringBuilder sb, string? description)
    {
        if (!string.IsNullOrWhiteSpace(description))
        {
            sb.Append(" (").Append(Collapse(description)).Append(')');
        }
    }

    /// <summary>
    /// Every whitespace run becomes one space. Schema descriptions are routinely written as multi-line
    /// prose, and a newline inside one would break the one-line-per-tool format the parser side and the
    /// model both read as a list.
    /// </summary>
    private static string Collapse(string text)
    {
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
