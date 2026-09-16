# Future work and deferred findings

Things deliberately not done yet. Adversarial-review findings that fall outside the chunk under review
land here instead of widening the chunk. Each entry says where it came from and why it waits.

## Product scope (from the plan)

- **x64 backends.** Both frameworks are ARM64-only today; Microsoft says x64 Aion is "coming soon".
  When it lands: add `win-x64` to `RuntimeIdentifiers`, lift `Platforms`, and re-check
  `FrameworkDependency`'s architecture flag. No x64 hacks before then.
- **Structured output for tool calling.** Phi Silica (Windows App SDK 2.0) has
  `GenerateStructuredJsonResponseAsync(prompt, schema)`. Constraining the tool-call reply to a schema
  would remove most parser failure modes. Biggest reliability upgrade available; Aion lacks the API.
- **Speculative streaming with tools.** Hold tokens only while the prefix could still be a fenced/JSON
  tool call; flush the moment it cannot be. Better prose latency; more edge cases. Decided against for
  v1 (DECISIONS D10).
- **Embeddings endpoint.** Phi Silica exposes `GenerateEmbeddingVectors`; `/v1/embeddings` is a small
  wrapper. Aion has no equivalent.
- **Context TTL / idle expiry** in the context cache, in addition to LRU.
- **Per-build context-window discovery.** Phi Silica currently exposes the D80-measured 3,581-token usable window as a backend constant for D97's native-system guard. A future initialization probe could create an empty context, call the preflight on long ASCII input, and convert the result with the backend counter. It needs hardware validation and must never use an unsafe system prompt.
- **Listener auth** (bearer token) for anyone who binds beyond localhost.
- **Content-filter option pass-through** (`ContentFilterOptions` on Phi Silica).
- **LoRA adapters** (`LanguageModelOptions.LowRankAdapter`).
- **Multiple model ids per process** (e.g. serve both backends at once, one model each).
- **Aion 1.0 Plan backend.** Announced at Build 2026 (2026-06-02): 14B parameters, 32K context, native
  tool calling and reasoning, "in-box on capable devices in the coming months". On 2026-09-15, a secondary
  WindowsForum report described October 1 as the first sideloadable Aion Instruct testing package, October 23
  as an Insider rollout under a Controlled Feature Rollout with a registry override whose path and velocity
  key remain unpublished, and November 24 as the retail date with Phi Silica removed. The July open-weights
  release was not visible through the Hugging Face API, and no SDK or preview package exists; the sample
  repository still has only Aion Instruct v1.0.0.0. The October 1 package is the first date this machine can
  run an Aion generation, but it is unknown whether it will use the in-box workload host and avoid D70 or
  use Windows ML. When it lands, the model needs a ToolCalling capability that bypasses chunk 7's emulation
  and a per-backend context-window hint. Tracked as a GitHub issue.

## 2026-09-15 smoke wave deferrals

- **F3: existing local settings skip the cache assertion.** The gap came from the whole-branch review's F3 finding on smoke.ps1: the guard correctly avoids overwriting the LAF token file, but the step skips checking context_cache_capacity when appsettings.local.json already exists. It waits because the current hardware run had no existing file and proved the write branch. The reviewer's proposed fix is to read the existing file, use its ContextCacheSize or the default 4, and assert the reported capacity without writing.
- **F5: identity-step teardown can mask the assertion.** The review's F5 finding showed that a throw in the identity step's finally can replace the real failure, while an unguarded stop has the same narrow race as F4. It waits because the dedicated teardown row already records Stop-AuxServer outcomes and the hardware run did not hit the masking path. The reviewer's proposed fix is to record teardown as its own result instead of throwing from finally.
- **F7: relative JSON output resolves against the process directory.** The review's F7 finding showed that -JsonOut uses Environment.CurrentDirectory, so a relative path can land elsewhere and a missing directory can terminate the script before its verdict line. It waits because documented examples use absolute paths and the current run wrote a valid summary. The reviewer's proposed fix is to resolve the provider path explicitly and catch write failures so the verdict still prints.
- **F8: the help check can match prose.** The review's F8 finding showed that the --help assertion searches for bare verbs and can pass when only descriptive prose contains them. It waits because the current executable's help output passed and the issue is a narrow false-positive guard. The reviewer's proposed fix is to anchor the match to npu-bridge service or npu-bridge task usage lines.

## Chunk 8 deferrals (concurrency scheduler and `/v1/completions`)

- **`/v1/completions`'s request id uses the `chatcmpl-` prefix**, not OpenAI's `cmpl-`: it is allocated
  by the same `ChatCompletionId.NewId()` chat and `/debug/generate`'s log lines use, and giving
  completions its own prefix would mean threading a per-endpoint prefix through
  `ChatRequestPreparer.PrepareCoreAsync` for a cosmetic difference no test in `docs/CLIENTS.md`'s
  audience depends on. Worth revisiting if a strict OpenAI client ever parses the `id` shape.
