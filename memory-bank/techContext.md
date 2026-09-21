# Tech Context: npu-bridge

## Machine
Samsung Galaxy Book4 Edge, Snapdragon X Elite (X1E80100), Windows 11 ARM64 Dev build 29667 (since
some time before 2026-09-16; the earlier notes say 29648, where D70 was measured). The
Git Bash tool runs under x64 emulation and misreports `AMD64`; trust PowerShell. This is the only
machine; the NPU is here.

## Toolchain
- .NET SDK 10.0.400 (arm64), `npu-bridge.slnx`, `Directory.Build.props` (nullable, implicit usings,
  warnings as errors, `latest-recommended` analyzers minus CA1848/CA1873/CA2007).
- Windows App SDK **2.4.1-experimental** (metapackage) + `Microsoft.WindowsAppSDK.Runtime` (for the
  version-constants source) + CsWinRT 2.3.1 (direct) + `Microsoft.Windows.SDK.BuildTools` 10.0.26100.4948
  (makeappx/signtool; also used by `identity.ps1`). CsWinRT reads Windows metadata from the
  `Microsoft.Windows.SDK.NET.Ref` 10.0.26100.57 NuGet, so no Windows SDK install is needed.
- Tests: xunit 2.9.3, `Microsoft.AspNetCore.TestHost` 10.0.11. 952 tests, about 2 s. `dotnet test`
  does not build the exe project (`src/NpuBridge`); a change there needs `dotnet build` as well.
  `Directory.Build.props` sets `InvariantGlobalization=true` solution-wide, which also gates off the
  globalization analyzers (CA1305 and siblings) in `src/`; the test project flips it to `false` for
  the de-DE culture test and carries CA1305 in its NoWarn (#35).
- `Microsoft.ML.Tokenizers` 2.0.0 is Core's one package reference (D80); it pulls `Google.Protobuf`.
  `LlamaTokenizer.Create(stream, addBeginOfSentence: false)` over the embedded Phi-3.5-mini
  `tokenizer.model` (499,723 bytes, sha256 `9e556afd…8347`, MIT, `src/NpuBridge.Core/Tokenizers/Phi3/`).
  Facts learned: `GetIndexByTokenCount` and `EncodeToTokens` offsets refer to the normalized text,
  one char longer than the input (the dummy prefix); byte-fallback tokens of one character share
  that character's offsets; the instance is thread-safe; counting 5,000 chars takes about 3 ms here
  and the index about 1 ms; the model parses in about 57 ms (`tokenizer_load_ms` in `/healthz`).
  `coverlet.collector` is referenced: `dotnet test --collect:"XPlat Code Coverage"` (Core was at
  94.2 % lines and 89.8 % branches on 2026-09-11 before D79). TestServer completes the response pipe
  before it signals `RequestAborted`, so a "client gone" write usually attempts and fails rather
  than being skipped. The host's shutdown timeout is the .NET default (30 s); `BackendLifecycle`
  adds a 15 s disposal grace for a backend still initialising.
- gitleaks 8.30.1 (pre-commit hook + CI workflow) with project rules for LAF tokens. Docs and the
  memory bank are scanned like code (the path allowlists were removed 2026-09-11; the placeholder
  attestation format is excused by regex). When testing a rule, use random-looking secrets: the
  default stopword list excuses alphabet runs such as `AbCdEf…`.
- The GitHub repository is `ookla-ariel-ride/npu-bridge` (renamed 2026-09-11 from
  `Phi-Silica-OpenAI`, which redirects). The local folder was renamed to match on 2026-09-12, after
  the chunk 8 merge: it is now `C:\Users\jimsi\OneDrive\Documents\GitHub\npu-bridge`. Package
  identity is registered against the build output path, so the registration made under the old
  folder is stale. Re-run `.\scripts\identity.ps1 -Install` before the next `--backend phi-silica`
  run; if the relaunch says "registered for <other folder>", that is this and nothing else.
  Done 2026-09-12, and the run is worth recording because it exercised a path nothing had before:
  `Add-AppxPackage` **refused** the in-place update with `0x80073D0B`, "already installed with a
  different external location", and D82's remove-then-add fallback took over and succeeded. So the
  settled note that "`Add-AppxPackage` updates a same-identity registration in place, so a re-run
  removes nothing" holds only while the external location is unchanged — a rebuild. A folder rename
  is the case that refuses, and the fallback D82 kept as defensive is the reason `-Install` still
  worked. `identity.ps1 -Status` cannot show any of this: it prints the WindowsApps
  `InstallLocation`, not the external location, so a stale registration looks healthy.

## Runtime prerequisites on the machine
- Windows App Runtime 2.4.0 (stable) and **2.4.1 experimental** (`Microsoft.WindowsAppRuntime.2-experimentalB`
  + DDLM); the experimental one is what the exe currently binds to. `identity.ps1` installs it from
  `~/.nuget/packages/microsoft.windowsappsdk.runtime/2.4.1-experimental/tools/MSIX/win10-arm64`.
- Windows App Runtime 1.8 (WinML stack; needed by Aion).
- Sparse package `NpuBridge_0.1.0.0_arm64__jtas4mnxdyzpe` registered for
  `src\NpuBridge\bin\Debug\net10.0-windows10.0.26100.0\win-arm64`; cert `CN=npu-bridge-dev` in
  `CurrentUser\My`, trusted in `LocalMachine\TrustedPeople`.
- Installed 2026-09-11: the Aion framework MSIX (1.3 GB, from the sample repo's v1.0.0.0 release), its
  SDK NuGet in `nuget-local/` (gitignored), and the two Qualcomm QNN provider packages (see "Aion
  facts" below for why they cannot be loaded here).

## Phi Silica facts (SDK 2.4)
- `LanguageModel.CreateAsync`, `CreateContext()` / `CreateContext(system)` / `(system, filter)`,
  `GenerateResponseAsync(context, prompt, options)` (only context overload), `GetUsablePromptLength`
  returns `ulong`, `LanguageModelOptions { float Temperature, float TopP, uint TopK (0-32064, default 40) }`,
  `LanguageModelResponseResult { Text, Status, ExtendedError }`.
- `AIFeatureReadyState`: Ready, NotReady, NotSupportedOnCurrentSystem, DisabledByUser,
  CapabilityMissing, NotCompatibleWithSystemHardware, OSUpdateNeeded.
- **No tokenizer and no token count in the API** (checked 2026-09-11 in the 2.4.4 and
  2.4.8-experimental `Microsoft.Windows.AI.Text` metadata): the only tokenizer-informed members are
  `GetUsablePromptLength`, a `GetUsablePromptLength2` overload not yet examined, and the experimental
  `CompressPromptAsync`. **Measured 2026-09-11 (D80): the runtime's tokenizer is Phi-3.5-mini's.**
  The preflight lands on exactly 3581 Phi-3 tokens at every ASCII text boundary (words, digits,
  spaces, markdown, code) and about 1 % lower on punctuation-cluster-dense text (JSON 3543); the
  usable window of an empty context is 3581 tokens, so 515 of 4096 are the runtime's template and
  reply reservation. **`GetUsablePromptLength` returns a UTF-8 byte offset**, not a UTF-16 index
  (Microsoft's page says only "the index"); CJK, emoji and typographic punctuation boundaries agree
  only once converted. Phi Silica *does* return `PromptLargerThanContext` for a prompt moderately
  over the window (584 ms for 1.6 ×); D55's 225 KB prompt is the case that gets the generic error.
- LAF: `LimitedAccessFeatures.TryUnlockFeature("com.microsoft.windows.ai.languagemodel", token, attestation)`;
  stable → `Unavailable` without token; experimental works regardless.
- Measured: model create 15.7 s to 23.6 s cold across two runs on 2026-09-05 (10 s was one earlier
  chunk-2 recording; it varies); on 2026-09-11 three starts in a row measured `create_ms` 34, 38
  and 8,221 (0.2 s, 0.2 s and 8.5 s to ready), so a warm start is under a second when the Windows
  runtime still holds the model and about 8 s when it does not; about 10 tok/s counted by progress
  callbacks (the chars/4 estimate reads 34.5 to 35, D44 and D75); first token in 1.1 s to 1.7 s
  (`/debug/generate`) in 2026-09-05 runs and 256 ms to 378 ms through `/v1/chat/completions` on
  2026-09-11; a full non-streaming one-word reply in 415 ms to 453 ms (677 ms to 899 ms in earlier
  runs); an early cut (`max_tokens=4`) ended in 472 ms against 2,703 ms for a late cut of the same
  prompt (D53 holds); progress delivers multiple tokens per callback.
- **Runtime RPC wedge (three episodes by 2026-09-12):** twice the first generation after a start
  failed with `COMException: The remote procedure call failed` and every later call with `The RPC
  server is unavailable (0x800706BA)`; once (2026-09-12, 17:28 local) a freshly ready bridge answered
  its first and every following call with the same 502 in 3 to 16 ms, with nothing in the Application
  log and no oversized prompt anywhere near it, and recovered on its own about eight minutes later.
  The throw is `CreateContext`, the first cross-process call on the channel, which is why the health
  recorder (D98) is armed before context acquisition. The bridge does not recreate the model. In a
  smoke run a first-generation fault shows as failed steps from `/debug/generate` on; re-run once.
  The event-log signature of the *crash-induced* wedge (D94) is `WorkloadsSessionHost.exe` with
  `0xc0000409`; the daily `0xc0000005` in `tokapi.dll` is unrelated.
- `--system-prompt-placement auto|native|prompt` (new in chunk 3, default `auto`): native context when
  the backend advertises the capability. Measured on this NPU: both placements produce the instructed
  reply under the chunk 3 template; only bare `/debug/generate` ignores system text (D45).

## Aion facts (from the SDK IDL, confirmed against the 1.0.0 nupkg on 2026-09-11)
`CreateAsync`, `CreateContext()`, `GenerateResponseAsync(prompt)`, `GenerateResponseAsync(ctx, prompt)`;
status enum Complete=0, InProgress=1, Error=2, PromptLargerThanContext=3. `LanguageModelResponseResult`
has `Text` and `Status` only (no `ExtendedError`). Unpackaged via
`TryCreatePackageDependency`/`AddPackageDependency` on `Microsoft.AionInstructPreview.Framework.1.0_8wekyb3d8bbwe`
(minVersion 0) plus `Microsoft.WindowsAppRuntime.1.8_8wekyb3d8bbwe` (min 8000.836.2153.0; the SDK's
packaged path injects the same two dependencies). The nupkg's props add the winmd to `CsWinRTInputs`
and the namespace to `CsWinRTIncludes`; it projects inside `NpuBridge.csproj` as-is.

Runtime stack the SDK uses (from its debug output): `Microsoft.Windows.AI.MachineLearning.dll` and
`onnxruntime.dll` 1.23 from Windows App Runtime 1.8; execution provider chosen through the WinML
`ExecutionProviderCatalog` (`selected EP=QNN, Device=NPU, reason=catalog-certified, backend=QnnHtp.dll`);
compiled model cache under `C:\ProgramData\Aion Instruct Preview\Cache\<ver>\QNN\arm64` with a
`.aion-instruct-preview-cache-state.log` beside it. The QNN provider is a separate, sideloaded main
package `MicrosoftCorporationII.WinML.Qualcomm.QNN.EP.1.8` (installed here at 1.8.30.0 by
`ExecutionProvider.EnsureReadyAsync` on 2026-09-11; a `...QNN.EP.2` 2.2450.47.0 arrived with it). Its
DLLs fail `LoadLibrary` with `E_ACCESSDENIED` on this machine, which is why `CreateAsync` fails (D68).
Root cause (D70): the provider is a main package that opts in as a dynamic-dependency target; the OS
accepts the dependency (`TryCreatePackageDependency` and `AddPackageDependency` succeed) but this
Insider build still refuses to map the package's DLLs as images from any process, packaged or not.
Every main package on the machine behaves the same; every framework package loads. The ACL,
signatures, Developer Mode (turned on 2026-09-11, no change) and the driver are ruled out.

Installed on this machine (2026-09-11): the framework MSIX 1.0.0.0 (user scope, `Add-AppxPackage`, no
elevation), the SDK NuGet in `nuget-local/`, both QNN provider packages. Developer Mode is on.
The machine-wide package registry also holds an in-box `WindowsWorkload.EP.Qualcomm.QNN.Framework.1.8`
and the `WindowsWorkload.LanguageModel.*` packages as *staged only* (registered for no user).

## Current docs, read through Context7 on 2026-09-11 (learn.microsoft.com)
- **Dynamic dependencies on main packages** exist only in the Windows 11 OS API (the Windows App SDK
  implementation targets framework packages), and the target main package must opt in with
  `DependencyTarget` in its manifest (the QNN provider does, `uap15:DependencyTarget`). Windows App
  SDK 1.7+ on 24H2+ delegates dynamic dependencies to the OS and extends them to packaged processes.
- **Windows ML moved providers to framework packages** in Windows App SDK 2.1.3 ("discovery of
  execution providers delivered as framework packages"; 2.4.0 adds ARM64EC). Aion is pinned to
  Windows App Runtime 1.8, whose catalog still uses main-package providers, so Aion is the only thing
  here exposed to the main-package path.
- **Phi Silica is a Limited Access Feature on the stable channel** as of Windows App SDK 2.1.3, with
  `AIFeatureReadyState.CapabilityMissing` and `OSUpdateNeeded` added for diagnosis (matches D31).
- **Structured JSON output** (stable `Microsoft.Windows.AI.Text` in 2.4.x, present in both the 2.4.4
  and 2.4.8-experimental metadata): `LanguageModel.GenerateStructuredJsonResponseAsync(..., jsonSchema)`
  returns `GenerateStructuredJsonResponseResult` with its own `GenerateStructuredJsonResponseStatus`
  carrying a schema-failure value; the 2.4.0 notes say output is "strictly constrained to a
  caller-supplied JSON Schema". Chunk 7 shipped the instruction-plus-tolerant-parser approach instead
  and measured 20/20 with it, so a schema-constrained tool-call turn is now a deferral rather than a
  design option (`docs/FUTURE.md`, chunk 7): Phi Silica only, so the parser stays for Aion either way,
  and the two paths would have to agree on every shape.
- **Prompt compression** (`Microsoft.Windows.AI.Text.Experimental`, 2.4.8-experimental metadata only):
  `LanguageModelExperimental.CompressPromptAsync` with `LanguageModelOptionsExperimental.PreferredRetentionRatio`.
  An alternative to dropping turns under `--truncate-history`, deferred: experimental, Phi Silica
  only, and the exe references 2.4.1-experimental, so it would need a version bump.

## Commands
```powershell
dotnet build ; dotnet test
.\scripts\identity.ps1 -Install|-Status|-Uninstall
.\scripts\smoke.ps1 -Backend phi-silica|fake [-Port 5298] [-ToolProbeRuns 20] 6>&1 | Tee-Object -FilePath smoke.log
.\scripts\tool-probe.ps1 -Stream -Include ToolCountSweep,WindowOccupancy,MultiStep -Runs 3 -HeadlineRuns 3 -OccupancyCells '70%' -JsonOut probe.json   # against a RUNNING bridge; -SelfTest needs none
NpuBridge.exe --backend fake --listen http://127.0.0.1:5299 --verbose [--queue-capacity 1]
NpuBridge.exe task install|status|uninstall        # elevated for install/uninstall
NpuBridge.exe service install|start|stop|uninstall # elevated; aion/fake only
```
`--queue-capacity 1` is the quickest way to see chunk 8's 429 by hand: fire three requests at once and
two come back `rate_limit_error`/`queue_full` with `Retry-After`.

## Repository file facts
- `scripts/smoke.ps1` is CRLF in the working tree (`.gitattributes` `eol=crlf`), LF in the index;
  `.cs` and `.md` files are LF both ways. The Edit tool preserves that; `git ls-files --eol` shows it.
- No `python` on the machine. `perl` exists in Git Bash. Multi-line shell heredocs have mangled
  `\u` sequences before; commit messages through `git commit -F -` with a heredoc work.
- A safety hook blocks a command that combines a delete with a `C:\Program Files` path; split it.
- `grep -c` exits non-zero when it counts nothing, which short-circuits an `&&` chain silently. One
  commit was skipped that way on 2026-09-11 and only found by re-reading the log; put a count in its
  own command, or end the chain with `|| true`.
- GitHub treats a closing keyword anywhere in a commit message as a close, including inside a
  sentence explaining something (`fixed: #19` in a "filed rather than fixed" line). Issue #19 was
  closed by accident twice this way, the second time by the commit whose message described the first
  accident. Write "Split out: #N" or "Filed: #N" for an issue that stays open, and keep the real
  `closes #N` on a line of its own.
- Ports used by the smoke script: the main server on `-Port`, auxiliary servers on `-Port + 1`
  (placement runs) and `-Port + 2` (`--truncate-history`).

## Session tooling around the repository (2026-09-12, updated 2026-09-13)
- **Sidequest board** (`sidequest@eigenwise-toolshed` at project scope, profile `coding`): tickets
  in `~/.claude/sidequest/sidequest.db`, executors in `~/.claude/sidequest/worktrees/<project>/agent-<id>`
  on their own branches, delivery `merge` into the board's `integrationBranch` in this checkout, never a
  push. Routes: coding tiers on GPT-5.6 Terra (normal, high) and Luna (easy, medium) through the model
  gateway, `review-audit` on Opus high (cross-family from the coders), debugging and hard coding on
  Opus. This harness's Agent tool lacks `name`/`mode`, so dispatch with `reducedAgentSchema: true`.
  A review bound with `reviewTarget` needs a submitted, un-integrated candidate; a rejected candidate
  is repaired by a fresh ticket that later supersedes it. Executors cannot run the hardware smoke (a
  worktree build has no package identity), and `dotnet test` in this checkout fails while a bridge
  holds the exe. Three executors so far died on API stream idle timeouts at their first step (all
  Opus) and were resumed by `SendMessage` with the briefing command restated. The board refuses a
  ticket `verify` that chains with `;` or contains `$name` tokens: one command or an `&&` chain,
  multi-step checks in the description, and a PowerShell parse check written for an executor needs
  `$errs = $null` before `[ref]$errs`. With `worktreeBase: auto` the first dispatch of a wave wants
  `origin/<integration branch>`; set `local-main` when the wave branch is cut. A plugin update
  mid-session refuses dispatch until `/reload-plugins`, and if the board MCP later disconnects the
  CLI (`node <plugin>/bin/sidequest.js <verb> --project <path>`, forward slashes) still files,
  lists and integrates but refuses to dispatch. Integrating runs `dotnet test` in this checkout and
  rolls the merge back on red, so integrate only while no smoke server holds the exe.
- **Serena and worktrees do not mix.** The serena MCP server is one process per session with one
  active project; three executors editing through it wrote into whichever worktree had activated it
  last (2026-09-12). Dispatch briefs forbid serena in executors until an upstream fix; the
  orchestrating session may still read through it. Not yet reported on Eigenwise/eigenwise-toolshed.
- **Wave branches and PRs.** Each wave integrates on `wave/<name>` (cut from `main`, set as the
  board's `integrationBranch`), ships as a PR whose body carries `closes #N`, CI runs on it, and the
  merge is on GitHub. PR #36 was the first. Never repoint `integrationBranch` while a submission is
  pending against the old target. The rules are in `~/.claude/CLAUDE.md`, which also carries the
  cross-family review rule, the standing authorization to close issues on their definition of done,
  and the no-Claude-attribution rule.
- **Observability plugin** (`observability@eigenwise-toolshed`, project scope, enabled 2026-09-12):
  a loopback observer on 14319, a pinned OpenTelemetry Collector on 4318, the `workbench-otel-lgtm`
  container (Grafana on 3000, OTLP on 14318) under Docker Desktop, SQLite under
  `%LOCALAPPDATA%\Eigenwise\Workbench`, this repository opted in through `.claude/settings.local.json`
  (gitignored, as is the plugin's `.claude/settings.local.workbench-telemetry.json`). The plugin
  cannot install its Collector here: it asks for `otelcol-contrib_0.120.0_windows_arm64.tar.gz`, which
  the release does not publish; the amd64 archive, verified against the release checksums, was
  extracted by hand into `%LOCALAPPDATA%\Eigenwise\Workbench\collector\` and runs under emulation.
  Redo that after any Collector version bump. Docker Desktop must be running for the dashboard; the
  plugin prints "could not start the container" while the container is in fact starting. Verify with
  `bin/verify-project-telemetry.js --project <repo>` from a session started after the opt-in.
- Marketplace auto-update is on for `eigenwise-toolshed` (`~/.claude/plugins/known_marketplaces.json`,
  `autoUpdate: true`); an update still needs `/reload-plugins` in open sessions.

## Aion model family (researched 2026-09-10)
- **Aion 1.0 Instruct**: the Phi Silica successor, announced at Build 2026 on 2026-06-02. Preview SDK
  available now from the sample repo's release v1.0.0.0 (framework MSIX + `AionInstructPreview.Text.Framework.1.0.0.nupkg`,
  ARM64 only, QNN NPU, no CPU fallback; first load compiles the model for 3 to 5 minutes). Also in Edge
  Insider; open weights on Hugging Face were promised for July 2026. The sample repo's last code change
  is 2026-08-07 (the QNN acquisition fix); `aka.ms/tryaion` points at it.
- **How Aion Instruct actually ships (learn.microsoft.com/windows/ai/apis/phi-silica, updated
  2026-07-24, re-read 2026-09-11):** as a model swap behind the existing
  `Microsoft.Windows.AI.Text.LanguageModel` API, not a new SDK. Early October 2026: a standalone
  sideloadable package for testing and LoRA training. October 2026: Insider rollout; Phi Silica stays
  present, the active model is chosen by a Controlled Feature Rollout, and a registry key lets
  developers test side by side. November 2026: retail rollout, Phi Silica removed. "Unlike Phi Silica,
  LAF tokens are no longer needed with Aion Instruct." Windows App SDK 2.4.4 / 2.4.8-experimental
  metadata carries no `Aion` identifier and the Insider builds seen here (29648, then 29667) have no Aion registry keys or
  Aion-branded workload packages yet. Consequence: the preview SDK adapter (chunk 6) is a stopgap; the
  production Aion path is the Phi Silica adapter with a different model behind it, so the October
  work is a `--backend phi-silica` smoke run with the registry key flipped and a re-check of D31.
- **Aion 1.0 Plan**: a different model, not a newer Instruct. 14B parameters, 32K context, native tool
  calling and reasoning, for agentic workloads. "In-box on capable devices in the coming months"; one
  secondary source cites 2026-11-24. As of 2026-09-10 it has no SDK and no preview package, and it is
  on neither Hugging Face nor the Foundry Local catalog. API unpublished; probably the Windows AI Foundry APIs
  rather than the Instruct framework. Needs a 40+ TOPS NPU (this machine qualifies); Windows AI APIs
  now also target GPUs and CPUs.
- Consequence for the plan: a backend with native tool calling should bypass chunk 7's emulation via a
  `ToolCalling` capability, and `--context-window-hint` becomes per-backend. Tracked as issue #11.
