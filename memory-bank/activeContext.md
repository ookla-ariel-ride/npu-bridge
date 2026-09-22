# Active Context: npu-bridge

_Last updated: 2026-09-21, after the chunk8-leftovers wave. All eight chunks of `docs/PLAN.md` are built and merged; work is GitHub issues on wave branches that ship as pull requests. `docs/SESSION-HANDOFF.md` has the current state and the next integration steps._

## Where we are
`main` is `5b34d4f`, the merge of PR #40 on 2026-09-22; 996 tests, run by CI on that PR. The last
clean hardware smoke ran on `949ed25`, the wave's last code commit: all steps, 0 skipped, 7
informational, `first-generation RPC retry: 0`, final health `ok`. The board's `integrationBranch` is
`main`, every ticket is done, and no wave is open.

The wave summary was: the scheduler gate kept its three-signal exactly-once release; the stale-timeout guard stayed deliberately untested; the drain warning gained the no-timeout rule and required a hard-route fix for streamed aborts with no further delta; Aion cancellation remained unadvertised; and the empty tool-call fence stayed content and became a client re-ask contract. D104 records the rulings and the process lessons.

The machine is on Dev build 29667 (noticed 2026-09-16); both smoke runs passed on it. Chunk 6, the Aion
Instruct Preview adapter, is merged but code-verified only: on build 29648 Windows never appended
`WIN://SYSAPPID` for a main-package dynamic dependency, so the Qualcomm QNN provider could not be
image-mapped and no Aion generation has ever run here (D70; issue #2 open). That was not re-checked
on 29667; the check is one `--backend aion` start and a `/healthz` read, and nothing beyond it.

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
The wave closed #24, #27, #28, #17 and #33. #15 stays open for item 7 and its manual checklist; #14 and #16 remain coverage candidates. #2 and #11 remain Aion work, with the hardware blocker still open. The deferred drain and scheduler findings are recorded in `docs/FUTURE.md` under 2026-09-21.

## How to resume
1. Push `wave/chunk8-leftovers` and open the PR with the five `closes` lines named in `docs/SESSION-HANDOFF.md`.
2. Let CI run, merge, then switch to `main`, pull, and repoint the board integration branch to `main`.
3. Close idle sessions before hardware work because the machine reached memory pressure during this wave. October 1 is the first Aion Instruct package date; #15 item 7, #14 and #16 are next-wave candidates.
