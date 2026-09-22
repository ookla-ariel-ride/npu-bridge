namespace NpuBridge.Backends;

/// <summary>
/// The shape shared by Phi Silica (<c>Microsoft.Windows.AI.Text</c>), Aion Instruct Preview
/// (<c>AionInstructPreview.Text</c>) and the fake backend used by tests.
/// </summary>
/// <remarks>
/// Contract every adapter must honour:
/// <list type="bullet">
///   <item><see cref="InitializeAsync"/> is called exactly once by <see cref="BackendLifecycle"/> before
///   any other member, and may take minutes (first-run NPU compile).</item>
///   <item><see cref="GenerateAsync"/> invokes <c>onDelta</c> with the newest token only, on whatever
///   thread the runtime picks. Callers marshal; adapters never buffer.</item>
///   <item><b><see cref="GenerationResult.Text"/> is the concatenation of the deltas delivered</b>, in
///   order, for every status but <see cref="GenerationStatus.ContentFiltered"/> — where the runtime
///   withheld the answer, the text is empty, and any deltas already delivered are the caller's problem
///   rather than a contradiction. This is not a nicety: the client-side cut (D53) applies
///   <c>max_tokens</c> and <c>stop</c> to the returned text on the non-streaming path and to the delta
///   stream on the streaming one, and the two shapes only agree because those are the same characters.
///   An adapter that summarised, re-tokenised or post-processed its text would make the same request
///   answer differently depending on whether the client asked for a stream.</item>
///   <item>Cancellation is reported as <see cref="GenerationStatus.Cancelled"/> with the partial text.
///   Adapters catch the runtime's cancellation exception; they never let it escape.</item>
///   <item>Contexts are owned by the caller. A context whose generation ended in anything other than
///   <see cref="GenerationStatus.Complete"/> has indeterminate state and must be disposed, not reused.</item>
///   <item>Members that a backend cannot support are advertised through <see cref="Capabilities"/>;
///   the pipeline branches on the flags rather than on the backend type.</item>
/// </list>
/// </remarks>
public interface ILanguageModelBackend : IAsyncDisposable
{
    /// <summary>Model id reported by <c>/v1/models</c>, e.g. <c>phi-silica</c> or <c>aion-instruct</c>.</summary>
    string ModelId { get; }

    /// <summary>Human-readable name for logs and <c>/healthz</c>.</summary>
    string DisplayName { get; }

    BackendCapabilities Capabilities { get; }

    /// <summary>
    /// How this backend's tokens are counted, for <c>usage</c> and the <c>max_tokens</c> budget (D80):
    /// the runtime's own vocabulary where it is known (Phi Silica), the chars/4 estimate otherwise.
    /// Never null; the preflight, not the counter, decides what fits.
    /// </summary>
    Tokenizers.ITokenCounter TokenCounter { get; }

    /// <summary>
    /// Measured usable context-window size in this backend's tokens, or <c>null</c> when it is unknown.
    /// A known window lets the native-system-text guard refuse text that would leave no prompt room.
    /// </summary>
    int? ContextWindowTokens => null;

    /// <summary>
    /// Backend-specific facts surfaced verbatim in <c>/healthz</c> (for example <c>laf_status</c>).
    /// Keys are snake_case; values must be JSON-serialisable.
    /// </summary>
    IReadOnlyDictionary<string, object?> Diagnostics { get; }

    /// <summary>Loads the model. Throws on failure; the exception message is shown in <c>/healthz</c>.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Opens a fresh conversation context. When <paramref name="systemPrompt"/> is non-null and the backend
    /// lacks <see cref="BackendCapabilities.SystemPromptContext"/>, the caller is responsible for rendering
    /// the system prompt into the first user turn instead; the backend ignores the argument.
    /// </summary>
    IModelContext CreateContext(string? systemPrompt);

    /// <summary>
    /// Number of leading characters of <paramref name="prompt"/> that still fit in the context window, or
    /// <c>null</c> when the backend lacks <see cref="BackendCapabilities.PromptLengthPreflight"/>.
    /// </summary>
    int? GetUsablePromptLength(IModelContext context, string prompt);

    Task<GenerationResult> GenerateAsync(
        IModelContext context,
        string prompt,
        SamplingOptions? sampling,
        Action<string> onDelta,
        CancellationToken cancellationToken);
}

/// <summary>Opaque conversation state. Wraps the runtime's <c>LanguageModelContext</c>; owns its disposal.</summary>
public interface IModelContext : IDisposable
{
    /// <summary>Stable identifier for logs. Never exposed to clients.</summary>
    string Id { get; }
}

[Flags]
public enum BackendCapabilities
{
    None = 0,

    /// <summary><c>temperature</c>, <c>top_p</c>, <c>top_k</c> are honoured (Phi Silica's <c>LanguageModelOptions</c>).</summary>
    SamplingOptions = 1 << 0,

    /// <summary><see cref="ILanguageModelBackend.CreateContext"/> accepts a system prompt natively.</summary>
    SystemPromptContext = 1 << 1,

    /// <summary><see cref="ILanguageModelBackend.GetUsablePromptLength"/> returns a real answer.</summary>
    PromptLengthPreflight = 1 << 2,

    /// <summary>Cancelling the token stops generation in the runtime (not merely discards the output).</summary>
    Cancellation = 1 << 3,
}

public static class BackendCapabilitiesExtensions
{
    /// <summary>Names the supported flags in declaration order for <c>/healthz</c>.</summary>
    public static string[] ToHealthzNames(this BackendCapabilities capabilities)
    {
        var names = new List<string>(4);
        if (capabilities.HasFlag(BackendCapabilities.SamplingOptions)) { names.Add("sampling_options"); }
        if (capabilities.HasFlag(BackendCapabilities.SystemPromptContext)) { names.Add("system_prompt_context"); }
        if (capabilities.HasFlag(BackendCapabilities.PromptLengthPreflight)) { names.Add("prompt_length_preflight"); }
        if (capabilities.HasFlag(BackendCapabilities.Cancellation)) { names.Add("cancellation"); }
        return names.ToArray();
    }
}

/// <summary>Sampling knobs. Only forwarded when the backend advertises <see cref="BackendCapabilities.SamplingOptions"/>.</summary>
public sealed record SamplingOptions(float? Temperature, float? TopP, int? TopK)
{
    public bool IsEmpty => Temperature is null && TopP is null && TopK is null;
}

/// <summary>Terminal state of one generation, mapped from each runtime's own status enum by name, never by value.</summary>
public enum GenerationStatus
{
    Complete,
    PromptLargerThanContext,
    ContentFiltered,
    BlockedByPolicy,
    Cancelled,
    Error,
}

/// <param name="Text">
/// The deltas delivered to <c>onDelta</c>, concatenated in order — partial when the status is not
/// <see cref="GenerationStatus.Complete"/>, empty when it is <see cref="GenerationStatus.ContentFiltered"/>.
/// Callers rely on it being exactly those characters; see the contract on <see cref="ILanguageModelBackend"/>.
/// </param>
/// <param name="Status">Terminal status.</param>
/// <param name="Detail">Backend-specific detail for logs and error bodies, e.g. the raw runtime status name.</param>
public sealed record GenerationResult(string Text, GenerationStatus Status, string? Detail = null)
{
    public static GenerationResult Complete(string text) => new(text, GenerationStatus.Complete);
}