- **`/v1/completions`'s legacy-only parameters are warned about, not implemented (fix round 1, finding
  5).** `echo`, `best_of`, `suffix`, `logprobs` (the legacy integer form) and `logit_bias` now reach the
  same once-per-process `IgnoredParameterLog` warning every other accepted-but-ignored parameter uses
  (`ChatRequestPreparer.PrepareCoreAsync`'s new `extraIgnoredParameters` parameter, since none of the
  five has an equivalent field on `ChatCompletionRequest` to ride along on chat's own list); `seed`,
  `presence_penalty`, `frequency_penalty` and `user` ride that shared list directly, since their fields
  already exist there. None of the five legacy-only ones does anything: `echo: true` still does not
  prepend the prompt to `text`, which is the one a real client is most likely to notice, now with a log
  line explaining why rather than a silent difference. Implementing it for real is a small, self-
  contained change to `CompletionsEndpoint`/`CompletionsStreamEndpoint` (prepend `prompt` to the first
  chunk of text, or to the whole reply on the JSON shape) whenever it is worth a task of its own.
- **`StreamingPipeline.WaitForDeltaAsync`'s stale-timeout guard (`if (wait.IsCompleted) return await
  wait;`) has no dedicated test**, on either streaming shape (task 3b review, fix round 1, Finding 2).
  It is not intrinsically untestable: `Task.WaitAsync(TimeSpan, TimeProvider, CancellationToken)` plus a
  `FakeTimeProvider` would let a test complete the channel first and then advance the clock so the timer
  fires with `wait` already complete -- the guard's exact case, deterministically, with no wall-clock
  assertion (D54). That needs `_time` threaded into `WaitForDeltaAsync` in place of the real clock
  `Task.WaitAsync` uses today, which is a production change and so out of scope for a coverage-only
  task. A genuine client-disconnect mid-stream is covered on both endpoints as of task 3b (fix round 1).

## 2026-09-13 wave `leftovers` (issues #19, #22, #25, #26, #34, #35)

Deferred from the two candidate reviews and the whole-branch review of that wave (D100 to D102). None
blocked the merge.

- **A bridge throw before classification leaves `/healthz` stale** (issue #34 review, note F7). The
  fault tracker (D102) records only backend calls, and `GenerationOutcome.Classify` runs before the
  usage tokenizer, so a good generation followed by a tokenizer throw still records `ok`. But a throw
  in the window before classification, the raw-output log under `--verbose`, the cut, the cutter's
  own accept, or an SSE frame write, records nothing, so a `/healthz` that was already `degraded`
  stays so although the backend answered. Defensible: the request did fail. Undocumented on the wire
  and untested; a `bridge_error` outcome value would make it visible.
- **`duration_ms >= 0` proves nothing, and the cancellation-path duration has no test** (F3). The
  assertion in `GenerationSchedulerEndpointTests` is unfalsifiable because `GenerationHealth` floors
  the value at zero; the `BackendThrewCancellation` duration argument is reachable only on a scheduler
  shutdown that lands mid-generation, which no test drives. Pin it with `ManualTimeProvider` when the
  attempt duration moves onto the scheduler result (next item).
- **Attempt duration is plumbed four times when the scheduler already measures it** (whole-branch
  review, finding 5). Each endpoint keeps a stopwatch, a try/finally and a captured double that the
  shared helper reads back through a delegate, while `GenerationScheduler.RunWorkerAsync` measures the
  same interval with the injectable `TimeProvider`. Exposing the measured duration on `ScheduleResult`
  beside `QueueWait` would delete all of it and make the health duration testable from one place.
  Deferred because it is a refactor across four endpoints for a path that works and is tested; do it
  when the next change touches those closures anyway.
- **`BackendCallTracker.Caught` is last-exception reference equality** (F5). If the guarded
  `attemptLease.Dispose()` in the inner catch throws, its exception replaces the backend's and a real
  backend fault goes unrecorded. Needs a context whose `Dispose` throws; very narrow.
- **`DeltaGate` ignores cancellation and its counters are backend-wide** (F8). A test that times out
  before releasing the gate parks the fake with nothing able to release it, and `DeltasEmitted` and
  `CancellationsObserved` are per backend, not per generation, so the gate's waits are deterministic
  only while exactly one generation is in flight. Fine for the tests that use it today; a second
  in-flight generation in one of them would need per-generation counters.
- **The smoke's D80 window cross-check tolerates 2 %, not one token** (whole-branch review, finding 8,
  second half). Issue #35 item 3 widened the D80 cross-check from an exact match to the three-text
  [min, max] bracket widened by 2 %, because the step already tolerates that spread between its texts
  and a healthy machine could otherwise fail on punctuation drift. The cost is that up to about 70
  tokens of drift in `ContextWindowTokens` would pass. Kept on purpose; the token-window guard probe
  restored by SQ-28 is the check that the reported window actually governs a refusal.
- **One ticket per logical change** (finding 7). Commit 5d32b93 (issue #26) carries a refactor, a
  wire-visible behaviour change (`Retry-After` smoothing), two runtime fixes and a docs pass, because
  the issue listed five items and the ticket took the issue whole. A merge delivery cannot split it
  afterwards. Next wave: an issue with a behaviour change and a refactor is two tickets.
- **Two more wall-clock tests, the debug endpoint's mid-generation abort, and four notes from the last review** (SQ-27's review). `ChatCompletionsTests.Client_disconnect_disposes_the_context_and_does_not_500` and its `DebugGenerateTests` counterpart are still on `TokenDelay` and lack `AssertNoLeak`; the two `DeltaGate` tests do not release the gate in a `finally`, so a regression costs about 55 s per case instead of 10. A client that aborts mid-generation on `/debug/generate` still gets a fully built JSON body written into a dead connection where the other shapes return the empty result. `cancelledByCut = !watcherFaulted` in `JsonPipeline` is inert (a stored bridge fault always throws). `SchedulerOutcome.BackendThrewCancellation` is unreachable through every endpoint, because the scheduler's cancellation token is always the request's own, so the `health` argument passed to `FailureFor` at three call sites is dead. A non-conforming adapter that throws for the bridge's own cancel is recorded as a backend fault (D82/D88's choice). The bridge fault is rethrown without `ExceptionDispatchInfo`, so the log points at the pipeline. All small; the tests belong with issue #14.
- **The smoke's readiness loops treat a slow `/healthz` as a failure** (observed 2026-09-13 on the queue-full step, one run in four). Both loops poll with a 5-second client timeout and catch only `HttpRequestException`, so a poll the bridge answers slowly during model load escapes the loop long before the 600-second deadline. Filed as a scripts ticket in the wave; if it is still open when you read this, it is the fix for a `HttpClient.Timeout of 5 seconds` failure on any step that starts an auxiliary server.

## 2026-09-12 wave leftovers (issues #29, #30, #31)

- **An empty `tool_calls` fence reaches the client as content** (issue #33, D99). On the second turn
  of a tool round trip the model answered with a fenced `{"tool_calls": []}` and nothing else;
  `ToolCallParser` correctly treats an empty array as no call, so the client got the fence as prose
  with `finish_reason: stop`. Options: strip a reply that is nothing but an empty fence (hides what the
  model did), or document that clients treat it as "no answer" and re-ask. Either way the tool block's
  instructions could tell the model to answer in prose when no tool applies; one `tool-probe.ps1` cell
  would measure whether that removes it.
- **`/healthz` fault attribution leftovers** (issue #34). Resolved 2026-09-13 by D102 and its
  follow-ups; what remains is in the 2026-09-13 section above.
- **Serena is unusable by concurrent worktree executors** (observed 2026-09-12 during this wave). The
  serena MCP server is one process per session with one active project; three Sidequest executors in
  separate worktrees all edited through it and every edit landed in whichever worktree had activated
  serena last. Until the upstream fix, dispatch briefs forbid serena in executors; this session's
  orchestrator still uses it for reads. Worth an upstream report on the Toolshed repository.

## 2026-09-11 test coverage audit

Three subagent audits (options against tests, the uncovered lines of a coverlet report, the smoke
script) on the day chunk 5 merged. Core: 94.2 % lines, 89.8 % branches over 496 tests; the exe
project has no unit coverage by construction. The findings are work items: the unit and TestServer
gaps, the smoke script's vacuous steps and missing hardware checks, and a CI job that runs the exe
with the fake backend (blocked on an ARM64 runner). See the three `tech-debt` and `enhancement`
issues filed that day. `coverlet.collector` is now in the test project; run
`dotnet test --collect:"XPlat Code Coverage"` for the report.
The first six unit tests and the smoke script's first three items landed on 2026-09-11 (D79); the
rest of both issues stays open, as does the CI job.
- **Recreate the model after a runtime RPC fault.** `/healthz` now reports `degraded` after two
  consecutive backend faults, which is the measurable trigger for evaluating recovery. Do not recreate
  automatically yet: D94 records a crash-induced wedge that self-healed after several minutes and a
  separate sustained RPC wedge that only a process restart cleared. Recreating the shared model handle
  could prolong the first case, and it is unknown whether the two episodes are one fault. Revisit after
  more evidence establishes a safe recovery policy and whether to retry the request that found it.
- **Keep-alive waits driven by the injected `TimeProvider`.** `WaitForFirstDeltaAsync` times its wait
  on the wall clock, so a test can prove a non-positive first delay is accepted but not what it falls
  back to (zero or any non-negative span would pass), and the disabled-interval test's zero row leans
  on the timeout elapsing before the first delta reaches the channel, which is near-certain rather
  than guaranteed. A fake time provider would let both tests pin the exact delay requested (from the
  D79 reviews). Amended 2026-09-11 (D81): the wait is now `Task.WaitAsync(TimeSpan, CancellationToken)`
  rather than `Task.WhenAny` against a `Task.Delay`, so the change is to the
  `WaitAsync(TimeSpan, TimeProvider, CancellationToken)` overload; the deferral itself is unchanged.

## Chunk 7 deferrals (tool-call emulation)

- **Speculative streaming with tools.** PLAN §2.6 item 2 requires the whole reply to be buffered when
  `tools` is present, because nothing can tell a call from prose until the model has stopped, and D10
  decided against speculation for v1. The cost is real: a tool-using client sees no token until the
  reply is complete, where a tool-free one sees the first in a few hundred milliseconds. "Stream until
  it stops looking like JSON" — emit content while the text cannot be the start of the envelope, and
  retract to buffering the moment it can — would recover most of that. Retraction is the hard part: a
  delta already written cannot be recalled, so the point of no return has to be provably before the
  first character a call could begin with.
- **`GenerateStructuredJsonResponseAsync` instead of the tolerant parser, on Phi Silica.** Windows App
  SDK 2.4.x carries structured JSON output in the stable Text metadata, which would make the model
  emit the envelope by construction rather than being asked to and then read leniently. Phi Silica
  only — Aion has no equivalent — so the parser stays either way and the two paths would have to agree
  on every shape. Worth measuring against the 20/20 the instruction-plus-parser approach already gets
  before adopting it.
- **A zero-argument call without the wrapper is content** (issue #22, D83). Resolved 2026-09-12 by
  D101: a bare `{"name":…}` naming an offered tool, with no other key, is a call; everything else
  stays content.
- **Compliance on the hard case was measured on 2026-09-12** (issue #21, D93 to D96). PLAN's pessimism
  did not survive: 40/40 across 1 to 25 tools, 32/32 at up to 85 % window occupancy under deterministic
  sampling, and no argument hallucination. The hard case fails for a different reason — a real agent's
  tool schemas alone are nearly three times the window — and `--tool-schema full` and structured JSON
  output are both ruled out as a result. What remains deferred from that work is issue #31: every probe
  request was `stream: false`, so D83's buffered streaming branch and the tool-call cache round trip are
  still unmeasured on hardware.

## Chunk 5 deferrals (context cache and overflow handling)

- **After a truncation, the next request in that conversation pays refused preflight rounds before it
  hits (corrected 2026-09-11, D78).** This entry first said every later request misses and replays.
  It does not: the truncation loop drops the same exchanges again and the shortened transcript's own
  prefix keys are the stored key, so the follow-up hits the truncated context with only the new turn
  rendered. `TruncationTests.The_turn_after_a_truncation_finds_the_truncated_context_without_a_replay`
  pins it. The remaining cost is one fresh context, one preflight and one disposal per exchange dropped
  before the hit, milliseconds on Phi Silica. A suffix lookup on the first pass was considered and
  rejected: it would match a cached shorter conversation against a longer one that still fits, and
  truncate it for nothing.
- **Mixed formats on a hit after a raw first turn.** The common `curl` case sends one bare user
  message, which is passed through raw; its continuation is rendered as a marker-format tail on the
  same context, so the model sees a raw string followed by `### Conversation so far`. The smoke test
  measures that the continuation answers; whether quality differs from a replay is unmeasured. If it
  does, the fix is to render the first turn with markers whenever caching is enabled, which costs a
  little quality on the single-message case the D-series measurements were taken on.
- **Sampling parameters are not part of the key.** A conversation continued with a different
  `temperature` hits the context its earlier turns built. That is right: the context holds text
  rather than sampling state, and Phi Silica takes the options per generation. It is still a fact a
  reader of the key should not have to infer.
- **`prompt_tokens` on a hit counts the whole transcript, not what the runtime holds.**
  What the runtime actually keeps in its context after several turns (and whether it compacts) is not
  observable through the API; the number is the backend's count (Phi-3 tokens since D80) over the
  transcript the client sent. `CompressPromptAsync` (2.4.8-experimental, Phi Silica only, issue #1's comment) is
  the one lever if the runtime's window turns out smaller than the transcript suggests.
- **`ChatMessage.ToolCalls` is carried and keyed but not rendered.** Chunk 7 owns rendering the
  model its own tool-call protocol. Until then an assistant turn with only tool calls renders as an
  empty turn in the prompt while keying as a distinct one: the cache cannot collide, which is the
  half to have first, and the model does not see the call, which is the half left to do.
- **A status-driven truncation after a keep-alive loses the header.** Only reachable on a backend
  without a preflight, on the streaming path, when the verdict takes longer than the first keep-alive
  (about a second). The reply is still right and the Warning says the header was lost. A trailer or
  a chunk extension could carry it; neither is standard for OpenAI clients, so it waits for a need.
- **The pressure warning cannot fire on Phi Silica at the default hint.** `--context-window-hint`
  defaults to 4,096 tokens, so the warning threshold is nine tenths of 16,384 characters; the
  preflight refuses at about 13,400 (D55, D75), below that. Both chunk 5 reviewers noted it. The
  warning is real on a backend without a preflight (Aion) and for any hint set below the measured
  window; the default stays because the option is a hint of the model's advertised size and the
  preflight is the measurement. Lowering the default to about 3,300 tokens would make the warning
  fire first on Phi Silica, at the cost of a number that looks wrong next to the model's own.
- **Eviction disposes on the storing request's thread.** A runtime `Dispose` that blocks would
  delay that request's final bytes. Not observed on Phi Silica; noted so a future slow disposal is
  looked for here first.
- **The same-conversation concurrency test exists because nothing serializes requests yet.** Chunk
  8's scheduler will queue the second request behind the first, at which point it could wait for the
  first's context instead of missing. That is an optimisation for chunk 8 to consider; nothing is
  wrong today.

## Chunk 6 deferrals (Aion Instruct Preview adapter)

- **Hardware verification of `--backend aion` is blocked on this machine, not on the code (D68).**
  The adapter resolves both dynamic dependencies, the SDK loads its WinML stack and picks the QNN
  execution provider, and `LanguageModel.CreateAsync` then fails in the first-run NPU compile
  (`CacheApi::CreateCache failed: InvalidCache`, per-model `InvalidData`). The SDK's own debug line
  before that is `TryRegister returned false for WinML EP: QNNExecutionProvider`, and the reason is
  that every DLL in the `MicrosoftCorporationII.WinML.Qualcomm.QNN.EP.1.8` package fails `LoadLibrary`
  with `E_ACCESSDENIED`, even from a process that has the package in its dependency graph and even though
  the files read fine. The Aion framework's own DLLs load from the same `WindowsApps` root without
  trouble. Root cause, pinned later the same day (D70): the provider is a main package that opts
  in as a dynamic-dependency target, the OS accepts the dependency (`TryCreatePackageDependency` and
  `AddPackageDependency` both succeed from a plain process), and the build still refuses to map the
  package's DLLs as images (error 5) from that process. Developer Mode was turned on and changed
  nothing; the ACL, signatures, Smart App Control, AppLocker, Defender and the driver are ruled out.
  Also tried without effect: the sample's own `AcquireQnnEp` tool (exit 5, `TryRegister` failed, the
  same access-denied loads), and running the backend inside the sparse-package identity. Left to try:
  remove and re-acquire the two provider packages on this build, or a different Windows build. When
  a build works, the sample's `scripts/Diagnose-AionInstructPreview.ps1` is the reference for what a
  healthy machine reports, and its OutputDebugString capture is the way to read the SDK's EP decision.
  Everything that depends on a generation stays unmeasured: load time, TTFT, tok/s, system-prompt
  adherence under folded placement, whether cancel stops the device, and the over-length verdict.
- **`BackendCapabilities.Cancellation` is not advertised on Aion** until the cut measurement earns it.
  The pipeline reads the flag nowhere yet (chunk 4 deferral above), so this changes no behaviour; the
  point is that the adapter claims nothing the smoke test has not shown.
- **The third overflow behaviour is still unknown.** Aion's status enum does have
  `PromptLargerThanContext`, unlike what Phi Silica returns in practice (D55), so it may be the one
  backend that reports overflow honestly, or it may fail generically like Phi Silica. Chunk 5 built
  the `--truncate-history` loop for a backend with no preflight to retry on a `PromptLargerThanContext`
  status (D73); whether Aion ever reports that status is what the measurement will tell, and the fake
  is the only backend that has exercised the path.
- **No setup script for the Aion prerequisites.** Getting to a first run took: the framework MSIX from
  the sample repo's release (`Add-AppxPackage`, user scope, no elevation), the SDK NuGet into
  `nuget-local/`, and the QNN execution provider 1.8 package, which is not on the machine by default and
  is acquired by the sample's `tools/AcquireQnnEp` (its `EnsureReadyAsync` downloads and installs it;
  the tool targets .NET 9, which is not installed here, so a .NET 10 retarget was built in a scratch
  folder). A `scripts/aion-setup.ps1` doing those three steps would save the next machine an hour. Not
  written now because the third step's result does not work here and a script that installs something
  unusable would mislead.
