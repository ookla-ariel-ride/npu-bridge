# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`npu-bridge`: a .NET 10 Windows console app / Windows service that exposes the Copilot+ PC on-device
language model as an OpenAI-compatible HTTP API (`/v1/chat/completions`, `/v1/completions`,
`/v1/models`, `/healthz`) so agent tools (OpenCode, Hermes) can use the NPU model as a provider.
Two real backends behind one interface: Phi Silica (`Microsoft.Windows.AI.Text`, Windows App SDK)
and Aion Instruct Preview (`AionInstructPreview.Text`, Microsoft's announced replacement), plus a
fake backend for tests. Aion 1.0 Plan is a different model (14B, 32K context, native tool
calling) with no SDK as of 2026-09-10; it is tracked as a GitHub issue, and a backend for it would
bypass the tool-call emulation rather than use it.

**All eight chunks of `docs/PLAN.md` are built and merged; there is no next chunk.** Work from here
is GitHub issues. `docs/PLAN.md` is the signed-off design; `docs/DECISIONS.md` records why things are
the way they are, decision by decision (D1 to D102), and is where the chunk-by-chunk history this
section used to duplicate actually lives; `docs/SESSION-HANDOFF.md` carries the current state and
what to do next; `docs/FUTURE.md` holds deferred work. Read the handoff first; update DECISIONS and
FUTURE whenever work changes a choice or defers something.

Current state: 980 tests pass on `wave/leftovers` (last run by the Sidequest integration gate on `f1d3b8b`, 2026-09-13),
`smoke.ps1 -Backend phi-silica` passed clean on hardware at `3c97d48` (2026-09-13, morning: all steps, 0 skipped, 6
informational; the 2026-09-12 run with `-ToolProbeRuns 20` called the tool 20/20) and the D80 cross-check matched
`context_window_tokens` 3581 exactly on every run. The wave is not yet merged to `main`; the repository is
`ookla-ariel-ride/npu-bridge`.

Standing facts that will cost you a session if you do not know them:

- **Chunk 6 (Aion) is code-verified only — no Aion generation has ever run here.** Build 29648 never
  appends the `WIN://SYSAPPID` token attribute for a main-package dynamic dependency, so the Qualcomm
  QNN provider that Windows ML 1.8 needs cannot be image-mapped (D70, issue #2). **Do not spend time
  on that blocker again**: Developer Mode, SFC, DISM, ACLs, drivers and package identity are all ruled
  out. Only another Windows build or a Feedback Hub report remains.
- **Package identity is registered against the build output path.** Moving or renaming the folder
  invalidates it; re-run `.\scripts\identity.ps1 -Install` before the next `--backend phi-silica` run.
  `identity.ps1 -Status` will not reveal this — the symptom is a relaunch failing with
  "registered for \<other folder\>".
- **Build 29661 broke Phi Silica and was rolled back to 29648.** If the Insider flight is offered
  again, expect the same (workload packages fail to register, model `NotReady`).
- **An empty `Get-AppxPackage -Name 'WindowsWorkload.LanguageModel*'` listing is not proof of
  breakage** on 29648. `/healthz` is the check.
- **A real agent client does not fit.** Measured 2026-09-12 (D93): a terminal agent's tool schemas
  alone are nearly 3 times the 3,581-token window and its whole fixed prompt 3 to 7 times, so an agent must have its toolset cut down before it
  can use this bridge at all. Compliance below that boundary is near-perfect; the window is the
  constraint, not the model's protocol discipline.

Open issues carry the rest: the `leftovers` wave (D100 to D102, 2026-09-12/13) took #19, #22, #25,
#26, #34 and #35 and ships as one PR; #24, #27 and #28 remain from chunk 8; #33 (an empty
`tool_calls` fence delivered as content) still needs a ruling; #2, #11, #14 to #17 are longer-running.

## Machine reality

- **This machine is the Copilot+ PC** (Galaxy Book4 Edge, Snapdragon X Elite, Windows 11 ARM64).
  The Git Bash tool runs under x64 emulation and reports `AMD64`; trust PowerShell's
  `RuntimeInformation.OSArchitecture` (Arm64), not `uname`. Windows App Runtime 1.8 and 2.x are
  installed; check the Aion framework with `Get-AppxPackage Microsoft.AionInstructPreview.Framework.1.0`.
- Unit tests run against `FakeBackend` only and must never need the NPU. Real-adapter verification is
  `scripts/smoke.ps1`, which can be run here. Claim only what the smoke test showed.
- Both frameworks are ARM64-only. Do not add x64 workarounds for the backends; note them in
  `docs/FUTURE.md` instead.
- Secrets: the LAF token/attestation live in `appsettings.local.json` next to the exe (gitignored).
  `NPU_BRIDGE_LAF_*` env vars work for the fake/aion backends, but the Phi Silica path relaunches through
  package activation, which does not inherit the shell's environment; env secrets are deliberately not
  forwarded to the child (D38). gitleaks runs as a pre-commit hook (`git config core.hooksPath .githooks`)
  and in CI with the project rules in `.gitleaks.toml`.
- CI (`.github/workflows/build.yml`) builds the whole solution and runs the tests on `windows-latest`
  on every push and pull request. It needs no NPU: the tests only use `FakeBackend`.

## Commands

Requires the .NET 10 SDK (`winget install --id Microsoft.DotNet.SDK.10`); check with `dotnet --list-sdks`.
Default listener is `http://127.0.0.1:5273`.

```powershell
dotnet build                                   # whole solution (npu-bridge.slnx); exe builds win-arm64
dotnet test                                    # all tests (Core + FakeBackend + TestServer; no NPU needed)
dotnet test --filter "FullyQualifiedName~HealthzTests"      # one test class
dotnet test --filter "DisplayName~Loading_backend"          # one test by name fragment
dotnet run --project src/NpuBridge -- --backend fake --verbose   # run the exe (bin\Debug\...\win-arm64\NpuBridge.exe)
.\scripts\identity.ps1 -Install                # sparse package identity for Phi Silica; installs the runtime dep; prints the PFN
.\scripts\identity.ps1 -Status                 # is the package registered, which PFN
.\scripts\smoke.ps1 -Backend phi-silica        # real NPU run: health, models, /debug/generate, chat (JSON and SSE), the cut, the cache hit, the overflow refusal and --truncate-history (on a second server), the D80 tokenizer boundary check, the D53/D55 measurements, the tool-call compliance probe (-ToolProbeRuns, default 5), the chunk 8 concurrency steps (two requests really queue; --queue-capacity 1 admits one and 429s the rest) and /v1/completions on both shapes, teardown; -JsonOut <path> writes a UTF-8 JSON run summary
.\scripts\tool-probe.ps1 -Include WindowOccupancy -Runs 8   # issue #21 hard-case tool-call measurement against a RUNNING bridge; five dimensions (tool count, schema depth, system-prompt pressure, multi-step, window occupancy), -Stream checks JSON/SSE tool-call parity, -SelfTest checks the parser without a bridge, and -JsonOut writes the numbers. Never sends >32K chars of system text: above ~44K the Windows model host fail-fasts (D94)
NpuBridge.exe task install|status|uninstall    # logon task that starts Phi Silica with identity (install/uninstall elevated)
NpuBridge.exe service install|start|stop|uninstall   # Windows service for aion/fake (elevated)
```

Re-run `identity.ps1 -Install` after the build output folder or the manifest changes. If Phi Silica
relaunch fails with "registered for <other folder>", that is why.
`smoke.ps1` reports with `Write-Host`; redirect with `6>&1` if you need a transcript, plain `>` captures nothing.
Do not `dotnet build` while a server is up — `smoke.ps1`'s, or one you started yourself: the exe is
locked and the copy fails. **`dotnet test` builds too**, so it fails the same way; stop the server
first, or accept that the suite cannot run while one is live.
Two smoke lines look like failures and are not. A *first*-generation "the remote procedure call failed"
is the model runtime's known flake — re-run once; the same fault on a later generation is real. And
`system prompt honoured: False` on the `/debug/generate` row is D45: only the bare debug path ignores it.

**Never send more than ~40,000 characters of system text to Phi Silica (D94, issue #29).** At 44,000
and above, `CreateContext` fail-fasts `WorkloadsSessionHost.exe` (`0xc0000409`) and wedges the NPU for
the whole machine for several minutes: every generation then returns 502 `The RPC server is
unavailable` in 3 to 17 ms, `/healthz` still says `ready` (issue #30), a bridge restart does not clear
it, and the host processes are protected so they cannot be killed. It self-heals after minutes. This is
easy to trigger by accident, because tool emulation renders the tool block into the system text — a
real agent's toolset is ~37 KB on the wire. Find limits with `POST /debug/tokenize`, not by sending the request.
"RPC server is unavailable" persisting past one retry means this happened; wait it out. The bridge now refuses native system text above 32,000 characters or the backend's known window token count before `CreateContext` (D97); the warning still applies to bare backend calls and to `/debug/generate` on builds before this change.

Aion's SDK NuGet is not on nuget.org. It comes from the sample repo's GitHub release
(`AionInstructPreview.Text.Framework.1.0.0.nupkg`) and lives in `nuget-local/`, wired by `nuget.config`.

Chunk 3 introduced `--system-prompt-placement auto|native|prompt` (default `auto`, native when the
backend advertises the capability) and made `POST /v1/chat/completions` real. Chunk 4 made
`stream: true` real (one `chat.completion.chunk` per delta, `: keep-alive` comments while waiting for
the first token, `stream_options.include_usage`) and added the client-side cut for `max_tokens`,
`max_completion_tokens` and `stop` on both response shapes (D53). Chunk 5 made `--context-cache-size`
(default 4, `0` disables) and `--truncate-history` real, and `--context-window-hint` now drives a
context-pressure warning. Chunk 8 finally made `--queue-capacity` (default 4) real and added
`POST /v1/completions`. See "Protocol rules" below.

## Architecture (see docs/PLAN.md §2 for the full version)

Three projects, deliberately:

- `src/NpuBridge.Core` (net10.0, AnyCPU, no WinRT references): everything with logic. Built so
  far: OpenAI DTOs and endpoint mapping (`FrameworkReference` to ASP.NET Core so `TestServer` covers
  HTTP framing), message flattening + prompt template (`PromptTemplate`), the shared preparation
  phase (`ChatRequestPreparer`), the JSON and SSE generation phases (`ChatCompletionsEndpoint`,
  `ChatCompletionsStreamEndpoint`), the client-side cut (`OutputLimits`/`OutputCutter`), one failure
  mapping and one outcome classifier for both shapes (`GenerationFailure`, `GenerationOutcome`), the
  rest of the post-generation pipeline both shapes share (`GenerationPipeline.cs`: `DeltaSink`,
  `CutWatcher`, the guarded cancel and the raw-output log, D81), `ILanguageModelBackend`, `FakeBackend`, the
  conversation key and the context cache (`ConversationKey`, `ContextCache`), the session that
  drives lookup, tail rendering and overflow handling for both shapes (`ConversationSession`,
  `ContextLease`), and the token counters (`Tokenizers/`: `ITokenCounter`, `CharEstimateTokenCounter`,
  `Phi3TokenCounter` over the embedded Phi-3.5-mini `tokenizer.model`, D80; `Microsoft.ML.Tokenizers`
  is Core's only package reference).
  the tool-call emulation (`Tools/`: `ToolCallParser`, `ToolCatalog`, `ToolSchemaRenderer`, plus
  `Api/ToolCallReply`, D83), and chunk 8's concurrency and legacy endpoint (`Api/GenerationScheduler.cs`,
  the one-worker bounded queue and `IHostedService`; `Api/SchedulerAdmission.cs`, which maps a scheduler
  outcome to the wire — 429 with `Retry-After`, 503 `queue_shutting_down`; `Api/StreamingPipeline.cs`,
  the SSE plumbing both streamed shapes share; `Api/JsonPipeline.cs`, the scheduled closure both JSON
  shapes share (D100); and `Api/CompletionsEndpoint.cs`,
  `CompletionsStreamEndpoint.cs`, `CompletionRequest.cs`, `CompletionResponse.cs`, `CompletionChunk.cs`
  for `/v1/completions`, D84 to D92). `Backends/BackendLimits.cs` holds the 32,000-character native
  system-text ceiling (D97, D101 addendum) and `Backends/GenerationHealth.cs` the outcome recorder that
  `GenerationPipeline.BackendCallTracker` feeds (D98, D102); `FakeBackend.DeltaGate` is the
  mid-generation gate the cut and cancellation tests use (D54, D102).
  Everything in `docs/PLAN.md` is now built; there is no "not built yet" list.
- `src/NpuBridge` (net10.0-windows10.0.26100.0, ARM64 exe): `Program.cs`, config, service and task
  verbs, `PhiSilicaBackend`, `AionBackend` (behind a conditional SDK reference: when
  `nuget-local/` lacks the Aion nupkg the adapter is excluded and `--backend aion` explains why in
  `/healthz`, D66), `PackageDependency` (adds the Aion framework and Windows App Runtime 1.8 to the
  process graph), `PackageActivation`, packaging manifest. The delta accumulator both adapters share
  lives in Core (`DeltaAccumulator`, D67/D69) so it is unit-tested.
- `tests/NpuBridge.Tests` (xunit): runs against `FakeBackend` through `TestServer`.

Keep logic out of the exe project; if it needs a test, it belongs in Core.

Writing tests: never assert on wall-clock timing; gate the fake and assert on ordering (D54). Which
gate depends on where the hold has to be — `StartGate` before the generation decides anything at all,
the prompt-length verdict included; `FirstTokenGate` after that verdict and before the first token;
`InitGate` during model load (D81). `FakeBackend` delivers deltas on the thread pool by default, like
WinRT, so non-thread-safe state in a delta callback fails in the suite rather than on the NPU.
`BridgeTestHost.cs` holds the shared helpers: `TestWait.UntilAsync` for a polled condition,
`Sse.Payloads`/`Sse.Chunks` for an SSE body, `ChatBody.User` for the minimal request. Count contexts
created against disposed plus cached on every new generation path (`BridgeTestHost.AssertNoLeak`): a
context is in the cache or disposed, never both, never neither.

### Request flow (as of chunk 8)

```text
HTTP → ChatRequestPreparer (shared by both shapes, and by /v1/completions after the model-id check):
       body → validate DTO → backend readiness
     → ignored-parameter warnings → placement → PromptTemplate (messages → system + transcript)
     → native system guard (character ceiling and context-window tokens) → 400 context_length_exceeded
     → OutputLimits (max_tokens/stop) → PreparedChatRequest
     → GenerationScheduler.ScheduleAsync: bounded queue (--queue-capacity), one worker
         queue full      → 429 + Retry-After (depth x rolling mean), rate_limit_error/queue_full
         cancelled queued→ dropped without touching the model (503 queue_shutting_down)
       everything below runs INSIDE the scheduled closure, because Acquire touches the shared
       model handle too (D84), and a --truncate-history retry keeps its slot rather than
       re-queueing (D86)
     → ConversationSession.Acquire: prefix keys (ConversationKey) → ContextCache.CheckoutLongest
         hit  → the cached context, prompt = PromptTemplate.RenderTail(turns after the prefix)
         miss → backend.CreateContext(native system), prompt = the whole rendered transcript
       → preflight (GetUsablePromptLength) where the backend has one: fits → lease;
         overflow → cached context returned untouched, then --truncate-history drops the oldest
         exchange and retries, or 400 context_length_exceeded
     → stream: false → backend.GenerateAsync on the lease (deltas watched for the cut)
                     → whole-text cut → GenerationFailure mapping → JSON body → usage estimate
     → stream: true  → GenerateAsync started, deltas cross a Channel<string>
                     → keep-alives until the first delta → role chunk → one chunk per cutter release
                     → finish chunk → optional usage chunk → data: [DONE]
     → no preflight (Aion): a PromptLargerThanContext status with --truncate-history drops and retries
     → the lease is settled once on both shapes: Keep (Complete and uncut → back into the cache under
       the new key) or Dispose in the finally (stream: cancel → drain → settle, D51)
```

Tool-call emulation (D83) adds a buffered branch to the streaming path when `tools` is present.
`/v1/completions` wraps its `prompt` into one user message and joins this flow at the model-id check;
its streamed shape has no role chunk, so its headers commit at the first cutter release (D91).

The lease is published from inside the closure the instant `Acquire` hands it over, never read off the
scheduled task's return value (D85): a streaming client that vanishes mid-frame can unwind before the
task is unwrapped, and the `finally` would then see `null` and never release the context.

Because `Acquire` is inside the closure, a second concurrent request for one conversation does not even
attempt its own cache lookup until the first's whole attempt has run its course. Whether it then reuses
the seed context (the first's `Keep` having already returned it) or creates a second that loses the key
is a scheduling detail chunk 8 deliberately does not promise either way;
`Two_concurrent_requests_for_one_conversation_never_share_a_context` was rewritten to pin the bound
rather than the coin flip — at most one extra context beyond the seed, never two live at once.

### Backend contract facts that must not be "simplified" away

- The two WinRT APIs are not identical. Aion has only `CreateAsync`, `CreateContext()`,
  `GenerateResponseAsync(ctx, prompt)`. No `LanguageModelOptions`, no system-prompt `CreateContext`,
  no `GetUsablePromptLength`. Sampling and system-prompt-context are per-backend *capabilities*.
- Status enums differ numerically (`Error` is 6 on Phi Silica, 2 on Aion). Map by name inside each
  adapter to `GenerationStatus`; never share numeric values.
- `Progress` delivers deltas (not accumulated text) on a WinRT thread. Never write to the HTTP
  response from that callback; hand off through a `Channel<string>`.
- `LanguageModel` and `LanguageModelContext` are `IDisposable`. A context whose generation ended in
  anything other than `Complete` (error, cancel, overflow) has indeterminate state: dispose it, never
  return it to the cache. Evicted contexts are disposed. Tests count creates vs disposes on the fake.
- Phi Silica needs package identity (sparse package with `systemAIModels` capability). Identity is
  granted only when Windows *activates* the app through its package, never when the exe is started by
  path (D24). So: `--backend phi-silica` started by path relaunches itself via
  `IApplicationActivationManager` (`PackageActivation.cs`), re-expressing `NPU_BRIDGE_*` on the child's
  command line (activation inherits no environment, D38), and then *supervises* the child: waits on its
  pid, forwards its exit code, kills it on Ctrl+C; the child (`--supervisor-pid`) exits when the parent
  dies (D37). A Windows service cannot carry identity, hence `task install` (logon scheduled task) is the
  Phi Silica auto-start and `service install` is for aion/fake only. The registered PFN on this machine is
  `NpuBridge_jtas4mnxdyzpe`. `EnsureReadyAsync` (multi-GB download) only runs with `--install-model` (D39).
- The exe targets the experimental Windows App SDK channel (2.4.1-experimental) because stable needs a
  LAF token that has not been issued (D31). The experimental runtime is a separate framework family
  (`Microsoft.WindowsAppRuntime.2-experimentalB`) named in `packaging/AppxManifest.xml` and installed by
  `identity.ps1` from the NuGet payload. Changing the SDK version means updating the manifest dependency
  and re-running `identity.ps1 -Install`. Aion needs none of this.
- The Windows App SDK auto-bootstrap is disabled in the csproj (`WindowsAppSdkBootstrapInitialize=false`)
  so `--backend fake` starts even when the runtime is absent; only the Phi Silica adapter bootstraps.
  CsWinRT is pointed at the NuGet-delivered Windows metadata (`CsWinRTWindowsMetadata`) so no Windows
  SDK install is needed; the SDK's version constants come from `WindowsAppSDK-VersionInfo.cs` linked
  from the Runtime package.
- SDK 2.4 API facts: `GenerateResponseAsync(context, prompt, options)` is the only context overload
  (always pass a `LanguageModelOptions`); `AIFeatureReadyState` includes `CapabilityMissing` (= the
  manifest lacks `systemAIModels`); `Progress` may deliver several tokens per callback.
- `POST /debug/generate` sends one literal prompt straight into the backend and returns text + timing.
  Use it to check the model or the adapter without the prompt template; `scripts/smoke.ps1` relies on it.
- **The model does follow a system prompt** when the transcript is rendered through `PromptTemplate`,
  under both native and folded placement (measured, D45). Only the bare `/debug/generate` path ignores
  its system prompt, so a finding from that endpoint is about the raw model, not this API.
- **Token counts come from the backend's `ITokenCounter`, never from progress callbacks (D80).** On
  Phi Silica that is `Phi3TokenCounter` over the vendored Phi-3.5-mini `tokenizer.model`, measured to
  be the runtime's own vocabulary (the preflight lands on 3581 of its tokens at every ASCII boundary;
  about 1 % off on punctuation-dense text). Aion, the unavailable backend and the fake's default use
  `CharEstimateTokenCounter` (`ceil(chars/4)`, D44). Phi Silica batches several tokens per callback
  under speculative decoding, so a callback count undercounts by roughly 3x (D44). The usable window
  of an empty Phi Silica context is 3581 tokens.
- **`GetUsablePromptLength` answers in UTF-8 bytes (D80).** `PhiSilicaBackend` converts with
  `Utf8Offsets.CharIndexAtByteOffset`; read as chars the answer is up to three times too generous
  for CJK. `Microsoft.ML.Tokenizers` 2.0.0 is Core's one package dependency; `POST /debug/tokenize`
  (loopback, works while loading) returns the backend's count and its counter's name.
- **A context is disposed on every path that creates one.** `/v1/chat/completions` creates its
  context immediately before generating and disposes it in a `finally`, covering success, prompt
  overflow, content filter, a backend `Error`/`Cancelled` status, a thrown exception, and a client
  abort. Requests that fail before a context exists (bad JSON, validation failure, backend not ready,
  a forced-placement conflict) never create one, so the guarantee is about paths that create a
  context, not literally every request (D43).

### Traps for the next chunks

- **The cache key is `ConversationKey`, never the rendered prompt (D71).** The three collision
  surfaces (unescaped turn markers, native placement dropping the system text from the prompt, the
  raw pass-through of a lone user message) are closed by a length-prefixed encoding of
  `(system, turns)`; `ConversationKeyTests` pins each one both ways. Anything a turn gains must enter the key through
  `PromptTemplate.TurnText` or the system text, or a cached context will be handed to a conversation
  the model never saw. Chunk 7 did both: tool calls render into the turn body, and the injected tool
  block is part of the system text (D83). Sampling parameters are deliberately not in the key.
- **A tool-call reply is stored under what the client will send back, not under what the model wrote
  (D83).** `ChatMessage.ToolCalls` renders into the turn body and `ConversationKey` hashes the array's
  ids, names and arguments as fields of their own. So `ContextLease.Keep` stores the *calls*, not
  `result.Text`: the context absorbed the fence and the prose around it, but the client returns an
  assistant message with null content and the array this bridge emitted. Keyed by the text instead —
  which is what it did first — every turn of an agent loop missed, which is the case the cache exists
  for. The round trip holds only while the client echoes the `arguments` string byte for byte.
- **Overflow is decided by the preflight where one exists (D73).** Phi Silica reports
  `PromptLargerThanContext` only sometimes (a prompt moderately over the window gets it in about
  600 ms; D55's 225 KB prompt got a generic `Error` after 26 s, D80), so `ConversationSession` asks
  `GetUsablePromptLength` before generating and the 400 arrives in tens of milliseconds. Aion has no preflight, so both endpoints
  retry on a `PromptLargerThanContext` status when `--truncate-history` can drop something; Aion's
  actual overflow status is still unmeasured (D70), so re-check that path when it runs. After a
  truncation the next request in that conversation misses and truncates again (`docs/FUTURE.md`).
- **A context in the cache is never in use, and a context that failed is never in the cache (D72).**
  `Keep` only after the generation task has ended `Complete` with the client-visible text equal to
  the backend's text; everything else disposes. Chunk 7 buffers a reply for tool detection and still
  obeys it. Chunk 8's scheduler made the second concurrent request for one conversation wait for the
  first's whole attempt before it even looks the context up, so which of the two survives in the cache
  is now a scheduling detail rather than a promise (D84).
- **The post-generation pipeline is shared now, so a third caller uses it rather than copying it
  (D81).** `GenerationOutcome.Classify(result, cancelledByCut)` is the one place failure, filtered and
  content are told apart, and both shapes agree only because neither decides for itself. Chunk 7's
  buffer-when-tools-present path calls it, and supplies the cut's post-flush verdict as an
  argument the way the other two do — the classifier reads no cutter, deliberately, because when that
  verdict is legible differs by shape (D57). `DeltaSink` takes a `ChannelWriter<string>` or a
  `CutWatcher` and never a delegate, so the callback provably cannot reach the response; a path that
  needs both destinations adds a factory and decides their order there.
- **Aion Instruct ships as a model swap behind the Phi Silica API, not as a new SDK.** Microsoft's
  Phi Silica page (updated 2026-07-24) says: a standalone sideloadable package early October 2026;
  Insider rollout in October with Phi Silica still present, the active model chosen by a Controlled
  Feature Rollout and a registry key for side-by-side testing; retail in November 2026 with Phi Silica
  removed; no LAF token. So `PhiSilicaBackend` is the production Aion Instruct path and the preview
  SDK adapter is a stopgap. When the swap reaches this machine, the work is a `--backend phi-silica`
  smoke run under the registry key and a re-check of D31, not the Aion adapter. Windows App SDK
  2.4.8-experimental metadata carries no `Aion` identifier (`memory-bank/techContext.md`).
- **Windows App SDK 2.4.x has structured JSON output and 2.4.8-experimental has prompt compression.**
  `LanguageModel.GenerateStructuredJsonResponseAsync(..., jsonSchema)` is in the stable Text metadata
  of both 2.4.4 and 2.4.8-experimental (a design option for chunk 7's tool calls on Phi Silica);
  `LanguageModelExperimental.CompressPromptAsync` with `PreferredRetentionRatio` is experimental-only
  and needs a bump from the 2.4.1-experimental the exe references (an alternative to dropping turns
  under `--truncate-history`, Phi Silica only). Both noted on issues #3 and #1.
- **Cancelling really stops the NPU.** Measured on the streaming path with the cut: an early cut
  ended the request in a fraction of the uncut time, and no request is answered until its generation
  has ended. So `Cancellation` is a real capability on Phi Silica, and the cancel → drain → dispose
  order (D51) is what makes disposing the context afterwards safe.

### Protocol rules

Live today:

- **Wire shapes follow OpenAI's schema (D77).** `model` is required and must be the served id (any
  other is a 404 `model_not_found`; the reply always carries the served id); `choices[].logprobs`,
  `message.refusal`, every streamed choice's `finish_reason` and every error's `param` and `code` are
  written as explicit nulls when unset; with `include_usage` every chunk before the usage chunk carries
  `"usage": null`; `temperature`, `top_p`, `n` and `stream_options` are range-checked as the schema
  states. `OpenAiConformanceTests` pins each rule.
- Errors use the OpenAI body `{"error":{"message","type","param","code"}}`, all four keys always present. An over-length
  transcript is HTTP 400 with code `context_length_exceeded` (from the preflight before any
  generation on Phi Silica, from the generation's status on a backend without one), and nothing is
  silently truncated. `--truncate-history` is the only switch that may drop turns instead: it removes
  the oldest exchange (every turn up to the next user turn, tool calls and results included) until the
  transcript fits, never the message being answered, logs each drop at Warning, and adds
  `x-npu-bridge-truncated-turns: N` (turns dropped) to the response once a generation is attempted on
  the truncated transcript; the 400 refusal carries no header. On a stream that had already sent
  a keep-alive when a status-driven truncation happened, the header cannot be sent and the log says so.
- **Native system-text guard (D97).** During preparation, before the scheduler, the bridge refuses native system text when it exceeds 32,000 characters or a backend's known usable context window in tokens. It uses the same 400 `context_length_exceeded` envelope, takes no queue slot, does not create a context, and does not retry with `--truncate-history`; folded placement remains governed by the normal preflight.
- **The context cache** (D71, D72): a request whose transcript extends a cached prefix (ending in an
  assistant turn, longest match wins) generates on that context with only the tail rendered, in the
  marker format; a context goes back in only after a `Complete`, uncut generation, under the key of
  the transcript plus the reply. `usage.prompt_tokens` estimates the whole transcript on a hit and a
  miss alike; the log line's `prompt_chars` is what was sent, and it also carries `cache=hit|miss`,
  `tail_turns=N` and `truncated_turns=N`. `/healthz` reports `contexts_cached`,
  `context_cache_capacity`, `context_cache_hits`, `context_cache_misses`, `last_generation`,
  `consecutive_backend_faults` and `context_window_tokens`. It returns `503 degraded` after two consecutive backend faults while
  still admitting requests, so a later successful generation can clear the state.
- Token counts in `usage` are the backend's counter's (D80): Phi-3 tokens on Phi Silica, `ceil(chars/4)`
  on Aion and the fake (D44). `prompt_tokens` counts the whole rendered transcript plus the native
  system text, the same on a hit and a miss; on a stream, `completion_tokens` counts the text the
  cutter actually released, not the backend's returned text.
- **Streaming** (`stream: true`): one `chat.completion.chunk` per cutter release, the first carrying
  `role: "assistant"`, `finish_reason` on the last real chunk, an optional `usage` chunk with empty
  `choices` when `stream_options.include_usage` is set, then `data: [DONE]`. The headers are committed
  by the first frame, which is a `: keep-alive` comment after about 1 s or the first delta, whichever
  is first (D52). A failure before that is the ordinary HTTP status and JSON body, so a streamed
  request that fails validation, readiness or the first generation step is a plain 400/502/503. A
  failure after it is a `data: {"error":...}` event with the identical envelope, then `[DONE]`.
- **The client-side cut** (D53, D80): `max_tokens`/`max_completion_tokens` (the smaller wins) is a
  budget in the backend's counter's tokens, cut at the counter's index for that many tokens over
  everything generated (exactly `cap * 4` characters under chars/4); `stop` strings are excluded from
  the output; the generation is cancelled at the cut. A stream holds back `longest stop - 1`
  characters so a stop string split across deltas is never leaked, and neither the holdback nor the
  budget may split a surrogate pair (D58). With a BPE counter the stream also holds everything after
  the last whitespace boundary once within 8 tokens of the budget, because a later merge can move
  the budget's index (a run of one character retokenizes from its start); a whitespace-free reply is
  cut exactly at the end, and the model is stopped once the text runs 8 tokens past the budget
  (`OutputCutter.StopRequested`). `completion_tokens` is the tokens of the generated text that cover
  what was delivered (`ITokenCounter.TokensCovering`), never the prefix counted on its own. Only a `Cancelled` status
  may be reinterpreted by a cut; any other failure status is still a failure (D56), and a filtered
  reply outranks the cut. Both shapes cut through the same `OutputCutter`, so the same text always
  yields the same reply and finish reason.

- **Tool calling is emulated (D83).** With `tools` present and `--tool-emulation on` (the default), an
  instruction block naming the tools in a compact signature form (`--tool-schema compact|full`) is
  appended to the **system text** — which is what puts it in the conversation key, so two requests
  offering different tools cannot share a context. Offering no tools, `tool_choice: "none"` and
  `--tool-emulation off` all disable it by one path, a null catalog, and a disabled request keys
  identically to one that never mentioned tools. The streamed shape **buffers the whole reply** before
  emitting anything, sending `: keep-alive` comments meanwhile, because only a finished reply can be
  told from prose; then one chunk carrying the whole `tool_calls` array, then the finish chunk.
  Because the block lands in the system text, it is also what makes a real agent client unusable here
  and what can crash the model host: measured, a terminal agent's 25 tools render far past the window
  and past the ~44,000-character boundary at which `CreateContext` fail-fasts (D93, D94, issue #29).
  Any change that makes the rendered block larger is a change to that hazard, not just to a prompt.
  A parsed call is `message.tool_calls` with `content: null` and `finish_reason: "tool_calls"` — except
  that a `max_tokens` cut which still parses reports `length` (the client gets the calls *and* the
  truth that the text was truncated), and `index` is written only on the streaming shape, per OpenAI's
  schema. Unknown tool names are surfaced, not filtered: the client decides.
- **`ToolCallParser` never throws, and a false positive is worse than a miss** — the client's answer to
  a call is to run it. Anything it cannot read is content. Its rules exist because each was violated:
  an object that never declared itself a call needs both `name` and `arguments` (a fenced
  `{"name":"Ada"}` became a call to a tool named Ada); `parameters` is accepted only inside a
  `tool_calls` wrapper (outside one, `name` + `parameters` is the tool *definition* echoed back);
  arguments supplied and unreadable drop the call rather than defaulting to `{}`; and a bare array
  does not declare its elements. Its tests are the main regression guard for the feature — add to them
  before changing it.

- **`POST /v1/completions` is the legacy shape over the identical pipeline (D91).** The `prompt` is
  wrapped into one user message and joins the chat flow at the model-id check, so readiness, placement,
  rendering, the cache, the scheduler, the cut and `GenerationOutcome` are all shared. The wire is
  `object: "text_completion"` with `choices[].text`; `finish_reason` is `stop`, `length` or
  `content_filter` and never `tool_calls`, because `tools` does not exist on this endpoint at all.
  Four rulings PLAN left open: a multi-element `prompt` array is a 400 `invalid_request_error` (a
  single-worker scheduler cannot serve OpenAI's several-choices batching, so it refuses rather than
  silently answering the first element); the id keeps the `chatcmpl-` prefix rather than `cmpl-`,
  since every checked consumer treats it as opaque; the legacy-only parameters `echo`, `best_of`,
  `suffix`, `logprobs` and `logit_bias` are accepted and warned through the shared ignored-parameter
  mechanism but never implemented (`echo: true` not prepending the prompt is the one a real client
  notices); and the streamed shape commits its headers at the first *cutter release*, because it has
  no role chunk to send ahead of the text.
- **A queue-full request is 429 with `Retry-After`** (whole seconds, queue depth × rolling mean
  generation time, floored at 1) and the OpenAI body's `type` is `rate_limit_error`, `code`
  `queue_full`. `Retry-After` is written only while the response has not started; on an already-started
  stream it is silent by necessity. A 429 carries no truncated-turns header.

Chunk 8's rules, now live (D84 to D92):

- **The scheduler serializes the model handle, not just the generation (D84).** One worker reads a
  bounded `Channel<GenerationJob>`; `ConversationSession.Acquire` runs inside the scheduled closure
  because `CreateContext` and `GetUsablePromptLength` are calls on the one shared handle exactly as
  `GenerateAsync` is. A `--truncate-history` retry keeps its slot instead of re-queueing behind newer
  arrivals (D86), so `Retry-After`'s rolling average measures a whole attempt, not one generation.
- **The lease is published from inside the closure (D85)**, never read off the scheduled task's return
  value — otherwise a client that vanishes mid-frame unwinds before the task is unwrapped and the
  `finally` never releases the context, defeating D43 and D51 at once.
- **`queue_depth` is a live counter, not `Reader.Count` (D87).** It drops at whichever comes first of
  the caller cancelling while queued or the worker dequeuing, so a client that enqueues, gives up and
  retries cannot inflate `Retry-After` for a whole generation. The channel *slot* is still held until
  drained, so a burst of aborted clients can still 429 a live one.
- **A queued job that never ran is 503 `queue_shutting_down`; one that ran and threw its own
  `OperationCanceledException` is a 502 through `GenerationFailure` (D88).** `ScheduleResultKind.Cancelled`
  carries `Ran` to tell them apart, because the second is a backend contract violation and must not be
  swallowed as a cheerful shutdown message.
- **D52's first-frame boundary now has a queue wait inside it (D89).** A preflight refusal or a
  queue-full rejection that used to be a clean 400/429 can surface as an SSE `data: {"error":...}`
  event once the wait exceeds about a second. Accepted deliberately: holding the first keep-alive for
  admission would reintroduce the "client sees nothing" failure D52 exists to prevent.
- **Publish-before-arm is the scheduler's recurring bug shape (D92).** `ScheduleAsync` writes to the
  channel before finishing the state that job needs, and the worker can dequeue, run and settle inside
  that window; it produced the leaked cancellation registration and the stuck `_liveQueueDepth`. Arm
  before the write, and use `Interlocked` on *both* sides — the two flags are a Dekker pair, and ARM64
  permits the store-buffer reordering that plain volatile release/acquire does not close. Its
  regression test is probabilistic (~60 % catch rate); issue #24 wants a deterministic one.

## Code navigation: use serena

The serena MCP server is installed and its C# language server works against this solution. Reach for
its symbol tools before reading files whole, and for its reference-aware edits before hand-editing a
symbol:

- `get_symbols_overview` on a file, then `find_symbol` with `include_body` on the one symbol you
  actually need. Reading a 700-line endpoint file to change one method is waste.
- `find_referencing_symbols` before changing any signature. This repo's shared pieces
  (`GenerationOutcome`, `DeltaSink`, `ChatRequestPreparer`, `ILanguageModelBackend`) have callers on
  both response shapes, and missing one is how the D56/D57 drifts happened.
- `replace_symbol_body`, `insert_after_symbol`, `rename_symbol`, `safe_delete_symbol` for
  structure-aware changes; `replace_content` for a few lines inside a larger method.
- `search_for_pattern` to locate candidates when you do not know the symbol's name yet.

Where it does not help: the prose docs (`docs/*.md`, this file, `memory-bank/`), `scripts/smoke.ps1`
and the other PowerShell, and any file you are about to rewrite in full. Use the ordinary tools there.

Two traps. **Serena reports 0-based line numbers**; every other tool here is 1-based, so convert
before quoting a location into a commit message or an issue. And serena's own memories
(`.serena/memories/`) are an index into the documents named above, not a second source of truth — when
a memory and `docs/DECISIONS.md` disagree, the decision record wins and the memory is the thing to fix.

`.serena/` is gitignored: the project config is per-machine and the language-server cache is large.
If the server fails to connect, the cause on this machine is the interpreter, not serena — uv picks a
Python that PyYAML ships no wheel for and the source build then wants MSVC. The plugin's `.mcp.json`
pins `--python 3.12` for that reason.

## Working method for this repo

Each chunk runs in this order: build and tests green, then an adversarial review (correctness, OpenAI
spec, streaming races, `IDisposable` leaks, untested branches), then fix the in-scope findings and file
the out-of-scope ones in `docs/FUTURE.md`, then append to `docs/DECISIONS.md`, then a whole-branch
review, then a fast-forward merge to `main`. Do not widen a chunk to absorb review findings.

After the merge, in the same session: update the status paragraph at the top of this file, the chunk
table in `docs/PLAN.md`, `docs/SESSION-HANDOFF.md` and `memory-bank/`. A merge without this leaves the
next session working from the wrong state.

Work is tracked in GitHub issues (`gh issue list`). Each remaining chunk has an issue labelled `chunk`
carrying its scope, the decisions that constrain it, its known blockers and its definition of done;
defects and cleanups from reviews are issues labelled `bug` or `tech-debt`. Read the issue before
starting the work, put context a later session will need into the issue rather than only into chat,
and close it from the merge commit (`closes #N`). `docs/FUTURE.md` stays the long-form record of why
something was deferred; the issue is the work item. Sidequest (project-scoped plugin, enabled in
`.claude/settings.json`) is the in-flight board: a ticket holds the dispatch, verification and review
loop for one issue while it is being worked, and the GitHub issue stays the public record that opens
and closes the work.

## Delegate to subagents

Run the work in subagents and keep this session for the decisions. What to hand off: a `dotnet build`
plus `dotnet test` pass, a `scripts/smoke.ps1` run, a survey of the code a chunk is about to touch,
each task's implementation, and every review. What not to hand off: the rulings. When a review
contradicts the plan, or two requirements disagree, the session driving the work decides and records
why — a subagent that decides for itself leaves no trace of the choice.

- **Every review is a separate agent that did not write the code.** This is what the working method's
  "adversarial review" means in practice, and it earns its cost: chunk 8's review found that the
  brief's own ordering instruction contradicted PLAN §2.7 and would have shipped the scheduler
  serializing generation while `CreateContext` still raced. The author could not have found that,
  having implemented exactly what it was told.
- **An implementer never spawns its own reviewer.** It reviews the code it just argued itself into.
  The review that counts is the one dispatched after the report.
- **Never run two implementers at once on one branch**, and never build while a smoke server holds the
  exe — that applies across agents as well as within one.
- **Hand work over as files, not pasted prose.** A brief, a report and a diff on disk keep long tool
  output out of this session's context and survive a compaction; the same text pasted into a prompt is
  re-read on every later turn.
- **Name the model on every dispatch.** Omitting it inherits this session's, which is usually the
  expensive one.
- **A subagent's report is a claim, not evidence.** Require the command and its actual output, and
  treat "all tests pass" without a summary line the way `CLAUDE.md` already treats an NPU claim without
  a smoke run. Reports have been wrong; the ones with pasted output have not.
- Resume the same implementer for the first fix rounds — its context is intact and it knows why it made
  the choice being questioned. Switch to a fresh agent on a stronger model only when a finding survives
  three rounds, which usually means it cannot see its own problem.

