namespace NpuBridge.Configuration;

public enum BackendKind
{
    PhiSilica,
    Aion,
    Fake,
}

/// <summary>Where a request's system/developer text is delivered to the model.</summary>
public enum SystemPromptPlacement
{
    /// <summary>Native system context when the backend advertises it, otherwise folded into the prompt.</summary>
    Auto,

    /// <summary>Always the backend's native system context; a backend without it fails the request.</summary>
    Native,

    /// <summary>Always folded into the prompt text, even when a native system context exists.</summary>
    Prompt,
}

public enum ToolSchemaMode
{
    /// <summary>Signature-style rendering (<c>name(arg: type, opt?: type) — description</c>); saves tokens.</summary>
    Compact,

    /// <summary>Full JSON Schema as sent by the client.</summary>
    Full,
}

/// <summary>
/// All runtime settings. Populated by <see cref="BridgeOptionsBinder"/> from configuration whose sources
/// are layered as: defaults &lt; appsettings.json &lt; <c>NPU_BRIDGE_*</c> environment variables &lt; command line.
/// Key names are the property names; the CLI maps <c>--kebab-case</c> to them (see <see cref="CommandLine"/>).
/// </summary>
public sealed class BridgeOptions
{
    public const string DefaultListen = "http://127.0.0.1:5273";

    public BackendKind Backend { get; set; } = BackendKind.PhiSilica;

    /// <summary>Kestrel URL(s), semicolon separated. Localhost only unless deliberately changed.</summary>
    public string Listen { get; set; } = DefaultListen;

    /// <summary>Requests waiting for the single generation worker before new ones get 429.</summary>
    public int QueueCapacity { get; set; } = 4;

    /// <summary>Seconds between warnings while a cancelled generation drains before its context can be settled.</summary>
    public int DrainWarningSeconds { get; set; } = 10;

    /// <summary>Conversation contexts kept alive (LRU). 0 disables the cache.</summary>
    public int ContextCacheSize { get; set; } = 4;

    /// <summary>Drop oldest non-system turns and retry on context overflow instead of returning 400.</summary>
    public bool TruncateHistory { get; set; }

    /// <summary>
    /// Where the system/developer text goes: the backend's native system context, or the top of the
    /// rendered prompt. <see cref="SystemPromptPlacement.Auto"/> picks by capability; the explicit values
    /// exist because Phi Silica has been measured ignoring a natively delivered system prompt, and which
    /// placement a model actually obeys has to be measured on the real NPU.
    /// </summary>
    public SystemPromptPlacement SystemPromptPlacement { get; set; } = SystemPromptPlacement.Auto;

    public bool ToolEmulation { get; set; } = true;

    public ToolSchemaMode ToolSchema { get; set; } = ToolSchemaMode.Compact;

    /// <summary>
    /// Approximate context window in tokens. A hint, not a measurement: the per-request context-pressure
    /// warning fires when the transcript reaches nine tenths of it (times four characters per token).
    /// Where the backend has a preflight, that is the real check.
    /// </summary>
    public int ContextWindowHint { get; set; } = 4096;

    /// <summary>Phi Silica Limited Access Feature token. Optional; never logged.</summary>
    public string? LafToken { get; set; }

    /// <summary>Phi Silica LAF attestation string. Optional; never logged.</summary>
    public string? LafAttestation { get; set; }

    /// <summary>Echo flattened prompts and raw model output to the log.</summary>
    public bool Verbose { get; set; }

    /// <summary>Windows service name used by the <c>service</c> verbs.</summary>
    public string ServiceName { get; set; } = "NpuBridge";

    /// <summary>
    /// When the Phi Silica backend starts without package identity and the sparse package is registered for
    /// this exe, relaunch through package activation (which is the only way identity is granted) and exit.
    /// </summary>
    public bool SelfRelaunch { get; set; } = true;

    /// <summary>Hide the console window after startup (for the logon task / activated instance).</summary>
    public bool HideConsole { get; set; }

    /// <summary>Scheduled task name used by the <c>task</c> verbs.</summary>
    public string TaskName { get; set; } = "npu-bridge";

    /// <summary>
    /// Allow the Phi Silica adapter to call <c>EnsureReadyAsync</c> when the model is not installed, which
    /// starts a multi-gigabyte Windows Update download. Off by default: a headless server must not do that
    /// without an explicit opt-in.
    /// </summary>
    public bool InstallModel { get; set; }

    /// <summary>
    /// Internal: set by the by-path parent on the activated child. The child exits when this process
    /// exits, so stopping the parent (Ctrl+C, <c>schtasks /End</c>) stops the server.
    /// </summary>
    public int? SupervisorPid { get; set; }
}

public static class BackendKinds
{
    public static string ToConfigName(this BackendKind kind) => kind switch
    {
        BackendKind.PhiSilica => "phi-silica",
        BackendKind.Aion => "aion",
        BackendKind.Fake => "fake",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Accepts the documented names plus forgiving variants (<c>phisilica</c>, <c>phi_silica</c>, <c>aion-instruct</c>).</summary>
    public static bool TryParse(string? value, out BackendKind kind)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace("_", "-", StringComparison.Ordinal);
        switch (normalized)
        {
            case "phi-silica":
            case "phisilica":
            case "phi":
                kind = BackendKind.PhiSilica;
                return true;
            case "aion":
            case "aion-instruct":
            case "aioninstruct":
                kind = BackendKind.Aion;
                return true;
            case "fake":
                kind = BackendKind.Fake;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
