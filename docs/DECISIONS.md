# Decisions

Running log of choices and the reasons behind them. Newest at the bottom. Each chunk appends.
For deferred or out-of-scope items see `FUTURE.md`.

## 2026-09-01 — Plan sign-off

**D1. Three projects, not one.** `NpuBridge.Core` (net10.0, no WinRT) holds all logic including the
ASP.NET Core endpoint mapping; `NpuBridge` is the ARM64 exe with the two adapters; `NpuBridge.Tests`
runs against `FakeBackend` through `TestServer`. Reason: tests must never need the NPU or the WinRT
projections, and the adapters must stay thin enough to review by eye.

**D2. .NET 10 / `net10.0`, not .NET 9.** .NET 10 is the current LTS; .NET 9 support ends November
2026. The Aion sample targets net9.0, so this is the one place we knowingly diverge from it. If the
Aion NuGet or CsWinRT 2.1.5 refuses net10.0, drop to net9.0 and record it here.

**D3. Default listener `127.0.0.1:5273`, default backend `phi-silica`.** Port chosen by the owner.
Localhost-only unless `--listen` says otherwise.

**D4. Phi Silica adapter before the core endpoints (chunk 2).** Phi Silica needs package identity and
a LAF token bound to the Package Family Name; the token has a multi-day turnaround. Doing the identity
work first lets the request go out while the backend-agnostic chunks are built.

**D5. LAF token is optional configuration, never code.** `TryUnlockFeature` is always called; both
`Available` and `AvailableWithoutToken` count as success. Token and attestation come from
`appsettings.local.json` or `NPU_BRIDGE_LAF_TOKEN` / `NPU_BRIDGE_LAF_ATTESTATION`, both gitignored and
covered by gitleaks rules. Stable Windows App SDK first; experimental channel only if stable returns
`Unavailable` without a token.

**D6. Package identity via a package with external location (sparse package).** It is the only way a
plain exe reaches Phi Silica, and it is what Microsoft's own Electron guide does. The cert subject is
fixed in chunk 1 because the PFN, and therefore the LAF token, depends on it.

**D7. Windows service verbs are built as specified and verified locally.** Whether an SCM-launched
process gets sparse-package identity is undocumented. Verify in chunk 2; a scheduled-task fallback
is added only if that verification fails.

**D8. Backend capabilities, not a common denominator.** The Aion IDL shows it lacks
`LanguageModelOptions`, system-prompt contexts and `GetUsablePromptLength`. Rather than dropping those
from Phi Silica, `ILanguageModelBackend.Capabilities` advertises them and the pipeline branches:
sampling options passed or ignored-with-one-log, system prompt via context or prepended, overflow
preflight or blind retry.

**D9. Status enums mapped by name per adapter.** `Error` is 6 on Phi Silica and 2 on Aion. A shared
`GenerationStatus` enum in Core is the only thing the pipeline sees.

**D10. Tool calling buffers the whole reply when `tools` is present.** Reliable `tool_calls`
detection beats first-token latency for agent loops. SSE keep-alive comments cover the wait.
Speculative streaming is in `FUTURE.md`.

**D11. Contexts are disposed on anything but `Complete`.** After an error, cancel, or overflow the
runtime's context state is unknowable, so the cache never keeps such a context. Evicted contexts are
disposed. Tests count creates against disposes on the fake.

