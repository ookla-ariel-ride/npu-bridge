using System.Globalization;
using System.Text.Json;
using NpuBridge.Configuration;
using NpuBridge.Tools;

namespace NpuBridge.Tests;

/// <summary>
/// The injected instruction block. Two things are pinned exactly and everything else by content: the
/// JSON envelope, because the tolerant parser's first branch matches what the model was told to
/// produce, and the fence instruction around it. Whitespace elsewhere is deliberately not asserted —
/// a test that pins the whole block re-states the renderer instead of checking it, and fails on a
/// reworded sentence that broke nothing.
/// </summary>
public class ToolSchemaRendererTests
{
    private const string Envelope = """{"tool_calls":[{"name":"<tool>","arguments":{...}}]}""";

    private static ToolCatalog Catalog(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ToolCatalog.From(document.RootElement)!;
    }

    private static ToolCatalog OneTool(string name, string? description, string? parameters)
    {
        var descriptionJson = description is null ? "" : $",\"description\":{JsonSerializer.Serialize(description)}";
        var parametersJson = parameters is null ? "" : $",\"parameters\":{parameters}";

        return Catalog($"[{{\"type\":\"function\",\"function\":{{\"name\":\"{name}\"{descriptionJson}{parametersJson}}}}}]");
    }

    private static string Compact(ToolCatalog catalog) => ToolSchemaRenderer.Render(catalog, ToolSchemaMode.Compact, default);