- **The sample's unpackaged console could not be built as a control experiment.** With CsWinRT 2.3.1
  and the NuGet-delivered Windows metadata, the `AionInstructPreview.Text` namespace was not projected
  in a fresh console project even with `CsWinRTIncludes` set, while the same package reference projects
  fine inside `NpuBridge.csproj` (which also references the Windows App SDK). Worth understanding before
  anyone else copies the reference into a new project.
- **The exe's Windows App SDK 2.4.1-experimental reference and Aion's Windows App Runtime 1.8
  dependency coexist untested.** `--backend aion` never bootstraps the 2.x runtime and adds 1.8 as a
  dynamic dependency; `--backend phi-silica` does the opposite. Nothing runs both in one process, and
  nothing should, but the two `LanguageModel` projections now share one assembly.

### From the chunk 6 review (D69)

- **A cut that races a genuine runtime `Error` is reported as `Cancelled`.** Both adapters check
  `cancellationToken.IsCancellationRequested` before mapping the runtime's status, so a generation
  that delivered enough text for the cut to fire and then returned `Error` comes back as `Cancelled`,
  which the pipeline treats as a successful cut (D56 says only a `Cancelled` status may be
  reinterpreted). The text before the cut is real and the client asked for the stop, so the other
  reading, a 502 for a request that got exactly what it asked for, is not obviously better; the
  behaviour is inherited from the Phi Silica adapter as verified in D62 and left alone. Decide when
  chunk 8's scheduler gives cancellation a second caller.
