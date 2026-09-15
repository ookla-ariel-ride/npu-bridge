# Active Context: npu-bridge

_Last updated: 2026-09-15 (local afternoon), after the smoke wave reached `edaf254`. PR #37 merged as `8e9444e`; **all eight chunks of `docs/PLAN.md` are built and merged; the plan is complete.** Work is GitHub issues, run as board waves on `wave/<name>` branches that ship as pull requests; `docs/SESSION-HANDOFF.md` has the wave's state and what it taught._

## Where we are
`main` and `origin/main` are `8e9444e` after PR #37 merged. `wave/smoke` is `edaf254`, 19 commits over `main`, unpushed and unmerged; the board's `integrationBranch` is still `wave/smoke`. 980 tests remain the last recorded full-suite result from `f1d3b8b`; the smoke branch's last clean hardware run had 41 steps, 34 pass, 0 fail, 0 skip and 7 informational, with the JSON verdict `pass` and the full commit hash.

**The last clean hardware smoke was on `edaf254`** (2026-09-15): 41 steps, 34 pass, 0 fail, 0 skip and 7 informational, final health `ok`, with the identical 26-pin baseline recorded in the handoff. The D80 cross-check reported 3,581 tokens and the first-generation retry count was 0. The earlier clean run on `3c429ab` had the same counts and pins line.

Chunk 6, the Aion Instruct Preview adapter, is merged but code-verified only: build 29648 never appends
`WIN://SYSAPPID` for a main-package dynamic dependency, so the Qualcomm QNN provider cannot be
image-mapped and no Aion generation has ever run here (D70; issue #2 open). Do not re-investigate.

Aion Instruct ships as a model swap behind the Phi Silica API (Microsoft's Phi Silica page,
2026-07-24): standalone package early October 2026, Insider rollout in October under a Controlled
Feature Rollout with a registry key, retail in November with Phi Silica removed, no LAF token.
`PhiSilicaBackend` is therefore the production Aion path. Details in `techContext.md`.

## What the 2026-09-12/13 session built: the `leftovers` wave (D100 to D102)
Six issues as ten board tickets (seven implementers, three bound cross-family reviews), one
whole-branch `/code-review` that found nine findings and fed a three-ticket fix round, four smoke runs.
Every code candidate was reviewed by a different model family than wrote it.

- **D100, one JSON pipeline (#26).** `JsonPipeline.RunAsync<TResponse>` holds the scheduled closure
  both JSON endpoints used to copy (the `Acquire` loop, the lease publication, the cut race, the
  guarded cancel, the truncation retry, the admission block, the catch pair); each endpoint keeps its
  DTO, its model-id check and a `respond` factory. The two closures were diffed before folding: five
  differences, all the tool-call branch (provably inert on `/v1/completions`) or the DTO.
  `CacheLabel` lives once on `GenerationPipeline`. With it: `WaitAsync` replaces the never-completing
  `Task.Delay` shapes in the scheduler and the lifecycle (the worker's own fault is caught and logged
  so shutdown still does not throw); `Retry-After` averages the last 16 attempts under the existing
  stats lock; `ContextLease._settled` is `Interlocked`. The review established that a status-driven
  `--truncate-history` retry is unreachable on `/v1/completions` (one user turn, nothing to drop).
- **D101, offered zero-argument calls (#22).** `ToolCallParser.Parse` takes the request's offered tool
  names; an unwrapped object whose only key is `name`, naming an offered tool, is a call with `{}`.
  The whole-branch review found the first cut one key too wide (an echoed zero-argument definition,
  `{"name":"get_time","description":…}`, became a call); the exact-keys narrowing landed the same day.
  Bare arrays, unknown declared names and `parameters` echoes are unchanged. The probe's multi-step
  cell now offers a zero-argument tool. Addendum: #25 (`/debug/generate` maps a foreign
  `OperationCanceledException` to the 502 envelope and has a `ClientGone` branch), #35 (`.editorconfig`
  raises CA1305 for `src/`; `BackendLimits` holds the 32,000 ceiling and `SystemTextGuard` is internal
  again; `/healthz` writes `context_window_tokens: null`; the smoke's D80 bracket is 2 % and its guard
  probes cover both branches), #19 (`identity.ps1` sorts by parsed version, re-checks before removing
  a superseded registration, and warns when the removal fails).
- **D102, the backend-call fault tracker (#34).** `BackendCallTracker` wraps the eight backend call
  sites across the five shapes; the unfiltered catch records a fault only for the exception a tracked
  call threw. A bridge-side throw is a 502 that leaves health alone; no new outcome value, because
  `Classify` runs before the usage tokenizer so a good generation has already recorded `ok`. Duration
  is the attempt's own. `FakeBackend.DeltaGate` (holds after delta N, before the next token's
  cancellation check) made the cut and cancellation tests deterministic. Follow-ups from its review:
  the cutter's tokenizer runs in the delta callback, so its throw is stashed by `DeltaSink`, the
  watcher's signal is completed so the model is cancelled at once, and the fault is rethrown outside
  the tracked region; the smoke's final-health read tolerates 503 again.

## Open threads
No chunk is outstanding. The smoke wave is complete on `wave/smoke`; its PR and post-merge cleanup remain.
- **Next wave candidates:** #24 and #17 (scheduler and `/healthz`, both reshaped by this wave), #27
  and #28 (the streaming drain), #33 (a ruling first: tell the model to answer in prose when no tool
  applies, measure with the probe, then decide whether to strip an empty fence).
- **Deferred from this wave's reviews** (`docs/FUTURE.md`, 2026-09-13 section): a bridge throw before
  classification leaves `/healthz` stale; `duration_ms >= 0` is unfalsifiable and the cancellation
  duration path is unreachable; attempt duration is plumbed four times where the scheduler measures
  it once; `Caught` is last-exception equality; `DeltaGate` ignores cancellation and its counters are
  backend-wide; two twins of the disconnect test are still on `TokenDelay`; the debug endpoint's
  mid-generation abort writes a body into a dead connection.
- **Coverage (#14, #15, #16)**, **#11** (Aion Plan, no SDK), **#2** (Aion hardware half, blocked on
  the OS). Aion's overflow status is unmeasured; the status-driven truncation path (D73) is exercised
  by the fake only and must be re-checked when any Aion generation runs.
- Two Toolshed defects still unreported upstream: serena's single active project under concurrent
  worktree executors (serena was not used at all this session, by anyone), and the observability
  plugin's missing `windows_arm64` Collector archive (`techContext.md`).

## How to resume
1. Read `CLAUDE.md`, then `docs/SESSION-HANDOFF.md`, then `docs/DECISIONS.md` D100 to D103.
2. Push `wave/smoke`, open and merge its PR, then run `git switch main && git pull` and repoint the board's `integrationBranch` to `main`.
3. `dotnet build; dotnet test` (expect 980). Do not build while a smoke server is running.
4. Resolve the still-undecided #33 ruling and report the serena concurrent-worktree defect to Eigenwise/eigenwise-toolshed.
5. Cut the next wave from `main`: one ticket per logical change (not per issue when an issue mixes a
   refactor with a behaviour change), `worktreeBase: local-main`, verify fields as one command or an
   `&&` chain, a cross-family review bound to every code candidate before it integrates, and the
   whole-branch `/code-review` before the PR.
