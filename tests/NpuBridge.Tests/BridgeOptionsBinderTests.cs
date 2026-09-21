using System.Collections;
using Microsoft.Extensions.Configuration;
using NpuBridge.Configuration;

namespace NpuBridge.Tests;

public class BridgeOptionsBinderTests
{
    [Fact]
    public void Defaults_when_nothing_configured()
    {
        var o = BridgeOptionsBinder.Bind(new ConfigurationBuilder().Build());

        Assert.Equal(BackendKind.PhiSilica, o.Backend);
        Assert.Equal("http://127.0.0.1:5273", o.Listen);
        Assert.Equal(4, o.QueueCapacity);
        Assert.Equal(4, o.ContextCacheSize);
        Assert.False(o.TruncateHistory);
        Assert.Equal(SystemPromptPlacement.Auto, o.SystemPromptPlacement);
        Assert.True(o.ToolEmulation);
        Assert.Equal(ToolSchemaMode.Compact, o.ToolSchema);
        Assert.Equal(4096, o.ContextWindowHint);
        Assert.Null(o.LafToken);
        Assert.Null(o.LafAttestation);
        Assert.False(o.Verbose);
        Assert.Equal("NpuBridge", o.ServiceName);
        Assert.True(o.SelfRelaunch);
        Assert.False(o.HideConsole);
        Assert.Equal("npu-bridge", o.TaskName);
    }

    [Fact]
    public void Relaunch_console_and_task_name_bind()
    {
        var o = Bind(("SelfRelaunch", "off"), ("HideConsole", "yes"), ("TaskName", "npu-dev"), ("InstallModel", "on"), ("SupervisorPid", "4321"));
        Assert.False(o.SelfRelaunch);
        Assert.True(o.HideConsole);
        Assert.Equal("npu-dev", o.TaskName);
        Assert.True(o.InstallModel);
        Assert.Equal(4321, o.SupervisorPid);
        Assert.Null(Bind().SupervisorPid);
        Assert.False(Bind().InstallModel);
        Assert.Throws<BridgeConfigurationException>(() => Bind(("TaskName", "npu dev")));
        Assert.Throws<BridgeConfigurationException>(() => Bind(("SupervisorPid", "0")));
    }

    [Fact]
    public void Command_line_beats_environment_beats_local_json_beats_json()
    {
        using var json = Stream("""{ "Backend": "phi-silica", "QueueCapacity": 2, "Verbose": true, "ContextCacheSize": 9 }""");
        using var local = Stream("""{ "ContextCacheSize": 1, "LafToken": "from-local" }""");
        var env = new Hashtable
        {
            ["NPU_BRIDGE_BACKEND"] = "aion",
            ["NPU_BRIDGE_QUEUE_CAPACITY"] = "3",
            ["NPU_BRIDGE_LAF_TOKEN"] = "from-env",
        };

        var config = new ConfigurationBuilder()
            .AddJsonStream(json)
            .AddJsonStream(local)
            .AddNpuBridgeEnvironmentVariables(environment: env)
            .AddCommandLine(["Backend=fake"])
            .Build();

        var o = BridgeOptionsBinder.Bind(config);
        Assert.Equal(BackendKind.Fake, o.Backend);   // command line
        Assert.Equal(3, o.QueueCapacity);            // env (underscored spelling)
        Assert.Equal("from-env", o.LafToken);        // env beats local json
        Assert.Equal(1, o.ContextCacheSize);         // local json beats json
        Assert.True(o.Verbose);                      // json
    }

    [Theory]
    [InlineData("NPU_BRIDGE_LAF_TOKEN", "LafToken")]
    [InlineData("NPU_BRIDGE_LAFTOKEN", "LafToken")]
    [InlineData("npu_bridge_laf_attestation", "LafAttestation")]
    [InlineData("NPU_BRIDGE_TOOL_EMULATION", "ToolEmulation")]
    [InlineData("NPU_BRIDGE_CONTEXT_CACHE_SIZE", "ContextCacheSize")]
    [InlineData("NPU_BRIDGE_BACKEND", "Backend")]
    [InlineData("NPU_BRIDGE_SOMETHING_ELSE", "SOMETHING_ELSE")]
    public void Environment_names_map_to_option_properties(string variable, string expectedKey)
    {
        var env = new Hashtable { [variable] = "v" };
        var config = new ConfigurationBuilder().AddNpuBridgeEnvironmentVariables(environment: env).Build();
        Assert.Equal("v", config[expectedKey]);
    }

    [Fact]
    public void Environment_ignores_unprefixed_variables()
    {
        var env = new Hashtable { ["VERBOSE"] = "1", ["Backend"] = "fake" };
        var config = new ConfigurationBuilder().AddNpuBridgeEnvironmentVariables(environment: env).Build();
        var o = BridgeOptionsBinder.Bind(config);
        Assert.False(o.Verbose);
        Assert.Equal(BackendKind.PhiSilica, o.Backend);
    }