- **The drain timeout's window.** `DeltaAccumulator.Drain` waits up to five seconds for in-flight
  callbacks. A callback that passed the closed check and was then descheduled for that long would
  append after `Text` had been read. Unreachable in practice; the alternative (wait forever) trades it
  for a hung request if a sink ever blocks. Noted so the timeout is not "tidied" away in either
  direction.
- **The delta sink must stay non-blocking.** The accumulator delivers under its append lock, and the
  drain waits for delivery to finish. Today's sinks (the JSON watcher's lock, the stream's unbounded
  channel `TryWrite`) never block. Chunk 7's whole-reply buffer and chunk 8's scheduler must keep it
  that way, or a stuck client could hold the WinRT callback thread and turn the drain timeout into a
  real path.
- **`AionCapabilityProfileTests` asserts `SystemPrompt` is null, which the fake guarantees itself.**
  `FakeBackend.CreateContext` nulls the argument when the capability is absent, so that assertion
  cannot fail; the `Prompt` equality beside it is the load-bearing one. The two overflow tests pinned
  behaviour chunk 5 then built on (`TruncationTests`). Both noted in the tests.

## Chunk 4 review deferrals

- **The drain is unbounded and silent.** The streaming handler cancels the generation, awaits it to
  completion, and only then disposes the context (D51). A runtime that never completes after being
  cancelled therefore parks the request and its context forever, with nothing in the log to say so. The
  fix is a warning after N seconds still waiting, naming the request id. A timeout that gives up and
  disposes anyway would be the wrong fix: disposing a context whose operation is still running is
  exactly the use-after-dispose D51 removed, and a timeout would reinstate it under a different name.
  If the wait ever has to be bounded, the context has to be leaked deliberately (handed to a reaper
  that disposes it when the operation finally ends) rather than disposed on time. Unobserved so far:
  the fake always completes, and neither runtime has been seen to hang after a cancel. **The stakes
  went up in chunk 8.** The consequence used to be one parked request and one parked context; with the
  scheduler behind it, a generation that never completes after its cancel parks the single worker as
  well, and with the worker every request queued behind it, until the drain grace period at shutdown.
  Issue #4 asked for the warning itself and it was not implemented in chunk 8; it is now an issue of
  its own.