**D12. Usage token counts are estimates.** Completion tokens = `Progress` callbacks (may undercount
under Phi Silica's speculative decoding); prompt tokens = chars / 4. Documented for clients.

**D13. Windows App SDK auto-bootstrap disabled.** `WindowsAppSdkBootstrapInitialize=false` so
`--backend fake` starts even without the runtime; only the Phi Silica adapter bootstraps.

**D14. Real backends installed and smoke-tested on this machine.** The dev machine is the Copilot+ PC.
Aion's framework MSIX and the Phi Silica identity package are installed here; `scripts/smoke.ps1`
runs here as part of each adapter chunk's definition of done. Unit tests still never depend on them.

**D15. Secret hygiene from day one.** gitleaks 8.30.1 with project rules for the LAF token,
attestation sentence, literal `TryUnlockFeature` arguments and cert passwords; enforced by
`.githooks/pre-commit` and a GitHub Actions workflow.

## 2026-09-01 — Chunk 1 (skeleton)

**D16. Configuration keys are flat property names; the CLI normalises to `Key=value`.** `--backend fake`
becomes `Backend=fake` before it reaches `AddCommandLine`, so appsettings.json, `NPU_BRIDGE_BACKEND`
and the command line all address the same key with no switch-mapping table to keep in sync. A
hand-written binder (`BridgeOptionsBinder`) accepts `on/off/yes/no/1/0` and fails fast naming the key.

**D17. The backend loads in the background; the HTTP server is up immediately.** `BackendLifecycle`
runs `InitializeAsync` on a worker task and `/healthz` reports `loading` (503, `Retry-After: 10`) until
it finishes, with `first_run_compile_likely` set after 60 s. A backend that cannot be built on this
machine is represented by `UnavailableBackend`, whose failure shows up in `/healthz` rather than as a
crash at startup.

**D18. Cancellation is a status, not an exception, at the backend boundary.** `GenerateAsync` returns
`GenerationStatus.Cancelled` with partial text. Adapters translate the runtime's cancellation
exception; the pipeline never has to catch `OperationCanceledException` from a backend.

**D19. The exe is a console-SDK project with a framework reference to ASP.NET Core.** Not
`Microsoft.NET.Sdk.Web`: no wwwroot/launchSettings noise, and Windows-service tooling expects a
console app. Kestrel and the endpoint mapping come from `NpuBridge.Core`.

**D20. `sc.exe`, not `ServiceController`/`ServiceInstaller`.** The verbs are four short commands; a raw
argument string is easier to reason about than sc.exe's quoting via `ArgumentList`. The install
command bakes settings into the service's command line as `--option value` pairs so the service and
the interactive exe share one parser.

**D21. Analyzer level `latest-recommended` with warnings as errors, minus CA1848/CA1873/CA2007.**
LoggerMessage source generation is disproportionate for a handful of log lines; `ConfigureAwait` is
applied by hand in Core where it matters.

**D22. Solution file is `.slnx`.** That is what `dotnet new sln` produces on .NET 10; every `dotnet`
command accepts it.

**D24. Package identity is granted by activation, not by path (verified 2026-09-01).** With the sparse
package registered, `NpuBridge.exe` launched directly reports no identity; launched through its
application user model id (`explorer.exe shell:AppsFolder\NpuBridge_jtas4mnxdyzpe!NpuBridge`) it reports
`package_identity: true`. Consequences: (a) the Phi Silica path must start the process through package
activation, so chunk 2 adds self-relaunch via `IApplicationActivationManager` when `--backend phi-silica`
runs without identity; (b) an SCM-started Windows service is launched by path and cannot carry identity,
so the auto-start story for Phi Silica is a logon scheduled task that activates the package, while the
service verbs remain valid for `aion` and `fake`. Supersedes the "verify in chunk 2" half of D7.

**D25. Environment variables go through a custom source (chunk 1 review, blocker).** The stock
prefixed provider keeps underscores, so `NPU_BRIDGE_LAF_TOKEN` never bound to `LafToken`.
`BridgeEnvironmentVariablesSource` strips the prefix and underscores and maps onto the option property
names, so both `NPU_BRIDGE_LAF_TOKEN` and `NPU_BRIDGE_LAFTOKEN` work. The unprefixed provider that
`WebApplication.CreateBuilder` adds is removed so `VERBOSE=1` in a shell cannot flip settings.
`appsettings.local.json` (gitignored) is loaded above `appsettings.json` for secrets.

**D26. Secrets are refused on the service command line.** `service install --laf-token …` is
rejected because sc.exe writes the command line to `ImagePath`, readable by every local user. The
token belongs in `appsettings.local.json` next to the exe. Service-mode logs go to the Application
event log (source `npu-bridge`) at Information level for our categories.

**D27. The fake backend behaves like the runtimes in the ways that bite.** Deltas are delivered on a
thread-pool thread (as WinRT `Progress` does), use before `InitializeAsync` throws, disposal is enforced
on every member, and a first-token delay is separate from the per-token delay. Tests that pass
against the fake should not pass vacuously against the NPU.

**D28. `BackendLifecycle` owns the backend (Codex review, chunk 1).** The backend is no longer a
container-owned disposable; the lifecycle disposes it after initialization finishes, with a 15 s grace
period for a runtime whose `CreateAsync` ignores cancellation. Prevents tearing down a WinRT model
handle underneath its own creation during shutdown.

**D29. One configuration composition for the server and the service verbs.** `BridgeConfiguration`
defines `appsettings.json < appsettings.local.json < NPU_BRIDGE_* < CLI` once; `service install|…`
resolves `ServiceName` through it, so a name set in the JSON file or the environment targets the same
service the server would run as.

**D30. 404s use OpenAI's `invalid_request_error` type.** OpenAI has no `not_found_error`; unknown models
and endpoints are `invalid_request_error` with codes `model_not_found` / `unknown_endpoint` and HTTP 404.

## 2026-09-01 — Chunk 2 (Phi Silica adapter)

**D31. Experimental Windows App SDK channel, no LAF token.** Verified on this machine: with stable
2.4.0 `TryUnlockFeature` returns `Unavailable` and no token was available; with 2.4.1-experimental the
model loads and generates although the LAF probe still says `Unavailable`. Microsoft's troubleshooting
page recommends experimental releases for exactly this reason. Costs: the experimental runtime is a
separate framework family (`Microsoft.WindowsAppRuntime.2-experimentalB`) that `identity.ps1` now installs
from the NuGet payload, the sparse manifest must name it, and experimental APIs may change between
releases. Switching back to stable is two version strings plus one manifest line, documented in the manifest.

**D32. LAF failure is logged, not fatal.** The adapter records `laf_status` in `/healthz`, warns, and lets
`GetReadyState`/`CreateAsync` decide; an `E_ACCESSDENIED` from those is turned into a message that names
the Package Family Name to request a token for. This keeps one code path for both channels.

**D33. Self-relaunch through package activation, verified.** `IApplicationActivationManager.ActivateApplication`
passes the argument string to a packaged Win32 exe's command line (the child honoured `--listen`), so
options survive the relaunch. The child is started with `--self-relaunch off` so a misconfiguration can never
loop. The parent exits 0 after printing the child's pid.

**D34. Logon scheduled task is the Phi Silica auto-start; the service stays for aion/fake.**
`task install` creates an `ONLOGON` task for the current user with `/IT` (interactive session) and `/RL
LIMITED`, action = the exe by path with `--hide-console`; the exe relaunches itself with identity.
`service install --backend phi-silica` is refused with an explanation. The chunk 1 question "do the
Windows AI APIs work from a non-interactive session" is moot: `/IT` keeps the process in the interactive
session, and the service path is not used for Phi Silica at all.

**D35. `POST /debug/generate` is a permanent diagnostic.** One prompt straight into the backend with timing
(ttft, tok/s), bypassing template, cache and scheduler. It let the adapter be verified on the NPU before the
OpenAI endpoints existed and stays for debugging what the model does with a literal prompt. Not part of
the OpenAI surface; same localhost listener.

**D36. Build output path is fixed to `bin\<Config>\<tfm>\win-arm64`.** `AppendPlatformToOutputPath=false`
so project-level and solution-level builds agree; the sparse package is registered against that folder and
a second exe copy in `bin\ARM64\...` silently broke relaunch once.

**D37. The by-path process supervises the activated instance (chunk 2 review).** Activation makes the
child a stranger to the parent: the parent used to exit 0 immediately, so a scheduled task could not stop
the server, `task status` was always "Ready / last result 0", a child that died at startup was reported as
success, and a second `schtasks /Run` produced a port fight. Now the parent waits on the child's pid,
forwards its exit code, kills it on Ctrl+C or its own exit, and reports an immediate child death; the
child receives `--supervisor-pid` and stops when that process disappears (covers `schtasks /End`, which
is `TerminateProcess`). Net effect: Ctrl+C, `/End` and crashes behave like a single ordinary process.

**D38. Environment is re-expressed on the child's command line.** Package activation does not inherit
the parent's process environment, so `NPU_BRIDGE_*` set in the shell used to vanish across the relaunch
(review blocker). `RelaunchArguments` forwards every effective, non-secret `NPU_BRIDGE_*` value as a CLI
option (CLI values still win). Secrets set only in the parent's process environment are dropped with a
warning: they belong in `appsettings.local.json`, which the child reads from the exe folder; secrets given
on the parent's command line are forwarded unchanged since they were already visible there.

**D39. No unsolicited model download.** `EnsureReadyAsync` (a multi-GB Windows Update download) runs only
with `--install-model`; otherwise `NotReady` fails with the Settings path. Follows Microsoft's consent
guidance; a hidden logon task must never start it silently.

**D40. `/debug/generate` is loopback-only** (403 otherwise) because it bypasses the request queue that
chunk 8 adds. Routing it through the scheduler is deferred. (Superseded by D90: it goes through the
scheduler since chunk 8, and creates no context until its turn. The loopback rule stands.)

**D41. Progress callbacks are drained before `GenerateAsync` returns (Codex review, chunk 2).** WinRT
does not guarantee the last `Progress` invocation has finished when the operation completes. The
adapter counts in-flight callbacks, closes a gate on completion so stragglers are dropped instead of
delivered, and waits (bounded, 5 s) for the count to reach zero. Callers can therefore dispose the
context or reuse it the moment the call returns.

**D42. Supervisor halves validate the process, not just the pid.** Pids are reused; both the parent
(child pid from activation) and the child (`--supervisor-pid`) check image name and a plausible start
time before waiting on or killing anything, and the parent unsubscribes its Ctrl+C/exit handlers.
`/debug/generate` also fails closed when the remote address is unknown.

**Observations recorded for later chunks.** (1) Phi Silica's `Progress` callback delivers multi-token
chunks under speculative decoding (11 callbacks for ~25 words), so `completion_tokens` estimated from
callbacks undercounts; a character-based estimate may be better. (2) A strict system prompt set through
`CreateContext(systemPrompt)` was not followed ("I am Ada" → "AI Assistant"); chunk 3 should test whether
rendering the system text into the user turn works better on this model.

**D23. `identity.ps1` signs from the certificate store, never from a PFX on disk.** `signtool /sha1
<thumbprint>` uses the key in `CurrentUser\My`; only the public `.cer` is exported (to
`packaging/out`, gitignored) for the one-time `TrustedPeople` import.

## 2026-09-05 — Chunk 3 (non-streaming chat completions)

**D43. One fresh context per request, disposed on every path that creates one.**
`/v1/chat/completions` creates its context immediately before generating and disposes it in a
`finally`, so success, prompt overflow, content filter, a backend `Error` or `Cancelled` status, a
thrown backend exception and a client abort all release it. Four rejections return *before* a context
exists and so never create one: an unreadable or non-JSON body, a validation failure, a backend that
is not `Ready`, and the forced-`native` system-prompt placement conflict. The disposal guarantee is
therefore about the paths that create a context, not literally about every path. Tests pin it on the
failure paths and not only the happy one, and each leak case also asserts how many contexts were
created — one where the backend is reached, zero for the early rejections — so the guard cannot be
satisfied vacuously. The cache is chunk 5, so nothing is reused yet.

**D44. `completion_tokens` is `ceil(chars/4)`, not the progress-callback count.** Supersedes the
estimate proposed in PLAN §2.2. Measured on this NPU in one generation: 29 callbacks for 367
characters, so the character estimate is **3.17x** the callback count. Phi Silica batches tokens per
callback under speculative decoding, so counting callbacks undercounts by roughly three times. Both
sides of `usage` use the same formula and both are documented as estimates.

**D45. The system prompt is delivered natively by default, and the model does follow it.** This
reverses the working assumption recorded in the chunk 2 observations. Measured on this NPU with the
chunk 3 prompt template, system prompt "You are Ada. Always answer with exactly the two words: I am
Ada.": **both** placements returned "I am Ada." The same run still shows `/debug/generate` ignoring
its system prompt and answering "AI Assistant". The difference is not the placement but the
rendering: a bare prompt is ignored, a transcript ending in
`### Reply as the assistant to the latest message.` is obeyed. So the earlier finding was a property
of the raw diagnostic path, not of the model. `--system-prompt-placement auto|native|prompt` (default
`auto`, native when the backend advertises the capability) stays, because it is what produced this
measurement and it is how chunk 6 will check Aion, which has no native system context at all.

**D46. `stream: true` returns 400 until chunk 4.** Returning a non-streamed body to a client that
asked for server-sent events would hang or mis-parse it. A clear error beats a wrong success.

**D47. Unsupported parameters are accepted, ignored, and warned about once per process.**
`max_tokens`, `max_completion_tokens` and `stop` (the client-side cut is chunk 4), `tools` and
`tool_choice` (chunk 7), sampling options on a backend without the capability, and the OpenAI
parameters this bridge has no answer for. The guard is a `ConcurrentDictionary` on a DI singleton, so
it is thread-safe and cannot leak between tests.

**D48. `scripts/smoke.ps1` gates each feature separately and skips rather than fails.** One gate for
three features meant that building only the non-streaming endpoint made a correct chunk 3 look broken:
the pre-chunk script fails three steps against the fake backend. Streaming and tool calling now report
SKIP with the chunk that owns them. Skips and informational steps never affect the exit code. The
client-disconnect step additionally skips on the fake backend only, because a backend with no token
delay finishes before the abort window opens; it still runs, and can still fail, on phi-silica and
aion, where it passes.

**D49. Validation rejects what the deserializer will happily produce.** A `messages` array containing
a null element returned HTTP 500 (found by the Codex adversarial review, reproduced against the running
exe). `System.Text.Json` permits null elements despite the nullable annotation, and validation runs
outside the handler's exception guard. A null element and a `text` part with a missing or null `text`
are both 400 now. A user message with **missing or null `content` stays valid** and renders as empty:
that is deliberate, not an oversight.

**D50. Forcing a system-prompt placement only fails a request that actually has a system prompt.**
Found by the final whole-branch review. `--system-prompt-placement native` on a backend without a
native system context used to reject *every* request, because the check ran before the prompt was
rendered and so consulted nothing about the request. A plain single-turn request with no system
message needs no native context and now succeeds; only a request carrying system text is rejected,
with the same error. Dormant on both shipping backends, which advertise the capability. It would have
rejected all traffic in chunk 6, where Aion has no native system context at all. Verified after the
fix that `auto` and `native` still deliver the system text through the native context and `prompt`
still folds it into the prompt body.

**D51. A streamed generation is cancelled, drained, and only then has its context disposed.** Found by
review of the chunk 4 streaming path. A response write that throws — which is what a client disconnect
looks like — unwound straight into the `finally` that disposes the model context while the generation
task was still running against it. On the real backends that is a use-after-dispose on a live WinRT
handle, not merely an unobserved task; D11 already says a context outlives nothing but its own
operation. The exit is now ordered: cancel the generation's own linked token, await the task to
completion however it ends, then dispose. Awaiting a cancelled operation is the drain the plan asks for
in the scheduler ("still awaits the op to completion before picking the next job"), and it is why the
generation gets a linked source of its own rather than the request's token. The fake backend grew a
`CancellationGate` so a test can hold a generation open after the client has gone — a runtime whose
in-flight operation cannot be stopped on demand is exactly the case this ordering exists for, and
without it the fake stops so promptly that the window cannot be observed. Removing the drain makes that
test fail; that was checked, not assumed.

**D52. The stream's headers are committed by the first frame, not by the handler's first line.**
(Amended by D89: since chunk 8 the queue wait, the cache lookup and the preflight all happen inside
this window, so a refusal that would have been a 400 can arrive as an SSE error event instead.) Also
found by review: an over-length prompt reached a streaming client as HTTP 200, an empty reply and
`finish_reason: "stop"` — the model reported as having answered when it refused — while the JSON path
correctly returned 400 `context_length_exceeded`. Writing the role chunk up front is what forced that:
it spent the status line before the outcome was known. Nothing is written now until either the first
delta arrives or the keep-alive interval elapses, so a failure discovered before the first token keeps
the ordinary status and the ordinary body (`GenerationFailure` produces both forms, so the two response
shapes cannot drift), and only a failure after the first byte travels as `data: {"error":...}` followed
by `[DONE]`. `stop` is unreachable for a prompt that did not fit, on either side of that boundary.
Content filtering is unchanged and is not an error: a successful response with a `content_filter`
finish. The cost is that a client sees no response headers until the first token or the first
keep-alive, so the keep-alive runs on **two clocks**: the first comment is due after 1 second, every one
after it at the 15-second interval. They answer different questions. The interval is about proxies
calling a connection idle; the first delay is how long a client waits on response headers, and clients
time that out sooner — httpx allows 5 seconds by default, so a single 15-second interval would have made
a stalled generation look dead to an ordinary client. Both are `StreamingOptions` properties rather than
constants because a test drives them in milliseconds instead of sleeping through them.

The first delay was reasoned as needing to stay wider than the time a backend takes to report the
prompt-too-long verdict, because once the keep-alive commits the headers that verdict can no longer be
a 400. `scripts/smoke.ps1` now measures it, and **the measurement overturns that reasoning on this
hardware**. Phi Silica, given a 225,042-character prompt, does not report a prompt-too-long verdict at
all: it returns a generic error after **26.5 seconds** (`Error: Unspecified error`; 502 on the JSON
path). Against a 1-second first keep-alive that is not a close call — it is twenty-six times over, and
no plausible delay would win that race. The streamed request emitted three keep-alive comments, then
the role chunk, then `data: {"error":...}`, then `[DONE]`: the specified after-the-headers behaviour,
working correctly. So the deferral does **not** buy a real 400 for an over-length prompt here, and this
entry should never have implied it would.

What the deferral does buy is the correct HTTP status for the failures that are decided *quickly*,
which is most of them: a backend that is not ready or failed to initialize (503), an adapter that
throws on the way in (502), and a backend that does report `PromptLargerThanContext` promptly (400
`context_length_exceeded` — the fake does, and Aion is untested). Request validation is settled before
this handler is reached at all and never depended on the deferral. Those all land in single-digit
milliseconds, so the 1-second delay is generous for every one of them.

**The 1-second default stands, for a different reason than the one written above.** It is not buying a
race against a slow backend verdict — that race is unwinnable and does not need winning, because a
failure after the headers correctly becomes an in-stream error frame. It is bounding how long a client
waits on response headers, and 1 second against httpx's 5-second default read timeout is the whole
justification. Do not lower it, and do not raise it in the hope of catching a slow verdict: 26 seconds
of silence would break ordinary clients to salvage a status code for one error case that the stream
already reports faithfully. The measured numbers behind this paragraph, and what they mean for chunk
5, are in D55.

**D53. `max_tokens`, `max_completion_tokens` and `stop` are cut client-side, and the cap is measured
in characters.** Neither Windows runtime offers either feature: Phi Silica's `LanguageModelOptions`
carries sampling knobs and nothing else, and Aion has no options object at all. So the bridge watches
the text as it arrives and cuts it itself, on both response shapes. They leave D47's accepted-and-
ignored list as of this chunk and no longer warn.

Both are **best effort** in one specific sense worth being plain about: the model is not steered by
them, it is interrupted by them. Every token up to the cut is generated either way, so a cap saves
latency, not work the model already did, and a stop string is removed from the reply rather than never
produced. A cut cancels the generation's own linked token, which is why `Cancelled` is no longer
automatically the 502 of D51's exit path: the cut is consulted before the status mapping on both
shapes, and content filtering still outranks both, because that status means "do not hand this text
on" and a cut is not a licence to.

The cap is a **character** budget, `cap * 4`, and not a count of progress callbacks. `usage` reports
`ceil(chars/4)` (D44) and a callback is several tokens under speculative decoding, so a cap counted in
callbacks would let a reply report roughly three times the completion tokens the client allowed — a
response contradicting its own usage block. Cutting at exactly `cap * 4` characters makes
`completion_tokens` land on the cap and never above it. A zero or negative cap is a 400 rather than an
empty completion; when both fields are present the smaller wins; an empty stop string is dropped
rather than honoured, since it matches at position 0 of everything.

The streaming path has a problem the JSON path does not: text already written cannot be recalled, and
a stop string can straddle two deltas — `"EN"` then `"D"` is a hit on `"END"` that neither delta
contains. So the streamed reply is always **held back by the longest stop string's length minus one
character**, and those characters are released only by further text proving no stop string starts
inside them, or by a flush when the generation ends without a hit. Both ways of getting the size wrong
are worse than not having the feature — too little leaks half a stop string to the client, too much
silently drops the end of an ordinary reply — so both are tested directly, the second with a reply
that ends in `EN` while `END` is the stop string. One `OutputCutter` implements it, and the JSON path
runs the same class over the whole text in one call rather than a second implementation, so the two
shapes cannot decide differently; a test asserts that across every split point of the same text.

**The cap waits on that same lookahead**, which review caught and the first implementation got wrong. A
stop string can straddle the budget boundary too: with `stop: "dEFG"` and a four-character budget,
`"abcd"` then `"EFGH"` is a stop hit at character three, so the reply is `"abc"` finishing `stop`.
Committing the cap the moment the budget was reached decided that before the evidence arrived — the
streaming path answered `"abcd"` finishing `length`, disagreeing with the whole-text answer and putting
half a stop string on the wire. The cap is now committed only once the text runs at least `Holdback`
characters past the budget, or the generation ends: a stop string beginning one character before the
budget ends by exactly `budget + Holdback`, so that is precisely enough to rule one out. The same
deferral fixes a smaller wrong answer for free — **a reply landing exactly on the budget finishes
`stop`, not `length`**, because the cap now fires when text is actually dropped rather than when the
budget is touched, and the streaming path cannot tell those apart until it has looked past the budget.

Two smaller consequences of the cut being a thing the bridge does rather than a thing the model does.
The held tail is **not** flushed when the runtime reports the answer withheld: the deltas already on
the wire cannot be recalled, but the held ones have not been written and the bridge now knows they were
filtered, so writing them there would be the one place a filtered reply gained text. And streamed
`usage` is counted off the cutter's content length unconditionally rather than off the backend's
returned text, so it always describes what actually went out — after a cut that text runs past the wire,
after a filtered reply it is empty while deltas did go out.

One asymmetry survives, deliberately. The JSON path cuts the text the backend finally reports, while
the streaming path cuts the concatenated deltas. Both adapters accumulate their deltas into exactly
that text, so the two agree, but a future adapter whose reported text differs from its delta stream
would make them differ too. That is now a **written requirement on `ILanguageModelBackend`** —
`GenerationResult.Text` is the concatenation of the deltas delivered, `ContentFiltered` excepted —
rather than an assumption two call sites happen to share, because chunk 6's Aion adapter has to inherit
it and the interface is where its author will read it. The JSON path's early cancellation is therefore
only an optimisation: the
authoritative cut is applied afterwards to the final text, so the answer does not depend on which
deltas the watcher happened to see before the cancellation landed. That cancellation is scheduled with
`CancelAfter(TimeSpan.Zero)` rather than called outright, because it is raised on the backend's
callback thread, and cancelling there can complete the generation's own `await` inline — re-entering
the adapter while it is still inside the callback, where Phi Silica would spin for its five-second
drain timeout waiting for a callback that cannot return until we do.

**D54. Two tests were asserting on the clock; they assert on ordering now.** Review reproduced both
failing under load, at roughly 8 in 10 and 6 in 10. The non-streaming client-disconnect test cancelled
after a fixed 150 ms, which on a busy machine fires before the request reaches the handler — it then
proved that a request nobody started leaked no context, and passed for the wrong reason or failed for
one. It waits for the fake to have actually been called before disconnecting, which is what the
streaming disconnect tests already did by reading a byte off the response first. The first-keep-alive
test asserted that response headers arrived inside 350 ms while the first token was 400 ms away; the
behaviour it is guarding is an ordering, not a duration, so `FakeBackendOptions` gains a
`FirstTokenGate` (the third gate, after `InitGate` and `CancellationGate`) that holds the generation
after the prompt-length verdict and before its first token. The test releases it only once it has the
headers, so the ordering is a property of the arrangement; the send's cancellation token is a deadlock
guard, not a measurement. Verified by running the whole suite twenty times in parallel, which pushed
individual runs from 2 s to 18 s and stayed green.

**D55. Phi Silica does not report an over-length prompt as over-length, and takes 26 s to say
anything; chunk 5 must use the preflight instead.** (Amended by D80: a prompt moderately over the
window does get `PromptLargerThanContext`, in about 600 ms; the 225 KB prompt below is the case that
gets the generic error. The preflight stays the decision, and it answers in UTF-8 bytes.) Measured on this machine by `scripts/smoke.ps1
-Backend phi-silica` with a 225,042-character prompt, and confirmed directly outside the script. Three
facts, all from that run:

- `GenerateAsync` ends in a **generic** `Error` — `The model failed to generate a response. Error:
  Unspecified error` — not `PromptLargerThanContext`. The bridge maps that faithfully: 502 on the JSON
  path, an in-stream `data: {"error":...}` frame on the streaming path.
- It takes **26,512 ms** to reach that verdict. Nothing about the failure is cheap or early.
- `GetUsablePromptLength` — the preflight — answered **13,429 of 225,042 characters usable**, up
  front, and got it right.

So the status enum is not a reliable overflow signal on this backend, and waiting for the generation
to refuse costs half a minute per attempt. **Chunk 5, which owns overflow handling and the
history-truncation loop, must drive both off the preflight**, not off a failed generation's status:
ask `GetUsablePromptLength` before generating, and treat "usable < prompt" as the overflow condition.
A truncation loop built on the generation's status would cost 26 s per iteration and could not tell an
over-length prompt from any other backend fault.

A consequence worth stating plainly, since chunk 3 shipped the mapping: **400 `context_length_exceeded`
may be unreachable on Phi Silica** without a preflight check. `GenerationFailure` still maps
`PromptLargerThanContext` to it, and the fake backend still produces it (the tests are real tests, of a
real mapping), but no Phi Silica generation this project has observed has returned that status. Aion
cannot even be asked: its API has no `GetUsablePromptLength`, so it carries no `PromptLengthPreflight`
capability, and chunk 6 should expect a third behaviour rather than assume either of these two.

This is also why D52's over-length reasoning was rewritten rather than annotated: the header deferral
was justified partly by a race it cannot win on this hardware, and the honest version says so.

## 2026-09-07 — Chunk 4 whole-branch review

Four independent reviewers over `main..HEAD` — three Claude passes (streaming races and `IDisposable`
lifetime; OpenAI wire conformance; correctness and untested branches) and one Codex pass. Three of the
findings below were reported by three or four of them independently, which is the reason they are fixes
rather than deferrals. Everything they raised that is not fixed here is in `docs/FUTURE.md` under the
chunk 4 deferrals; the chunk was not widened to absorb it.

Two things the review confirmed rather than changed, both worth recording because they were the reasons
this pass existed: the D51 cancel-drain-dispose fix is genuinely complete (every path that creates a
context was enumerated and each disposes exactly once, after the generation ends), and the extraction
of `ChatRequestPreparation` is behaviour-preserving (compared statement by statement against main's
endpoint by two reviewers, including the D50 ordering and the `Retry-After` rule).

**D56. A cut may reinterpret `Cancelled` and nothing else.** Both shapes gated the whole of
`GenerationFailure.FromStatus` on "a cut fired", which suppressed every failure status rather than the
self-inflicted cancellation it was written for. A backend `Error` arriving alongside a cut was answered
with HTTP 200, truncated text and `finish_reason: "stop"` — the client had no way to know the
generation faulted. On the JSON path this was a **regression against main**, which always answered 502,
and it did not even need a cancellation to have happened: the whole-text cut can report a cap the
watcher never cancelled for, because with a long stop string the watcher is still waiting for lookahead
when the generation ends. It matters most on this hardware, since D55 established that a real Phi Silica
prompt overflow surfaces as exactly that generic `Error`. The guard is now
`cut && status is Cancelled`. On the stream, `IsCut` is deliberately still read *before* the flush: a cut
committed while streaming is the only kind that could have caused the cancellation.

**D57. The finish reason is read after the flush, not before.** `Flush()` can be the call that commits
the cap — it is deferred until the text runs `Holdback` past the budget (D53), so a reply ending inside
that window is only cut at the end. Reading `FinishReason` first labelled such a request `stop` while
the JSON path, which reads it after its own flush, called the same generation `length`. A client that
resumes on `length` stopped silently instead. This is the drift the shared-cutter design exists to
prevent, and it survived four earlier task reviews because every existing cross-shape case had the reply
overrun the budget by more than `Holdback`, skipping the disagreement region entirely.

**D58. Neither the holdback nor the budget may slice a surrogate pair.** Both are character counts with
no relationship to character boundaries, so both could land between the halves of an astral character.
That does not delay the character, it destroys it: each slice is serialized as its own JSON string and
`System.Text.Json` writes a lone surrogate as U+FFFD, so an emoji split across two SSE frames reaches
the client as two replacement characters no client can reassemble. Slice indexes now step back one when
they would split a pair, which on a cap also keeps `ceil(chars/4)` under the budget rather than over it.
No test in the suite used a non-BMP character, so nothing could have caught it.

**D59. A stop match is committed only once no longer stop string starting earlier can still form.** With
`stop: ["abcd", "b"]`, `"ab"` + `"cd"` cut at index 1 while the same text as one delta cut at 0 — the
reply depended on how the runtime happened to batch its callbacks, which is precisely what the holdback
exists to prevent. The holdback was being applied to the *release* but not to the *cut*. A match is
settled when `_pending.Length - stopAt >= Holdback`, which is the same evidence rule in the same place.

**D60. The dispose guarantee is not allowed to depend on the cancel succeeding.**
`await generationCts.CancelAsync()` was the one statement in the streaming handler outside a `try`, and
it stands immediately before the drain and `context.Dispose()`. `CancelAsync` faults when a registration
on the token throws, and CsWinRT registers one that calls `IAsyncInfo.Cancel()` on the live WinRT
operation — a COM call that can fail rather than no-op. A throw there skipped both the drain and the
disposal, leaking the handle D43 guarantees is released: worse than the D51 defect it sits beside, which
disposed too early rather than never. Now guarded. Relatedly, the second `catch` no longer excludes
`OperationCanceledException`, so the two clauses really are exhaustive and "no unhandled exception
escapes" is unconditional rather than nearly always true — the gap was "cancelled, but not by the
client", which an adapter that lets the runtime's cancellation escape produces through the cut's own
linked token.

**D61. A test that fails one run in 65 is a broken test.** `Streaming_never_leaks_a_stop_string_split
_across_deltas` asserted `DoesNotContain("EN")` over the whole wire body, but the chunk id is repeated
on every frame and is Crockford base32 — an alphabet containing both E and N. Measured over 200,000
generated ids, 1.54% contain `EN`. The ids are stripped before the assertion now; the claim is about the
text the bridge wrote, not the identifier it drew.

## 2026-09-10 — Review fixes (issues #5 to #8)

The 2026-09-10 code review of the merged tree filed four defects as GitHub issues rather than widening
chunk 4. Fixed on `review-fixes`, one commit per issue, each test-first: the failing test was watched
to fail for the reported reason before the fix (the JSON case of #5 fails by crashing the test host,
which is the reported symptom exactly). Chunk 5 builds on this code, so these landed before it.

**D62. "The cut caused this cancellation" is a fact the handler records, never an inference from the
cutter.** Both shapes reinterpret a `Cancelled` status as a successful cut (D56), but they decided "did
the cut fire" from different inputs: the JSON path from the whole-text cut, which includes a cap
committed by `Flush()`, the stream from `IsCut` before its flush. A backend reporting `Cancelled` on
its own, with a reply ending inside the lookahead window, was HTTP 200 `finish_reason: "length"` on
one shape and a 502 on the other — the drift `GenerationFailure` exists to prevent. Each handler now
sets a flag beside the call that cancels, and `selfCancelled` is `flag && status is Cancelled` on both.
Reachable today only with a backend that reports `Cancelled` unprompted, which `PhiSilicaBackend`
never does; chunk 6's adapter has not been written yet, and this is the rule it inherits. (#8)

**D63. The backend's callback thread never cancels anything.** The JSON cut called `CancelAfter(0)`
from the delta callback. Zero delay was chosen so the cancel would not re-enter the adapter inline,
but it moved the cancel onto a timer thread, and a registration that throws there — CsWinRT's
`IAsyncInfo.Cancel()` on the live WinRT operation is one — is rethrown by `TimerQueueTimer.Fire` with
nothing above it: the process terminates. Reproduced in the suite (the test host crashed with the
registration's exception on the timer thread). The callback now completes a `TaskCompletionSource`
(with `RunContinuationsAsynchronously`, so the continuation stays off the callback thread too), the
request task races it against the generation, and cancels on its own thread inside a `try`, as the
streaming path's finally already did. The stream's in-loop cancel at the cut had the same hole with a
milder symptom — the fault took the unfiltered catch and the client got its capped content followed
by an error event instead of `finish_reason: "length"` — and now goes through the same guard. This also
closes the chunk 4 deferral about the callback touching a disposed `CancellationTokenSource`: it no
longer touches one. (#5)

**D64. A release never ends on a high surrogate.** D58 stepped a slice index back when it fell between
the halves of a pair, but only when the index was strictly inside the text. With no stop strings the
holdback is zero and the release is everything pending, so a delta ending on a high surrogate — a
runtime that split the pair across two callbacks — went out whole, and `System.Text.Json` wrote the
half as U+FFFD; the low half followed as a second U+FFFD. Any unrelated stop string hid it, because the
holdback then happened to catch it, so the output depended on a setting with nothing to do with it.
The release now holds the high half back regardless of holdback; the next delta or the flush releases
it, so a genuinely lone surrogate is delayed by one delta and never dropped by the cutter (on the
wire a lone half is U+FFFD either way, which is the model's doing, not the bridge's). Reachable only if the
runtime ever splits a decoded UTF-16 pair across callbacks, which is not established either way on
Phi Silica; the cutter should not depend on it. (#6)

**D65. The Phi Silica adapter returns the delivered deltas as the text, on every status; the runtime's
own text is a cross-check.** Chunk 4 made "`GenerationResult.Text` is the concatenation of the deltas
delivered" load-bearing (the JSON path cuts the returned text, the stream cuts the delta stream, and
they agree only because those are the same characters), but `PhiSilicaBackend` returned the runtime's
`result.Text` on `Complete` and fell back to the accumulated deltas only when that was empty, and it
silently dropped a `Progress` callback that arrived after its completion barrier. `FakeBackend` honours
the contract by construction, so the suite cannot see either. Of the two fixes the issue offered —
honour the contract in the adapter, or relax it and cut the JSON path over the deltas too — the first
is taken: one adapter-local rule beats a pipeline-wide change of what `Text` means, and chunk 6's
adapter inherits the same rule. A mismatch between the runtime's text and the deltas, and a callback
after a completed generation ended (one after a cancelled generation is expected and only logged at
Debug), are each a Warning and a counter in `/healthz` (`text_mismatches`, `late_deltas`).
`scripts/smoke.ps1` gained a text-contract step that sends one prompt on both shapes and asserts both
counters read zero, reporting (not asserting) whether the wire texts matched. **Verified on hardware
2026-09-11** on build 29648, after the owner rolled back the Insider flight to 29661 that had left the
Phi Silica workload packages unregisterable (`0x80073CF6`, access denied registering the
`windows.accessControl.undocked` extension, elevated or not; the model reported `NotReady` and the
smoke test could not reach a generation, which is why #5, #6 and #8 merged a day ahead of #7).
`smoke.ps1 -Backend phi-silica -Port 5298` passed every step: the text-contract step read
`text_mismatches=0 late_deltas=0` over every completed generation of the run and the JSON and SSE
texts matched; the cut, disconnect and over-length steps behaved as before (early cut 590 ms against
a 2,965 ms control; over-length verdict a generic error after 8.7 s with the preflight answering
13,429 usable at once). (#7)

## 2026-09-11 — Chunk 6 (Aion Instruct Preview adapter)

Issue #2. Built on `chunk-6-aion`. The adapter, the plumbing and the unit tests are complete and
reviewed by the build and the suite; the hardware steps are blocked by the machine (D68), so this
section records what was decided and what is deliberately still open.

**D66. The Aion SDK is a conditional reference: absent NuGet, absent adapter, one solution everywhere.**
`AionInstructPreview.Text.Framework.1.0.0.nupkg` is not on nuget.org; it comes from the sample repo's
GitHub release into `nuget-local/`, which is gitignored. CI has no copy and must stay green, and a
fresh clone that has not fetched it must still build `--backend fake` and `--backend phi-silica`. Of
the two options the issue offered, the exe project now references the package only when the file
exists at build time (`AionSdkAvailable`), defines `AION_SDK`, and otherwise removes
`Backends\AionBackend.cs` from the compile and maps `--backend aion` to an `UnavailableBackend` whose
message says the SDK was absent at build time and where to get it. The alternative, building only
Core and the tests on CI, would have left the exe uncompiled on every push, which is where the Phi
Silica adapter and the packaging glue live; this way CI compiles everything but the one file it cannot.
Verified both ways: the full build with the file present, `dotnet build src/NpuBridge
-p:AionSdkAvailable=false` without it, and the pushed branch's CI run (no NuGet) green. The cost is
that a machine with a stale `obj/` can carry the wrong decision until it restores again; `dotnet build`
restores by default, so this is a `--no-restore` hazard only.

**D67. Both adapters share one delta accumulator; Aion inherits the text contract by construction.**
`PhiSilicaBackend`'s Progress handling — append and deliver under one lock, drain the in-flight
callbacks after the operation ends, count a callback after a completed generation and a runtime text
that disagrees with the deltas (D65) — was 120 lines of concurrency code that the Aion adapter would
otherwise have copied. It is now `DeltaAccumulator` in Core: it depends on nothing WinRT, and
`OnProgress(string)` is the test seam a Progress handler would use, so `DeltaAccumulatorTests` drives
it from thread-pool threads and pins delivery order, the barrier, the late-delta rule and the
reconcile count without a runtime (the chunk 6 review moved it; it had first landed in the exe on the
mistaken claim that it had no test seam). Each adapter supplies its logger, its display name for log
lines, and the two counter callbacks. The Phi Silica adapter's behaviour is unchanged by
inspection and by the smoke test re-run after the refactor (D68 has the numbers). Two things stay
per-adapter on purpose: the Phi Silica `E_ACCESSDENIED` translation and its `ContentFiltered` rule
(return empty text), because Aion's four-value enum has no moderation status and no access gate.
Aion's `InProgress` is mapped to `Error` like any unknown value: it is not a terminal status, and a
result carrying it is a fault rather than a success. Sampling options never reach the adapter (the
capability is absent, preparation drops them), and it would have nothing to hand the runtime anyway.
Overflow on this backend, when it can be measured, is expected to be one of two things: Aion's enum
does carry `PromptLargerThanContext`, so it may be the backend that reports it honestly, or it may fail
generically like Phi Silica (D55). The pipeline already turns the first into `400
context_length_exceeded` on both shapes with the context disposed (tested under the Aion capability
profile), and the second into a 502 or an in-stream error; chunk 5's `--truncate-history` loop for a
backend with no preflight waits for the measurement.

**D68. Aion's hardware verification is blocked by the machine; the adapter claims nothing until it
runs, and Phi Silica is re-verified after the shared refactor.** Measured 2026-09-11 on build 29648.
`--backend aion` starts by path, resolves both dynamic dependencies
(`Microsoft.AionInstructPreview.Framework.1.0_1.0.0.0_arm64` and `Microsoft.WindowsAppRuntime.1.8_8000.946.1701.0_arm64`),
and `LanguageModel.CreateAsync` fails within a second: `CacheApi::CreateCache failed: InvalidCache:
The model cache is not valid (PsResult=-988)`, per-model `muffin_ctx = -996`, `muffin_iter = -996
(InvalidData)`. The SDK's own `OutputDebugString` lines, captured with the sample's diagnostic
listener, say why in order: it loads `Microsoft.Windows.AI.MachineLearning.dll` and `onnxruntime.dll`
from Windows App Runtime 1.8, then `TryRegister returned false for WinML EP: QNNExecutionProvider`,
then `selected EP=QNN, Device=NPU, reason=catalog-certified, backend=QnnHtp.dll` regardless, then the
cache build fails. The registration fails because every DLL in the
`MicrosoftCorporationII.WinML.Qualcomm.QNN.EP.1.8` package (1.8.30.0, installed today by
`ExecutionProvider.EnsureReadyAsync`) fails `LoadLibrary` with `E_ACCESSDENIED`, from a process that has
the package in its dependency graph and can read the files; the Aion framework's DLLs load from the
same `WindowsApps` root without trouble. The provider package is a sideloaded main package
(`SignatureKind=Developer`, `IsFramework=False`) whose folder ACL differs from the framework's; the
sample's validated path had Developer Mode on and this machine has it off, and the sample's issue #6
was fixed by a newer Qualcomm NPU driver (this machine: 30.0.219.1000, 2025-11). None of that was
tried here; they are the next things to try (`docs/FUTURE.md`, chunk 6). Consequences: the adapter
advertises `BackendCapabilities.None` (Cancellation waits for the cut measurement; nothing reads the
flag yet), `/healthz` carries the SDK's message verbatim, `smoke.ps1 -Backend aion` fails at
"healthz becomes ready" with that message and 9 steps, and the third overflow behaviour stays
unmeasured. The sample's own console app could not be built as a control (`FUTURE.md`).

Phi Silica after the D67 refactor, same day, `smoke.ps1 -Backend phi-silica -Port 5298`: every step
passed. Model create 37 ms warm (9.2 s on the earlier cold run); streamed one-word reply 509 ms end
to end, first chunk at 344 ms, headers at 338 ms, 0 keep-alives; the new throughput step read 32.9
estimated tokens per second over the decode phase of a 128-token reply; both placements obeyed the
Ada system prompt; early cut 518 ms against a 2,808 ms control (0.18 end to end, 0.05 on decode);
text contract `text_mismatches=0 late_deltas=0` with matching texts; the over-length verdict an
in-stream error after 7.0 s with the preflight answering 13,429 usable at once (D55 holds). The
smoke script's auxiliary server is now stopped on every failure path: the Aion run had leaked one on
port 5299 and the following Phi Silica placement measurement found the port busy.

**D69. Chunk 6 review: the drain runs in a `finally`, and a straggler is judged by how the generation
ended, not by the token.** Two reviewers (a Claude subagent and Codex) found the same gaps in the
shared accumulator and its callers. (a) Both adapters drained the Progress callbacks on the completed
and the cancelled exits but not on a thrown one, so an exception from the runtime's task returned to
the pipeline with a callback possibly still inside the sink, and the pipeline's `finally` disposed the
context behind it; the drain is the barrier D51's cancel-drain-dispose order depends on, so it now
runs in a `finally` on every exit in both adapters. (b) The late-delta rule read the token when the
straggler arrived. The streaming endpoint cancels the token in its `finally` on every path, completed
ones included, so on that shape a callback after a completed generation was always classified as
"expected after a cancel" and `late_deltas` could never move; the smoke test's assertion that it reads
zero was vacuous on one of the two shapes. The accumulator now records whether the generation had been
cancelled at the moment the barrier closes and judges stragglers by that. (c) The gate is closed with a
full fence (`Interlocked.Exchange`) rather than a release write, so the callback-side "increment then
read closed" and the drain-side "write closed then read in-flight" cannot both miss on a memory model
weaker than ARM64's. (d) The accumulator moved to Core with its own tests (D67 amended). (e)
`smoke.ps1`: the D50 branch of the placement measurement now fires only for the aion backend's native
run, so the same 400 from Phi Silica or from the prompt run reads as the regression it would be; the
throughput measurement refuses to time a generation that ended in an in-stream error or without the
done marker. (f) The once-per-process warning for an ignored parameter no longer says "not implemented
yet" now that a real backend triggers it for parameters the runtime cannot apply. Deferred to
`FUTURE.md`: a cut that races a genuine runtime `Error` is reported as `Cancelled` (inherited from the
Phi Silica adapter as verified in D62), the five-second drain timeout's theoretical window, and the
requirement that chunk 7's buffering sink and chunk 8's scheduler keep the delta sink non-blocking.
Phi Silica smoke re-run after these changes, build 29648: every step passed (1 skipped, 5 informational); text contract 0/0, texts match; streamed one-word reply 411 ms with the first chunk at 267 ms; throughput 35 estimated tok/s; early cut 469 ms against a 2,658 ms control; over-length verdict in-stream after 7.4 s, preflight 13,429 usable. (chunk 6 review)

**D70. The Aion blocker is the OS refusing image-load access to a main package's content, not the
provider, its ACL, Developer Mode or the driver.** Measured 2026-09-11 on build 29648 after the owner
turned Developer Mode on (no change: the same `InvalidCache` failure). Probing from a plain Arm64
PowerShell process: every provider DLL fails `LoadLibrary` in place with error 5, loads as a data
file or image resource, has a valid Qualcomm or Microsoft signature, and loads as an executable image
once copied out of `WindowsApps`; so the bytes are fine and the denial is location-bound. The same
in-place test fails for every *main* package probed (Store-signed Microsoft ones included) and passes
for every *framework* package, which is Windows's design: a main package's code may only be mapped by
processes that hold the package in their graph. The provider packages opt in to that
(`<uap15:DependencyTarget>true</uap15:DependencyTarget>`, and a
`com.microsoft.windowsmlruntime.executionprovider` package extension naming
`onnxruntime_providers_qnn.dll`), and the OS calls Windows ML 1.8 makes to use it
(`TryCreatePackageDependency` for the family with Arm64, then `AddPackageDependency`) both return
`S_OK` here with the right full name resolved; yet `LoadLibrary` of the provider DLL afterwards still
fails with error 5, by path and by name. That is the whole failure: on this build the grant that a
dynamic dependency on a main package is supposed to confer is not applied, so `TryRegister` fails
inside the SDK and the NPU cache build has no provider. Current docs (learn.microsoft.com, read via
Context7) say main-package dynamic dependencies exist only in the Windows 11 OS API and need that
opt-in, which is what is present, and that Windows ML in Windows App SDK 2.1.3+ moved to
"execution providers delivered as framework packages", which is why nothing else on this machine
depends on this path: Aion is pinned to Windows App Runtime 1.8, whose catalog uses main-package
providers. Ruled out: Developer Mode (on, no effect), the driver (irrelevant before a load), the
package folder's ACL (Users have read-and-execute on every file), Smart App Control (off), AppLocker
(no policy), Defender blocks (no events), a package staged on the broken 29661 flight (the folders
date from 2026-08-16 and were registered after the rollback boot at 19:57). Not tried: removing and
re-acquiring the two provider packages on this build, and a different Windows build. Also tried, same
day: the sample repo's own `AcquireQnnEp` tool (rebuilt for .NET 10) reports the provider ready, adds
the dependency, and then fails every load with `E_ACCESSDENIED` and exits 5 (`TryRegister` failed),
so it reproduces the finding independently of npu-bridge; and running `--backend aion` inside a
process that carries the sparse-package identity (activated through the package with the backend on
its command line) fails identically at `TryRegister` before the cache build, so a packaged process
gets no different treatment. The sample repo's closed issue #1 is the mirror image: on a retail
26200 build the hard-coded provider family was absent (that machine carried in-box
`WindowsWorkload.EP.Qualcomm.QNN.*` packages, including a framework variant) and acquiring the main
package fixed it. Here the machine-wide registry shows `WindowsWorkload.EP.Qualcomm.QNN.Framework.1.8`
and the LanguageModel workload packages as staged but not registered for any user, which is also why
Phi Silica works only through the workload session host. Consequence for chunk 6: the adapter is
code-verified and review-clean; the hardware half of the definition of done cannot be met on this
machine until the OS honours main-package dependencies again. (chunk 6)

Mechanism, traced the same night. A main package's folder grants ordinary users only read
unconditionally; execute is granted by a conditional ACE that requires the process token's
`WIN://SYSAPPID` attribute to contain the package family (SDDL on the provider folder:
`(XA;OICI;0x1200a9;;;BU;(WIN://SYSAPPID Contains "MicrosoftCorporationII.WinML.Qualcomm.QNN.EP.1.8_8wekyb3d8bbwe"))`
beside `(A;OICI;FR;;;BU)`), while a framework folder grants read-and-execute unconditionally, which is
exactly the observed split: reads and data mappings succeed everywhere, image mappings succeed only for
frameworks and for copies made outside the folder. So `AddPackageDependency` on a main package must get
that attribute onto the caller's token, and on this build it does not: the deployment service is
contacted (it logs "validation and setting the Trust Label" on the provider package with flags 0x122,
already set) and nothing else happens. The related deployment failure is not a 29661 artefact either:
the `windows.accessControl.undocked` extension failed to register with `E_ACCESSDENIED` at 15:02 and
15:11 on 2026-09-10 before the flight rebooted and again at 19:58, one minute after the rollback boot
into 29648, each time while re-registering `WindowsWorkload.QueryBlockList.1` and
`WindowsWorkload.TextRecognition.Qnn.1` (the service then "repairs ACLs" and gives up). That extension
is an undocked deployment extension handler shipped by the inbox `MicrosoftWindows.UndockedDevKit`
package (10.0.29648.1000, status Ok), documented as not for third parties. Public sources say nothing:
the 29648 and 29661 release notes list no package or AI issues (29648's only known issue is an update
error), and no GitHub issue in the Windows App SDK, Windows AI docs, AI Dev Gallery, Foundry Local or
the Aion sample describes execute being denied on a resolved main-package dependency; the nearest are
the sample's #1 (family absent on retail 26200) and Foundry Local #393 (duplicate provider families on
26220). Repair options that remain are the owner's: an elevated `sfc /scannow` and
`DISM /Online /Cleanup-Image /RestoreHealth` to repair inbox components, a Feedback Hub report under
Developer Platform, or a different build. (chunk 6)

Proof and the one remaining lead, later the same night. Reading the process token's security
attributes (kernel `TOKEN_SECURITY_ATTRIBUTE_V1` form) from a plain Arm64 process shows only
`TSA://ProcUnique` and `APPID://PATH`, and exactly the same set after `AddPackageDependency` on the
provider main package returns `S_OK` and again after adding a framework package: no `WIN://SYSAPPID`
attribute is ever added, so the conditional execute ACE on the provider folder can never match. That
attribute can only be written by Windows with TCB privilege; `kernelbase.dll` on this build references
both `WIN://SYSAPPID` and `AddPackageDependency`, and `AppXDeploymentServer.dll` references
`WIN://SYSAPPID` and `DependencyTarget`, so the server is the component expected to append it, and its
verbose log for the call shows only the trust-label validation. Public sources confirm the design but
not the failure: the conditional ACE and the attribute are documented by security researchers and by
the Inside MSIX blog ("only Windows can write it, and only select code paths"); the Python project hit
the same wall in August 2026 for the Store Python main package and closed it as not planned. The one
lead: the in-box *framework* variant `WindowsWorkload.EP.Qualcomm.QNN.Framework.1.8` 1.8.46.0 is
staged on disk with an unconditional read-and-execute grant, and every DLL in it loads from a plain
process; but it declares the `com.microsoft.windowsmlruntime.osexecutionprovider` extension, while the
1.8 Windows ML runtime Aion uses (`Microsoft.Windows.AI.MachineLearning.dll` in Windows App Runtime
1.8) contains only the `...windowsmlruntime.executionprovider` string plus the `MicrosoftCorporationII.WinML`
and `WindowsWorkload.EP` family prefixes, so registering the framework variant for the user would not
be discovered by that runtime. The main package in use, 1.8.30.0, is the one Windows Update ships as
KB5078978. (chunk 6)

## 2026-09-11 — Chunk 5 (context cache and overflow handling)

Issue #1. Built on `chunk-5-context-cache`. A continuing conversation now sends only its newest
turns to the NPU, and an over-length transcript is refused before a token is generated wherever the
backend can be asked.

**D71. The cache key is a boundary-preserving encoding of `(system, turns)`, never the rendered
prompt.** The three collision surfaces recorded before the cache existed (unescaped turn markers,
native placement dropping the system text from the prompt, the raw pass-through of a lone user
message) are closed by construction rather than by escaping: `ConversationKey` hashes a version
magic, then the system text, then each turn as a tagged sequence of fields, every field written as a
presence byte, its byte length and its bytes. No content can imitate a boundary, and the system text
is in the key whichever placement delivered it. A test pins each surface both ways (the two
transcripts render identically and key differently), so a regression to keying on the prompt
string fails on the twin assertion. What enters a field is the text the model saw for that turn
(`PromptTemplate.TurnText`: parts joined, trailing whitespace trimmed), so a client that echoes our
reply with different trailing whitespace still hits. Null, empty and present system text are three
different keys because they are three different context states. An assistant turn's `tool_calls`
enter the key field by field (id, type, function name, trimmed arguments); `ChatMessage` gained the
field for that, and it is carried but not rendered until chunk 7. The stored key after a generation
is `Compute(system, turns, reply)`, which is exactly the prefix key the next request computes for
`turns + assistant(reply)`; a lookup walks the prefixes that end in an assistant turn, longest
first, in one pass with `IncrementalHash.GetCurrentHash`.

**D72. A context goes back into the cache only after a `Complete`, uncut generation; everything else
disposes it, and a tail is always rendered with the markers.** `ContextLease` settles a context
exactly once: `Keep(reply)` stores it under the new key, `ReturnUntouched` puts a checked-out context
back under its old key when a preflight refused the prompt before anything ran (a fresh one is
disposed instead), and `Dispose`, which the endpoints' `finally` calls after the drain, disposes it
unless one of the others already settled it. So the D11/D43/D51 guarantees survive unchanged: no
context is disposed while its generation may still write to it, and none that is not in the cache
outlives its request. A cut reply is not kept even when the status is `Complete` (the whole-text
stop-string cut on the JSON path is the case): the context holds text the client never saw, so the
transcript it would be stored under is not the one the client will send back. Two concurrent
requests for one conversation cannot share a context: checkout is exclusive under one lock, and the
second misses and creates its own. When both store under the same key the older is disposed.
`--context-cache-size 0` disables caching without a second code path: lookups miss and `Store`
disposes. The cache is owned by `BackendLifecycle`, which empties it at stop and again at dispose,
before the backend, so a `LanguageModelContext` never outlives its `LanguageModel`; a request that
finishes after stop hands its context in and the cache disposes it on arrival. The tail on a hit is
`PromptTemplate.RenderTail`: the marker format with no system text, never the raw pass-through,
because the context already holds a marked-up conversation and a bare string in the middle of one
is not the format the model was shown. `usage.prompt_tokens` estimates the whole transcript on a hit
as on a miss (a client budgeting its window wants that number stable), while the log line's
`prompt_chars` is what was actually sent.

**D73. Overflow is decided by the preflight where one exists and by the generation's status where
none does, and `--truncate-history` drops whole exchanges from the front, never the message being
answered.** `ConversationSession.Acquire` asks `GetUsablePromptLength` before generating on a backend
with `PromptLengthPreflight` (D55: Phi Silica never says `PromptLargerThanContext`, and asking the
generation costs 26 s per attempt); a refusal is the same 400 `context_length_exceeded` as before,
with a message naming the backend, the characters that fit, the transcript size and the switch. With
`--truncate-history` the session drops every turn from the start of the transcript up to the next
user turn, which is the oldest exchange: the question and everything the model did in answer to it,
tool calls and results included (amended by D76; the first cut was at the first assistant turn). It
then re-renders, looks the shorter transcript up again and re-checks. The final turn is never
dropped, and a transcript with no second user turn is the active exchange and is refused with a
message saying nothing is left to drop. A checked-out context whose tail does not fit goes back
untouched and the truncated transcript starts afresh. Each drop is a Warning. The response carries
`x-npu-bridge-truncated-turns: N` (turns, not exchanges) once a generation is attempted on the
truncated transcript, whatever it reports (the 400 refusal carries none, since no reply was produced;
D76), and the log line carries `truncated_turns=N`. On a backend without a preflight (Aion) both
endpoints retry on a `PromptLargerThanContext` status when the session can drop something, disposing
the failed context first; on the stream this can only happen before the first delta, and if a
keep-alive has already committed the headers the header cannot be sent and a Warning says so. Aion's
actual overflow status is still unmeasured (D70), so that path is exercised by the fake only. A
consequence to know: after a truncation the context is stored under the truncated transcript's key,
and the client's next request carries the full transcript, so it misses and truncates again. That is
correct, slower, and recorded in `docs/FUTURE.md`. Context pressure is a Warning at nine tenths of
`--context-window-hint` (tokens, times four characters), once per request; the hint is a hint, and
the preflight is the measurement.

**D74. `/healthz` reports the cache truthfully and the smoke test proves a hit by the counters, not
by the count.** `contexts_cached` is the live count; `context_cache_capacity`, `context_cache_hits`
and `context_cache_misses` were added when the first smoke step tried to prove a hit by watching
`contexts_cached` alone and could not: once the cache is full, a miss evicts one and adds one, so the
count is unchanged on a hit and on a miss alike. The step now checks that a new conversation misses
exactly once, its continuation hits exactly once and leaves the count unchanged, and a control with
the assistant text altered misses; it reports the three TTFTs. The overflow step sends eight
two-thousand-character exchanges (past the 13,429 characters D55 measured) to the main server and
requires the 400 within five seconds on a backend with a preflight, then starts a second server with
`--truncate-history` and requires a 200 with the header. Numbers from the run on this machine are in
D75.

**D75. Measured on the NPU: a cache hit answers in a little more than half the time of the replay,
and the preflight turns a 26 s refusal into a 31 ms one.** `scripts/smoke.ps1 -Backend phi-silica
-Port 5298` on this machine (build 29648, warm model, `create_ms` 7,788), all steps passed, one
skipped (the chunk 7 tool probe), five informational. The cache step: the first turn missed (TTFT
250 ms, 399 ms total, reply "Red"); its continuation hit (TTFT 235 ms, 390 ms total, reply "Blue"),
with `contexts_cached` unchanged at 3 and `context_cache_hits` 0 to 1; the control with the assistant
text altered missed and replayed (TTFT 392 ms, 808 ms total), `contexts_cached` 3 to 4. So on a
three-message transcript the hit saved about 40 % of the TTFT and half the total; the saving grows
with the prefix, since what a hit skips is re-reading it. The overflow step: seventeen messages,
16,361 characters of content, 16,603 rendered; the main server answered 400 `context_length_exceeded`
after 31 ms with the preflight's own numbers in the message (`can take 13179 characters of the
16603-character prompt`) and no generation; a second server with `--truncate-history` answered 200
after 17,014 ms with `x-npu-bridge-truncated-turns: 4`, `prompt_tokens` 3,121 and the reply "PONG",
the 17 s being the prefill of the roughly 12,500 characters that remained. The D52 over-length
measurement, which sends one 225,042-character user message on the streaming path, now lands on its
first branch: the verdict is the ordinary HTTP 400 after 151 ms, before a byte is written, with the
preflight answering 13,429 usable as in D55, instead of the in-stream error frame after 7 to 26 s it
produced in chunks 4 and 6. Everything else in the run matched the chunk 6 numbers: text contract
0/0, the cut stops the device, both placements obey the Ada system prompt. Note that the preflight's
answer differs with the text (13,179 usable here against 13,429 in D55 for a different prompt): it is
a tokenizer's verdict rather than a constant, which is one more reason to ask it every time instead
of remembering a number.

**D76. Chunk 5 review: an exchange ends at the next user turn, a throwing preflight disposes the
context it was asked about, and each retry attempt owns its cancellation.** Two reviewers (a Claude
subagent and Codex) read the branch independently and found the same two defects, and Codex a third.
(a) `TryDropOldestExchange` dropped through the first assistant turn, so with tool use the tool
results and the final answer that followed it survived as an orphaned head: `[user, assistant(calls),
tool, assistant(answer), user]` lost its question and its call and kept the result. D73 said "tool
results included" and the code did not do it. An exchange is now every turn up to, not including,
the next user turn, so a question and everything the model did in answer to it go together, and a
transcript with no second user turn (a lone question, or a question still being answered through
tool results) is the active exchange and cannot be truncated. (b) `Acquire` called the preflight on
a lease it already owned with nothing to release it if the call threw; on Phi Silica that call is a
raw WinRT call whose guard translates only access-denied, so any other COM fault would have dropped a
checked-out context from the cache and never disposed it. The call is now guarded and a throw
disposes the lease (the runtime just faulted against that very context) before propagating as the
usual 502. The fake gained `PreflightFailure` so both shapes have the test, fresh and cached. (c) On
the JSON path the linked cancellation source and the `cancelledByCut` flag lived outside the retry
loop: an attempt that emitted enough text to trip the cut and then reported `PromptLargerThanContext`
retried on an already-cancelled token, the retry returned `Cancelled` at once, and the stale flag
called that a successful cut, HTTP 200 with empty content. Both now belong to the attempt, as the
stream's channel and sink already did, and the TTFT counters reset with them. (d) The truncation
header is set at the same moment on both shapes: once a generation is attempted on a truncated
transcript, whatever it goes on to report, so a 502 after truncation carries it; the 400 refusal
carries none on either shape, because no reply was produced for the dropped turns to describe. The
JSON path used to set it only on the 200. Both reviewers also noted that at the default
`--context-window-hint` of 4,096 tokens the pressure warning cannot fire on Phi Silica, whose
preflight refuses at about 13,400 characters, below nine tenths of the 16,384 the hint implies; the
default stays (the option is documented as a hint, and the preflight is the measurement) and the
note is in `docs/FUTURE.md`. Ten tests were added for the branches the reviews named; 472 pass, and
the Phi Silica smoke run was repeated after the fixes with every step passing (hit TTFT 274 ms
against a 417 ms replay; the preflight refusal again in 31 ms; the truncated request answered with
the header after 16.1 s).

## 2026-09-11 — OpenAI conformance pass

Issue-less: a request from the owner to check the API against OpenAI's published schema and fix what
was missing or out of place. Built on `openai-conformance`, checked against the `openai-openapi`
repository's `openapi.yaml` (the `CreateChatCompletionResponse`, `CreateChatCompletionStreamResponse`,
`ChatCompletionStreamResponseDelta`, `CompletionUsage`, `Error`, `Model` and `ListModelsResponse`
schemas and the `CreateChatCompletionRequest` constraints).

**D77. The wire shapes follow the schema's required-but-nullable fields, `model` is required and must
be the served id, and the request constraints the schema states are enforced.** Six changes, each a
schema rule. (a) `choices[].logprobs` and `message.refusal` are required on a chat completion and
`choices[].finish_reason` on every streamed chunk; the serializer omits nulls everywhere else, so
these three (and `logprobs` on chunks, which OpenAI's streams carry as null) are marked to be written
as explicit nulls. (b) With `stream_options.include_usage`, every chunk before the usage chunk carries
`"usage": null`, as OpenAI's do; without it the field is absent. The null travels in the chunk's
extension data because a property cannot be both omitted-when-null and present-when-null. (c) The
error envelope always carries all four keys; `param` and `code` are written as nulls when unset.
(d) `model` is required (the schema's own list is `model, messages`; the message is OpenAI's "you
must provide a model parameter"), and an id other than the served one is a 404 with code
`model_not_found`, checked before readiness so a loading server answers it too. The reply's `model`
is always the served id; the request's id is matched case-insensitively. The bridge used to default
a missing id to its own and echo any other, which the chunk 3 review had already called dishonest:
a client asking for `gpt-4o` was told it had been served by one. Clients configure a provider with
the model id they will send, so `phi-silica` (or `fake`, `aion-instruct`) is what they send. (e)
`temperature` outside 0 to 2, `top_p` outside 0 to 1 and `n` below 1 are 400s naming the parameter,
on every backend, whether or not the backend applies the value. (f) `stream_options` without
`stream: true` is a 400, in the schema's own words. Not changed: `system_fingerprint`,
`service_tier`, the `usage` detail objects and `annotations` are optional in the schema and stay
absent; `/v1/completions` is chunk 8. The smoke script already read `finish_reason` and `usage` with
null-tolerant checks, so it passed unchanged on the fake backend and on Phi Silica (one NPU run failed
at the first generation with an RPC fault from the model runtime before any bridge code ran; the
re-run passed every step). A Codex review added two more schema rules: `message.content` is also
required-but-nullable and is now written as a null, and the `/v1/models/{id}` 404 now carries
`param: null` like the chat endpoint's. The null-usage dictionary is allocated per chunk rather than
shared. 495 tests.

**D78. No suffix lookup for truncated conversations: the truncation loop already finds them.** Issue
#12 asked for a lookup that tries the transcript's suffixes so a context stored under a truncated
transcript is found again. Written as a test first, the follow-up turn hit the truncated context on
the unchanged code: the loop drops the same exchanges, and the shortened transcript's prefix keys are
the stored key. The only cost is the refused preflight rounds before the hit (a fresh context, one
preflight, a disposal per dropped exchange), which the test pins beside the hit. The first version of
the test used 40-character turns and a 250-character window and saw a third exchange dropped; that
was the fake's window counting the tail's headings against what the context had absorbed, not a
lookup failure, and it is why the test uses 200-character turns. A suffix lookup on the first pass
would have been worse than nothing: a cached shorter conversation would match the tail of a longer
one that still fits, and the longer one would lose its oldest turns for no reason. The
`docs/FUTURE.md` entry that prompted the issue overstated the cost ("replays the whole transcript")
and is corrected. `ConversationPrefix` gained an `Offset` field while this was being explored; it is
always zero and stays for the record of what was tried.


**D79. The smoke script may fail on a contradiction, and the readiness step says what ready means.**
The 2026-09-11 audit (issue #15) found steps that could pass vacuously: the preflight step accepted
a null answer on a backend that advertises the preflight, the readiness step accepted a Phi Silica
server running without identity, four measurements could never fail, and teardown never checked
that the activated child had gone. Now: an `InfoStep` still reports numbers and never fails on a
surprising one, but its body may call `Fail` for a contradiction of something the bridge guarantees,
which is recorded as FAIL and sets the exit code. Two things do: a placement run that could not
answer 200 (D50's aion-native refusal excepted), and `/healthz` not carrying the keep-alive timing
the D52 measurement reads. The D52 measurement's "exceeded" branch, whose own text said "this should
not happen", was the audit's third candidate and is deliberately not one: the keep-alive timer starts
only once a generation is being waited on, after the cache lookup and the preflight, so a refusal
the preflight took over a second to reach still lands as an ordinary status, and that branch now
reports it as pre-generation latency (body parse, cache lookup, the preflight where one exists). The
readiness step requires `package_identity` true and `diagnostics.bootstrap` equal to `ok` on
phi-silica, whoever started it, and `package_identity` false on the fake and aion when the script
started them by path (a server found running under `-NoStart` is tested as found); it also reads
the served model id off `/healthz`, before those checks, for every chat request,
which the script used to spell as the backend selector (wrong for aion, whose id is
`aion-instruct`). The preflight step requires a non-null `usable_prompt_chars` on every backend but
aion. Teardown runs whenever the script started the server, whether the parent was stopped or had
exited on its own: on phi-silica an activated child carrying `--supervisor-pid <parent>` on its
command line must exist before the stop (the D37 contract, read through `Win32_Process`; another
user's process shows no command line and cannot match), and afterwards the parent, every such
child and the port's listener must be gone within 60 s. That sits above the worst case the child
can take: a backend still initialising holds `BackendLifecycle.StopAsync` for the host's 30 s
shutdown budget and `DisposeAsync` for a further 15 s grace. A process query that fails is
recorded as a teardown failure, not read as nothing left. On the fake and aion there is no child,
so the row checks the parent and the port. Each auxiliary server (the `--truncate-history` run and
the two placement runs) gets a teardown row of its own on the same terms; on phi-silica those are
the only places the child-exit half of D37 is exercised more than once per run. A parent that had
died on its own before teardown is reported as that, since its child is gone through `WatchParent`
by then and says nothing about D37.
`/healthz` gained `first_keep_alive_ms` and `keep_alive_interval_ms` so the D52 margin is read off
the running server rather than from a copy of the constant in the script; a test pins that the
endpoint reports the registered options and not the defaults. In the same branch, issue #14's first
six tests: an exception mid-generation on a cache hit on both shapes, the refusal of a tail that does
not fit a cached context with the context going back untouched, `max_completion_tokens` in a body
that carries no `max_tokens` at all, wrongly typed `content` and `stop` and a literal `null` body as
400s, a non-positive keep-alive interval disabling the comments, a non-positive first delay being
accepted with a keep-alive still committing the headers, and a failure after the client has gone.
Three of those needed care. The disabled-interval test proves its point without a clock by setting
the first delay to zero as well: were the disabling branch missing, `Task.Delay(TimeSpan.Zero)`
would write a comment before the gated generation could produce anything; a negative interval
would throw there instead. The negative row is deterministic; the zero row has a window of a few
instructions between the backend call the test waits on and the handler entering its wait, in
which a delta could satisfy the wait first, so it is near-certain rather than proof. The first-delay
test pins only that a non-positive value is accepted: a fallback to zero, or to any other
non-negative span, would pass it too. Both gaps close once the keep-alive waits are driven by the
injected `TimeProvider`, filed in `docs/FUTURE.md`. The client-gone test names a branch
(`FailAsync`'s client-gone arm) that is a race by construction: nine runs in ten in isolation took
the other arm, the write attempted and its failure swallowed, because TestServer completes the
response pipe before it signals `RequestAborted`. The test pins the contract instead and accepts
either arm: the request runs off the end of the pipeline with no exception escaping, the context is
disposed, nothing is logged at Error, and the client saw the delta and nothing after it (that last
one is the weakest, since the client cancelled its own read). Its synchronous hold between tokens
runs off the request's thread, behind the fake's first-token delay, so it cannot block the handler
before the handler enters its wait. `BridgeTestHost` gained a request ledger (a middleware counting
completed requests and recording escaped exceptions, since TestServer has no Kestrel to log one at
Error) and the `CapturingLoggerProvider` an `OnRecord` hook so a test can act inside the window a
log line marks. 512 tests.


**D80. `usage` and the `max_tokens` budget are counted in Phi-3 tokens on Phi Silica, because the
runtime's tokenizer is Phi-3.5-mini's; the preflight answers in UTF-8 bytes; chars/4 stays where the
tokenizer is unpublished.** Issue #13 asked for a measurement before adopting a tokenizer, and the
measurement decided two things. Method: a lone over-length user message reaches the model raw (D71),
so the 400 body's `can take N characters of the M-character prompt` is the preflight's own answer
with no template and no generation, in about 60 to 230 ms; the fitting prefix of each text was then
counted with `Microsoft.ML.Tokenizers` 2.0.0's `LlamaTokenizer` over Phi-3.5-mini-instruct's
`tokenizer.model` (SentencePiece, 32,000 pieces, MIT, vendored under
`src/NpuBridge.Core/Tokenizers/Phi3/` with its licence and provenance). Fourteen texts on build
29648 with Windows App SDK 2.4.1-experimental.

(a) **The vocabulary is the runtime's.** Every ASCII text whose tokens are words, digits, spaces or
newlines lands on exactly 3581 tokens at the preflight's boundary: D55's fox filler (13,429 chars),
the smoke transcript's turns (13,340), a digit run (3,580), markdown lines (10,149), `line N`
lines (5,426), twenty-space runs (28,652), C# identifiers (14,502) and a C# source file (13,058
chars once the units below are right). Text dense in punctuation clusters lands about 1 % lower:
JSON objects at 3543 and `{"a":"b","c":"d"},` runs at 3546, so the runtime counts `":`-style
clusters slightly more finely than the ML.Tokenizers implementation does; PLAN.md prose, which has
both, lands at 3588. Chars/4 over the same texts ranges from 0.88 to 8.0 characters per token. The
usable window of an empty context is therefore **3581 tokens** (3582 with BOS), and 4096 − 3581 =
515 tokens are the runtime's own template and its reply reservation, which explains the issue's
question about the 4K window and the 13.4K characters exactly: 13,429 / 3.75 characters per token
on that filler. The counter carries no BOS, since `usage` reports what the caller sent.

(b) **`GetUsablePromptLength` returns a UTF-8 byte offset, not a UTF-16 char index.** Microsoft's
page says only "the index in the given prompt". Read as characters, the CJK boundary (9,474) was
10,739 Phi-3 tokens and emoji (6,562) 4,375; read as bytes and converted, the same three answers
(CJK, emoji, typographic punctuation `→ § ≈ — “ ”`) are 3581, 3578 and 3581 tokens, the emoji
off by three because a pair straddles the byte boundary. `PhiSilicaBackend` had treated the answer
as a char index (its comment said so); identical for ASCII, and up to three times too generous for
CJK. Confirmed live: a 5,001-character CJK prompt (15,001 bytes, about 5,700 tokens, 1.6 × the
window) passed the preflight, generated, and came back 400 from the generation's own
`PromptLargerThanContext` status after 584 ms. Two consequences. The adapter now converts the byte
answer with `Utf8Offsets.CharIndexAtByteOffset` (Core, tested), rounding down to a character and
never between the halves of a surrogate pair. And D55 needs an amendment: Phi Silica *does* report
`PromptLargerThanContext`, quickly, for a prompt moderately over the window; D55's 225 KB prompt got
the generic `Error` after 26 s. Both are true, and the preflight remains the overflow decision (D73)
because it is exact and costs tens of milliseconds.

**The design.** `ILanguageModelBackend.TokenCounter` is an `ITokenCounter` (`Count`,
`IndexAtTokenCount`, `PrefixStable`, `Name`): `Phi3TokenCounter` on Phi Silica, loaded once per
process from the embedded model; `CharEstimateTokenCounter` (chars/4, D44) on Aion, whose
tokenizer is unpublished and which has never generated here, on the unavailable backend and as the
fake's default, so every older usage assertion in the suite still reads as D44 defined it. When Aion
Instruct arrives as a model swap behind the Phi Silica API, the smoke step below is the check
before trusting the counter for that model. The preflight still decides what fits; the counter only
counts. `usage.prompt_tokens` counts the whole rendered transcript plus the native system text, the
same on a hit and a miss (D74); `completion_tokens` counts the text the client received, the
whole-text cut on the JSON shape and the cutter's emitted text on the stream. `max_tokens` is a
budget in the counter's tokens: `OutputLimits` carries `MaxTokens` and the counter, and the cut
point is the counter's `IndexAtTokenCount` over everything generated so far, which for chars/4 is
exactly the old `cap * 4` characters (D53, D58 unchanged). With a BPE counter the stream has one
more thing to wait for: a merge can reach into the word still being written, so the last tokens of
the text are provisional. SentencePiece pieces never span a whitespace boundary and are at most 16
characters, so everything before the trailing partial word, or more than 16 characters back, is
settled. Within `NearBudgetReserveTokens` (8) of the budget the stream releases only settled text
and commits the cap only once its index is settled or the generation ends; farther from the budget
a provisional count cannot matter and text flows as it arrives. The reserve is the honest bound:
overshooting the cap would take a re-merge that shifts a count by more than eight tokens inside one
whitespace-free run, which this vocabulary does not do in practice, and the tests pin the
equivalence of the two shapes on stand-in counters that recount a word when it grows. Measured on
the NPU after the change: `prompt_tokens` 41 and `completion_tokens` 2 for the smoke's PONG exchange
(36 and 1 under chars/4), 3,394 for the truncated transcript, and the cut step's eight-token cap
streamed 31 characters counted as 8 Phi-3 tokens.

**Repeated per build.** `POST /debug/tokenize` (loopback, needs no model) returns the backend's
count and its counter's name, and `scripts/smoke.ps1` has a step that sends the fox filler, JSON
objects and a CJK run as lone over-length messages, reads each preflight boundary, counts the prefix
and fails if the counts differ by more than 2 %: on the day, 3581, 3543 and 3581, a spread of 38.
A different vocabulary, or the preflight read in the wrong units again, lands far outside that.
Not done: the pressure warning still compares characters against `--context-window-hint × 4`
(`docs/FUTURE.md`); Aion keeps chars/4 until a generation runs and the same measurement can be made.
One more number from the same run, for D44's record: the measurement step's English reply was 367
characters and 68 Phi-3 tokens (5.4 characters per token), so chars/4 had been *over*counting
English prose by 1.35 × while the progress callbacks undercount it by 2.3 ×; the decode rate that
read as 35 tokens per second under chars/4 is 27.4 real tokens per second.

**D80 review round (Codex, 2026-09-11).** Two counterexamples on the real tokenizer, both confirmed
with the scratch probe and now pinned by tests. (a) A fixed lookback does not settle a BPE boundary:
twenty hyphens tokenize as `----` first and twenty-one as `-` first, so the "16 characters back is
settled" rule was wrong; the settled prefix is now everything before the whitespace run that precedes
the last word, and nothing else. The consequence for a reply with no whitespace at all (CJK) is that
its exact cut is decided only at the end, over everything that arrived; so that the model is not left
generating, the cutter sets `StopRequested` once the text runs the reserve (8 tokens) past the
budget, both handlers cancel on that flag instead of on `IsCut`, and the stream keeps feeding the
deltas already in flight to the cutter for the final decision. (b) Removing a stop string can leave a
prefix that tokenizes to more on its own than the model spent on it: `international` is one token,
`internation` two. `completion_tokens` is therefore `ITokenCounter.TokensCovering(generatedText,
deliveredLength)`, the tokens of the generated text that cover what was delivered, counting the one
the cut ends inside; under chars/4 that is `ceil(chars/4)` as before. A third finding of my own fell
out of the CJK test: the byte-fallback tokens of one character all carry that character's offsets, so
a budget that ends inside a character now stops before it (`min(end of token n, start of token n+1)`),
or the cut would hand over a character the budget did not pay for. Also from the review: the counters
return the raw token boundary and the cutter alone steps back from a surrogate pair, so stop-string
precedence under chars/4 compares the same unrounded budget it did before; the per-delta cost of a
token budget is one whole-text encoding (measured 3 ms for 5,000 characters on this machine, so
about 3 % of a five-second reply), with no encoding at all once the stop has been requested; and
`prompt_tokens` under chars/4 now rounds the native system text and the prompt separately, which can
read one token higher than the old `ceil((system + prompt) / 4)`, accepted as immaterial for an
estimate. 630 tests.
The second reviewer (a Claude subagent) reached the same two findings independently, with a fuzz over
twelve corpora: 480 randomised stream trials with the real counter never went over budget, the
largest re-merge shift seen was two tokens (the reserve is eight), and the stream and whole-text cuts
disagreed only on runs of one character, exactly the case the settled rule now excludes. It added a
nuance recorded in the cutter's comment: thirty-seven of the 32,000 pieces contain a carriage return
or a no-break space, so "a piece never spans whitespace" is not literally true; a merge across one of
those can only lower the settled prefix's count, so the budget still cannot be overshot. Also from
that review: the Phi-3 counter is warmed during the adapter's initialization rather than on the first
request; the tokenizer smoke step no longer spends a generation to learn whether the preflight exists;
folded system placement has a usage test; D55 carries a pointer to the amendment.

**D81. The post-generation pipeline is written once, and the two response shapes differ only in what
they write.** Issue #9. Phase two of `/v1/chat/completions` existed twice: once in
`ChatCompletionsEndpoint` and once in `ChatCompletionsStreamEndpoint`, with comments in each copy
telling the reader it had to agree with the other. That is not a hypothetical cost — D56 and D57 are
each a recorded drift between exactly those two copies, one answering a backend `Error` with HTTP 200
on one shape and 502 on the other, one labelling the same generation `stop` on one shape and `length`
on the other. Chunk 7's buffer-the-whole-reply path would have been the third copy, so the shared
steps were lifted out before it rather than during it.

**What moved.** `src/NpuBridge.Core/Api/GenerationPipeline.cs` now holds `DeltaSink` (it was private
to the streaming endpoint; the non-streaming path now takes its first-token timing from the same type
instead of an inline lambda that was character-for-character `OnDelta`), `CutWatcher` (the non-streaming path's early stop — the `OutputCutter` under a lock plus the
`TaskCompletionSource` that is deliberately completed *on* the backend's callback thread, carrying
`RunContinuationsAsynchronously` so that the continuation which cancels the generation is what stays
off it), and
`CancelGuardedAsync` and the verbose raw-output log, both of which existed in both files. `DeltaSink`
takes its destination as a `ChannelWriter<string>` or a `CutWatcher` through one of two factories,
never as a delegate: the reason the type exists is that "the callback cannot reach the response" is
checked by the compiler rather than remembered, and an `Action<string>` parameter would accept a
closure over the response and hand that back. A caller that ever wants both destinations has to add a
third factory and decide their order there, which is the point at which the question needs answering.
`GenerationOutcome`, beside `GenerationFailure`, decides failure / filtered / content as three ordered
rules: filtering outranks everything, a `Cancelled` the handler itself asked for is the cut, anything
else `FromStatus` calls a failure is one. The non-streaming copy's `!filtered &&` guard was redundant
against `FromStatus` and is gone with it.

**What deliberately did not move.** The cut's verdict stays an argument rather than something the
classifier reads, because when it is legible differs by shape: the streaming path may only read
`FinishReason` after `Flush()`, which can be the call that commits the cap (D57), while the
non-streaming path cuts the whole text in one go. The two shapes also count `completion_tokens` from
different lengths on purpose (the stream counts what the cutter released, D80), so usage is assembled
by each caller; only `CompletionUsage.For` is shared, and only because `total_tokens` is a sum rather
than a third fact.

**Smaller things from the same review.** `ChatRequestPreparer`'s catch hand-built the 502
`backend_error` envelope `GenerationFailure.FromException` already produces. `roleSent` was always
equal to `streamed` at its only read, so `streamed` is hoisted out of the retry loop and `roleSent` is
gone. `WaitForFirstDeltaAsync` is `wait.WaitAsync(next, ct)` with a `TimeoutException` catch instead
of a linked source and `Task.WhenAny` against a `Task.Delay`; the `ThrowIfCancellationRequested`
before the write stays, because a cancel and a timeout that become ready together can be reported
either way round. `ChatRequestMetrics.CharsPerToken`/`EstimateTokens` are deleted: the ratio is spelled in
`CharEstimateTokenCounter` and the one caller left is the `--context-window-hint` pressure warning,
which still compares characters rather than the backend's tokens — deliberately, since an operator's
hint is not worth tokenizing the whole transcript a second time for, and the preflight is the real
measurement.

**One thing did change.** The non-streaming path's Debug line for a cancel that threw now reads
"draining and disposing anyway" and carries a `{Where}` property, because it is the streaming path's
line and there is only one of them now. Everything a client can observe is unchanged: the same
statuses reach the same bodies, finish reasons, cache decisions and usage numbers on both shapes. The
suite could not have caught the log line, since the test asserts a substring that survived it.

**Tests.** `GenerationOutcomeTests` states the classification rules once rather than through two
endpoints: every status crossed with the handler's own cancel, the finish-reason labels, and the
caching rule. It does not replace the endpoint tests; it is the guard that says which of them is right
when they disagree. Four copies of `WaitUntilAsync`, six hand-rolled SSE body parsers and four
rebuilds of the minimal request body collapsed into `TestWait.UntilAsync`, `Sse.Payloads`/`Sse.Chunks`
and `ChatBody.User` in `BridgeTestHost.cs`; the tests that are *about* the raw framing still read raw
lines, since parsing through the shared helper would assume what they check. One expectation was
wrong in a way the deduplication surfaced: the cache's `prompt_tokens` test counted `"sys" + prompt`
as one string, while the bridge counts the prompt and the native system text separately (the runtime
holds the system text in the context, outside the prompt string). The two agree only when the
prompt's length is not 1 modulo 4, so the test had been passing on the length that conversation
happens to render. It now mirrors the bridge.

**The last three `StartDelay` races are gone.** `FakeBackendOptions.StartDelay` existed so a test
could arrange "the prompt-length verdict lands after a keep-alive comment has already committed the
headers", and its three users each did that by racing a 300 ms or 100 ms delay against a 20 ms or
10 ms keep-alive interval — the style D54 replaced everywhere else. `StartDelay` is deleted and
`StartGate` takes its slot: a `TaskCompletionSource` held in the same position, before any verdict.
`FirstTokenGate` cannot stand in for it, because it is held *after* the prompt-length verdict and so
never delays the verdict itself. The three tests now follow the pattern the keep-alive-ordering test
already used — `HttpCompletionOption.ResponseHeadersRead`, assert the headers arrived, release the
gate, then read the body — so "a keep-alive went out before the verdict" is a property of the
arrangement rather than a millisecond bound. The remaining first-keep-alive delay decides only how
long the test takes, never what it asserts, and removing the gate makes all three fail at once, which
was checked. The knob count did not grow: four before, four after, and `StartGate` and
`FirstTokenGate` now share one `WaitAtGateAsync` while each keeps its own status detail.
`InitializeAsync`'s gate is deliberately left out of it — it has no `GenerationResult` to report and
lets the cancellation throw — and `CancellationGate` is a predicate read inside the token loop rather
than a wait at all. Two clocks remain in the suite on purpose: `FakeBackendTests` bounds a
`FirstTokenDelay` whose delay *is* the subject, and the two keep-alive tests still lean on
`Task.Delay` because the keep-alive wait is not driven by the injected `TimeProvider`
(`docs/FUTURE.md`).

**Not done here.** The `IAsyncEnumerable<string>` responder the issue sketched for `FakeBackend` was
not built: each of the four knobs models a distinct real-runtime behaviour and is separately
documented, and replacing them churns every test that sets `Responder` for modest gain. The defect
inside that bullet — the wall-clock races — was fixed instead. `BackendCapabilities.Cancellation` is
advertised by `PhiSilicaBackend` and read by nobody; that is its own question (report it in
`/healthz`, read it in the fake, or drop it) and is issue #17.

**D81 review round (a Claude subagent and Codex, 2026-09-11, then a whole-branch pass).** Neither
reviewer found a defect either could demonstrate, and both independently produced the same equivalence
table for `GenerationOutcome` — all six statuses plus an unmapped one, crossed with the handler's own
cancel, answering identically to both hand-written copies. Four things came out of the round.

**The tie-break in the keep-alive wait.** `Task.WhenAny(wait, delay)` settled a tie by argument order, which put the
channel first; `wait.WaitAsync(timeout, ct)` settles it by which fired first. So if the timer expires
and the generation completes in the same gap before the awaiting thread is scheduled, the rewrite
writes a keep-alive where the old code returned. That matters because a backend with no preflight
reports an over-length prompt by completing the generation with no delta at all: the keep-alive
commits HTTP 200 and the refusal that should have been a 400 becomes an SSE error event. Codex
reasoned it out of the .NET sources rather than reproducing it, and no test could see it, but the
preference was real and is now spelled out — the timeout path returns the delta if `wait` has since
completed, instead of inheriting the old behaviour from an overload's parameter order.

**`DeltaSink`'s destination is a type, not a delegate.** `DeltaSink` was first hoisted with an `Action<string>?`
observer, which would have accepted a closure over the `HttpResponse` — the exact thing the type
exists to make impossible, weakened in the commit that hoisted it for chunk 7 to use. It now takes a
`ChannelWriter<string>` or a `CutWatcher` through one of two factories, with a private constructor, so
the compiler is back to checking what the comment claims.

**Two comments were wrong about the framework.** `Task.WaitAsync` does not leave a continuation behind
per timed-out lap — its promise unregisters itself and releases its timer on the timeout path too —
and a timeout has no precedence over a cancellation that becomes ready at the same moment; either can
win, which is why the explicit `ThrowIfCancellationRequested` before the keep-alive write stays. The
entry above also had the callback-thread invariant backwards: `CutWatcher` completes its
`TaskCompletionSource` *on* the backend's callback thread on purpose, and
`RunContinuationsAsynchronously` is what keeps the continuation that cancels the generation off it.

**Verified on the NPU.** `smoke.ps1 -Backend phi-silica -Port 5298` passed every step on build 29648
(one skip, the chunk-7 tool probe; five informational), and the numbers are the ones D80 recorded
before the refactor: `prompt_tokens` 41 and `completion_tokens` 2 for the PONG exchange on both
shapes, 3581 Phi-3 tokens at the preflight boundary for the fox filler and the CJK run, the
eight-token cut streaming 31 characters as 8 tokens with `finish=length`, a cache hit on the
continuing conversation, `text_mismatches=0` and `late_deltas=0`, and four clean teardown rows. The
unit suite only ever sees `FakeBackend`, so this is the check that the live path both shapes share
still behaves as it did.

**Coverage the new test file claimed and did not have.** A `Complete` that the handler had also
cancelled, and a status the mapping has never heard of, are now pinned rather than asserted in a doc
comment. 655 tests.

**D82. The three low-severity notes from the 2026-09-10 review, and what one of them turned out to
be.** Issue #10. All three were recorded in `docs/FUTURE.md` after chunk 4 as "none load-bearing,
none fixed in that session", and each was a small contained change. Taken now rather than folded into
chunk 7, because two of the three live in the two endpoint files D81 had just rewritten.

**The JSON path let a non-client cancellation escape as a bare 500.** Its catch was
`when (ex is not OperationCanceledException)`, so "cancelled, but `RequestAborted` is not set" matched
no clause and died as an unhandled request exception: HTTP 500 with no OpenAI envelope, where the
streaming sibling answered the identical event with a 502 and the ordinary error body. An adapter that
breaks the `ILanguageModelBackend` rule about swallowing the runtime's own cancellation produces it,
and the cut's linked token makes it reachable without the client going anywhere. The shape is now the
streaming path's exactly: a first clause filtered on `RequestAborted` that returns nothing and logs
`http=0`, then an unfiltered one that reports through `GenerationFailure`. One test per clause, and
each was checked to fail against the old filter.

Getting the second of those tests to reach its clause took three attempts and is worth recording,
because the obvious arrangement does not work. A client disconnect does not ordinarily reach either
catch at all: both real adapters and the fake *return* `Cancelled` rather than throwing, as the
`ILanguageModelBackend` contract requires, so the disconnect lands on the status check inside the
try. To make a *throw* arrive with `RequestAborted` set, the fake's `CancellationGate` is held shut so
the generation ignores its token, and the responder is an iterator that parks inside `MoveNext` before
the token the injected failure fires on. No gate would do for that park: every gate in the fake awaits
with the caller's token and would answer the cancel with a `Cancelled` status instead of the throw.
The release cannot wait for the client's own task to throw either, tempting as that is as the stronger
ordering signal — TestServer does not complete the client's task while the handler is still in the
pipeline, and the handler is parked, so waiting for it deadlocks. What makes the ordering sound is
that `CancelAsync` runs TestServer's abort registration before it returns; what proves it is the
`http=0` assertion, since a throw observed too early is reported as 502. The first draft asserted
neither the exception name nor the clause, so it passed against the old filter while never reaching
the new one — an adversarial review caught that, with a standalone probe of the fake to prove it.

**`SseStream.Started` is the response's `HasStarted` rather than a flag of its own — and the note's
premise was wrong.** The note said a first write that throws for a reason other than the client
leaving would leave the flag raised, so the failure path would believe the status line was spent and
write an error frame into a response that had never begun. That cannot happen the way it describes:
`HttpResponse.WriteAsync` calls `StartAsync` before it writes a byte, so by the time a body write or
flush fails the response really has started and the old flag was right. The two answers part only
when starting the response is itself what fails, and there the new one is right. So this is a
simplification — there is no longer any state here that can contradict the response — and not the bug
fix it was filed as. No test: the only way to reach the difference is to make `StartAsync` fail, and
then the ordinary error result the caller returns instead fails to start for the same reason. The
client sees nothing either way; what does differ on that path is the server log, since the old code
swallowed the second failure inside the stream's own error-writing catch and the new one lets it reach
the pipeline as an unhandled request exception. Said plainly here because the note will otherwise read
as an open defect.

**`identity.ps1 -Install` no longer destroys the working registration on the path where the install
succeeds.** It removed the old package and then added the new one, so the registration was gone for
the duration of every install, and a failing `Add-AppxPackage` left the machine with nothing
registered and `--backend phi-silica` failing with "no package is registered" — nothing in that
message says the install is what broke it. Be clear about how much this fixes: the fallback below
retries remove-then-add for *any* add failure, so for the likeliest causes — an expired certificate, a
mis-edited manifest, a broken signtool — the retry fails identically and the end state is what it
always was. What changes is that the successful path, which is every install so far, no longer has a
window at all, and the failing path's window is no longer entered before anything has been tried. The
order is reversed:
add first, and remove the previous package full name afterwards only if it differs from the new one.
`Add-AppxPackage` updates a registration of the same identity in place, which is what a re-run after a
rebuild always is, so the usual path now removes nothing at all; verified by re-running `-Install`
over the live registration, which went straight through with no removal step and left
`NpuBridge_0.1.0.0_arm64__jtas4mnxdyzpe` registered throughout. The remove-then-add order survives
only as a fallback for a refused in-place update, entered after the safe path has already failed —
so the window that was previously guaranteed is now merely possible.
Two further defects in that script came out of the same review and are issue #19, both unreachable
while the manifest stays at 0.1.0.0: `Get-RegisteredPackage` sorts `Version` as the string it is, so
`0.9.0.0` outranks `0.10.0.0` and a version bump can leave two registrations; and the superseded
removal is unguarded under `$ErrorActionPreference = 'Stop'`, so if a bump replaces the registration
rather than adding to it, the removal fails "not found" and kills the script after the install has
already succeeded.

657 tests. `smoke.ps1 -Backend phi-silica -Port 5298` passed every step again afterwards, which is
what says the reordered install still grants identity: the run relaunches through package activation
and reports `identity=True`.

**D83. Tool-call emulation: the model is asked for JSON, and everything after that is the bridge not
believing it.** Chunk 7, issue #3, PLAN §2.6. `tools` and `tool_choice` now produce OpenAI-shaped
`tool_calls` from a runtime with no native tool calling. The design was signed off; what follows is
what building it decided.

**Injection goes into the system text, not beside it.** `ToolSchemaRenderer` writes the block —
preamble, one compact signature per tool, the envelope the parser reads — and `PromptTemplate.Render`
appends it to the system section. That placement is the whole cache story: `ConversationKey` hashes
the system text, so two requests offering different tools cannot share a context, having been told
about different tools. Compact signatures are not a nicety either: full JSON Schema for OpenCode's
~15 tools is 2–3K tokens against Phi Silica's ~3.5K window. `tool_choice: "none"` renders nothing and
nulls the catalog, so all three ways of turning the feature off — no tools, `none`,
`--tool-emulation off` — are one code path and one question for phase two.

**Buffering is what the streamed shape costs.** With tools present nothing goes out until the reply is
whole, because only a finished reply can be told from prose, and a delta already written cannot be
recalled. The cut still runs during the drain, so `max_tokens` and `stop` behave as they always did.
The price is that the window in which a failure is still an ordinary HTTP status now spans the whole
generation, and that keep-alives have to cover it — a deadline measured from the last frame written,
not from the last delta received.

**The parser's rule is that a false positive is worse than a miss**, because the client's answer to a
tool call is to run it. That single principle decided every contested case: an object that never
declared itself a call needs both `name` and `arguments`; `parameters` is accepted only inside a
`tool_calls` wrapper, since outside one an object with `name` and `parameters` is the tool
*definition* echoed back; arguments that were supplied and cannot be read drop the call rather than
defaulting to `{}`, which would hand over a confident call with every optional parameter at its
default. Unknown tool *names* are the exception and are surfaced, because PLAN says the client
decides. The parser never throws: every malformed reply is content.

**What the hardware said.** `smoke.ps1`'s probe, 20 runs on build 29648: 20/20 called the tool, no
prose, no leaked protocol, no unoffered tool, every argument valid JSON. PLAN predicted 60–80 %. That
is better than expected and it is not the case the prediction was about — the probe asks one tool with
one required string argument, and the plan's pessimism is for 10+ tools, deep schemas and a 3K-token
agent prompt. What 20/20 establishes is that a model this size understands the instruction and the
compact signature form at all, and that the parser handles what it actually emits. The hard case is
issue #21.

**D83 review round (a Claude subagent and Codex, 2026-09-11, then a whole-branch pass).** Fifteen
findings across the three, all real; the fix commits list them. Both reviewers independently found the same one, and it is the one worth keeping in
mind.

**Every turn of an agent loop missed the cache.** The reply was stored with `Keep(result.Text)`, so
the key described an assistant turn whose *content* was the raw model text — fence, prose and all.
What a client sends back is an assistant message with null content and the `tool_calls` array the
bridge emitted, and `ConversationKey.AppendTurn` hashes that array's count, ids, types, names and
arguments as fields of their own. The two could never match, on any input, including a model that
wrote the normalised envelope verbatim. `Keep` and `Compute` now take the turn rather than its text.

Two consequences to state, because the fix changes what a key means. The stored key now describes
**what the client will send back**, not what the context literally holds: the context absorbed the
fence and the surrounding prose, and the key describes the normalised envelope. That is sound because
a hit renders only the turns *after* the prefix, so the model never sees the divergence — but "the key
is the transcript the context holds" has stopped being true. And the round trip hits only for a client
that echoes the `arguments` string byte for byte, since the hash takes it as sent; a client that
re-serialises with different spacing misses.

This was hiding behind a comment of mine that asserted the opposite, and the mistaken reasoning in it
— that rendering the call id would poison the key — was wrong twice: the id is hashed as its own field
whatever the rendering does, and it round-trips precisely because the client echoes ours. Omitting it
from the *prompt* is still right, for the unrelated reason that the model is never asked to write one.

The other finding worth recording is a framework trap. `JsonDocument.Parse(string)` transcodes UTF-16
to UTF-8 before it parses, so invalid input fails with `ArgumentException`, not `JsonException` — and
a `catch (JsonException)` around it looks exhaustive and is not. A lone surrogate in a reply therefore
escaped the parser and answered a perfectly successful generation with a 502 that blamed the backend
for the bridge's own parser. D58 exists because this runtime splits surrogate pairs across callbacks,
so that input is not hypothetical. The same catch appears in `PromptTemplate`, unreachable from the
wire today because the request deserializer rejects one first, and was widened anyway.

A smaller one with a lesson: `tools` and `tool_choice` were still on the accepted-but-ignored list, so
the operator's "is this feature on?" signal said the opposite of the truth the moment the feature
shipped. The test that should have caught it asserted the list's contents and passed, because the
stale entries were in the expected value; it now names each implemented parameter individually.

866 tests.

**D83's two wire-visible decisions, which PLAN did not settle.** Both came out of the reviews and both
differ from §2.6 item 4's letter, so they are recorded here rather than left in a commit message.

**A cut that still parses reports `length`, not `tool_calls`.** PLAN says a tool-call reply finishes
`tool_calls`, flatly. But a `max_tokens` budget that fired produced its call out of a reply the model
had not finished — the cut may have landed after the first of two calls, or in the prose after one —
and answering `tool_calls` tells a client that resumes on `length` there is nothing left to resume.
The client gets both facts instead: the calls it can run, and the truth that the text was truncated.
Both shapes do it, which was already true; what was missing was a decision about which label wins.

**`index` is written only on the streaming shape.** OpenAI's `ChatCompletionMessageToolCall` has
`id`, `type` and `function`; `index` exists only on the streamed delta, where a client assembles the
array across chunks by it. Writing it on both was one type tidier and wrong by D77, which is a
decision to follow the schema's shapes exactly — a client generated from that schema rejects an
unknown field. `ChatCompletionToolCall.Index` is therefore nullable and omitted off the stream.

**Accepted costs, both from the parser's declaration rule.** An unwrapped call with no arguments
(`{"name":"get_time"}` for a zero-argument tool) is content, because outside a `tool_calls` wrapper an
object needs both keys or a sentence quoting `{"name":"Ada"}` becomes a call. The wrapper is what the
injected instruction asks for and what the model produced 20 times out of 20, so this bites only a
reply that drops the wrapper *and* omits arguments; issue #22 carries the fix if it ever shows up. A
bare array does not declare its elements either, for the same reason: `The staff list is
[{"name":"Ada"}]` is a sentence, and brackets are punctuation the model did not have to mean.

**The scan bound counts characters, not candidates.** A reply that is nothing but openers costs one
scan to end-of-text per opener. Bounding the *number* of candidates punishes the wrong reply, because
a code block is full of balanced braces that each cost only their own short span — an agent's reply
would stop being searched partway through and a call after the code would be dropped. Unbalanced
braces are what cost, and each spends the whole remaining text, so a character budget stops exactly
them: 32 times the reply's length, with a floor for short replies.

**D84. The scheduler serializes the model handle, not the generation: `ConversationSession.Acquire`
runs inside the scheduled closure — and the chunk's own brief said the opposite.** Chunk 8, issue #4,
PLAN §2.7. One worker drains a bounded queue so two requests can never generate at once. The task
brief elaborated on that by putting the queue wait "after the preflight and cache lookup", which left
`backend.CreateContext` and `backend.GetUsablePromptLength` — both calls on the one shared
`LanguageModel` handle — outside the queue. The task-2 review traced what that means: while request A
generates inside the worker, request B makes those two calls on the same handle from a request thread,
unguarded, with no admission limit anywhere before them, so N simultaneous requests make N
simultaneous handle calls. That is the exact gap `docs/FUTURE.md` describes as the reason chunk 8
exists, and it contradicts PLAN §2.7's own sentence, "a job cancelled while queued is dropped without
touching the model". The spec is the binding authority and the elaboration of it was wrong. It is
recorded here rather than fixed quietly because the wrong instruction was the controller's, the briefs
are gitignored, and a later session reading a brief instead of the code would put the race back.

Two further consequences confirmed the reading rather than merely agreeing with it. The *rejection*
path did the most model work of any path in the bridge: a burst of fifty requests would have created
fifty contexts and issued fifty preflights in order to throw forty-five of them away. And because a
`ContextCache` checkout is exclusive for the whole lease, a queued request turned a second request for
the same conversation from a cache hit into a miss — the agent-loop case the cache exists for.

**What it costs.** An over-length transcript under load waits its turn before being refused instead of
being refused in tens of milliseconds, and on the streaming shape that refusal can degrade further
(D89). D73's real value — not spending 26 seconds of NPU time to discover an overflow — survives
intact, because the preflight still runs before the generation; it just runs later. The cost is
bounded by the queue's capacity and is identical to the old behaviour whenever nothing else is
running, which is every request on this single-user machine.

**D85. The lease is published from inside the scheduled closure, never carried out on its return
value.** The closure returns a `ChatAttemptResult`, and the first wiring took the lease off it. The
outer `finally` is the one place a context is released (D43), and on the streaming shape the reader
loop can unwind long before the scheduled task is ever unwrapped — a client that vanishes mid-frame is
the ordinary case, not an exotic one. A lease the outer scope learns about only from a returned value
is a lease it does not have on every path where that value never arrives, so the `finally` saw null
and the context was never released: D43 and D51 both gone at once, in a chunk whose whole subject is
the handle those contexts belong to. It cost a fix round, and it was behind four of that round's five
failing tests. The closure now assigns the outer `lease` variable the instant `Acquire` hands one
over, before anything in it can throw. A `--truncate-history` retry reassigns it over a lease the
closure has already disposed; `ContextLease.Dispose` is idempotent, so a stale reference settles to a
no-op rather than a double release, and the disposed lease's `CacheHit`, `TailTurns` and `PromptChars`
stay readable afterwards because they are plain fields set once in the constructor — the catch
clauses' log line needs them to say `cache=hit|miss` rather than `cache=-`.

**D86. A `--truncate-history` retry keeps its scheduled slot instead of re-entering the queue.** The
preflight truncation loop and the retry-after-a-failed-generation loop both live inside one scheduled
closure, so a request that drops turns and tries again never goes back to the end of the line. One
`ScheduleAsync` per attempt was the alternative, and it would 429 a request that is already mid-flight
whenever the queue had filled behind it — the least defensible moment there is to shed load, since the
work is half done, the client has already waited, and the retry exists at all because the bridge chose
to salvage the request rather than refuse it. Keeping the slot costs the scheduler nothing: the retry
is the same conversation against the same handle, which is what the worker is already holding. The
price is that one slot can be held for several preflight rounds and then a generation, so the rolling
average behind `Retry-After` is measured over a whole attempt rather than over a single generation —
which is the number a waiting client actually wants anyway.

**D87. `QueueDepth` is a live counter, not `Reader.Count`, and reading that as cosmetic was wrong.** A
job whose caller cancels it while it is still queued completes as `Cancelled` the instant the token
fires, without waiting for the worker to drain to its position — otherwise an aborted client's handler
stays pending for a whole generation. The first fix stopped at the caller and left the *slot* half:
`/healthz` still reported `_queue.Reader.Count`, which shrinks only when the worker actually dequeues.
That was filed as cosmetic, a number on a diagnostic endpoint. It is not. `ComputeRetryAfterSeconds`
multiplies by that number, and more to the point, a caller that enqueues, gives up and retries several
times against one long generation leaves every one of those dead jobs counted for the generation's
whole duration: the bridge advertises itself as busy on behalf of work nobody is waiting for, and
sheds live load to protect it. Depth is therefore `_liveQueueDepth`, incremented once the job is
actually written to the channel and decremented at whichever comes first of "its own caller cancelled
it while it was queued" and "the worker dequeued it".

**The half that is still true, stated plainly.** The channel slot itself is still held until the
worker drains to the dead job, because a bounded channel has no way to withdraw an entry. So the
admission gate — the capacity — can still be occupied by jobs that will never run, and a burst of
aborted clients can still 429 a live one. What changed is that no reported number and no
`Retry-After` is computed from that stale count any more. The remaining exposure is bounded by the
capacity (4 by default) and by one generation's duration.

**`Retry-After` before anything has finished.** PLAN §2.7 defines the estimate as depth × rolling
average and does not say what the average is before any generation has completed. It is 0, so a
cold-start flood's rejection falls to the floor of one second; the floor already existed for exactly
this shape of gap, and a client's first retry after a cold-start flood arriving sooner than ideal is a
retry, not a failure. The average is a plain cumulative mean over every job whose body actually ran —
never one dropped while only queued, which touched the model for zero seconds and would only drag the
mean down — with no decay and no window, so it reacts slowly in a long-running process. Recorded
rather than fixed: nothing here has run long enough for it to matter.

**D88. `Cancelled` carries `Ran`, and an enqueue after shutdown is 503 rather than 429.** Two
decisions about one enum value, both wire-visible.

`ScheduleResultKind.Cancelled` covers two events that a caller must be able to tell apart. A job the
worker never got to — dropped while still queued, or enqueued after shutdown had begun — touched the
model for zero seconds, and is HTTP 503 `queue_shutting_down`. A job that *ran* and ended by throwing
`OperationCanceledException` for its own token is an adapter breaking the `ILanguageModelBackend` rule
that the runtime's own cancellation is swallowed and reported as a status: the exact contract
violation D82 exists to answer. It is reported through `GenerationFailure.FromException`, exactly as
an escaped exception always has been, so that a client cannot tell "the queue is fine and the backend
broke its contract" from "the bridge threw" by the shape of the two bodies. Without `Ran` the two were
indistinguishable to a caller, and a live generation that threw came back as a cheerful "the queue is
shutting down" — a 503 inviting a retry, for a condition a retry cannot help.

The second decision: a post-shutdown enqueue answers `Cancelled`, not `Rejected`. `Rejected` becomes
"429, retry in N seconds", and a scheduler that has stopped is never coming back to honour a
`Retry-After`. Taken before task 2 rather than deferred, because task 2 baked it into the wire shape.
Ahead of all of it, `clientAlreadyGone` is checked first and wins over every other reason, matching
the convention every failure path in these endpoints already follows: an aborted client is answered
with silence, never with a body nobody will read.

**D89. D52's boundary has a queue wait inside it now.** An amendment, not a new rule. D52 says the
headers of a streamed reply are committed by the first frame — a `: keep-alive` comment after about a
second, or the first delta, whichever comes first — that a failure before that moment is the ordinary
HTTP status with the ordinary JSON body, and that a failure after it is a `data: {"error":...}` event
with the identical envelope. The boundary itself has not moved. What has moved is how much work now
happens on the far side of it: since D84 the queue wait, the cache lookup and the preflight all take
place while the stream is already counting down to its first keep-alive. So a preflight refusal that
would have been a 400 `context_length_exceeded` in tens of milliseconds becomes an SSE error event on
a 200 whenever the request spent about a second waiting for its turn first. A queue-full rejection
degrades the same way: `Retry-After` is written only while the response has not started, and a stream
that has already sent a keep-alive learns the queue was full from the error event's body instead.

This is a real loss of fidelity and it is accepted rather than engineered around. The obvious
alternative — hold the first keep-alive until admission, so the status line stays the server's through
the wait — buys the status code back by reintroducing the failure D52 was written to prevent: a client
that sees nothing at all for a queue wait plus a generation, and gives up. It is reachable only under
concurrent load, which on this machine is a burst its single user created, and the error body a client
receives is identical either way; what degrades is the status code that carries it, not what the
client is told. One further imprecision on the same path is accepted for the same reason: a queued job
dropped asynchronously at host shutdown, with the client still connected, surfaces as a 502
`server_error` event rather than 503 `queue_shutting_down`. D52 mandates an event rather than a status
there in any case, so only the code inside it differs.

**D90. `/debug/generate` goes through the scheduler, superseding D40's deferral.** D40 deferred it in
as many words — "once chunk 8 exists" — and the chunk-2 entry in `docs/FUTURE.md` said the same. Issue
#4 puts it in scope explicitly and is the newer authority, and "once chunk 8 exists" is now. The
reason is stronger than politeness about a debug endpoint: it creates a context and generates on the
one shared handle exactly as the OpenAI endpoints do, so an unqueued `/debug/generate` is precisely
the race D84 closes, arriving by a second door. It now creates no context at all until its turn comes,
and a job dropped while queued touches the model for zero seconds. The cost is that a debug request
waits behind real traffic, which is the point of routing it there. It has no streamed shape and no
client worth the `clientAlreadyGone` distinction, so it passes `false` explicitly at its own call site
rather than inheriting a default that would hide the question.

Two imprecisions on that endpoint are known and deliberately left: a client abort while queued is
reported as 503 `queue_shutting_down`, untrue for that case and harmless because the client is gone;
and an `OperationCanceledException` for a token other than the request's still escapes as a bare 500,
which is what the OpenAI shapes stopped doing in D82. Both are filed as issues rather than fixed here,
because this is a loopback debug endpoint and the working method forbids widening a chunk to absorb
review findings.

**D91. `/v1/completions`: the wire decisions PLAN did not settle, and the 160 lines its existence
forced into one place.** The legacy shape wraps `prompt` into one user message and runs the identical
pipeline the chat shape runs from the model-id check onward. Four decisions were left to the chunk.

**A `prompt` array with more than one element is a 400 `invalid_request_error`.** PLAN §2.2 promises
"string or single-element array" and stops there. Real OpenAI accepts several prompts and answers with
several choices, which is a batching feature this bridge has no way to serve behind a single-worker
scheduler. Refusing is honest; silently generating from the first element and discarding the rest is
not. A client that batches gets an error and files an issue, which is the outcome that carries
information back.

**The id keeps the `chatcmpl-` prefix rather than OpenAI's `cmpl-`.** Every realistic consumer was
checked — the OpenAI Python and Node SDKs, LangChain, LiteLLM, OpenCode — and all treat `id` as
opaque; one id allocator serving both shapes is one fewer thing to keep in step. Genuinely cosmetic,
and on the future list as exactly that rather than as a defect.

**The legacy parameters are accepted and warned, never implemented.** `echo`, `best_of`, `suffix`,
`logprobs` and `logit_bias` have no equivalent field on the chat shape at all, so they go through the
shared ignored-parameter check and earn a log line each, which is the point: an operator's "is this
parameter doing anything?" signal is worthless if it covers only the parameters that would have been
harmless to ignore (D83 made the same mistake with `tools`). `echo` is the one whose answer differs
materially from a real server's, since the prompt is not prepended to `text`. Its warning is the same
generic one every ignored parameter gets and does not spell that consequence out; the README does,
and a line of its own here would be the better place for it.

**Headers on the streamed shape commit at the first cutter release rather than the first delta**,
because there is no role chunk to send ahead of the text. That is a consequence of the shape
difference and arguably more faithful to D52 than the chat shape is; recorded so that a later reader
does not mistake it for drift between the two.

**What the endpoint cost the codebase.** Its streamed shape arrived as a copy of the chat stream's SSE
plumbing: `SseStream`, both delta waits, `FailAsync` and `ReportSchedulerOutcomeAsync` — about 160
lines. The copy had been taken after all of task 2's fix rounds, so unlike the usual case it was
byte-identical rather than already drifted, and every one of the seven helpers diffed clean. That is
what made extracting them a pure move with the chat shape's large streaming suite standing as the
regression guard for the move itself, and it is why the extraction happened immediately instead of
being deferred: D81's rule is that this pipeline is written once, the only thing that had ever made
these helpers chat-shaped was the static type of one argument to `WriteChunkAsync` (now a type
parameter inferred at each call site), and leaving the copy would have installed a permanent "fix both
by hand" rule on the file this chunk had already fixed three times. They live in
`Api/StreamingPipeline.cs`. The same extraction answered a coverage finding that would otherwise have
taken a parallel test suite to answer — the duplicated paths had no D52, queue-full, keep-alive,
disconnect or stale-timeout test of their own — with one exception that had to be written by hand: the
client-disconnect tests cover the `http=0` clause and the D51 cancel-drain-settle `finally`, which are
per-endpoint code that was not extracted, so those were ported to `/v1/completions` rather than
inherited.

**D92. Publish-before-arm, twice in one file, and why the fix is `Interlocked` on both sides.**
`GenerationScheduler.ScheduleAsync` writes a job to the channel and then sets up state that job needs.
`TryWrite` hands the job to the worker immediately, and the worker can dequeue it, run it and settle
it before the enqueuing thread reaches the next line — so anything armed after the write has a window
in which it never happens at all. That shape produced two bugs in one file, both on this branch and
both named here so the count is checkable: the cancellation registration, fixed in `4ee9268`, and the
live depth counter beside it, fixed in `cb40c5e`. (An earlier draft of this entry said three. The
third was the unsynchronised registration field from the same task-1 review round, which is a data
race on a field rather than an arming that never happens, and does not belong in the count.)

The first was the cancellation registration, armed after the write: the worker could settle the job
before the assignment landed, so no disposal path ever saw a live registration and it leaked for as
long as the caller's own token source lived. Fixed by arming before the write. The second was the
depth counter standing beside it, left on the old order by the very commit whose comment describes the
window verbatim. `MarkDequeued` called `LeaveQueueIfNeeded`, which read `_queueDepthArmed == 0` and
returned without decrementing; the enqueuer then incremented and armed, and nothing ever decremented
again. `_liveQueueDepth` stayed one too high for the life of the process, `/healthz` drifted
monotonically upward, and `Retry-After` was computed by multiplying by the inflated number. Nothing
wedges — the admission gate is the bounded channel's own capacity, not this counter — which is exactly
why it would have shipped. It surfaced as a flake, one failure in eleven full-suite runs, and was
diagnosed from the failure mode rather than from a reproduction: the report said "expected 0, got 1",
and a `WaitAsync` bound expiring raises `TimeoutException` rather than an `Assert.Equal` mismatch, so
the only assertion in that test that could have produced the message was the one on `QueueDepth`.

**The fix is a memory-model decision, not a tidier one.** `MarkEnteredQueue` arms and then re-checks
`_dequeued`; `MarkDequeued` sets `_dequeued` and then checks `_queueDepthArmed`. Two threads each
store one flag and load the other in mirrored order — a Dekker pair — and release/acquire per field
does not forbid the outcome where both loads miss, because the two accesses are to different
locations. This project's exe targets ARM64, whose model permits precisely that store-buffer
reordering, so the original `Volatile.Write`/`Volatile.Read` pairing was the enabling condition rather
than an incidental detail, and only the full fence each `Interlocked` call carries closes it.
Exactly-once is the `CompareExchange` on `_queueDepthClaimed` and not the flags; the counter cannot go
negative, because the increment precedes the arm in program order and two `Interlocked` operations
cannot reorder; and the "retry" is a single conditional re-check rather than a spin, so there is no
livelock. Verified by removing the fix and putting it back: three failures in five runs without it,
eight clean runs with it.

**The regression test is probabilistic, which is the unsatisfying part.** It detects the pre-fix bug
about 60 % of the time. A deterministic construction exists and is a visibility change only — make the
two-flag gate visible to the test project and call `MarkDequeued(); MarkEnteredQueue();` in the
adversarial order, asserting that the leave fires exactly once — and is filed as tech debt rather than
written here. Given that this file has now had two bugs of one shape, the counter's remaining
assumption deserves stating too: it is correct only while every job written to the channel is
eventually dequeued, which holds today because the worker loop cannot fault and drains after
`TryComplete`.

**Chunk 8's review rounds, the hardware run, and the defect the whole-branch review caught
(2026-09-12).** Six tasks, each reviewed, each but one taking a fix round; the decisions above are the
rulings those rounds forced. Three things from the round are worth the record.

**The first hardware verification of the chunk.** `smoke.ps1 -Backend phi-silica` passed on the first
attempt: 28 PASS, 0 FAIL, 0 SKIP, 5 INFO, no RPC flake. Two concurrent requests on the real NPU queued
correctly — the live `queue_depth` peaked at 1 while both were in flight, and both completed — a run
at `--queue-capacity 1` admitted one request and rejected two with 429, `Retry-After` and
`rate_limit_error`/`queue_full`, and `/v1/completions` answered on both shapes. The review checked
each step against its source rather than against the report: none of the four bottoms out in a
wall-clock comparison, none can skip in the merge-gating configuration, the streamed step really reads
`choices[0].text` rather than a chat-shaped `.Content` that would have read empty either way, and the
concurrency step proves serialization rather than merely that both requests eventually succeeded. The
unit suite only ever sees `FakeBackend`, so this run is what makes the concurrency claim a measurement
instead of a property of a test double.

**One code defect, and it was this chunk's own regression.** The streamed shapes' first-frame hook
could stamp a partial truncated-turns count and make it permanent. Before chunk 8 the arrangement
could not arise — the header was applied after `Acquire` had finished truncating, so the count was
always complete or absent — but with the truncation loop now on the scheduler's worker (D84) and
keep-alives on the request thread, a keep-alive landing inside the loop committed whatever had been
dropped so far, `_headerAppliedFor` locked it in, and the true, larger count reached only the log. The
client is told two turns were dropped when six were. Narrow to reach and silent when reached, and a
wrong number is worse than a missing one, so the hook now writes nothing until `Acquire` has returned:
the post-outcome call then either writes the complete count, if the response has somehow not started,
or logs the "cannot be sent" warning a late truncation has always produced. Both of those are honest.
The test parks the truncation loop in its second round with two of four turns gone, waits for a
keep-alive to commit the headers, and asserts that no header arrives; it fails against the old hook.
`FakeBackendOptions.OnPreflight` exists for it, because the truncation loop runs start to finish
inside `Acquire` and has no other observable moment. 932 tests.

**The same defect had a sibling, and the brief said it did not.** The whole-branch review's write-up
stated that the *other* way turns get dropped — the status-driven retry, which a backend with no
preflight takes because it learns the prompt was too long only by finishing the generation — "already
degrades correctly to header absent, warning logged". It does not. The endpoint closures call
`TryDropOldestExchange` themselves on that path, outside `Acquire` and therefore with the settled flag
true, and the count grows before the loop re-enters `Acquire` to clear it again. The window spans the
failed attempt's lease disposal, which on hardware is a real WinRT context disposal rather than a few
nanoseconds. So `TryDropOldestExchange` clears the flag itself, on its success branch, before the
count moves: a no-op inside the preflight loop where it is already clear, and the fix for the sibling
path. The lesson is the one D81 keeps teaching in a different costume — when two places do the same
thing, hardening one of them is half a fix, and a comment that promised "a partial count can never be
the one the client is given" was written from the same half-view and has been corrected to say what
the code does.

**A correction to the record of an earlier round.** Task 2's implementer reported that one finding's
interleaving "does not reproduce". The re-reviewer checked the pre-fix source and the finding was
correct as written: both shapes passed the cut's linked source as the scheduler's token, so the filter
matched and a cut became a 503. The implementer's own rewiring — pass `http.RequestAborted`, create
the linked cut source inside the closure — is what had removed the route. A fix, not a refutation, and
it is written down that way because the report said otherwise.

## 2026-09-12 — Issue #21: hard-case tool-call compliance, measured on hardware

Chunk 7 measured 20/20 single-tool compliance and `docs/PLAN.md` §2.6 predicted 60 to 80 % on the
hard case — 10+ tools, deep schemas, a 3K-token agent system prompt. That case is now measured, and
the prediction was wrong in both directions: compliance is far better than 60 to 80 %, and the hard
case is unreachable for a different reason than compliance.

**D93. The hard case does not fit, and what does not fit is the tool schemas.** A real agent client
(Hermes Agent v0.21.2, the first ever driven against this bridge) puts **23 tools and 37,069 bytes of
tool-schema JSON on the wire** — measured from the client's own captured request dumps, not estimated.
`hermes prompt-size` reports 25 tools and ~40 KB; it counts every toolset regardless of what a run
actually sends, so it overstates, and the wire figure is the one to quote.

Phi Silica's usable window is 3,581 tokens, and the tool schemas alone are ~10,000 — **nearly three
times the window on their own**. The whole fixed prompt is three to seven times it: `prompt-size`
gives ~102 KB (~25,000 tokens) for the default configuration and ~48 KB (~12,000) stripped to a fresh
config root in an empty directory. **No full-toolset configuration fits** — the earlier phrasing "no
Hermes configuration fits" was wrong, since `-t clarify` demonstrably does.

Restricted to one toolset it works and answers correctly in about 13 s, from inside this repository as
well as from an empty directory, so the binding constraint is the **toolset** rather than the working
directory: the `AGENTS.md`/cwd context tier, at 46 KB the obvious suspect, is not what pushed it over.
That much is a designed 2x2 comparison. "Not the system prompt" is weaker — the two failing requests
carried 55,663 and 8,061 characters of system message and both failed, which is consistent with it,
but nothing captured what the successful `-t clarify` runs sent. `docs/CLIENTS.md` carries the table.
The timings there are process wall-clock around the whole `hermes` invocation as read at the shell,
rounded to the second on purpose: the first write-up quoted 13.1 s for two independent runs and
38.9/33.6 s for the failing pair, while the request dumps' own timestamps put those failures at
roughly 36 and 31 s. The tenth-of-a-second figures were never reconciled and are not reproducible
from the evidence files, so the record keeps "about 13 s" and "~37 s / ~32 s" and nothing finer.

One thing the wire dumps settle that the prose had guessed at: **neither captured request set
`stream`**, so nothing on this branch exercised the buffered streaming branch on hardware (issue #31).
And because the rendered system-text length of those requests was never logged, whether they crossed
D94's 44,000-character boundary is inferred from the identical `COMException` and crash signature,
not shown — the empty-cwd request in particular may have sat below it and crashed anyway.

**D94. An over-large system prompt crashes a Windows system component, and that reframes the fix.**
Native placement sends the system text — which is where tool emulation (D83) renders the tool block —
to `CreateContext`. Below about 8,000 characters the request simply succeeds. From 16,000 to 40,000
the preflight answers correctly and the request is a clean 400 `context_length_exceeded` in 0.45 to
1.2 s. At 44,000 and above, `CreateContext` throws, and the throw is the client-side symptom of
`WorkloadsSessionHost.exe` fail-fasting with exception `0xc0000409` (STATUS_STACK_BUFFER_OVERRUN) in
`ntdll`.

**Eighteen** `0xc0000409` fail-fasts were logged, all inside 12:22:18 to 12:26:56 — the interval when
oversized prompts were being sent — and **none in the preceding three days** of ordinary Phi Silica
use. That is the evidence for the causal claim, and it is narrower and stronger than the "36 crashes"
this entry first recorded: 36 was the count of *all* `WorkloadsSessionHost` faults in that hour, which
lumps in 12 `0xc0000374` heap-corruption faults in `ntdll` and 6 `0xc0000005` access violations in
`tokapi.dll`. The tokapi signature recurs daily with no probe running (103 in the same three days) and
is not attributed here.

Repeated crashes wedge Phi Silica for the whole machine: every generation afterwards, including a bare
"reply PONG" with no tools and no system text, returns 502 `The RPC server is unavailable` in 3 to
17 ms; `/healthz` keeps reporting `status: ready`; the host processes are protected and survive
`Stop-Process -Force`. The same volume sent as a *user* message is refused correctly at every size to
96,000 characters, so this is specific to the system text.

**Recovery behaved differently in the two episodes seen, and this entry does not pretend otherwise.**
In the crash-induced wedge, a bridge restart produced one successful generation and then failed again;
it cleared on its own several minutes later (verified recovered, 6/6 at 413 to 619 ms). A separate RPC
wedge earlier the same day arrived after roughly 86 successful generations with no oversized prompt,
did *not* self-clear across 24 consecutive calls, and *was* cleared by a process restart (issue #30).
Whether these are one fault with different timing or two faults is unresolved. A reader hitting either
should try a restart, and wait several minutes before concluding the machine is broken.

So the guard proposed in issue #29 — count the system text with the backend's `ITokenCounter` before
calling `CreateContext`, refuse with 400 when it alone cannot fit — is not wire-conformance tidiness.
It is what stops this bridge from crashing an OS service, and it should sit well below 40,000
characters rather than at the observed edge. Issue #30 covers `/healthz` reporting ready while
nothing can generate. Do not probe above 40,000 characters of system text on this machine; establish
the boundary offline with the token counter instead. A Feedback Hub report is warranted separately:
a user-supplied string length reaching `__fastfail` in a system service is a Windows defect.

**D95. Compliance is not the problem, and the two design options PLAN left open are both closed.**
`scripts/tool-probe.ps1` (new) measured five dimensions over 115 recorded calls with zero bridge
defects — no malformed body, no non-null content beside `tool_calls`, no invalid JSON arguments, no
protocol leaking into content. Tool count 1 to 25 at 5 runs each: 40/40 right tool, correct
arguments. System prompt at 0, 526 and 1,501 tokens: 9/9. Multi-step, the first hardware exercise of
`PromptTemplate`'s tool rendering: turn 2 did not repeat the call and finished `stop`. It answered
inside a JSON envelope (`{"reply":"Yes, it may be better to carry an umbrella as it is cloudy."}`),
not in prose as this entry first said — which the parser correctly read as content, because the
object declares neither `name` nor `arguments`. That is a near-miss on D83's "a false positive is
worse than a miss" rule and worth knowing.

The 115 recorded calls break down as 40 tool-count + 6 schema-depth (corrected re-run) + 9
system-prompt pressure + 1 multi-step record covering 2 generations + 32 occupancy validation, plus
the 21 of the retracted first sweep and the 6 of the superseded schema-depth run. Earlier drafts said
"114" and the working report said "82"; neither reconciled to the evidence files, and this does. Window occupancy at 50 %, 70 %, 70 %-reversed and 85 % of the window, 8 runs each
under `temperature: 0`: **32/32**, every call the right tool with correct arguments.

PLAN predicted "frequent argument hallucination, prose-wrapped JSON, calling tools that weren't
offered". Mostly it did not happen, but **one of the three did, and the first draft of this entry
erased it.** In the retracted stochastic sweep's 70 % cell, all three runs called `weather` — a name
that was never offered; the catalog holds `get_weather`. It did not recur under `temperature: 0`
(32/32 correct), so it is not a property of occupancy, but it is the one model-side compliance
failure in the whole dataset and it is exactly the failure PLAN named. Arguments parsed as valid
JSON in every call that recorded them, and the parser never leaked protocol.

Scope, because the catalog matters: the probe's tools average ~237 characters each against a real
agent's ~1,616, every request was `stream: false`, and no positive claim here extends past 85 %
occupancy. The `SystemPromptPressure` dimension recorded only "called" and "right tool", so the
argument claim covers the 96 calls where arguments were actually checked, not all of them. The
schema-depth dimension is part of this: its first run recorded `Fidelity = False` across all six
calls because of a probe bug (`param($args)` shadowing PowerShell's automatic variable), and the
corrected re-run is what the 3/3 above refers to. One more provenance note: the committed
`scripts/tool-probe.ps1` is a later revision than the one that produced the dimension 1 to 4
evidence files. It gained the rendered-character reporting for dimension 1 and D96's tokenize
guards after those runs, so a re-run prints columns those files lack. The verdict columns (called,
right tool, arguments valid, arguments correct) are unchanged by that, and the files stand.

So **`--tool-schema full` should not be built** — compact rendering already consumes the window and a
fuller form moves the boundary the wrong way. **Structured JSON output** (2.4.x stable, Phi Silica
only) **is not indicated for parsing** — parsing was never the failure and it buys back no window —
but the ruling is narrower than the first draft claimed: constraining emission to an enum of offered
tool names is precisely what it would be good for, and the `weather` hallucination is the one thing
it would have prevented. Revisit if that recurs. Issues #3 and #1 close on the parsing question, not
on tool-name fidelity.

**D96. A retraction, and why the first run said otherwise.** The first occupancy sweep reported
compliance degrading at ~70 % of the window (0/3, a hallucinated tool *name*), recovering at 85 %,
then not calling at all at 95 %. An adversarial review found the sweep hardcoded 3 runs per cell,
ignoring `-Runs`, and that no cell set `temperature`, so every measurement ran at stochastic
defaults; a lone 0/3 flanked by 3/3 on both sides is what sampling noise looks like across 21
chances. It also found that the sweep left the target tool first in the catalog while dimensions 1
and 2 deliberately rotate it to the midpoint, so the two were never on one scale. Re-run at
`temperature: 0` with the rotation applied, the same cell that had failed 0/3 twice is 8/8, and a
reversed-content control at the same occupancy and repetition depth is also 8/8. **The degradation
finding is withdrawn, not caveated.** What is measured is that occupancy up to 85 % shows no
degradation under deterministic sampling. Because both corrections landed in one re-run, neither can
be singled out as the cause of the original dip, and the record says so rather than guessing.

The review also caught the probe building a 9.5-million-character request had `/debug/tokenize` ever
returned a non-200 — `-SkipHttpErrorCheck` yields no `tokens` property, `Max(1, $null)` floors to 1,
and the padding loop had no iteration cap. That request would have been 200x past the crash boundary
D94 describes. The script now validates every tokenize answer, clamps to 30,000 characters, asserts a
32,000-character ceiling, and caps the loop. A measurement tool whose failure mode is crashing the
machine it measures is worth reviewing before trusting its numbers.

**What is still unmeasured.** Every probe request is `stream: false`. D83's buffered streaming branch
— the whole reply held behind keep-alives, the calls arriving in one chunk — is the half every real
agent client uses, and no systematic measurement covers it; the Hermes runs exercised it only
incidentally. The tool-call round trip through the context cache (D83's ruling that a reply is stored
under the calls the client will echo, not the text the model wrote) is also still unverified on
hardware, because the multi-step cell never read `context_cache_hits` around its two turns. Both are
on issue #21's successor work rather than closed by it.

**D97. Native system text is rejected before context creation.** Issue #29 showed that the normal
preflight was too late for native placement: `CreateContext(systemText)` consumes the system text first,
and D94 measured the Windows model host fail-fast at 44,000 characters. On a cache miss, the bridge now
checks a non-null native system text before it calls `CreateContext`. More than 32,000 characters is
refused as a hard safety ceiling below D94's measured boundary. When a backend has a measured usable
window, text whose backend-token count is at least that window is also refused because it leaves no room
for the prompt. The failure uses the existing `PromptLargerThanContext` mapping and returns HTTP 400
`context_length_exceeded` without creating a context.

Phi Silica reports the D80-measured 3,581-token usable window as a backend constant. The smoke boundary
check verifies that number on hardware, so the constant is checked rather than assumed. Discovering the
window during each initialization is deferred to `docs/FUTURE.md`; it would add a context and preflight
probe that needs its own hardware validation. A backend whose window is unknown still gets the character
ceiling.

`--truncate-history` cannot shrink system text, so this refusal does not enter the drop loop or attach a
truncated-turns header. Folded placement is unchanged: its system text is part of the ordinary prompt and
continues through the existing preflight. Rendered tool definitions are part of native system text, and
the refusal says so when they contributed to the count.

**2026-09-12 addendum.** The guard now runs in `ChatRequestPreparer`, before a request enters
`GenerationScheduler`. A busy queue therefore returns the normal HTTP 400 without taking a slot or
changing the rolling duration that sets `Retry-After`. `PhiSilicaBackend.CreateContext` also checks the
same ceiling for direct adapter callers. `/healthz` exposes `context_window_tokens`, and the D80 smoke
step compares it with the measured boundary on hardware. The native-system smoke step sends at most
32,000 characters; unit tests cover the character ceiling.

**D98. Health reflects generation outcomes, not a probe.** Issue #30 found a Phi Silica process that
still reported `ready` after generations had stopped reaching the model. `/healthz` now records real
terminal generation outcomes: a backend exception, `Error`, or an unrequested `Cancelled` is a backend
fault; `Complete`, a cut-induced cancellation, and filtered or policy-blocked output are successful
runtime answers. Validation, preflight refusals, queue outcomes, client aborts and other work that did
not call the backend leave the record alone. A health check does not generate its own probe because the
single-worker scheduler would contend with client traffic and spend NPU time on every poll.

Two consecutive backend faults return `503` with `status: degraded` and the last fault message. One
fault stays `ready` because the known first-generation RPC flake has cleared on retry. A successful
answer resets the counter. Degraded is advisory: `BackendLifecycle.IsReady` and request admission stay
unchanged so a runtime that self-heals can demonstrate recovery instead of being hidden behind a
refusal.

Automatic model recreation is deferred. D94 records two wedges with different recovery behavior: one
cleared after several minutes, while the other persisted until restart. Recreating a shared model handle
without knowing whether those are one fault could make the self-healing case worse. The degraded state
is the trigger to evaluate if later evidence establishes a safe policy.

**2026-09-12 (evening, local) addendum.** An exception from any backend call inside a scheduled attempt, including
`CreateContext`, `GetUsablePromptLength`, and `GenerateAsync`, is a backend fault; a preflight that
returns a refusal is not. At 17:28 local (00:28 UTC on 2026-09-13; this machine, its commits and the Windows Application log are on Pacific time, the Sidequest board stamps UTC), a fresh bridge reported `/healthz` ready after a 14 s load, then its
first generation took 1,580 ms and returned 502 `The RPC server is unavailable`. Twenty-six more calls
returned 502 in 3 to 16 ms. The Application log had no `WorkloadsSessionHost` crash, and no oversized
prompt had been sent.

**2026-09-12 (late evening, local) addendum, second.** `/debug/generate` runs its prompt even when the
preflight has answered that it does not fit (the endpoint exists to measure the raw backend, and
`smoke.ps1`'s D52 step relies on that), and the runtime answers such a prompt with a generic `Error`
after seconds (D55, D80). That `Error` is not recorded as a backend fault: when the preflight answered
and `usable < prompt.Length`, the attempt records nothing, the same as a preflight refusal on the chat
shapes. Two oversized debug prompts had turned a healthy bridge `degraded` before this. A *thrown*
exception on the same prompt still records a fault, deliberately: a throw from any backend call is the
wedge signature this state exists to surface, and suppressing it would hide the wedge whenever an
operator retried an oversized prompt. Found by the whole-branch review of PR #36.

**D99. The streamed tool-call shape agrees with the JSON shape on hardware, the cache round trip hits, and the wire fields sit where D83 said.**
Issue #31's measurement, run with `scripts/tool-probe.ps1 -Stream -Include ToolCountSweep,WindowOccupancy,MultiStep -Runs 3 -HeadlineRuns 3 -OccupancyCells '70%'` against a bridge built from `main` at 9003a06 (the #29 guard and the SQ-9/SQ-10 probe fixes in), build 29648, `temperature: 0`, on 2026-09-12 at 17:37 local (00:37 UTC on 2026-09-13). 34 calls, every one HTTP 200.

- **Parity.** Six streamed runs (three at one tool, three at 70 % window occupancy) were each paired with a JSON run of the identical request. After removing `index` and `id` and ordering keys canonically, the assembled `tool_calls` were byte-identical to the JSON shape's on all six pairs, and `finish_reason` was `tool_calls` on both sides every time. `id` is minted per response and differs by design. Key order differs by shape and is not a defect: the JSON shape writes `id, function, type`, the streamed chunk writes `index, id, type, function`. The first attempt at this comparison called every pair a mismatch because it compared ids and key order; the probe now records both as informational.
- **Shape checks.** Every element on both shapes carried a non-empty `id`, `type: "function"`, `function.name` and a string `function.arguments`. `index` was present as an integer on every streamed element and absent from every JSON element. The `tool_calls` <=> `finish_reason` biconditional held both ways on all 34 replies.
- **Cache round trip.** Bracketing the multi-step cell with `/healthz` reads: `context_cache_hits` 0 -> 1 and `context_cache_misses` 27 -> 28 across turn 2. The second turn of a tool round trip is served from the cached context, which is the case D83's keying rule exists for.
- **Latency.** At one tool: JSON 2,423 to 2,718 ms, streamed 2,642 to 2,902 ms. At 70 % occupancy (11,949 characters, 2,502 tokens of rendered block): JSON 18,103 to 18,280 ms, streamed 18,007 to 18,420 ms. Buffering the whole reply behind keep-alives costs nothing measurable over the JSON shape.
- **One model behaviour, not a bridge defect.** On the multi-step second turn the model answered with a fenced `{"tool_calls": []}` and nothing else; `ToolCallParser` correctly treated the empty array as content (D83: a bare or empty array declares no call), so the client received the fence as prose with `finish_reason: stop`. An earlier run of the same cell (17:18 local, `main` at eaf98b1) answered "Yes, it's wise to bring an umbrella." The two transcripts differ only in the turn-1 call id, so this is the model's sensitivity to the transcript, not non-determinism. Whether the bridge should swallow an empty `tool_calls` fence is deferred (`docs/FUTURE.md`).

The run between those two (17:28 local) is not a measurement of anything in this entry: the runtime was wedged from the first call (issue #30, D98's addendum), all 27 calls were 502, and the probe's own reporting of that run had two bugs that SQ-10 fixed (a 502 pair was reported as a parity mismatch; the occupancy trailer claimed no 502 had been seen after stopping on one). Raw files: `tool-probe-stream-run1-2026-09-12.json`, `tool-probe-stream-run3-clean-2026-09-13.json` and their transcripts, session scratchpad.

**D100. The JSON endpoints share one scheduled closure, `JsonPipeline`, and the scheduler's shutdown waits, its `Retry-After` mean and the lease's settle flag were tidied with it (issue #26).**
Chunk 8 left `ChatCompletionsEndpoint` and `CompletionsEndpoint` holding the same 60-line scheduled closure twice: the `Acquire` loop, the lease publication (D85), the `CutWatcher` race, the guarded cancel and the `PromptLargerThanContext` retry (D86), then the same admission block and catch pair. That is the shape D56 and D57 came out of, and D81 is the rule against it. The extraction was done on 2026-09-12 (Sidequest wave `leftovers`, candidate 5d32b93, reviewed cross-family) and both endpoints now call `JsonPipeline.RunAsync<TResponse>(...)` with a `respond` factory that names the fields on its own DTO. Before folding, the two closures were diffed with comments and whitespace stripped: five differences, all after `GenerationOutcome.Classify` and all either the tool-call branch or the response DTO. `prepared.Tools` is provably null on `/v1/completions` (the preparer builds its `ChatCompletionRequest` with `Tools: null`), so the shared tool branch is one null check that cannot fire there. `ChatCompletionsEndpoint` went from 438 lines to 118 and `CompletionsEndpoint` from 276 to 93. `CacheLabel`, which existed three times, lives once on `GenerationPipeline`; `StreamingPipeline.CacheLabel` forwards to it so the two streamed endpoints stayed untouched. The health-recorder calls sit exactly where they sat relative to the closure (D98).

Four smaller cleanups landed with it. `GenerationScheduler.StopAsync` and `DisposeAsync`, and the matching pair in `BackendLifecycle`, used `Task.WhenAny(work, Task.Delay(...))`, which leaves a never-completing delay registered on the token when the work wins; each is now `WaitAsync` with a catch, which disposes its own registration. One behaviour had to be neutralised on purpose: `WhenAny` never observes a faulted task and `await` rethrows it, so `StopAsync` catches the worker's fault and logs it at Warning (the issue's item 3) and `DisposeAsync` catches it at Debug, keeping "shutdown does not throw" as it was. `Retry-After` (D87) is now queue depth times the mean of the last 16 attempt durations rather than a cumulative mean since process start: sixteen is four default queue capacities of work, so a burst dominates the estimate within a minute while one cold-start outlier cannot own it; the window is summed on demand under the existing stats lock on the rejection path only, avoiding floating-point drift; the 1 s floor and whole-second rounding are unchanged. `ContextLease._settled` is an `int` claimed by `Interlocked.Exchange`, so whichever of `Keep`, `ReturnUntouched` and `Dispose` gets there first wins without relying on the task-completion barriers that made the plain bool correct.

The review found no runtime defect and one should-fix: the D85 abort path and the D98 CreateContext-throw path were pinned deterministically on the chat shape only. Both tests were added on `/v1/completions` by issue #34's ticket (D102). The review also established that a status-driven `--truncate-history` retry is unreachable from `/v1/completions`: the legacy prompt wraps into one user turn, so there is no older exchange to drop and only the shared preflight refusal is reachable on that shape. The post-merge gate failed once on `A_backend_that_throws_the_cuts_cancellation_is_a_502_not_a_queue_error` (streamed completions case), a test whose 5 ms token delay raced the cut's cancel under full-suite load; it passed 6/6 and 8/8 in isolation on both trees and was rewritten onto a gate under #34 (D102).

**D101. A bare zero-argument call is a call when the request offered that exact tool name (issue #22, amending D83's accepted cost).**
D83's parser refused an unwrapped object that carried `name` without `arguments`, because two reviews had turned ordinary prose into executed calls that way (a fenced `{"name":"Ada"}`; a staff list). The cost accepted then was that a model writing a zero-argument tool in the short form, `{"name":"get_time"}`, was read as content. The fix, landed 2026-09-12 (candidate 532a1cd): `ToolCallParser.Parse` takes an optional set of offered tool names, and an unwrapped object with `name`, no `arguments` and a name in that set (ordinal) is a call with `{}`. The set decides nothing else. A declared call inside a `tool_calls` wrapper with an unknown name is still surfaced for the client (PLAN §2.6); an unwrapped object with both `name` and `arguments` is still a call whatever the name; a bare array still declares nothing, so `[{"name":"get_time"}]` stays content even when `get_time` is offered; a `name` plus `parameters` object is still the definition echoed back. `ToolCallReply` supplies `ToolCatalog.Names`; with no catalog (no tools, `tool_choice: "none"`, emulation off) it passes null and the parser behaves as before. The renderer and the injected block are unchanged, so D93/D94's size hazard did not move. The probe's multi-step cell now offers `get_time` with no parameters, so the rate at which the model writes the short form is measured rather than assumed; the hardware run that closed the issue (2026-09-12 evening, `-ToolProbeRuns 20`) called the tool 20 times out of 20 with no leaked protocol and no unoffered tool.

The whole-branch review then found the exception one key too wide: a zero-argument tool's own definition has no `parameters`, so the model echoing `{"name":"get_time","description":"Get the current time"}` back in prose met every condition and became a call. Narrowed the same day (candidate 18a8e96): an unwrapped object is the short form only when its keys are exactly `name` (or `name` and `arguments` under the existing rule); any other key, `description`, `type`, `parameters`, `function`, makes it content before the offered-name check runs. Two negative-control tests, fenced and in prose, fail without the narrowing.

**D101 addendum: the three small tickets of the same wave.** Issue #25: `/debug/generate`'s scheduled closure excluded every `OperationCanceledException` from its catch, so one raised for any token other than the request's escaped as a bare 500; it now excludes only the request's own cancellation, maps the rest through `GenerationFailure.FromException` to the ordinary 502 envelope, and classifies a client that aborted while queued as client-gone instead of answering `queue_shutting_down` (candidate c1727be, with a test that fails without the fix). Issue #35: a root `.editorconfig` raises CA1305 to a warning for `src/` (no hits surfaced; `InvariantGlobalization` stays on); the 32,000-character ceiling moved to `BackendLimits` in the backends namespace so `PhiSilicaBackend` no longer reaches into `Api` and `SystemTextGuard` is internal again; `RefusalFor` lost its optional pre-computed token count (the preparation overload is the one counting path); `/healthz` is pinned to write `context_window_tokens: null` rather than omit it; the smoke's window cross-check accepts a reported window inside the three-text [min, max] bracket widened by the same 2 % the step already tolerates, and its guard probe sits at 33,000 characters so a future rendering prefix cannot flip which branch it exercises (candidate 98bf52e). Issue #19: `identity.ps1` sorts registered packages by parsed `[version]` with a `0.0` fallback and re-queries the old full name before removing a superseded registration, with the comment corrected to say a version bump normally replaces the registration; the `-Install` ordering from D82 is untouched (candidate 5f2fc17, verified by parse check and `-Status` only, since the bump path cannot run here).

**D102. `/healthz` learns "a backend call was made" from the calls themselves, not from a flag at the top of the closure (issue #34, D98's leftovers).**
D98 armed a fault flag before context acquisition so that a `CreateContext` or preflight throw counted, which the third wedge episode had shown was the throw that matters. The same window covered in-process work: the client-side cut, the raw-output log, the tokenizer calls in the session lookup and the usage estimate. A throw from any of those reached the unfiltered catch before classification and was recorded as `backend_fault` with the tokenizer named in `error`. Landed 2026-09-13 (candidate 0281d69, reviewed cross-family on Opus): a `BackendCallTracker` in `GenerationPipeline` wraps exactly the eight backend call sites across the five shapes (`CreateContext` and `GetUsablePromptLength` in `ConversationSession.Acquire`, `GenerateAsync` in `JsonPipeline`, the two streamed endpoints and `/debug/generate`), and the unfiltered catch records a backend fault only when the exception it caught is the one a tracked call threw. A bridge-side throw is still the ordinary 502 through `GenerationFailure.FromException` but leaves the health state alone; no new `last_generation` value was added, because `GenerationOutcome.Classify` runs before the usage tokenizer on every shape, so a good generation whose tokenizer then throws has already recorded `ok` and cleared the fault count, which is what CLAUDE.md promises. The residual the review named (F7): a bridge throw in the window before classification, the raw-output log under `--verbose`, the cut, or an SSE frame write, records nothing, so a `/healthz` that was already `degraded` stays so although the backend answered; defensible, since the request did fail, and deferred (`docs/FUTURE.md`).

Duration is now the scheduled closure's own, start to terminal outcome, on every path: the JSON shapes already measured it that way, the streamed shapes and the backend-threw-cancellation path did not. `FakeBackend` gained `DeltaGate`, which holds the fake after delta N and before the next token's cancellation check, so the "a cut counts as a success" test and `A_backend_that_throws_the_cuts_cancellation_is_a_502_not_a_queue_error` (all four cases) drive the cut-cancelled branch deterministically instead of racing a 5 or 20 ms token delay (D54). New tests pin: a post-generation tokenizer throw on all four response shapes is not a fault; a `CreateContext` throw on all five shapes is; the D97 guard's refusal records nothing; an SSE write that throws before `RequestAborted` is set is not a fault (the response body is swapped for a stream that throws on its second write, so this is a real writer failure, not a client abort); and the D85 client-disconnect disposal on `/v1/completions`, closing the gap D100's review found. The smoke's ten `/healthz` reads that expected 200 now print the degraded message on a 503.

The review accepted with two should-fix findings, both fixed in the same wave (SQ-25): on the two JSON shapes the `CutWatcher` runs inside the tracked `GenerateAsync` (the delta callback feeds the cutter, whose `IndexAtTokenCount` can throw and fault the generation task), so a cutter throw with `max_tokens` set was still a backend fault; the delta sink now stashes a bridge-side exception and rethrows it outside the tracked region, with a backend fault winning if both happened. And the smoke's `Get-Json` threw on every 503 before its `$expect` check, so the one step written to report a degraded server lost `last_generation` and `consecutive_backend_faults`; it now throws only when 503 is not expected. Also from that review and deferred: `duration_ms >= 0` is an unfalsifiable assertion and the cancellation-path duration is reachable only on a shutdown that lands mid-generation; `Caught` compares against the last exception by reference, so a context `Dispose` that throws could mask a real backend fault; `DeltaGate` waits on no token and its counters are backend-wide, so a test that times out before releasing it parks the fake, and two in-flight generations would make its waits non-deterministic.

The whole-branch review added two more, fixed as candidate c6e44d5 and reviewed cross-family. First, stashing the cutter's fault meant nothing completed the watcher's signal, so `JsonPipeline` waited for the whole uncapped generation, holding the single scheduler worker, to deliver a 502 it knew about at delta one; `DeltaSink` now completes the watcher's signal after storing the fault, the pipeline runs its guarded cancel and drain, and rethrows the bridge fault outside the tracked region afterwards, with a genuine backend exception still winning. A gated test on both JSON shapes pins that the fake observes the cancellation before it would produce its second delta. Second, the D101-addendum change to `/debug/generate` (issue #25) let `SchedulerAdmission.Classify` return `ClientGone`, and the endpoint had no branch for it, so a client that vanished while queued fell into `FailureFor`'s throwing arm; it now returns the empty result like the other shapes. The review's remaining notes are deferred (`docs/FUTURE.md`): the chat-shape twin of the completions disconnect test and the debug endpoint's own are still on a token delay; `cancelledByCut = !watcherFaulted` is inert because a stored bridge fault always throws; `SchedulerOutcome.BackendThrewCancellation` is unreachable through every endpoint because the scheduler's cancellation token is always the request's own; a client that aborts mid-generation on `/debug/generate` still gets a fully built response written into a dead connection; a non-conforming adapter that throws for the bridge's own cancel is still recorded as a backend fault (D82/D88's documented choice); and the bridge fault is rethrown without `ExceptionDispatchInfo`, so its log line points at the pipeline rather than the cutter.

## D103: the smoke wave (2026-09-13 to 2026-09-15)
The eight smoke tickets made the hardware run harder to regress and left a durable record of each run. SQ-30 made the readiness loops tolerate a five-second /healthz response instead of escaping on a slow poll. SQ-31 moved the chat and debug disconnect tests onto DeltaGate after the CI flake and D54's requirement for ordering rather than wall-clock timing. SQ-32 put a pin list on every step and added the JSON summary so a run retained its decisions, counts, durations and full commit hash. SQ-37 made the known first-generation RPC flake visible and retried it once. SQ-33 added real chat-path assertions, moved the D45 system-prompt measurement off the bare debug path, read keep-alives from the server and measured content filtering. SQ-34 covered the two supervisor failure paths. SQ-35 proved local configuration and NPU_BRIDGE_* re-expression on a real start. SQ-36 covered the small routes: the served model route, the embeddings 404 envelope, --help and --version.

The whole-branch review of 3c429ab found eleven findings and a parse check with 0 errors. SQ-40 fixed F1's lost Aion margin text, F2's punctuation-sensitive chat assertions, F4's port-in-use teardown race, F6's empty-log diagnostic, and the script half of F11. F2 remained a hard assertion, with punctuation and quotes tolerated, because the hardware run returned PONG at all three sites and the chat path measures D45; the reviewer's concern that a model finding should be informational was recorded as dissent. F9 remains an assumption: the retry covers the known exception-shaped RPC fault, while a status-shaped fault would not retry. The hardware run superseded F10's missing exact-tip run. F3, F5, F7 and F8 were deferred to FUTURE, one sentence each there with the reviewer's proposed fixes.

The first JSON baseline on the hardware run reported `pins: #15 D16 D24 D37 D38 D44 D45 D50 D51 D52 D53 D55 D66 D67 D69 D71 D72 D73 D77 D79 D80 D83 D84 D87 D91 D97`, 41 steps with 34 pass, 0 fail, 0 skip and 7 informational, 26 pins, `verdict: "pass"`, `firstGenerationRetries: 0`, and the full `edaf254` commit hash. The three chat assertions returned PONG, the truncation step dropped four turns, both completion shapes returned PONG, and no local settings file remained beside the executable. The deferred existing-local-settings assertion is recorded in FUTURE under F3. F5's teardown masking, F7's relative JSON path and F8's help anchor are likewise deferred there.