    [Fact]
    public void Environment_source_reads_the_real_process_environment()
    {
        Environment.SetEnvironmentVariable("NPU_BRIDGE_TOOL_SCHEMA", "full");
        try
        {
            var config = new ConfigurationBuilder().AddNpuBridgeEnvironmentVariables().Build();
            Assert.Equal(ToolSchemaMode.Full, BridgeOptionsBinder.Bind(config).ToolSchema);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NPU_BRIDGE_TOOL_SCHEMA", null);
        }
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("OFF", false)]
    [InlineData("yes", true)]
    [InlineData("no", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    public void Tolerant_boolean_parsing(string raw, bool expected)
    {
        var o = Bind(("ToolEmulation", raw));
        Assert.Equal(expected, o.ToolEmulation);
    }

    [Theory]
    [InlineData("phi-silica", BackendKind.PhiSilica)]
    [InlineData("PhiSilica", BackendKind.PhiSilica)]
    [InlineData("phi_silica", BackendKind.PhiSilica)]
    [InlineData("phi", BackendKind.PhiSilica)]
    [InlineData(" aion ", BackendKind.Aion)]
    [InlineData("aion-instruct", BackendKind.Aion)]
    [InlineData("aioninstruct", BackendKind.Aion)]
    [InlineData("fake", BackendKind.Fake)]
    public void Backend_names_are_forgiving(string raw, BackendKind expected)
    {
        Assert.Equal(expected, Bind(("Backend", raw)).Backend);
    }

    [Theory]
    [InlineData(BackendKind.PhiSilica, "phi-silica")]
    [InlineData(BackendKind.Aion, "aion")]
    [InlineData(BackendKind.Fake, "fake")]
    public void Config_names_round_trip(BackendKind kind, string name)
    {
        Assert.Equal(name, kind.ToConfigName());
        Assert.True(BackendKinds.TryParse(name, out var parsed));
        Assert.Equal(kind, parsed);
    }

    [Theory]
    [InlineData("compact", ToolSchemaMode.Compact)]
    [InlineData("FULL", ToolSchemaMode.Full)]
    public void Tool_schema_values(string raw, ToolSchemaMode expected)
    {
        Assert.Equal(expected, Bind(("ToolSchema", raw)).ToolSchema);
    }

    [Theory]
    [InlineData("Backend", "gpt-4")]
    [InlineData("QueueCapacity", "0")]
    [InlineData("QueueCapacity", "1001")]
    [InlineData("QueueCapacity", "many")]
    [InlineData("ContextCacheSize", "-1")]
    [InlineData("DrainWarningSeconds", "86401")]
    [InlineData("ContextWindowHint", "10")]
    [InlineData("ToolSchema", "medium")]
    [InlineData("Verbose", "maybe")]
    [InlineData("Listen", "ftp://127.0.0.1:5273")]
    [InlineData("Listen", "http://127.0.0.1")]
    [InlineData("Listen", ";;")]
    [InlineData("ServiceName", "Npu Bridge")]
    [InlineData("ServiceName", "Npu/Bridge")]
    [InlineData("ServiceName", "Npu\\Bridge")]
    public void Invalid_values_name_the_key(string key, string value)
    {
        var ex = Assert.Throws<BridgeConfigurationException>(() => Bind((key, value)));
        Assert.Contains(key, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5273", "http://127.0.0.1:5273")]
    [InlineData("127.0.0.1:5273", "http://127.0.0.1:5273")]
    [InlineData("http://*:5273", "http://*:5273")]
    [InlineData("http://+:5273", "http://+:5273")]
    [InlineData("*:5273", "http://*:5273")]
    [InlineData("https://localhost:5274", "https://localhost:5274")]
    [InlineData(" http://127.0.0.1:5273 ; http://[::1]:5273 ", "http://127.0.0.1:5273;http://[::1]:5273")]
    public void Listen_accepts_kestrel_forms(string raw, string expected)
    {
        Assert.Equal(expected, Bind(("Listen", raw)).Listen);
    }

    [Fact]
    public void Upper_bounds_are_inclusive()
    {
        Assert.Equal(1000, Bind(("QueueCapacity", "1000")).QueueCapacity);
        Assert.Equal(1_000_000, Bind(("ContextWindowHint", "1000000")).ContextWindowHint);
        Assert.Equal(0, Bind(("ContextCacheSize", "0")).ContextCacheSize);
        Assert.Equal(86_400, Bind(("DrainWarningSeconds", "86400")).DrainWarningSeconds);
    }

    [Fact]
    public void Laf_values_pass_through_and_blank_means_unset()
    {
        var o = Bind(("LafToken", "abc=="), ("LafAttestation", "x has registered"));
        Assert.Equal("abc==", o.LafToken);
        Assert.Equal("x has registered", o.LafAttestation);
        Assert.Null(Bind(("LafToken", "   ")).LafToken);
    }

    private static MemoryStream Stream(string json) => new(System.Text.Encoding.UTF8.GetBytes(json));

    [Theory]
    [InlineData("auto", SystemPromptPlacement.Auto)]
    [InlineData("native", SystemPromptPlacement.Native)]
    [InlineData("PROMPT", SystemPromptPlacement.Prompt)]
    public void System_prompt_placement_binds(string value, SystemPromptPlacement expected)
    {
        Assert.Equal(expected, Bind(("SystemPromptPlacement", value)).SystemPromptPlacement);
    }

    [Fact]
    public void Unknown_system_prompt_placement_is_rejected()
    {
        var ex = Assert.Throws<BridgeConfigurationException>(() => Bind(("SystemPromptPlacement", "sideways")));
        Assert.Contains("SystemPromptPlacement", ex.Message, StringComparison.Ordinal);
    }

    private static BridgeOptions Bind(params (string Key, string Value)[] pairs)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();
        return BridgeOptionsBinder.Bind(config);
    }
}