- **Keep-alive covers only the wait for the first token.** Once deltas start flowing the comments stop,
  so a long stall *between* tokens (a model that pauses mid-generation, or a machine under load) can
  still trip a proxy's idle timeout even though the request is healthy. A keep-alive driven by "time
  since the last byte written" rather than "waiting for the first delta" would cover both; it needs the
  writer loop to hold a timer, which the current single-reader loop does not.
- **Content filtering means different things on the two response shapes.** The non-streaming path blanks
  the withheld text and returns an empty message with `finish_reason: content_filter`. The streaming path
  cannot: by the time the filter verdict arrives, the deltas have already been written to the client, so
  it sends the same finish reason after text the JSON path would have suppressed. Inherent to streaming
  rather than a defect, and unavoidable without buffering the whole reply (which chunk 7 does, but only
  when `tools` is present). Undocumented until now; a client that relies on the JSON path's blanking will
  be surprised by the stream.

### From the whole-branch review

- **A cut against a backend that ignores cancellation stalls the stream.** After the cut the handler
  breaks out of the reader loop and waits on the generation before writing the finish chunk and
  `[DONE]`, and keep-alives cover only the wait for the *first* delta, so that window is silent. Not
  currently reachable on Phi Silica: D54 measured that cancelling really does stop the NPU. It becomes
  real for a backend that does not, which is what chunk 6 brings: Aion's WinRT surface has no cancel at
  all. The client would sit through the rest of a generation whose answer it already has, and a 30 s read
  timeout would abort it. Part of the same fix: `BackendCapabilities.Cancellation` is declared on both
  adapters and read by nothing, so the cut assumes cancellation works and has no degraded path.
- **Late deltas may be dropped from a stream while the JSON path keeps them.** `PhiSilicaBackend`
  deliberately discards callbacks arriving after its completion barrier but can still return their text
  in `result.Text`. The streaming path sees only delivered callbacks, so a delta that loses that race is
  absent from the stream and present in the non-streaming reply for the same generation, with `usage`
  under-reporting to match. Unverified on hardware (the timing comes from the adapter's own comments
  rather than an observed run), and it needs a real NPU test before it is fixed or dismissed. Chunk 4
  newly makes "`Text` is the concatenation of the deltas" load-bearing for the cut, so the adapter is
  the right place to enforce it: assert `Partial()` against `result.Text` on `Complete`, or return
  `Partial()` always.
- **`finish_reason` and `usage` are omitted rather than sent as `null`.** Real OpenAI emits
  `"finish_reason": null` on every content chunk and `"usage": null` on all but the last under
  `include_usage`; the bridge omits both keys. The Python client and the Vercel AI SDK survive it, but a
  strictly generated client (an OpenAPI-derived Java or C# SDK where `finish_reason` is a declared
  property) can reject the frame. The same mechanism would let the error envelope carry its `param` and
  `code` keys explicitly instead of dropping them, which a client branching on `err.code` cannot read.
- **Validation is more permissive than OpenAI in three places.** `stop` is unbounded where OpenAI caps it
  at 4, and an enormous stop string makes the holdback, and so the stream's latency, client-controlled;
  `n: 0` is accepted and answered with one choice where OpenAI requires `n >= 1`; and `stream_options`
  sent without `stream: true` is silently ignored where OpenAI returns a 400, so the bridge hides that
  client bug instead of surfacing it.
- ~~**The chars/4 estimate is wrong by roughly 4x for non-Latin output.**~~ Fixed for Phi Silica by
  D80 (2026-09-11): `usage` and the `max_tokens` budget are Phi-3 tokens there, measured against the
  runtime's preflight on CJK and emoji among others. Still true on Aion, which keeps chars/4 until a
  generation runs and the same measurement can be made against its preflight, if it has one.
- **The pressure warning still measures characters.** `ConversationSession` compares the transcript's
  characters against `--context-window-hint × 4`; with a real counter on Phi Silica it could compare
  tokens against the hint directly, and the warning would then fire before the preflight refuses
  only if the hint is set below the measured 3581-token window. Deferred with the chunk 5 note on the
  default hint (from D80).
