# npu-bridge — Plan

OpenAI-compatible HTTP endpoint over the Copilot+ PC on-device language model (Phi Silica today,
Aion Instruct Preview next). Status: **signed off (§4); all 8 chunks built and merged as of 2026-09-12
(6 code-verified only, D70). The plan is complete.** Chunk 8 landed as D84 to D92 (the scheduler,
`/v1/completions`, `docs/CLIENTS.md`); the work before it landed as D80 (real token counts), D81 (the
shared post-generation pipeline), D82 (the 2026-09-10 review notes) and D83 (tool-call emulation).
Further work is tracked as GitHub issues, not as chunks. This document is the historical design record and is not updated to match the code as it
ships — current state lives in `memory-bank/progress.md`, and decisions made since sign-off are in
`docs/DECISIONS.md`.

Research inputs (read 2026-09-01): the Aion sample repo (README, `unpackaged-console/Program.cs`,
`FrameworkDependency.cs`, `AionInstructClient.cs`, `ViewModels/ChatViewModel.cs`, both csproj files,
`nuget.config`), the Aion SDK NuGet itself (`AionInstructPreview.Text.idl`, nuspec, package README), and
the Microsoft Learn pages for `Microsoft.Windows.AI.Text.LanguageModel`, `LanguageModelOptions`,
`LanguageModelResponseStatus`, the Phi Silica get-started/tutorial/troubleshooting pages, the WinAppSDK
LAF-in-unpackaged-apps discussion (#5062), and the Electron + Phi Silica winapp-cli guide.

---

## 1. Findings that change the design

These are facts from the sources above, not assumptions. Each one shaped a decision below.

### F1. Phi Silica requires package identity. Aion does not.
- Learn docs: "The `LanguageModel` API only works in a packaged MSIX app. If you accidentally run the
  unpackaged profile, `GetReadyState()` will fail with a COM error." The manifest must declare
  `<systemai:Capability Name="systemAIModels"/>` with `MinVersion="10.0.26100.0"`, or you get
  "Not declared by app" / `UnauthorizedAccessException`.
- The LAF token is bound to the app's **Package Family Name (PFN)**. The PFN is derived from the
  manifest `Identity Name` and the **signing certificate subject**. So the cert subject must be fixed
  *before* you request the token, and the token request has a multi-day turnaround.
- A plain exe *can* get identity via a **package with external location** ("sparse package"): a tiny
  signed MSIX whose manifest points at the exe's folder. Microsoft's own Electron guide does exactly
  this for Phi Silica (`winapp node add-electron-debug-identity` registers `electron.exe` with a
  temporary identity). Discussion #5062 confirms LAF works this way.
- Aion is the opposite: unpackaged is a first-class path (`TryCreatePackageDependency` +
  `AddPackageDependency` on the framework MSIX), no LAF, no capability.
- **Unverified:** whether a process started by the Service Control Manager from a sparse-package
  external location receives identity. Identity is granted at process creation from the exe path,
  so it should, but I have not found a source that says so. See open question Q3.

### F2. The two APIs are *not* identical. Aion is a strict subset.
From the IDL shipped inside `AionInstructPreview.Text.Framework.1.0.0.nupkg`:

| Member | Phi Silica (`Microsoft.Windows.AI.Text`) | Aion (`AionInstructPreview.Text`) |
|---|---|---|
| `LanguageModel.CreateAsync()` | yes | yes |
| `GetReadyState()` / `EnsureReadyAsync()` | yes | **no** |
| `CreateContext()` | yes | yes |
| `CreateContext(string systemPrompt[, ContentFilterOptions])` | yes | **no** |
| `GenerateResponseAsync(ctx, prompt)` | yes | yes |
| `GenerateResponseAsync(ctx, prompt, LanguageModelOptions)` | yes | **no** |
| `LanguageModelOptions.Temperature/TopP/TopK` | yes | **no** (type does not exist) |
| `GetUsablePromptLength(ctx, prompt)` | yes | **no** |
| `GenerateStructuredJsonResponseAsync(prompt, schema)` | yes (2.0) | **no** |
| `LanguageModelResponseStatus` | 10 values, `Error = 6` | 4 values, `Error = 2` |

Consequences: sampling options are a Phi Silica-only capability (Aion accepts-and-ignores them, logged
once); the system prompt goes through `CreateContext(system)` on Phi Silica and is prepended to the
first user turn on Aion; status enums are mapped per adapter, never by numeric value; the truncation
retry loop can preflight with `GetUsablePromptLength` on Phi Silica but must retry blind on Aion.

### F3. The two SDKs coexist in one project.
The sample's root csproj references `AionInstructPreview.Text.Framework` **and** `Microsoft.WindowsAppSDK
2.0.1` together. Type names collide (`LanguageModel` in both) but namespaces differ, so both adapters
compile into one exe with runtime selection. No plugin split needed.

### F4. Windows App SDK auto-bootstrap must be disabled.
Referencing `Microsoft.WindowsAppSDK` in an unpackaged exe adds a module initializer that calls the
bootstrapper at process start. If Windows App Runtime is missing, the exe would die before `--backend
fake` even parses. We set `<WindowsAppSdkBootstrapInitialize>false</WindowsAppSdkBootstrapInitialize>`
and call `Bootstrap.TryInitialize` only inside the Phi Silica adapter. (Aion's package README says
unpackaged Aion consumers should also bootstrap WAR 1.8; the console sample doesn't and works, so
the Aion adapter will try-bootstrap and log, not fail, on error.)

### F5. LAF may or may not be required depending on SDK channel.
Troubleshooting page: "we recommend using experimental releases as they do not require LAF tokens."
The Aug-2026 Electron guide calls `LanguageModel` with no `TryUnlockFeature` at all. A Q&A answer from
Microsoft staff says a fresh token was required on 2.0.0-preview1. So: LAF token + attestation are
**optional config**; the adapter always calls `TryUnlockFeature`, accepts `Available` or
`AvailableWithoutToken`, and reports the status in `/healthz` and in a clear startup error otherwise.

### F6. Streaming semantics are confirmed.
`Progress` delivers the newest delta only; `result.Text` is the accumulated text. Progress fires on a
WinRT thread pool thread. `IAsyncOperationWithProgress` has `Cancel()`; `op.AsTask(ct)` wires it to a
`CancellationToken`. Whether the NPU actually stops mid-generation on `Cancel()` is unverified.

### F7. Build environment (corrected 2026-09-01).
This machine **is** the Copilot+ PC: Samsung Galaxy Book4 Edge, Snapdragon X Elite (X1E80100), Windows
11 ARM64 build 29648. My first probe ran inside an x64 Git Bash under emulation and misreported AMD64.
Installed: Windows App Runtime 1.8 (several builds) and 2.x (2.3.1, 2.4.0), the QNN workload package.
Not installed: the Aion framework MSIX, the .NET SDK (`dotnet.exe` is runtime-only). The .NET 9 SDK
must be installed (`winget install Microsoft.DotNet.SDK.9`) before any chunk can be built.

Consequence: I can run the real adapters here, not just compile them. The fake backend and the test
suite stay as designed (fast, deterministic, no NPU), but every "laptop only" item in the chunk table
below is something I can execute myself, and `scripts/smoke.ps1` becomes part of each adapter chunk's
definition of done rather than a hand-off. "Never claim the adapters are verified" is replaced by:
claim only what the smoke test actually showed.

---

## 2. Architecture

```
                 +-----------------------------------------------------------+
 OpenCode /      |  NpuBridge (exe, net10.0-windows10.0.26100.0, ARM64)      |
 Hermes /  --->  |  Kestrel :5273  (localhost)   Windows service host        |
 curl            |  Program.cs: CLI, config, DI, service verbs               |
                 |  Adapters: PhiSilicaBackend | AionBackend  (+FrameworkDep,|
                 |            LAF unlock, WAR bootstrap)                     |
                 +---------------------------+-------------------------------+
                                             | project reference
                 +---------------------------v-------------------------------+
                 |  NpuBridge.Core (net10.0, AnyCPU, no WinRT)               |
                 |                                                           |
                 |  Api/        endpoint mapping, OpenAI DTOs, error bodies  |
                 |  Prompting/  message flattening, template, tool injection |
                 |  Tools/      tool-call parser, tool_calls response shaping|
                 |  Context/    ContextCache (LRU, prefix-hash keyed)        |
                 |  Engine/     GenerationScheduler (1 worker, bounded queue)|
                 |              GenerationPipeline (cache→prompt→gen→shape)  |
                 |  Streaming/  SSE writer, chunk builder, usage estimator   |
                 |  Backends/   ILanguageModelBackend, FakeBackend           |
                 |  Telemetry/  per-request log line, verbose prompt echo    |
                 +-----------------------------------------------------------+
                 +-----------------------------------------------------------+
                 |  NpuBridge.Tests (xunit, net10.0) — TestServer + FakeBackend|
                 +-----------------------------------------------------------+
```

**Deviation from "single console project":** the spec asks for one project. I propose three because
the test suite has to run on this x64 box, and a test project that references an ARM64 exe pulling
the Windows App SDK and the Aion winmd projection would either fail to load or need `#if` fences.
`Core` holds everything with logic (including the ASP.NET Core endpoint mapping via a
`FrameworkReference`, so HTTP framing is tested end to end through `TestServer`). The exe is
`Program.cs`, config, service verbs, the two adapters, and packaging. If you'd rather have one
project, say so and I'll fold Core in and accept a weaker test story (Q1).

### 2.1 Backend abstraction

```csharp
public interface ILanguageModelBackend : IAsyncDisposable
{
    string ModelId { get; }                          // "phi-silica" | "aion-instruct" | "fake"
    BackendCapabilities Capabilities { get; }        // flags: SamplingOptions, SystemPromptContext,
                                                     //        PromptLengthPreflight, Cancellation
    BackendState State { get; }                      // NotStarted | Loading(since) | Ready | Failed(msg)
    Task InitializeAsync(CancellationToken ct);      // LanguageModel.CreateAsync (+ LAF, ready-state)
    IModelContext CreateContext(string? systemPrompt);
    int? UsablePromptLength(IModelContext ctx, string prompt);   // null = unsupported
    Task<GenerationResult> GenerateAsync(IModelContext ctx, string prompt,
        SamplingOptions? sampling, Action<string> onDelta, CancellationToken ct);
}
public interface IModelContext : IDisposable { }     // wraps LanguageModelContext; owns dispose
public sealed record GenerationResult(string Text, GenerationStatus Status, string? Detail);
public enum GenerationStatus { Complete, PromptLargerThanContext, ContentFiltered, BlockedByPolicy,
                               Cancelled, Error }
```

Each real adapter is ~150 lines: map its own status enum to `GenerationStatus`, forward `Progress`
to `onDelta`, `op.AsTask(ct)`, own the `LanguageModel` handle, dispose contexts. `FakeBackend`
streams canned or scripted tokens with configurable delays/faults (mid-stream throw, overflow status,
slow first token, cancellation observation) and exposes counters the tests assert on (contexts
created/disposed, prompts received verbatim).

### 2.2 Request → prompt → response mapping

**`POST /v1/chat/completions`** request fields:

| Field | Handling |
|---|---|
| `model` | Accepted, ignored (only one model); echoed back. `/v1/models` lists the active backend id. |
| `messages[]` | `system`/`developer` → system section; `user`/`assistant` → transcript; `tool` → rendered tool result; `content` may be string or array of `{type:"text"}` parts (image parts → 400). |
| `stream` | SSE path vs JSON path. `stream_options.include_usage` honored (final usage chunk). |
| `temperature`, `top_p`, `top_k` | Passed when `Capabilities.SamplingOptions`; else accepted, ignored, logged once per process. |
| `max_tokens` / `max_completion_tokens` | Neither API has a max-tokens knob. We stop consuming deltas at the cap, cancel the op, and return `finish_reason:"length"`. Documented as best-effort. |
| `tools`, `tool_choice` | Emulated (section 2.6). `tool_choice:"none"` suppresses injection. `"required"`/named → stronger instruction, still best-effort. |
| `n`, `logprobs`, `response_format`, `seed`, `stop` | `n>1` → 400. `stop` honored client-side (truncate at first stop string, cancel op). Others accepted and ignored, logged once. |

Response: standard `chat.completion` / `chat.completion.chunk` objects with `id` (`chatcmpl-<ulid>`),
`created`, `model`, `choices[0].message|delta`, `finish_reason` ∈ `stop|length|tool_calls|content_filter`,
`usage{prompt_tokens,completion_tokens,total_tokens}`.

**Usage estimate.** `completion_tokens` = number of `Progress` callbacks (Aion documents one token
per callback; Phi Silica's speculative decoding may batch, so it can undercount). `prompt_tokens` =
`ceil(prompt_chars / 4)`. Documented as an estimate in `CLIENTS.md` and `DECISIONS.md`.
[Superseded by D44: measured on hardware, Phi Silica's progress callbacks undercounted completion
tokens by about 3x (29 callbacks for 367 characters), so `completion_tokens` is `ceil(chars/4)` too,
matching `prompt_tokens`.]

**Error bodies** follow OpenAI's `{"error":{"message","type","param","code"}}`:

| Condition | HTTP | `type` / `code` |
|---|---|---|
| Malformed JSON / schema | 400 | `invalid_request_error` |
| `PromptLargerThanContext` (and truncation off or exhausted) | 400 | `invalid_request_error` / `context_length_exceeded` |
| Content filtered (Phi Silica) | 200 | `finish_reason:"content_filter"` and empty content (matches OpenAI) |
| Backend not ready (loading/compiling) | 503 + `Retry-After: 10` | `server_error` / `model_loading` |
| Backend failed to initialize | 503 | `server_error` / `model_unavailable` |
| Queue full | 429 + `Retry-After` | `rate_limit_error` / `queue_full` |
| Backend `Error` status mid-generation | non-stream: 502 | stream: error event then `[DONE]` (see 2.4) |

**`POST /v1/completions`**: `prompt` (string or single-element array) → one user message → same
pipeline → `text_completion` object; streaming emits `text` deltas.

**`GET /healthz`** (never cached, 200 when ready, 503 otherwise):
```json
{ "status":"ready|loading|failed", "backend":"phi-silica", "model":"phi-silica",
  "loading_seconds": 187, "first_run_compile_likely": true,
  "laf_status":"Available|AvailableWithoutToken|Unavailable|n/a",
  "package_identity": true, "queue_depth": 0, "queue_capacity": 4, "contexts_cached": 2,
  "capabilities":["sampling_options","system_prompt_context","prompt_length_preflight","cancellation"], "error": null }
```
`first_run_compile_likely` is a heuristic (loading > 60 s) because neither API distinguishes
"compiling" from "loading".

### 2.3 Message flattening and the prompt template

The model runtime wraps whatever we send as *one user turn* in its own chat template. So we render a
transcript *inside* that turn. One `PromptTemplate` class owns the format; `--verbose` echoes it.
[This is what shipped in chunk 3. D45 measured on hardware that rendering through this template is
what makes the model follow a system prompt at all — under both native `CreateContext(system)` and
folding the system text into the prompt body, the same instruction was obeyed; the bare, unrendered
`/debug/generate` path still ignores its system prompt. This corrects the chunk 2 observation that
Phi Silica ignores system prompts, which turned out to be a property of that unrendered path, not of
the model.]

```
<system text>                                   ← Phi Silica: via CreateContext(system); Aion: this block

### Conversation so far
[User]
...
[Assistant]
...
[Tool result: get_weather (call_01)]
{"temp": 21}

### Reply as the assistant to the latest message.
[User]
<newest user text>
```
Rules: a single bare user message with no system/history/tools is sent **raw** (best quality for the
common `curl` case). Anything else uses the markers. On a context-cache hit only the *tail* (turns after
the cached prefix) is rendered, with the same markers, so the model sees a consistent format.

### 2.4 Streaming path

- Headers: `Content-Type: text/event-stream`, `Cache-Control: no-cache`, `X-Accel-Buffering: no`;
  response body flushed after every chunk.
- First chunk carries `delta:{role:"assistant",content:""}`; each `Progress` delta becomes one chunk;
  final chunk has empty delta + `finish_reason`; optional usage chunk; then `data: [DONE]`.
- Deltas are handed from the WinRT callback thread to the request via a `Channel<string>` (single
  reader, unbounded, completed by the awaiting task). No `HttpResponse` writes on the callback thread.
- Mid-stream backend error after headers are sent: we cannot change the status code. We emit an SSE
  `data: {"error":{...}}` event (what OpenAI does), then `data: [DONE]`, then close. Tests cover it.
- Client disconnect: `HttpContext.RequestAborted` → `op.Cancel()`; the worker still awaits the op to
  completion (drain) before picking the next job; the context is disposed (state indeterminate).
- Keep-alive: while waiting for the first token (or while buffering for tool detection) emit an SSE
  comment line `: keep-alive` every 15 s so proxies/clients don't time out.

### 2.5 Context cache

Goal: a continuing conversation sends only its newest turns to the NPU.

- **Key** = SHA-256 over the canonical rendering of `(system, turn_0 … turn_k)` where `turn_k` is the
  last *assistant* turn. After a successful generation the context has absorbed prefix + reply, so it
  is stored under `hash(prefix ∥ canonical(reply))`.
- **Lookup**: walk the incoming messages, find the longest prefix ending in an assistant message whose
  key is cached. Tail = the remaining messages (user and/or tool). Canonicalization trims whitespace
  and, for assistant messages with `tool_calls`, hashes the tool-call JSON rather than raw text, so a
  client that re-serializes our own output still hits.
- **Exclusive checkout**: a context is removed from the cache while a generation uses it. On success it
  is re-inserted under the new key. On any non-`Complete` status, exception, or cancellation it is
  **disposed** (we can't know what the runtime appended).
- **Miss**: fresh `CreateContext(system)`; render the entire transcript into one prompt.
- **Bound**: LRU, `--context-cache-size` (default 4, since each context pins NPU/KV memory of unknown
  size). Evicted and shut-down contexts are disposed. `--context-cache-size 0` disables caching
  (always replay).
- **Overflow**: `PromptLargerThanContext` → 400 `context_length_exceeded` with a message that names the
  prompt size in chars and the backend. With `--truncate-history`: drop the oldest non-system
  user/assistant/tool pair, re-render, retry; on Phi Silica use `GetUsablePromptLength` as a preflight
  to avoid burning a generation per attempt. Every truncation is logged at Warning with counts, and the
  response carries a `x-npu-bridge-truncated-turns: N` header. Context pressure is also logged per
  request (prompt chars vs the configured `--context-window-hint`, default 4096 tokens for Phi Silica).

### 2.6 Tool-calling emulation

When `tools` is present and `--tool-emulation on`:

1. **Injection** into the system section:
   ```
   You can call tools. Available tools:
   - get_weather(location: string, unit?: "c"|"f") — Get current weather
   ...
   To call one or more tools, reply with ONLY this JSON in a ```json fence and nothing else:
   {"tool_calls":[{"name":"<tool>","arguments":{...}}]}
   If no tool is needed, answer normally in plain text.
   ```
   Schemas are rendered in a **compact signature form** by default (`--tool-schema compact|full`)
   because full JSON Schema for OpenCode's ~15 tools alone is 2–3K tokens, i.e. most of Phi Silica's
   window. Required/enum/description are preserved; nested objects are rendered as `{a: string, b?: int}`.
2. **Buffering**: with tools present the whole reply is buffered before anything is emitted (with
   keep-alive comments), because we can't know whether the model is emitting a tool call until it's
   done. Speculative "stream until it stops looking like JSON" goes to `FUTURE.md`.
3. **Parser** (tolerant, in this order): fenced ```json block → first balanced `{…}` containing
   `"tool_calls"` → first balanced `{…}` with `"name"` + `"arguments"` (single call) → bare
   `[{…}]` array. Accepts `arguments` as object *or* JSON-encoded string, tolerates trailing/leading
   prose, trailing commas, and single quotes only via a last-resort relaxed pass. Unknown tool names are
   still surfaced (client decides). Anything unparsable is returned as plain content.
4. **Output**: `message.tool_calls[{id:"call_<ulid>", type:"function", function:{name, arguments:<string>}}]`,
   `content:null`, `finish_reason:"tool_calls"`. Streaming: one chunk with the `tool_calls` delta
   (index, id, name, full arguments), then the finish chunk — the same shape OpenAI produces when it
   sends arguments in one piece.
5. **Tool results** (`role:"tool"`) render as `[Tool result: <name> (<tool_call_id>)]\n<content>`.
   The assistant message that made the call renders as `[Assistant]\n<tool_calls JSON>` so the model
   sees its own protocol.

**Honest expectation.** Phi Silica is a ~3.3B model with ~4K context; Aion's size and window are
unpublished. From experience with Phi-3-mini-class models: single-tool, few-argument requests succeed
maybe 60–80% of the time with a good instruction; with 10+ tools, deep schemas, and a 3K-token agent
system prompt, expect frequent argument hallucination, prose-wrapped JSON (we handle), calling tools
that weren't offered (we surface), and forgetting to call a tool at all. OpenCode's loop will *run*,
but multi-step edits will be unreliable on Phi Silica; Aion is the real hope. Tests will hard-code
adversarial outputs (all the malformed shapes above) so the *bridge* side is solid; model compliance
is something only the smoke test on the laptop can measure, and `scripts/smoke.ps1` will include a
tool-call probe that reports pass/fail over N runs.

### 2.7 Concurrency

- `GenerationScheduler`: one worker task reading a bounded `Channel<GenerationJob>`
  (`--queue-capacity`, default 4). Enqueue failure → 429 with `Retry-After: <estimate>` (queue depth ×
  the mean duration of the last 16 generations, min 1).
- Each job carries the request's `CancellationToken`; a job cancelled while *queued* is dropped
  without touching the model.
- The worker never starts a job until the previous op has fully completed, even after `Cancel()`.
- Model initialization runs in a background task at startup; requests before ready → 503.

### 2.8 Observability

- `Microsoft.Extensions.Logging` with the console JSON formatter when running as a service, simple
  console when interactive. One Information line per request:
  `req=chatcmpl-… backend=aion prompt_chars=812 cache=hit tail_turns=1 ttft_ms=340 tokens=97 tok_s=18.2 status=Complete finish=stop http=200 queue_wait_ms=0`
- `--verbose` additionally logs the exact prompt string handed to the backend and the raw model text.
- One-time warnings for ignored parameters (`temperature` on Aion, etc.).

### 2.9 Configuration

Precedence: CLI flag > env `NPU_BRIDGE_*` > `appsettings.json` > defaults.

| Setting | CLI | Default |
|---|---|---|
| Backend | `--backend phi-silica\|aion\|fake` | `phi-silica` |
| Listen | `--listen 127.0.0.1:5273` | localhost only, port 5273 |
| Queue capacity | `--queue-capacity` | 4 |
| Context cache | `--context-cache-size` | 4 |
| Truncation | `--truncate-history` | off |
| Tool emulation | `--tool-emulation on\|off` | on |
| Tool schema rendering | `--tool-schema compact\|full` | compact |
| Context window hint (tokens) | `--context-window-hint` | 4096 |
| LAF token / attestation | `--laf-token`, `--laf-attestation` (or `NPU_BRIDGE_LAF_TOKEN`, `_ATTESTATION`) | none |
| Verbose | `--verbose` | off |
| Service verbs | `service install\|uninstall\|start\|stop` (via `sc.exe`, needs elevation) | — |

### 2.10 Package identity for Phi Silica (the part that needs your action early)

- `packaging/AppxManifest.xml`: `Identity Name="NpuBridge" Publisher="CN=npu-bridge-dev"`,
  `<uap10:AllowExternalContent>true</uap10:AllowExternalContent>`, `runFullTrust` + `systemAIModels`,
  `TargetDeviceFamily MinVersion="10.0.26100.0"`, `PackageDependency` on
  `Microsoft.WindowsAppRuntime.1.8` (and `.2` if we land on WAR 2.0).
- `scripts/identity.ps1 -Install|-Uninstall`: creates a self-signed cert with that subject (once),
  imports it to `LocalMachine\TrustedPeople`, runs `makeappx pack` + `signtool sign` from the
  `Microsoft.Windows.SDK.BuildTools` NuGet (no Visual Studio), then
  `Add-AppxPackage -ExternalLocation <bin dir>`. Prints the resulting **PFN** — that's what goes on
  the LAF request form.
- The exe checks `GetCurrentPackageFullName` at startup; the Phi Silica adapter refuses to initialize
  without identity and prints the exact command to fix it. Aion and fake never care.
- The cert subject is fixed in chunk 1 so you can send the LAF request on day one.

---

## 3. Chunks

Reordered from your list to put the Phi Silica *prerequisites* (identity, PFN, token request) as
early as possible without blocking on them, since that is the long-lead item. Each chunk ends with
build + tests green, an adversarial review pass, and updates to `DECISIONS.md` / `FUTURE.md`.

| # | Chunk | Verifiable here (x64) | Verifiable only on the laptop |
|---|---|---|---|
| 1 | **Done.** **Skeleton.** Solution, Core/exe/tests, config precedence, Kestrel host, `/healthz`, `/v1/models`, `ILanguageModelBackend` + `FakeBackend`, service verbs, `packaging/AppxManifest.xml` + `scripts/identity.ps1`, `DECISIONS.md`/`FUTURE.md` seeded. | build, tests, config tests | `identity.ps1` registers, PFN printed, `/healthz` shows `package_identity:true` |
| 2 | **Done.** **Phi Silica adapter (thin).** `PhiSilicaBackend`: WAR bootstrap, LAF unlock (optional token), ready-state, `CreateAsync`, `CreateContext(system)`, options mapping, status mapping, `GetUsablePromptLength`. `scripts/smoke.ps1` v1 (health + one non-streaming prompt). | compiles for ARM64 | smoke test; you can start it as soon as chunk 3 lands even without a token if the SDK channel doesn't need one |
| 3 | **Done.** **Non-streaming `/v1/chat/completions`.** Request DTOs + validation, `PromptTemplate`, pipeline (no cache yet: fresh context per request), usage estimate, error mapping, per-request log line, `--verbose`. | full test coverage via TestServer + FakeBackend | first real end-to-end on the NPU |
| 4 | **Done.** **Streaming SSE.** Channel hand-off, chunk framing, `[DONE]`, mid-stream error event, disconnect → cancel + drain, keep-alive, `stream_options.include_usage`, `max_tokens`/`stop` client-side cut. | tests for framing, error, cancel timing | tok/s numbers, does `Cancel()` actually stop the NPU |
| 5 | **Done (2026-09-11, D71 to D75).** **Context cache + overflow.** `ConversationKey` (length-prefixed encoding of `(system, turns)`, not the rendered prompt), `ContextCache` (LRU, exclusive checkout, disposal on eviction/replacement/shutdown), `ConversationSession`/`ContextLease` (lookup, tail rendering, preflight-driven overflow, the `--truncate-history` loop, status-driven retry without a preflight, the header, the pressure warning), `/healthz` cache fields, two smoke steps. | 462 tests incl. leak counting on the fake and both shapes | measured: hit TTFT 235 ms vs 392 ms replay; the preflight refuses 16.6K chars in 31 ms; truncation answers with the header |
| 6 | **Done (code-verified only, 2026-09-11).** **Aion adapter.** `PackageDependency` (from the sample's `FrameworkDependency`), `AionBackend` behind a conditional SDK reference, `nuget-local/` with the 1.0.0 nupkg, shared `DeltaAccumulator` in Core, capability-profile tests, smoke script gains `-Backend aion`. Hardware half blocked by the OS on this machine (D70); issue #2 open for it. | 404 tests, CI without the nupkg | smoke test after `Bootstrap.ps1`-style framework install: not yet possible here |
| 7 | **Done (2026-09-11, D83).** **Tool emulation.** Injection into the system text (so the cache key covers the tools offered), `ToolSchemaRenderer` (compact signatures by default), the tolerant `ToolCallParser`, `ToolCallReply` shaping both shapes, tool-call and tool-result rendering in the transcript, streaming buffered whole behind keep-alives. Five test files, 1,880 lines; the parser's 581 are the adversarial shapes. | 866 tests | measured: 20/20 runs called the tool, no prose, no leaked protocol, no unoffered tool, every argument valid JSON — on one tool with one required string argument; the 10-tool case is unmeasured (issue #21) |
| 8 | **Done (2026-09-12, D84 to D92).** **Concurrency + `/v1/completions` + docs.** `GenerationScheduler` (one worker, bounded channel, `--queue-capacity` finally read), 429 + `Retry-After` + `rate_limit_error`/`queue_full`, queued-cancel dropped without touching the model, `/debug/generate` through the queue too (D90), real `/healthz` `queue_depth`/`queue_capacity` (D87), `/v1/completions` on both shapes (D91), `Api/StreamingPipeline.cs` extracted from the two streamed endpoints, `docs/CLIENTS.md` (OpenCode, Hermes, curl, Python) and README pointers. The review's catch: the brief said to put the queue wait *after* the cache lookup and preflight, which contradicts §2.7 and would have shipped the scheduler guarding only `GenerateAsync` while `CreateContext` still raced — `Acquire` runs inside the closure (D84). | 932 tests, incl. the scheduler's ordering, admission and both completions shapes | measured: 28 PASS / 0 FAIL / 0 SKIP / 5 INFO on the first attempt, no RPC flake; two concurrent requests really queued (`queue_depth` peaked at 1); `--queue-capacity 1` admitted one and rejected two with 429 + `Retry-After`; `/v1/completions` answered on both shapes |

Chunks 2 and 6 are deliberately small; the point of doing 2 before 3 is that you can request the
LAF token and install runtimes while I build 3–5.

---

## 4. Open questions — answered 2026-09-01 (sign-off record)

| # | Question | Decision |
|---|---|---|
| Q1 | Project layout | **Three projects**: `NpuBridge.Core`, `NpuBridge` (exe), `NpuBridge.Tests`. |
| Q2 | .NET SDK | **.NET 10 SDK installed (10.0.400 arm64); target `net10.0`** (LTS). Fall back to `net9.0` only if the Aion/CsWinRT build breaks, and record why. |
| Q3 | Service vs identity | **Build the service verbs; verify service + Phi Silica on this machine in chunk 2.** Scheduled-task fallback only if identity doesn't reach the SCM-launched process. *Outcome: identity is granted only by package activation (D24), so the service verbs serve aion/fake and `task install` (logon task + self-relaunch with supervision, D34/D37) is the Phi Silica auto-start.* |
| Q4 | LAF token | **No token yet. Request one (form + PFN reply) as soon as chunk 1 prints the PFN; try the stable SDK meanwhile**, switch to the experimental channel if `TryUnlockFeature` returns `Unavailable` without a token. *Outcome (chunk 2): stable returned `Unavailable`; the exe now targets 2.4.1-experimental, which loads and generates without a token (D31). PFN: `NpuBridge_jtas4mnxdyzpe`.* |
| Q5 | Defaults | **`127.0.0.1:5273`, backend `phi-silica`.** |
| Q6 | Tool buffering | **Buffer the whole reply when `tools` is present, then stream**; keep-alive comments meanwhile. |
| Q7 | Phi Silica first | **Yes for the adapter (chunk 2); core chunks stay backend-agnostic.** |
| Q8 | x64 build | **Withdrawn**; machine is ARM64. |
| Q9 | Local NPU setup | **Yes, both**: install the Aion framework MSIX and register the Phi Silica sparse-package identity here; run smoke tests locally. Unit tests never depend on either. |

The original question text is kept below for context.

### Original questions

**Q1. Project layout.** OK with `NpuBridge.Core` (net9.0, testable on x64) + `NpuBridge` (ARM64 exe) +
`NpuBridge.Tests`, instead of a single project? *My recommendation: yes.*

**Q2. .NET 9 SDK on this machine.** There is no SDK here, only a runtime. May I run
`winget install --id Microsoft.DotNet.SDK.9`? Nothing can be built until then.

**Q3. Service vs. identity.** Phi Silica needs package identity; a Windows service started by SCM from
a sparse-package location *should* get it but I can't verify that here. Plan: build the service verbs
as specified, mark the "service + Phi Silica" combination as *unverified* in the README, and have
`smoke.ps1` check `/healthz.package_identity` when run against the service. If it turns out SCM
processes don't get identity, the fallback is a scheduled task at logon (still auto-start, no SCM).
Acceptable, or do you want the scheduled-task path built alongside from the start?

**Q4. LAF token.** Do you already hold a Phi Silica LAF token, and if so for which PFN? If not: the
plan fixes the cert subject in chunk 1 and prints the PFN so you can request one immediately.
Alternatively, do you want to target the **experimental** Windows App SDK channel (docs say no LAF
needed there) instead of stable 2.0.x? *My recommendation: stable 2.0.x with optional token config,
and switch channels only if `TryUnlockFeature` comes back `Unavailable` on your machine.*

**Q5. Default backend and port.** Defaults proposed: `--backend phi-silica`, port `11434`
(Ollama's port, which many agent tools already have muscle memory for). Change either?

**Q6. Tool-emulation buffering.** With `tools` present I buffer the whole reply before streaming (with
keep-alive comments) so tool calls can be detected reliably. OK to trade first-token latency for
correctness here? *My recommendation: yes; speculative streaming goes to FUTURE.md.*

**Q7. Phi Silica first?** You asked mid-research. My answer: yes for the *adapter and its prerequisites*
(that's why chunk 2 is now Phi Silica), no for the *core* — chunks 3–5 are backend-agnostic and are
what you'll actually be waiting on, so they're built against the fake regardless. Aion's adapter is
trivially thin (its API is a subset) and slots in as chunk 6.

**Q8. Withdrawn.** This machine turned out to be the ARM64 Copilot+ PC (see F7), so the real exe runs
here as-is. No `win-x64` build is needed. The three-project split (Q1) still stands, for test speed and
to keep WinRT out of the logic layer, not for cross-architecture reasons.

**Q9. Running the real model during development.** Since the NPU is here, I would like to run
`scripts/smoke.ps1` against Phi Silica and Aion myself after chunks 2 and 6, and use the real model
for ad-hoc checks of the prompt template and tool-call compliance. That means installing the Aion
framework MSIX (1.4 GB download, `Add-AppxPackage`) and the sparse-package identity for Phi Silica
on this machine. OK? *My recommendation: yes, with the caveat that unit tests never depend on it.*

---

## 5. Out of scope now, tracked in `docs/FUTURE.md` from chunk 1

x64 backends; embeddings endpoint (Phi Silica has `GenerateEmbeddingVectors`); structured output via
`GenerateStructuredJsonResponseAsync` (could make tool calling far more reliable on Phi Silica —
noted as the single biggest upgrade path); speculative streaming with tools; context TTL; multiple
model ids per process; auth token on the listener; content-filter option pass-through; LoRA adapters.