    /// <summary>The exact wording docs/PLAN.md section 2.6 item 1 fixes, including the example from it.</summary>
    [Fact]
    public void The_block_carries_the_preamble_the_fenced_envelope_and_the_plain_text_escape()
    {
        var rendered = Compact(OneTool(
            "get_weather",
            "Get current weather",
            """{"type":"object","properties":{"location":{"type":"string"},"unit":{"enum":["c","f"]}},"required":["location"]}"""));

        Assert.Contains("You can call tools. Available tools:", rendered, StringComparison.Ordinal);
        Assert.Contains("""get_weather(location: string, unit?: "c"|"f")""", rendered, StringComparison.Ordinal);
        Assert.Contains("Get current weather", rendered, StringComparison.Ordinal);
        Assert.Contains("reply with ONLY this JSON in a ```json fence and nothing else:", rendered, StringComparison.Ordinal);
        Assert.Contains(Envelope, rendered, StringComparison.Ordinal);
        Assert.Contains("If no tool is needed, answer normally in plain text. Never reply with an empty tool_calls list.", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The envelope has to survive verbatim on its own line: the model copies the line it is shown, and
    /// the parser's fenced-block branch looks for that object. A stray space inside it is the kind of
    /// change that would pass a Contains test on a substring.
    /// </summary>
    [Fact]
    public void The_envelope_is_a_line_of_its_own_byte_for_byte()
    {
        var lines = Compact(OneTool("ping", null, null)).Split('\n');

        Assert.Contains(Envelope, lines);
    }

    [Theory]
    // Required parameters are bare, optional ones are suffixed. Anything absent from "required" is
    // optional, including the case where the schema has no "required" array at all.
    [InlineData("""{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]}""", "f(a: string)")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string"}}}""", "f(a?: string)")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"integer"}},"required":["a"]}""", "f(a: string, b?: int)")]
    // integer/boolean render as int/bool, the spelling the PLAN's own example uses.
    [InlineData("""{"type":"object","properties":{"n":{"type":"integer"},"x":{"type":"number"},"b":{"type":"boolean"}}}""", "f(n?: int, x?: number, b?: bool)")]
    // An enum outranks its type: the literal values say more to the model than "string" does, and each
    // value keeps its own JSON spelling, so strings stay quoted and numbers do not gain quotes.
    [InlineData("""{"type":"object","properties":{"u":{"type":"string","enum":["c","f"]}},"required":["u"]}""", """f(u: "c"|"f")""")]
    [InlineData("""{"type":"object","properties":{"u":{"enum":[1,2]}},"required":["u"]}""", "f(u: 1|2)")]
    // Nested objects render as a brace list, arrays as an element suffix.
    [InlineData("""{"type":"object","properties":{"o":{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"integer"}},"required":["a"]}},"required":["o"]}""", "f(o: {a: string, b?: int})")]
    [InlineData("""{"type":"object","properties":{"xs":{"type":"array","items":{"type":"string"}}},"required":["xs"]}""", "f(xs: string[])")]
    [InlineData("""{"type":"object","properties":{"xs":{"type":"array"}},"required":["xs"]}""", "f(xs: any[])")]
    // A union item type is parenthesised: string|null[] would read as an array of null.
    [InlineData("""{"type":"object","properties":{"xs":{"type":"array","items":{"type":["string","null"]}}},"required":["xs"]}""", "f(xs: (string|null)[])")]
    // anyOf/oneOf is how most schema generators spell a union, including the nullable case.
    [InlineData("""{"type":"object","properties":{"v":{"anyOf":[{"type":"string"},{"type":"integer"}]}},"required":["v"]}""", "f(v: string|int)")]
    [InlineData("""{"type":"object","properties":{"v":{"oneOf":[{"type":"string"},{"type":"null"}]}},"required":["v"]}""", "f(v: string|null)")]
    // An object whose shape the schema does not describe is "object", not "{}": "{}" would claim it
    // has no properties, which is a different statement.
    [InlineData("""{"type":"object","properties":{"o":{"type":"object"}},"required":["o"]}""", "f(o: object)")]
    // No type at all says nothing; an unrecognised type name is passed through rather than flattened,
    // since a custom vocabulary the model may understand beats a placeholder.
    [InlineData("""{"type":"object","properties":{"a":{}},"required":["a"]}""", "f(a: any)")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"date-time"}},"required":["a"]}""", "f(a: date-time)")]
    public void Compact_renders_a_parameter_as_a_signature_fragment(string schema, string expected)
    {
        Assert.Contains(expected, Compact(OneTool("f", null, schema)), StringComparison.Ordinal);
    }

    /// <summary>A description-less parameter gets no parenthetical, and a described one gets exactly one.</summary>
    [Fact]
    public void A_parameter_description_is_preserved_as_a_parenthetical()
    {
        var rendered = Compact(OneTool(
            "f",
            null,
            """{"type":"object","properties":{"a":{"type":"string","description":"City name"},"b":{"type":"string"}},"required":["a","b"]}"""));

        Assert.Contains("f(a: string (City name), b: string)", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Schema descriptions are routinely multi-line prose. A newline inside one would split the
    /// one-line-per-tool list the model reads as a list, so every whitespace run collapses to a space.
    /// </summary>
    [Fact]
    public void A_multi_line_description_is_collapsed_onto_one_line()
    {
        var rendered = Compact(OneTool(
            "f",
            "  Runs\na command\t\tsomewhere  ",
            """{"type":"object","properties":{"a":{"type":"string","description":"first\n  second"}},"required":["a"]}"""));

        // The whole tool entry on one line, newline included, is what proves nothing wrapped.
        Assert.Contains("- f(a: string (first second)) — Runs a command somewhere\n", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{"type":"object","properties":{}}""")]
    [InlineData("""{"type":"object"}""")]
    public void A_tool_with_no_parameters_renders_as_an_empty_signature(string? schema)
    {
        Assert.Contains("- ping()", Compact(OneTool("ping", null, schema)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_tool_with_no_description_gets_no_separator()
    {
        var rendered = Compact(OneTool("ping", null, null));

        Assert.Contains("- ping()\n", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("—", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// A pathological schema is bounded: the depth cap elides the shape rather than recursing as deep
    /// as System.Text.Json's own parse limit allows, and a signature line stops saying anything useful
    /// long before that depth anyway.
    /// </summary>
    [Fact]
    public void A_schema_nested_past_the_cap_is_elided_rather_than_recursed()
    {
        var schema = """{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]}""";

        for (var i = 0; i < 12; i++)
        {
            schema = $"{{\"type\":\"object\",\"properties\":{{\"a\":{schema}}},\"required\":[\"a\"]}}";
        }

        var rendered = Compact(OneTool("f", null, schema));

        Assert.Contains("any", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("{a: {a: {a: {a: {a: {", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Full_mode_carries_the_schema_itself()
    {
        var catalog = OneTool(
            "get_weather",
            "Get current weather",
            """{"type":"object","properties":{"location":{"type":"string"}},"required":["location"]}""");

        var rendered = ToolSchemaRenderer.Render(catalog, ToolSchemaMode.Full, default);

        Assert.Contains("\"type\": \"object\"", rendered, StringComparison.Ordinal);
        Assert.Contains("\"location\"", rendered, StringComparison.Ordinal);
        Assert.Contains("\"required\"", rendered, StringComparison.Ordinal);
        Assert.Contains("- get_weather — Get current weather", rendered, StringComparison.Ordinal);
        // The signature form is compact's, not full's.
        Assert.DoesNotContain("get_weather(location: string)", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reason compact mode exists. Full JSON Schema for an OpenCode-sized tool set is 2-3K tokens,
    /// most of Phi Silica's ~3.5K usable window. The ratio is what is asserted rather than a byte
    /// count: the wording of the block may change, the order of magnitude may not.
    /// </summary>
    [Fact]
    public void A_large_catalog_renders_far_shorter_in_compact_than_in_full()
    {
        const string template = """
            {"type":"function","function":{"name":"tool_N","description":"Does the Nth thing","parameters":{
                "type":"object",
                "properties":{
                    "path":{"type":"string","description":"Absolute path to operate on"},
                    "mode":{"type":"string","enum":["read","write"],"description":"How to open it"},
                    "limit":{"type":"integer","description":"How many entries at most"},
                    "opts":{"type":"object","properties":{"deep":{"type":"boolean"},"glob":{"type":"string"}}}},
                "required":["path","mode"]}}}
            """;

        var tools = string.Join(",", Enumerable.Range(0, 15).Select(i =>
        {
            var n = i.ToString(CultureInfo.InvariantCulture);
            return template
                .Replace("tool_N", "tool_" + n, StringComparison.Ordinal)
                .Replace("the Nth", "the " + n + "th", StringComparison.Ordinal);
        }));

        var catalog = Catalog("[" + tools + "]");

        var compact = ToolSchemaRenderer.Render(catalog, ToolSchemaMode.Compact, default);
        var full = ToolSchemaRenderer.Render(catalog, ToolSchemaMode.Full, default);

        Assert.Equal(15, catalog.Tools.Count);
        Assert.True(full.Length > compact.Length * 3, $"compact {compact.Length}, full {full.Length}");
    }

    /// <summary>
    /// The client asked for no tool call, so the model is told nothing about tools. That also leaves
    /// the request keying as the same conversation it would be without tools at all (D71).
    /// </summary>
    [Theory]
    [InlineData(ToolSchemaMode.Compact)]
    [InlineData(ToolSchemaMode.Full)]
    public void Tool_choice_none_injects_nothing(ToolSchemaMode mode)
    {
        var catalog = OneTool("ping", "Ping", null);

        Assert.Equal(string.Empty, ToolSchemaRenderer.Render(catalog, mode, new ToolChoice(ToolChoiceKind.None, null)));
    }

    [Fact]
    public void Required_replaces_the_plain_text_escape_with_a_demand()
    {
        var catalog = OneTool("ping", "Ping", null);

        var auto = ToolSchemaRenderer.Render(catalog, ToolSchemaMode.Compact, default);
        var required = ToolSchemaRenderer.Render(catalog, ToolSchemaMode.Compact, new ToolChoice(ToolChoiceKind.Required, null));

        Assert.NotEqual(auto, required);
        Assert.DoesNotContain("If no tool is needed", required, StringComparison.Ordinal);
        Assert.Contains("required", required, StringComparison.Ordinal);
        // The tool list itself is unchanged; only the closing instruction differs.
        Assert.Contains("- ping() — Ping", required, StringComparison.Ordinal);
    }

    [Fact]
    public void A_named_choice_names_the_tool_and_still_lists_them_all()
    {
        var catalog = Catalog("""[{"function":{"name":"read"}},{"function":{"name":"write"}}]""");

        var named = ToolSchemaRenderer.Render(catalog, ToolSchemaMode.Compact, new ToolChoice(ToolChoiceKind.Named, "write"));
        var required = ToolSchemaRenderer.Render(catalog, ToolSchemaMode.Compact, new ToolChoice(ToolChoiceKind.Required, null));

        Assert.NotEqual(required, named);
        Assert.Contains("\"write\"", named, StringComparison.Ordinal);
        Assert.Contains("- read()", named, StringComparison.Ordinal);
        Assert.DoesNotContain("If no tool is needed", named, StringComparison.Ordinal);
    }

    /// <summary>
    /// A client whose <c>tool_choice</c> and <c>tools</c> disagree must not make the block advertise a
    /// tool that has no signature above it: the demand stays, the name does not.
    /// </summary>
    [Fact]
    public void A_named_choice_for_a_tool_that_was_not_offered_falls_back_to_the_plain_demand()
    {
        var catalog = Catalog("""[{"function":{"name":"read"}}]""");

        var unknown = ToolSchemaRenderer.Render(catalog, ToolSchemaMode.Compact, new ToolChoice(ToolChoiceKind.Named, "Read"));
        var required = ToolSchemaRenderer.Render(catalog, ToolSchemaMode.Compact, new ToolChoice(ToolChoiceKind.Required, null));

        Assert.Equal(required, unknown);
    }

    /// <summary>
    /// The block lands in the system text, and the system text is part of the context-cache key (D71).
    /// Two renderings of the same tools that differ by a byte — or by tool order — are two different
    /// conversations to the cache, so a silent reordering would look like a cache that never hits.
    /// </summary>
    [Theory]
    [InlineData(ToolSchemaMode.Compact)]
    [InlineData(ToolSchemaMode.Full)]
    public void Rendering_is_deterministic_and_keeps_the_tool_order(ToolSchemaMode mode)
    {
        var catalog = Catalog("""
            [{"function":{"name":"read","parameters":{"type":"object","properties":{"b":{"type":"string"},"a":{"type":"integer"}}}}},
             {"function":{"name":"write"}},
             {"function":{"name":"bash"}}]
            """);

        var first = ToolSchemaRenderer.Render(catalog, mode, default);
        var second = ToolSchemaRenderer.Render(catalog, mode, default);

        Assert.Equal(first, second);
        Assert.True(
            first.IndexOf("read", StringComparison.Ordinal) <
            first.IndexOf("write", StringComparison.Ordinal));
        Assert.True(
            first.IndexOf("write", StringComparison.Ordinal) <
            first.IndexOf("bash", StringComparison.Ordinal));

        // Property order follows the schema document, not an alphabetical sort.
        Assert.True(
            first.IndexOf("\"b\"", StringComparison.Ordinal) + first.IndexOf("b?: string", StringComparison.Ordinal) <
            first.IndexOf("\"a\"", StringComparison.Ordinal) + first.IndexOf("a?: int", StringComparison.Ordinal));
    }

    /// <summary>The block is joined to whatever system text the request had, so it must not end in a newline.</summary>
    [Fact]
    public void The_block_has_no_trailing_newline()
    {
        var rendered = Compact(OneTool("ping", "Ping", null));

        Assert.EndsWith("If no tool is needed, answer normally in plain text. Never reply with an empty tool_calls list.", rendered, StringComparison.Ordinal);
    }
}