- **`usage` disagrees between the shapes on a filtered reply.** Extends the content asymmetry above: the
  stream counts what it actually sent (the cutter's emitted text) while the JSON path counts the blanked
  content, so the same filtered generation reports N completion tokens streamed and 0 as JSON. No test
  asserts either number, so the divergence is unpinned.
- **A client that disconnects while uploading its body throws an unhandled `OperationCanceledException`.**
  `ChatRequestPreparation` guards `ReadFromJsonAsync` for `JsonException` and `InvalidOperationException`
  only. Pre-existing and identical on main (the extraction only moved it), and the streaming path now
  shares it.
- ~~**The non-streaming callback can touch a disposed `CancellationTokenSource`.**~~ Resolved with #5
  (2026-09-10, D63): the callback no longer cancels anything. It sets a `TaskCompletionSource`, which
  has no disposal to race, and the request task cancels on its own thread. The same change removed the
  timer-thread crash that a throwing cancellation registration caused.
- **No backpressure on a slow client.** The delta channel is unbounded, which is right for keeping the
  WinRT thread non-blocking, but a client that is slow rather than gone stalls the writer while the
  backend keeps generating into memory, and nothing signals the backend to slow down. Bounded by the
  reply length today; with no default cap and a runaway model, bounded by nothing the bridge controls.
- **Keep-alive comment frames are a superset of OpenAI's wire output.** `: keep-alive` is valid SSE and
  is ignored correctly by the Python client and by `eventsource-parser`, but OpenAI itself never sends
  comments, so a hand-rolled reader assuming every line is `data:` or blank can mis-frame. Worth stating
  in the client-compatibility notes chunk 8 owns.
- **Coverage gaps the review named and this pass did not close.** The keep-alive-disabled branch
  (`KeepAliveInterval <= 0`, whose documented consequence is that a late failure keeps a real HTTP
  status) is never exercised; a client disconnecting *before the first token* (the keep-alive wait and
  both client-gone branches of `FailAsync`) is untested, because both disconnect tests read a real chunk
  first; and content filtering with zero deltas, where the role chunk itself commits the 200, is never
  hit. Separately, `A_cut_cancels_the_generation_and_still_disposes_the_context_once` asserts
  `count < 200` against a 400-token responder, which would still pass if cancellation took a full second
  to propagate.

## 2026-09-10 whole-project review notes

Found by a read of the merged tree after chunk 4, none load-bearing, none fixed in that session.
**All three are done (issue #10, D82, 2026-09-11)**; kept here because one of them was not what it
said it was.

- ~~**The JSON path lets a non-client cancellation escape as a bare 500.**~~ Fixed. The catch now
  mirrors the streaming path's pair exactly: one clause filtered on `RequestAborted` that returns
  nothing and logs `http=0`, then an unfiltered one that reports through `GenerationFailure`. One test
  per clause, each checked against the old filter, where both fail. Reaching the client-gone clause
  needs more arrangement than it looks — see D82.
- ~~**`SseStream.Started` flips before the first write has succeeded.**~~ Changed, but the premise was
  wrong, so nothing was fixed. `HttpResponse.WriteAsync` calls `StartAsync` before it writes a byte,
  so by the time a body write or flush fails the response really has started and the old flag was
  right about it; the two answers part only when starting the response is itself what fails.
  `Started` is now `HasStarted`, which removes state that could contradict the response, and that is
  all it does. See D82 — do not re-file this as a defect.
- ~~**`identity.ps1 -Install` removes the old registration before adding the new one.**~~ Fixed: add
  first, remove the previous package full name afterwards only if it differs. `Add-AppxPackage`
  updates a same-identity registration in place, so a re-run after a rebuild now removes nothing;
  verified against the live registration. Remove-then-add survives only as a fallback for a refused
  in-place update.

## Chunk 3 review deferrals

- **Null fields are omitted rather than emitted as `null`, in error bodies *and* in responses.** The
  real OpenAI API emits all four error fields including nulls; ours drops `param` and `code` when they
  are null because the shared `JsonDefaults.Options` sets `WhenWritingNull` and every endpoint since
  chunk 1 uses the shared `OpenAiError` helper. The same serializer setting reaches success responses:
  `ChatCompletionResponseMessage.Content` is `string?`, and OpenAI emits `"content": null` alongside a
  `tool_calls` array, so in chunk 7 a tool-call reply would omit the `content` key entirely instead of
  sending it as null. Clients that read `message.content` unconditionally would break on that shape.
  Found by the Codex adversarial review. Deferred because changing it alters the error contract of every
  endpoint shipped in chunks 1 and 2, so it deserves its own decision and its own review rather than
  being absorbed into a chunk that happened to notice it. Chunk 7 still cannot ship the tool-call
  response shape without settling it first.
- **Error messages escape apostrophes as `\u0027`.** Same shared serializer options, same reasoning.
  Raised by the implementer rather than a reviewer, which is the right instinct. Fix it alongside the
  entry above.
- **Overflow detection must not wait for a generation to fail (resolved in chunk 5, D73: the session
  asks the preflight before generating; the refusal now takes 31 ms on the NPU).** Measured in chunk
  4's smoke run and recorded as D55: Phi Silica answers a 225,042-character prompt with a generic
  `Error` after 26.5 s, never with `PromptLargerThanContext`, while `GetUsablePromptLength` says
  13,429 of 225,042 characters fit, instantly and correctly. The truncation loop chunk 5 owned had to
  key off the preflight; a loop that generates and reads the status would cost 26 s per iteration and
  could not tell overflow from any other fault. It followed that 400 `context_length_exceeded` was
  unreachable on Phi Silica until something called the preflight, even though the mapping and its
  tests were correct.
- **The rendered prompt is not a usable conversation identity (resolved in chunk 5, D71:
  `ConversationKey` encodes `(system, turns)` with length-prefixed fields, and a test pins each of the
  three surfaces below both ways).** Distinct conversations can produce the same rendered string, so
  anything that treats that string as an identity will hand one cached context to two different
  conversations. Note what the cache key actually is: PLAN section 2.5 keys on SHA-256 over a
  *canonical rendering* of `(system, turn_0 … turn_k)`, which is a different function from what
  `PromptTemplate.Render` emits. An earlier version of this entry said the cache "hashes exactly this
  string", and it does not. The three collision surfaces the chunk 5 canonicalization had to close:
  1. **Turn markers are not escaped.** A user message containing a line reading `[Assistant]` (or
     `[User]`, or `### Conversation so far`) imitates a turn boundary, so a single forged user turn and
     a real two-turn history render identically.
  2. **Native placement drops the system text from the prompt entirely.** With
     `nativeSystemPromptSupported: true` the system text is returned separately in
     `RenderedPrompt.SystemText` and left out of `RenderedPrompt.Prompt`, so two conversations that
     differ *only* in their system prompt render byte-identically. Whatever the placement, the hash
     input must carry the system text, which is why PLAN section 2.5 puts `system` in the key.
  3. **The raw-passthrough branch has no markers at all.** A lone bare user message is sent verbatim, so
     a single user message whose text happens to *be* a rendered transcript collides with that real
     transcript's rendering.
  This was harmless while nothing was cached. The escaping question was settled by a boundary-preserving
  canonical hash input, kept distinct from the prompt string, before the cache landed.
- **`ChatMessage` carries no `tool_calls` field (the field landed in chunk 5 and enters the cache key;
  rendering it is still chunk 7's).** An assistant message with `content: null` and a `tool_calls`
  array deserialized to an empty assistant turn, so the tool call it made was lost. Chunk 7 needs the
  field to render the model its own protocol, and chunk 5 needed it for the canonicalization PLAN
  section 2.5 describes, where a client that re-serializes our output must still hit the cache.
- **`ChatCompletionRequest` is an 18-argument positional record.** Tests construct it with long runs of
  positional nulls, so inserting a field could silently shift arguments without a compiler error. Add a
  test builder or use named arguments before the parameter list grows in chunks 4, 7 and 8.
- **The per-request log line is only pinned for successful requests.** The rejected-request line, and
  the polymorphic `status=` field that carries an exception type name on the catch path, are untested.
  Consider `status=exception` with the type in the message so the field stays machine-parsable.
- **Two 502 shapes for one client-visible condition.** A backend `Error` status emits no error code
  (the spec table says so) while a thrown backend exception emits `backend_error`. If you unify them,
  drop the code from the exception path rather than adding one to the status path.
- **Smaller test gaps**, all noted by reviewers and none load-bearing: no test deserializes a message
  with the `role` key entirely absent; the empty-but-present system text case is untested; no dedicated
  test for a conversation with zero user or assistant turns; no test drives
  `--system-prompt-placement` through the command-line parser to the config key; the placement
  measurement's reply text in `smoke.ps1` is not truncated, unlike its two sibling lines.
- **Untested generation paths** flagged by the Codex review: a backend-originated `Cancelled` status
  with a client still connected, an unknown status value, a context-creation failure, and a throwing
  `Dispose`. None confirmed as production defects; all worth a fake-backend fault case.
- ~~**Nothing serializes concurrent requests against the single model handle (chunk 8 owns the fix).**
  Chunk 3 opens a generation endpoint that Kestrel will happily enter on several threads at once, while
  the request scheduler (one worker, bounded queue, PLAN section 2.7) is chunk 8. Between the two,
  context creation and generation on one shared `LanguageModel` are unguarded, and the smoke suite is
  strictly single-threaded, so two simultaneous requests against a real NPU are entirely untested. This
  is deliberate scope rather than an oversight, but it is a real gap in what has been verified: any
  claim that the endpoint works is a claim about one request at a time. Chunk 8 should include a
  concurrent smoke step as well as unit coverage of the queue.~~ Closed in chunk 8, both halves.
  `GenerationScheduler` runs one job at a time off a bounded queue, and `ConversationSession.Acquire`
  runs inside the scheduled closure rather than ahead of it, so `CreateContext` and
  `GetUsablePromptLength` are queued alongside `GenerateAsync` instead of racing a live generation
  from a request thread (D84 — the chunk's own brief had it wrong, and a review caught it). The
  verification half is closed too: `smoke.ps1` sends two requests at once on the real NPU and watches
  the live `queue_depth`, and a second step runs a server at `--queue-capacity 1` to see the queue-full
  429. Still true and deliberately so: one worker means a second request waits rather than running,
  which is the trade PLAN section 2.7 chose.
- **`RenderedPrompt.SystemInPrompt` is unused by production code.** No caller reads it; the endpoint
  re-derives the same fact from its own `useNativeSystem` plus `rendered.SystemText`, and only
  `PromptTemplateTests` asserts on the flag. Two ways to say one thing, which is how they drift. Either
  delete the flag and let the caller keep deriving it, or use it at the call site and stop deriving.
- **The raw-passthrough rule omits the `tools` clause PLAN section 2.3 specifies.** The plan sends a
  single bare user message raw only when there is no system text, no history and no tools;
  `PromptTemplate.Render` tests only `messages.Count == 1 && role == "user"`. Harmless in chunk 3, where
  `tools` is accepted and ignored, but chunk 7 must restore the clause: a request carrying tools needs
  the marker format so the model sees the tool protocol it is meant to answer in.
- **A present-but-empty system message reaches the native create-context call as `""`.** `BuildSystemText`
  deliberately returns the empty string (not null) for a `system` message with empty content, so the
  caller can tell "no system message" from "an empty one". The endpoint then passes that empty string
  straight into `backend.CreateContext(nativeSystem)`. `FakeBackend` does not care; what the Phi Silica
  and Aion runtimes do with an empty system context is unmeasured. Collapse it to null at the call
  site, or measure it, before it matters.
- **The response echoes back whatever `model` string the client sent (resolved by D77: an unknown id
  is a 404 `model_not_found`, a missing one a 400, and the reply always carries the served id).** `model` in the response body is
  `request.Model ?? backend.ModelId`, with no check that the requested model is the one being served, so
  a client asking for `gpt-4o` gets `"model": "gpt-4o"` back from the on-device model. Convenient for
  tools that assert on their own model id, and it is why the field is left alone for now, but it is not
  honest. Decide between echoing, validating against `/v1/models`, and always returning the served id.
- **Extract the prepared-request pipeline first in chunk 4, before writing the streaming path.**
  `ChatCompletionsEndpoint.PostAsync` is one long method owning parse, validate, readiness, ignored-
  parameter warnings, placement, render, generate, response shaping and logging. The streaming path
  needs everything up to and including generation and none of the shaping after it. If chunk 4 writes
  the SSE path alongside this method instead of on top of a shared prepared-request shape, the two will
  drift on exactly the parts that are easy to get subtly different: readiness, system-prompt placement
  and the usage estimate. This is chunk 4's opening task rather than a defect in chunk 3; the method is
  correct as it stands. Ruling (controller, end of chunk 3): the pipeline is not being extracted now.
  The review calls it an extension rather than a rewrite provided it happens before the streaming path
  is written, and this project's method forbids widening a chunk to absorb review findings. Cost if
  that judgement is wrong: chunk 4 opens with a refactor instead of a feature.

## Chunk 2 review deferrals

- **In-flight generation tracking in the adapter.** `PhiSilicaBackend.DisposeAsync` disposes the model
  (and would call `Bootstrap.Shutdown`, a no-op under identity) while caller-owned contexts or an
  in-flight `GenerateAsync` may exist. Harmless today; the cache now empties before the backend is
  disposed (chunk 5), and the scheduler (chunk 8) should drain in-flight work before disposal.
- **UAC over-the-shoulder elevation.** If a standard user elevates with an admin's credentials, the
  elevated token is the admin's: `task install` would register the task for the wrong account and the
  package lookup would miss the user's registration. Detect (elevated token user ≠ interactive session
  user) and refuse; needs `WTSQuerySessionInformation` or similar.
- **`--hide-console` under Windows Terminal.** `GetConsoleWindow` returns the ConPTY pseudo-window, so
  hiding works with conhost (as observed) but not when Windows Terminal is the default host. A `WinExe`
  launcher stub would fix it properly.
- **Slimmer package graph.** The `Microsoft.WindowsAppSDK` metapackage copies WinUI, WebView2 and
  OnnxRuntime binaries into the output. `Microsoft.WindowsAppSDK.AI` + `.Foundation`/`.Runtime` would
  be smaller; deferred because CsWinRT projection setup is fiddly and the metapackage is known to work.
- ~~**`/debug/generate` through the scheduler** once chunk 8 exists, so it cannot bypass the queue.~~
  Done in chunk 8 (D90, superseding D40's deferral): it enqueues like the OpenAI endpoints and creates
  no context until its turn, so a job dropped while queued never touches the model. Two imprecisions
  on that endpoint were left and are issues of their own: a client abort while queued is reported as
  503 `queue_shutting_down`, and an `OperationCanceledException` for a token other than the request's
  still escapes as a bare 500 rather than the 502 D82 gave the OpenAI shapes.
- **Automated cancel assertion on the NPU.** The smoke test proves a client disconnect is survived and
  drained; it cannot observe the adapter's `Cancelled` status from an aborted HTTP request. A future
  streaming smoke step (chunk 4) can assert the truncated stream instead.

## Chunk 2 deferrals

- **Stable-channel Phi Silica once a LAF token arrives.** The code path is identical; switching is
  `Microsoft.WindowsAppSDK`/`.Runtime` back to the stable version and the manifest's
  `PackageDependency` back to `Microsoft.WindowsAppRuntime.2`. Worth doing when the token is issued so
  the bridge does not depend on experimental packages.
- ~~**Token counting.** Progress callbacks undercount on Phi Silica (speculative decoding batches tokens).
  Consider `chars/4` for completion tokens too, or expose both. Decide in chunk 3 when `usage` is built.~~
  Answered in chunk 3: `usage` is `ceil(chars/4)` on both sides, after one generation measured 29
  callbacks for 367 characters, a 3.17x undercount (D44).
- ~~**System prompt fidelity on Phi Silica.** `CreateContext(systemPrompt)` did not make the model follow a
  strict identity instruction. Chunk 3's template should be measured both ways (native context vs.
  rendered into the user turn) with the smoke test.~~ Answered in chunk 3: both placements were measured
  on the NPU and both produced the instructed reply; the chunk 2 observation belonged to the bare
  `/debug/generate` path, not to the model (D45).
- **Activated instance's console.** With `--hide-console` the window is hidden after startup but still
  flashes briefly; a `WinExe` variant or a launcher stub would avoid it. Logs from the activated process
  are otherwise lost; add file logging (see chunk 1 deferral).
- **Self-relaunch args and secrets.** `LafToken` passed on the parent's command line is forwarded to the
  child's command line (visible in Task Manager for the user's own processes). Prefer the local settings file.

## Chunk 1 deferrals

- ~~Phi Silica auto-start needs package activation, not the SCM~~ Done in chunk 2: self-relaunch with
  supervision and `task install|uninstall|status` (D33, D34, D37). The non-interactive-session question is
  moot because the task runs with `/IT` in the user's session.
- **Automated validation of `identity.ps1` and the manifest** (Codex review, chunk 1). A Pester test
  that runs `makeappx pack /nv` against `packaging/AppxManifest.xml` and checks `-Status` output would
  catch schema regressions without the UAC step. Windows-only; run manually for now.
- **End-to-end `sc.exe` boundary test.** The quoting is verified by an in-test argv parser and by the
  reviewers' emulation, not by creating a real service (needs elevation). Add an opt-in elevated test.
- **Console output of an activated process.** A package-activated console exe gets its own console
  window rather than the caller's. Once self-relaunch exists, logs for the Phi Silica path should also go
  to a file or the Event Log so they are not lost.
- **File log sink.** Service mode logs to the Application event log (source `npu-bridge`, Information
  and up for our categories). A rolling file log would be friendlier for the per-request lines from
  chunk 3; not added to keep dependencies minimal.
- **Per-user package registration vs. service accounts.** `identity.ps1` registers the sparse package
  for the current user. A service under `LocalSystem` would not see it even if activation-by-path
  worked; any future service+identity experiment must run as the registering user (`sc create ... obj=`).
  Moot while D24 stands, recorded so the failure is not misdiagnosed later.
- ~~**`healthz` queue counters** are hard-coded to 0 until the scheduler (chunk 8) exists; the cache
  counters are real since chunk 5.~~ Real since chunk 8. `queue_depth` is the scheduler's live count
  of jobs still waiting for the worker, which deliberately stops counting a job whose own caller
  cancelled it rather than waiting for the worker to drain to it (D87); `queue_capacity` is
  `--queue-capacity`.
- **Sparse-package `PackageDependency` set** in `packaging/AppxManifest.xml` mirrors the Aion sample
  (WAR 2 + WAR 1.8). Whether Phi Silica additionally needs `Microsoft.WindowsAppRuntime.CBS.*` in a
  hand-written manifest is unknown until chunk 2 tries it; the Windows App SDK build targets inject
  dependencies for packaged apps automatically, which a sparse package does not get.
