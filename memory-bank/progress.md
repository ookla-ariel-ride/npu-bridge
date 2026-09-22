# Progress: npu-bridge

## 2026-09-21 wave state
The `chunk8-leftovers` wave ended at `949ed25` on `wave/chunk8-leftovers`, 21 commits over `main` at `66e309b`, with 996 tests and a final hardware smoke that passed all steps (0 skipped, 7 informational, `first-generation RPC retry: 0`, final health `ok`). The PR had not yet been opened. It closed #24, #27, #28, #17 and #33; D104 records the rulings and `docs/FUTURE.md` records the deferred review findings.

## Works today (verified)
| Area | Status | Evidence |
|---|---|---|
| Solution, build, tests | ✅ | `dotnet build` clean with and without the Aion SDK; **996** xunit tests green, 0 skipped on `949ed25` (the `chunk8-leftovers` wave, D104, ready for its PR; the `leftovers` wave, D100 to D102, integrated 2026-09-13 and shipped as a PR; the #29/#30/#31 wave, D97 to D99, merged 2026-09-12 through PR #36; chunk 5, the D77 conformance pass, D78, the D79 test hardening, D80 real token counts, the D81 shared pipeline, the D82 review notes and chunk 7 tool-call emulation merged 2026-09-11; chunk 8, D84 to D92, merged 2026-09-12). **All eight chunks of `docs/PLAN.md` are built and merged; the plan is complete.** |
| System-text guard (#29, D97) | ✅ | `Api/SystemTextGuard.cs`, called from `ChatRequestPreparer` before the queue and from `/debug/generate` before `CreateContext`: native system text at or over `ILanguageModelBackend.ContextWindowTokens` (3,581 on Phi Silica) or over 32,000 characters is 400 `context_length_exceeded` with no backend call, no queue slot, no context; the character ceiling is checked before tokenizing; `PhiSilicaBackend.CreateContext` throws above the same ceiling. `SystemTextGuardTests` (twelve tests: token check, character ceiling, tools named in the message, no truncate retry, plain 400 on a busy queue with the rolling mean untouched, folded placement left to the preflight, de-DE culture, no `Count` call on a ceiling refusal). Smoke on the NPU (2026-09-12): a 32,000-character system text counted offline at 13,421 tokens refused before any context; `/healthz` `context_window_tokens` 3581 equal to the measured D80 boundary |
| Outcome-based `/healthz` (#30, D98) | ✅ | `Backends/GenerationHealth.cs`, recorded from `GenerationOutcome.Classify`, `GenerationFailure.FromException` and `SchedulerAdmission.FailureFor` on all five shapes, armed before `session.Acquire()` so a `CreateContext` or preflight throw counts. `last_generation` and `consecutive_backend_faults` on the body; two consecutive faults are 503 `degraded` while requests stay admitted. `GenerationHealthTests` (eleven tests, including `CreateContext` and preflight throws, a cut counted as success, a refusal not moving the counter, a post-generation bridge exception not overwriting a success, a preflight-known overflow on `/debug/generate` recording nothing). Smoke on the NPU: the D52 cross-check's runtime `Error` recorded as one fault, not degraded |
| Streamed tool-call measurement (#31, D99) | ✅ | `tool-probe.ps1 -Stream` and `-SelfTest`: six streamed/JSON pairs identical after ignoring per-response ids and key order, `index` only on the streamed shape, the finish-reason biconditional both ways on 34 replies, `context_cache_hits` 0 to 1 across the second turn of the tool round trip, streamed latency equal to JSON. The script reads streamed error events, treats a non-200 pair as not comparable, and survives a 503 degraded `/healthz` |
| One JSON pipeline (#26, D100) | ✅ | `Api/JsonPipeline.cs`: the scheduled closure both JSON endpoints used to copy, once (`ChatCompletionsEndpoint` 438 to 118 lines, `CompletionsEndpoint` 276 to 93); `CacheLabel` once on `GenerationPipeline`; `WaitAsync` for the scheduler's and the lifecycle's shutdown waits; `Retry-After` over the last 16 attempts (`Retry_after_averages_only_the_last_sixteen_generations`); `ContextLease._settled` via `Interlocked`. Cross-family review accepted; the whole suite (cut parity, leak counts, truncation slot) is the oracle. Smoke on the NPU at `3c97d48`: all steps |
| Backend-call fault tracker (#34, D102) | ✅ | `GenerationPipeline.BackendCallTracker` around the eight backend call sites on five shapes; a bridge-side throw is a 502 that leaves `/healthz` alone; duration is the attempt's own; `FakeBackend.DeltaGate` for deterministic cut and cancel ordering. `GenerationHealthTests` grew to the tokenizer-throw, CreateContext-throw (five shapes), guard-refusal, SSE-write-failure and cutter-fault-cancels cases; `CompletionsTests` gained the disconnect disposal test. Two review follow-ups landed in-wave (the cutter's tokenizer outside the tracked region, cancelling the model at once; the smoke's degraded final-health read). Smoke on the NPU at `3c97d48`: final health `ok` |
| Offered zero-argument tool calls (#22, D101) | ✅ | `ToolCallParser.Parse(text, offeredToolNames)`: an unwrapped object whose only key is `name`, naming an offered tool, is a call with `{}`; any other key or an unoffered name is content; bare arrays and `parameters` echoes unchanged. 141 ToolCall tests including the echoed-definition negative controls. Smoke on the NPU at `3631444` with `-ToolProbeRuns 20`: 20/20, no leaked protocol, no unoffered tool |
| `/debug/generate` error mapping (#25) | ✅ | A foreign `OperationCanceledException` is the 502 envelope, not a bare 500; a client that vanishes while queued is `ClientGone` and gets the empty result. `DebugGenerateTests` has both, each shown to fail without its fix |
| Guard leftovers (#35) | ✅ | `.editorconfig` raises CA1305 to a warning under `src/` (no hits); `Backends/BackendLimits.cs` owns the 32,000-character ceiling and `SystemTextGuard` is internal again; `RefusalFor` lost its optional token count; `/healthz` pins `context_window_tokens: null`; the smoke's D80 cross-check accepts a 2 % bracket and its guard probes cover both branches (33,000 characters; 20,000 characters measured at 5,001 tokens on the NPU) |
| `identity.ps1` version handling (#19) | ✅ | Registered packages sorted by parsed `[version]` (fallback `0.0`); the superseded removal re-checks the old full name, is non-terminating, and warns with the full name when it fails. Verified by parse check and `-Status` only; the bump path cannot run at 0.1.0.0 |
| Generation scheduler (`--queue-capacity`) | ✅ | Chunk 8, D84 to D88, D92: `Api/GenerationScheduler.cs`, one worker on a bounded `Channel<GenerationJob>`. `ConversationSession.Acquire` runs inside the scheduled closure, not just `GenerateAsync`, because `CreateContext` and `GetUsablePromptLength` are calls on the same shared handle (D84 — the review's catch, against the task brief's own instruction). Queue-full → 429 + `Retry-After` (queue depth times the mean of the last 16 attempts since D100) + `rate_limit_error`/`queue_full`; a job cancelled while queued is dropped without touching the model (503 `queue_shutting_down`); a job that ran and threw its own OCE is a 502 (D88). Measured on the NPU: two concurrent requests really queued (`queue_depth` peaked at 1), `--queue-capacity 1` admitted one and rejected two |
| `POST /v1/completions` | ✅ | Chunk 8, D91: both shapes, `object: "text_completion"`, `choices[].text`, finish reasons `stop`/`length`/`content_filter` only (no `tools` on this endpoint). `prompt` wrapped into one user message and run through the identical pipeline from the model-id check onward; a multi-element `prompt` array is a 400; the `chatcmpl-` id prefix is kept deliberately; `echo`/`best_of`/`suffix`/`logprobs`/`logit_bias` accepted and warned, never implemented. Smoke answered on both shapes on the NPU |
| `/debug/generate` through the scheduler | ✅ | Chunk 8, D90, superseding D40's deferral: unqueued it raced the shared handle exactly as the OpenAI endpoints did. The two imprecisions it left (#25) were fixed in the `leftovers` wave |
| Tool-call emulation (`tools`, `tool_choice`) | ✅ | Chunk 7, D83: `Tools/` (`ToolCatalog`, `ToolSchemaRenderer`, `ToolCallParser`) and `Api/ToolCallReply`, on both response shapes. The instruction block is appended to the system text so `ConversationKey` covers the tools offered; the parser never throws and anything it cannot read is content; the stream buffers the whole reply behind keep-alives, then one chunk carrying the array. Five test files, 1,880 lines, of which `ToolCallParserTests` holds the adversarial shapes; smoke on the NPU: 20/20 runs called the tool, no prose, no leaked protocol, no unoffered tool, every argument valid JSON |
| One post-generation pipeline for both shapes | ✅ | D81: `Api/GenerationPipeline.cs` (`DeltaSink`, `CutWatcher`, `CancelGuardedAsync`, the raw-output log) and `GenerationOutcome` beside `GenerationFailure`; `GenerationOutcomeTests` pins every status crossed with the handler's own cancel, so the D56 and D57 drifts between the two hand-written copies cannot recur. No client-visible change: the smoke run on the NPU returned D80's numbers exactly |
| A cancellation that is not the client's | ✅ | D82: the JSON path's catch is the streaming path's pair exactly — one clause filtered on `RequestAborted` that logs `http=0` and returns nothing, then an unfiltered one through `GenerationFailure` — so a backend that lets the runtime's own cancellation escape is answered with 502 and the ordinary error body instead of an unhandled 500. `ChatCompletionsTests` has one test per clause, each checked to fail against the old `when (ex is not OperationCanceledException)` filter |
| Token counts (`usage`, `max_tokens`) | ✅ | D80: Phi-3 tokens on Phi Silica (`Phi3TokenCounter` over the vendored Phi-3.5-mini model, measured against the runtime's preflight: 3581 tokens at every ASCII boundary), chars/4 on Aion and the fake; `TokenCounterTests`, `TokenUsageTests`, `TokenBudgetCutTests`, `DebugTokenizeTests`; the smoke's tokenizer step repeats the measurement per build (fails above 2 % spread) |
| Preflight units | ✅ | D80: `GetUsablePromptLength` answers in UTF-8 bytes, converted by `Utf8Offsets`; before the fix a 5,001-char CJK prompt at 1.6 × the window passed the preflight |
| `/healthz`, `/v1/models`, `/v1` fallback | ✅ | TestServer tests + live curl on the exe; `/healthz` carries identity, cache counters (D74), keep-alive timings (D79), real `queue_depth`/`queue_capacity` since chunk 8 (D87: a live counter, not `Reader.Count`, so a caller who gives up while queued stops being counted at once) and backend diagnostics, and a test pins that the registered options, not defaults, are reported |
| Smoke script trust (`scripts/smoke.ps1`) | ✅ | D79 plus the smoke wave (D103): readiness requires identity and bootstrap `ok` on phi-silica, the preflight step refuses a null answer, teardown proves the activated child and the port are gone (60 s), each auxiliary server has its own teardown row, `InfoStep` may fail on a contradiction. Run on the NPU 2026-09-11 after chunk 7: all steps passed, 0 skipped, 5 informational, four teardown rows. The tool probe (`-ToolProbeRuns`, five by default) was the last SKIP placeholder and is now a real step; it fails on what the bridge guarantees (a reply shaped wrongly, arguments that are not JSON, protocol text leaking as content) and reports what the model chooses, including a call to an unoffered tool, which the bridge is required to surface |
| Config precedence json < local < env < CLI | ✅ | real-file test + live probes |
| CLI verbs `run`, `service`, `task`, `help`, `version` | ✅ | tests + live exit codes |
| Fake backend with faults/threads/init rules | ✅ | tests |
| Sparse package identity (`identity.ps1`) | ✅ | registered; PFN `NpuBridge_jtas4mnxdyzpe`. Since D82 `-Install` adds before it removes, so the successful path never leaves the machine unregistered. Re-registered 2026-09-12 after the folder rename, and that run proved the qualifier: `Add-AppxPackage` updates a same-identity registration in place only while the **external location** is unchanged, and refuses with `0x80073D0B` when it is not, at which point D82's remove-then-add fallback carries it. Smoke then reported `identity=True` |
| Self-relaunch via package activation + supervision | ✅ | child had identity, saw shell env, died with the parent |
| Phi Silica adapter (experimental SDK) | ✅ | smoke passed 2026-09-15 on Dev build 29667, twice (`edaf254` and `3c429ab`: 41 steps, 0 skipped, text contract `text_mismatches=0 late_deltas=0`, D65). Insider flight 29661 broke it on 2026-09-10 (workload packages fail to register, model `NotReady`) and was rolled back to 29648; 29667 arrived later and works |
| `/v1/chat/completions` non-streaming | ✅ | `ChatCompletionsTests`; smoke on the real NPU: 415 ms to 453 ms for a one-word reply on 2026-09-11 (677 ms to 899 ms in earlier runs), correct shape and usage |
| `/v1/chat/completions` streaming (SSE) | ✅ | `ChatCompletionsStreamingTests` (framing, error event, keep-alive, disconnect drain); smoke streaming step on the NPU |
| Client-side cut: `max_tokens`, `max_completion_tokens`, `stop` | ✅ | `OutputCutTests` on both shapes (chars/4) and `TokenBudgetCutTests` (token budgets, stand-in counters and the real Phi-3 one); smoke shows the cut cancels the NPU (D53) and that the streamed text counts exactly what `usage` reports (D80) |
| PromptTemplate (message flattening) | ✅ | exact-string tests; both system-prompt placements measured on hardware |
| Prompt overflow → HTTP 400 `context_length_exceeded` | ✅ | Decided by the preflight before any generation (D73): the smoke test's 16.6K-character transcript is refused in 31 ms on the real NPU, with the preflight's numbers in the message (D75); `TruncationTests` on both shapes |
| Context cache (`--context-cache-size`) | ✅ | `ConversationKeyTests`, `ContextCacheTests`, `ContextCacheEndpointTests` (hits/misses/eviction/concurrency/dispose-on-failure on both shapes); smoke on the NPU: continuation hit at 235 ms TTFT against a 392 ms replay, counters on `/healthz` (D71, D72, D74, D75) |
| `--truncate-history` and `x-npu-bridge-truncated-turns` | ✅ | `TruncationTests` with and without a preflight; smoke on the NPU: the refused transcript answers with `truncated-turns: 4` on a second server (D73, D75) |
| OpenAI wire conformance (D77) | ✅ | `OpenAiConformanceTests`: required-but-nullable fields written as nulls on both shapes, `usage: null` before the usage chunk, all four error keys, `model` required and served-only (404 `model_not_found`), schema ranges; Codex-reviewed; fake and Phi Silica smoke runs passed |
| Logon task install/status/run/uninstall | ✅ | live, elevated (pre-supervisor build; `/End` path covered by kill-parent probe) |
| Windows service verbs | ⚠️ | commands verified by tests and emulation; not exercised against the SCM |
| gitleaks hook + CI | ✅ | planted secrets blocked |
| Build + test CI (`.github/workflows/build.yml`, windows-latest) | ✅ | added 2026-09-10; green on every push, including chunk 6 with the Aion nupkg absent (conditional reference, D66) |
| Aion Instruct adapter (`--backend aion`) | ⚠️ | code-verified: `AionCapabilityProfileTests` and `DeltaAccumulatorTests`, two adversarial reviews applied (D69); `/healthz` reports the SDK's `InvalidCache` failure on this machine because the QNN provider cannot be loaded (D70) |

## Not built yet
- **No chunk is outstanding.** `docs/PLAN.md`'s eight chunks are all merged as of 2026-09-12; the
  `leftovers` wave merged as PR #37 (2026-09-15) and the `smoke` wave (D103) as PR #38 (2026-09-16).
  Remaining work is GitHub issues: #24, #27 and #28 from chunk 8, #33 left by the #29 to #31 wave,
  #14/#15/#16 from the coverage audit (#15 now down to its item 7 and the manual checklist), #17, plus
  #2 and #11 for Aion. (#19, #22, #25, #26, #34 and #35 closed with PR #37.)
- Aion Instruct adapter hardware verification: the adapter merged 2026-09-11 (chunk 6, D66 to D70) but
  build 29648 never grants a main-package dynamic dependency execute access, so no Aion generation has
  run; issue #2 stays open. Aion Instruct itself ships in October/November 2026 as a model swap behind
  the Phi Silica API, so `PhiSilicaBackend` is the production path.
- Aion Plan backend: unscheduled, since the model has no SDK yet (issue #11 tracks it)

## Known issues and caveats
- **A folder rename invalidates package identity, and `-Status` cannot see it.** `identity.ps1`
  registers with `Add-AppxPackage -ExternalLocation $BinDir`, while `-Status` prints only the
  WindowsApps `InstallLocation`, so a stale registration looks healthy. The rename to `npu-bridge` on
  2026-09-12 hit this; `-Install` was re-run and the smoke test then reported `identity=True`. Worth
  knowing: the in-place update was **refused** (`HRESULT 0x80073D0B`, "already installed with a
  different external location") and D82's remove-then-add fallback is what completed it — the first
  time that branch has been seen to fire, and a live-path argument for issue #19's unguarded removal.
- A queue-full rejection is 429 with `Retry-After`, but the channel *slot* of a job whose caller gave
  up is held until the worker drains it, so a burst of aborted clients can still 429 a live request
  (D87 states this half as unchanged). Since D100 `Retry-After` averages only the last 16 attempts.
- A preflight refusal or a queue-full rejection can surface as an SSE error event rather than a clean
  HTTP status once the queue wait reaches about a second, because D52's first-frame boundary now has
  the queue wait inside it (D89). Deliberate: holding the first keep-alive for admission would
  reintroduce the failure D52 exists to prevent.
- The publish-before-arm regression test (D92) is probabilistic, roughly a 60 % catch rate over 500
  sequential jobs; a deterministic version needs only test-visibility of the two flags (#24).
- Three chunk 8 paths ship without a deterministic test, each attempted and abandoned for a stated
  reason: the sibling truncation window from D92's final round, the `beforeHeaders` truncation hook on
  `CompletionsStreamEndpoint`, and `WaitForDeltaAsync`'s stale-timeout guard (#27).
- The drain wait after a cancel is still unbounded and silent, and the scheduler raised the stakes: a
  runtime that never completes now parks the single worker and everything queued behind it, with
  `queue_depth` never draining. A bounded timeout-then-dispose is the wrong fix — it reinstates the
  use-after-dispose race D51 removed. Unobserved in practice so far (#28).
- The Phi Silica runtime can wedge: every generation in the process then fails within milliseconds
  with `The RPC server is unavailable`. Three episodes: twice after a first generation on
  2026-09-11 (a restart cleared one), and once on 2026-09-12 from the first call of a freshly ready
  bridge, with no crash in the Application log and no oversized prompt, self-healed in about eight
  minutes. Since D98 `/healthz` answers 503 `degraded` after two consecutive backend faults, so a
  client or the smoke readiness step can tell. The bridge does not recreate the model (deferred,
  D98). A smoke run that fails on a first generation is re-run once.
- The model once answered a tool round trip's second turn with a fenced `{"tool_calls": []}`, which
  the parser correctly treated as content (#33). Whether the bridge should swallow an empty fence is
  open.
- Left by the `leftovers` wave's reviews (`docs/FUTURE.md`, 2026-09-13): a bridge throw in the window
  before classification records nothing, so an already-degraded `/healthz` stays degraded although the
  backend answered; `duration_ms >= 0` is an unfalsifiable assertion and `BackendThrewCancellation` is
  unreachable through every endpoint; attempt duration is measured four times where the scheduler
  measures it once; `BackendCallTracker.Caught` is last-exception equality; `DeltaGate` ignores
  cancellation and its counters are backend-wide; the chat and debug twins of the disconnect test are
  still on `TokenDelay`; a mid-generation abort on `/debug/generate` writes a body into a dead
  connection; the bridge fault is rethrown without `ExceptionDispatchInfo`.
- The smoke's two readiness loops catch only `HttpRequestException`, so a `/healthz` poll that takes
  longer than 5 seconds during model load escapes the loop and fails the step (seen once in four runs
  on 2026-09-13, on `queue-full`). Filed as board ticket SQ-30.
- A streamed tool-call reply delivers no token until the model has stopped; that is the price of
  telling a call from prose, and keep-alives cover it.
- Two keep-alive tests pin less than they claim to a reader of their names until the keep-alive
  waits go through the injected `TimeProvider` (`docs/FUTURE.md`); their summaries say so.
- Issues #14 and #15 are part-done: `honours a system prompt` and the chat steps in the smoke
  script still assert nothing about the text; the exe has no unit coverage by construction.
  `DeltaSink` and `CutWatcher` moved into Core in D81 without unit tests of their own and are
  reached only through the endpoint suites (#14).
- `BackendCapabilities.Cancellation` is advertised by `PhiSilicaBackend` and the fake's default and
  read by nothing: no endpoint branches on it, `/healthz` omits it, no test asserts it (#17).
- Tool-call compliance is measured across the range (#21, D93 to D96, 2026-09-12): 40/40 over 1 to 25
  tools, 3/3 flat and 3/3 deep schemas, 9/9 under system-prompt pressure, 32/32 at up to 85 % window
  occupancy under `temperature: 0`, and a multi-step round trip that answered without repeating the
  call. PLAN predicted 60–80 %. The streamed shape and the cache round trip were measured on 2026-09-12 (#31, D99): six
  streamed/JSON pairs identical after ignoring per-response ids and key order, `index` only on the
  streamed shape, and the second turn of a tool round trip hit the context cache. One
  compliance failure did occur, under stochastic sampling only — three calls to a tool name never
  offered — and it did not reproduce deterministically.
- Experimental Windows App SDK channel in use (no LAF token); APIs may change between releases.
- Phi Silica returns multi-token progress chunks → callback-based token counts undercount by roughly
  2.3x to 3x; `usage` used `ceil(chars/4)` on both sides (D44) until D80 replaced it with the Phi-3
  tokenizer on Phi Silica (chars/4 had been overcounting English prose by 1.35x). Aion still chars/4.
- A whitespace-free reply (CJK) with `max_tokens` streams nothing in its last stretch: within eight
  tokens of the budget the cutter holds text it cannot settle, stops the model eight tokens past the
  budget, and cuts exactly at the end (D80). Correct, but chunkier than English near the cap.
- The model follows system prompts under the chunk 3 template on both placements; only the bare
  `/debug/generate` path ignores them (D45).
- Activated instance's console window flashes before `--hide-console` hides it; its logs are not
  captured anywhere (file logging deferred).
- `task status` on a missing task exits non-zero with schtasks' own message.

## Review history
- `leftovers` wave (2026-09-12/13, D100 to D102): six issues as ten board tickets; three bound
  cross-family reviews (GPT-5.6 Terra on the Opus-authored #26; Opus on the GPT-authored #34 and on
  the fix round), all accepted with should-fix findings that became in-wave tickets (SQ-25, SQ-27) or
  FUTURE entries; the whole-branch `/code-review` at high effort returned nine findings (a parser
  false positive on an echoed zero-argument definition, a missing `ClientGone` branch on the debug
  endpoint, a cutter fault that held the worker for a whole generation, the smoke's lost token-window
  probe, two wall-clock tests, dead flags, a silent removal in `identity.ps1`, the bundled #26
  commit), seven fixed in three tickets and two deferred with reasons. The board's post-merge gate
  failed once on a 5 ms-delay test that passed 14/14 in isolation; it was gated and the retry passed.
- Chunk 1: in-session hostile review (20 findings, 15 fixed, blocker: env-var key mapping);
  Codex adversarial review (7 findings, all fixed). Commits `b6ae632`, `663fce5`.
- Chunk 2: in-session hostile review (29 findings; blocker: environment lost across activation;
  fixed with supervisor + env forwarding; 6 deferred to FUTURE.md); Codex adversarial review (5 findings, all fixed: callback drain, pid validation, fail-closed loopback, dependency version check, handler teardown).
- Chunk 3: four subagent-implemented tasks (DTOs + validation, `PromptTemplate`, endpoint + pipeline,
  smoke steps + hardware measurements), each with its own spec-and-quality review on landing. A Codex
  adversarial review over the whole branch found a crash the per-task reviews missed: a null element in
  the `messages` array reached the handler and threw HTTP 500 instead of failing validation (D49). A
  separate whole-branch review found a latent bug that would have broken chunk 6: forcing
  `--system-prompt-placement native` rejected every request, not only ones carrying a system message,
  because the check ran before the prompt was rendered. It was dormant since both shipping backends
  advertise native support, but it would have rejected all Aion traffic (D50). Two fix rounds addressed
  both findings; the rest of each review's findings were deferred to `docs/FUTURE.md`'s chunk 3 section.
  Commits `796252b`, `2c4bdc5`, `d6236e9`, `8f533fe`, `91383f4`, `030d49c`.
- Chunk 4: five reviewed tasks (preparation-phase extraction, streaming happy path, failure paths,
  client-side cut, smoke steps + measurements), each with its own review round (D51 to D55). The
  whole-branch review found four defects, all fixed in `7817044`: a cut suppressed every failure
  status rather than only `Cancelled` (D56); the stream read its finish reason before the flush that
  could commit the cap (D57); the holdback and the budget could slice a surrogate pair (D58); a stop
  match was committed before a longer stop string starting earlier had been ruled out (D59). The rest
  went to `docs/FUTURE.md`'s chunk 4 section. Fast-forward merged to `main` on 2026-09-07.
- 2026-09-10 whole-project review: the state docs (`CLAUDE.md`, `PLAN.md`, the handoff, this folder)
  still described chunk 4 as unmerged; fixed. Added the build-and-test workflow. Three low-severity
  code notes were filed in `docs/FUTURE.md` rather than fixed; they became issue #10 and were taken
  on 2026-09-11 as D82.
- Chunk 6 (2026-09-11): built in a forked subagent; a Claude subagent review and a Codex review found
  the same drain-on-exception and late-delta gaps in both adapters (D69), applied before the merge.
- Chunk 5 (2026-09-11): built in-session on `chunk-5-context-cache` with 462 tests, then the Phi
  Silica smoke run (D75). Two adversarial reviews (a Claude subagent and Codex) found the same two defects, the exchange boundary at the first assistant turn and a throwing preflight leaking its context, and Codex a third, the JSON retry inheriting a cancelled token; all applied with ten tests (D76) and the smoke run repeated. Fast-forward merged to `main`.
- OpenAI conformance pass (2026-09-11, D77): checked against the `openai-openapi` schema; a Codex
  review found `message.content` also required-but-nullable and the two `model_not_found` envelopes
  disagreeing on `param`, both fixed with tests. Fast-forward merged.
- D78 (2026-09-11): issue #12 (suffix lookup for truncated conversations) closed without a change
  after the test written first showed the follow-up turn already hits the truncated context.
- D79 (2026-09-11, branch `test-hardening`): the smoke script's vacuous steps from the coverage
  audit (issue #15's first three items) and issue #14's first six tests. Two whole-branch reviews
  (a Claude subagent and Codex) found the auxiliary servers' teardown demoted to a warning, the
  script header contradicting the D52 step, a thread-scheduling race in the client-gone test, two
  test summaries claiming more than they pinned, and a `-NoStart` identity check that belonged to
  launch provenance; all applied, 512 tests, the NPU smoke run passed with four teardown rows.
  Fast-forward merged. Both issues stay open for their remaining items.
- D80 (2026-09-11, branch `issue-13-tokenizer`): measurement first (fourteen lone over-length probes
  counted with the Phi-3.5 tokenizer), then six TDD commits. Codex and a Claude subagent (relaunched
  after a network failure) independently found the same two defects: a fixed 16-character
  "settled" lookback that a run of hyphens refutes (20 are `----` first, 21 are `-` first) and a
  stop-truncated prefix counting more on its own than the model spent (`international` 1 token,
  `internation` 2). Fixed with `StopRequested` and `TokensCovering`; a third fix from the CJK test
  (a budget ending inside a byte-fallback character stops before it). The Claude review's fuzz: 480
  stream trials never over budget, largest re-merge shift 2 tokens against a reserve of 8. Three NPU
  runs passed. Fast-forward merged; #13 closed.
- D81 (2026-09-11, branch `chore/issue-9-post-generation-pipeline`): the duplicated post-generation
  pipeline consolidated, issue #9. Two adversarial reviews (a Claude subagent and Codex) on the
  first two commits, then a whole-branch pass. Neither reviewer found a defect either could
  demonstrate, and both independently built the same equivalence table for `GenerationOutcome`
  against the two copies it replaced. Four things came out of the round: the first-delta wait's
  tie-break (`Task.WhenAny` settled a timeout-against-delta tie by argument order, `Task.WaitAsync`
  by which fired first, and on a preflight-less backend the difference turns an over-length 400 into
  an SSE error event — Codex reasoned it out of the .NET sources, no test can see it);
  `DeltaSink`'s destination changed back from an `Action<string>` to a `ChannelWriter<string>` or a
  `CutWatcher`, so the compiler again checks what the type exists to guarantee; two comments that
  were wrong about framework behaviour; and two rules `GenerationOutcomeTests` claimed in a doc
  comment rather than pinning. 655 tests, the NPU run reproduced D80's numbers. Fast-forward merged;
  #9 closed. `BackendCapabilities.Cancellation` went out as #17 and the missing `DeltaSink` and
  `CutWatcher` unit tests onto #14.
- D82 (2026-09-11, branch `fix/issue-10-review-notes`): the three low-severity notes from the
  2026-09-10 whole-project review, issue #10. One was a real defect (the JSON path's escaping
  non-client cancellation), one was wrong about its own premise (`SseStream.Started`; the change
  stands as a simplification and `docs/FUTURE.md` says not to re-file it), and one was a script
  ordering fix nobody had hit (`identity.ps1 -Install`). The adversarial review found the bug in the
  branch's own test rather than in its code: the client-gone test never reached the clause it named,
  because a disconnect returns `Cancelled` instead of throwing, so it passed identically against the
  old filter; it now holds `CancellationGate` shut, parks the responder inside `MoveNext` and asserts
  the exception name on the `http=0` line, fails without the clause, and was stable over eight runs.
  The same review produced #19. 657 tests, the NPU smoke run passed afterwards. Fast-forward merged;
  #10 closed from the commit, whose "Filed rather than fixed: #19" line also closed #19 by accident.
- Chunk 7 (2026-09-11, branch `feat/chunk-7-tool-calls`, D83): tool-call emulation, issue #3. Three
  passes — two adversarial (a Claude subagent and Codex) and a whole-branch review — fifteen
  findings, all real, all fixed. Both adversarial reviewers independently found the same one: tool
  calls were stored in the cache as the raw model text, while a client sends back the structured
  `tool_calls` array `ConversationKey` hashes, so the two could never match and every turn of an
  agent loop missed the cache. `Keep` and `Compute` now take the turn rather than its text, and the
  stored key describes what the client will send back rather than what the context literally holds.
  The second worth keeping is a framework trap: `JsonDocument.Parse(string)` throws
  `ArgumentException`, not `JsonException`, on invalid UTF-16, so a lone surrogate escaped the parser
  and answered a successful generation with a 502 blaming the backend. A third was in the operator
  signal rather than the code — `tools` and `tool_choice` were still on the accepted-and-ignored
  list, and the test that should have caught it asserted the list's contents with the stale entries
  in its expected value. 866 tests; the NPU run's tool probe called the tool 20 times out of 20.
  Fast-forward merged; #3 closed from the commit, #21 and #22 filed.
- Chunk 8 (2026-09-12, branch `feat/chunk-8-scheduler`, D84 to D92): the scheduler,
  `/v1/completions`, `docs/CLIENTS.md`, issue #4. 38 commits across five tasks, each with its own
  review and fix rounds, then a whole-branch review. The catch that justifies the whole practice: the
  task brief instructed the implementer to put the queue wait *after* the cache lookup and preflight,
  which contradicts PLAN §2.7 and would have shipped a scheduler that serialized generation while
  `CreateContext` and `GetUsablePromptLength` still raced on the shared handle — the exact bug the
  chunk exists to fix. The author could not have found it, having implemented exactly what it was
  told; the reviewer, which had not written the code, did (D84). Two more worth keeping: reading the
  lease off the scheduled task's return value instead of publishing it from inside the closure left
  `finally` with `null` when a streaming client vanished mid-frame, defeating D43 and D51 at once
  (D85); and `ScheduleAsync` published jobs to the channel before arming the state they needed, a
  window that produced two separate bugs and needed `Interlocked` rather than volatile on both sides
  because ARM64 permits the store-buffer reordering a Dekker-pair store/load exposes (D92). The
  whole-branch review then found a partial truncated-turns count that could be stamped permanent by a
  keep-alive landing mid-loop, plus a sibling window its own write-up had wrongly called safe; both
  fixed in `2114c36`. 932 tests; the NPU run passed 28 PASS / 0 FAIL / 0 SKIP / 5 INFO first time.
  Fast-forward merged; #24 to #28 filed for what it knowingly left.
- The #29/#30/#31 wave (2026-09-12, the first Sidequest wave, D97 to D99, PR #36): sixteen tickets.
  Every code change was reviewed by a different model family than wrote it (GPT-5.6 Terra and Luna
  wrote, Opus reviewed), and the whole PR branch had a `/code-review` pass on top. The catches that
  earned it: the first healthz candidate armed its fault flag only before `GenerateAsync` and so
  would have missed the very wedge #30 was filed for (the reviewer read D94 and saw the throw is
  `CreateContext`; a third wedge episode that evening confirmed it on hardware); a Terra review of
  the guard found the folded-placement test vacuous; the PR review found `/debug/generate` counting a
  preflight-known overflow's generic `Error` as a fault (the smoke had shown exactly one), the guard
  tokenizing 37 KB before its cheap character check, four probe-script gaps, and the docs calling
  UTC times local. One tooling failure shaped the wave: three executors in separate worktrees all
  edited through the one serena process and every edit landed in whichever worktree had activated it
  last; briefs since forbid serena in executors. 952 tests; the NPU run on `7a3207c` passed with the
  window cross-check exact. Merged on GitHub as `4b252c4`; #29, #30 and #31 closed with their
  evidence; #33, #34 and #35 filed for what the reviews left.
