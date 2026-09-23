# Session handoff, 2026-09-22 (evening): `main` is `0c94da3`, the board is empty, no wave open

Supersedes the 2026-09-22 handoff written by the wave's docs ticket. One long session (2026-09-15 to
2026-09-22) took the `smoke` wave to PR #38, ran the `chunk8-leftovers` wave end to end to PR #40,
and landed the post-merge docs as PR #41. This file records where everything stands so the next
session starts from the state and not from the transcript.

## State

- `main` = `origin/main` = `0c94da3` (PR #41, the post-merge status docs, merged 2026-09-22 on green
  CI). Below it: `5b34d4f` (PR #40, the `chunk8-leftovers` wave, D104), `66e309b` (PR #39, the README
  and status pass after the smoke wave), `9ebca06` (PR #38, the `smoke` wave, D103), `8e9444e` (PR #37,
  the `leftovers` wave, D100 to D102). The checkout is clean on `main`.
- 996 tests. CI ran them on PR #40 and again on PR #41. The last clean hardware smoke is the run on
  `949ed25` (the `chunk8-leftovers` wave's last code commit, 2026-09-21): all steps, 0 skipped,
  7 informational, `first-generation RPC retry: 0`, final health `ok`. The docs commits since it change
  no code.
- Board: every ticket SQ-1 to SQ-59 is `done`; `integrationBranch` is `main`, `worktreeBase`
  `local-main`, worktree isolation on. SQ-49 (the review that rejected SQ-43's first candidate) had
  come back as `todo` after its "rejected" verdict was recorded; it was closed by hand on 2026-09-22
  and the behaviour is filed on the Toolshed board as its SQ-21.
- Open issues: #2 and #11 (Aion, waiting on Microsoft), #14 (test-coverage list; the gated
  disconnect tests landed in the smoke wave), #15 (item 7, the LAN `--listen` step, and the manual
  checklist), #16 (CI end to end; needs an ARM64 runner or a CI-only x64 build). #24, #27, #28, #17
  and #33 closed with PR #40; #19, #22, #25, #26, #34, #35 with PR #37.
- #15 has been closed by accident twice, once by a PR body and once by a commit message that quoted
  the PR body's wording. GitHub's parser reads a closing verb before an issue number wherever it
  appears, negation and quotation marks included. Both times it was reopened within the hour with a
  comment. Never write a closing verb near an issue number that stays open, and never quote such a
  phrase with a real number.
- The machine is on Dev build 29667. Phi Silica passed every smoke run on it (five clean runs between
  2026-09-15 and 2026-09-21). The D70 Aion blocker was re-checked on 29667 on 2026-09-21: identical
  `InvalidCache` failure at 0.3 s, recorded on issue #2. A newer `WinML.Qualcomm.QNN.EP.2` package
  (2.2450.47.0) is now on the machine beside the 1.8.30.0 one; the preview SDK does not reach it.
- Serena works. Its plugin `.mcp.json` lost the `--python 3.12` pin in a plugin update on 2026-09-13;
  the pin now lives in `~/.claude/settings.json` as `env.UV_PYTHON = "3.12"` and survives updates.
- Toolshed was updated between waves on 2026-09-22 to sidequest 5.3.1, quartermaster 0.11.4,
  observability 0.7.34, model-gateway 0.51.4 (proxy 0.1.42). The session that did it still ran the
  older plugin code; run `/reload-plugins` (or restart) before the next dispatch. The hand-placed
  amd64 Collector binary survived both observability bumps.
- Memory: at the end of the wave the machine sat at 42 GB of 50 GB commit charge (WSL's `vmmem` at
  4 GB, 36 Edge WebView processes, two idle Claude sessions from earlier in the day) and the harness
  killed a background smoke run for it; the same run passed in the foreground. Close idle sessions
  before a hardware run, or run the smoke in the foreground.

## What the `chunk8-leftovers` wave settled (D104 has the full record)

- #24: the scheduler's publish-before-arm gate is pinned by five deterministic tests, and the tests
  exposed a third signal of the D92 shape (a cancel between `TryWrite` and `MarkEnteredQueue`),
  fixed and proved exactly-once. D104 carries the addendum to D92. A design alternative (arm and count
  before `TryWrite`) is in FUTURE and on issue #24.
- #27: `WaitForDeltaAsync` runs on the injected `TimeProvider`; the completions first-frame hook and
  the streamed pre-frame 400 are tested; the stale-timeout guard is deliberately untested because no
  `FakeTimeProvider` arrangement reaches it (measured 0/1000 and 0/5000), and FUTURE's earlier premise
  was withdrawn.
- #28: a Warning every `--drain-warning-seconds` (default 10) while a cancelled generation drains, on
  every shape and every cancel path including a streamed client hang-up with no further delta, which
  took four rounds because the reader loop read with `CancellationToken.None`. No timeout, no early
  dispose (D51). One narrowed claim recorded with dissent: the closure's tail after channel completion
  (a context `Dispose` on the thrown path) is awaited without a warning.
- #17: `/healthz` reports `capabilities`; the smoke asserts the per-backend profile; Aion's
  `Cancellation` stays withheld until measured.
- #33: the block's closing sentence gained "Never reply with an empty tool_calls list"; ten multi-step
  probes with it and ten without both answered in prose, so the sentence is kept as intent, the parser
  is untouched, and `docs/CLIENTS.md` tells clients an empty-fence reply means "no answer, re-ask".
- Deferred to `docs/FUTURE.md` (2026-09-21 section): eleven items, two of them dissents against a
  reviewer's recommendation (`DrainWarningSeconds` kept as an option; the closure-tail note not
  fixed).

## What the session taught about the process

- A `review-audit` filed after its candidate integrated binds to a commit hash, not a candidate.
  File the review before integrating.
- The board refuses `rework` on a review-bound candidate; a rejected candidate is superseded by a fresh
  repair ticket (`supersede_submission` with `reviewedReplacements` for any file whose delivered
  content differs).
- A ticket that asks for a negative control must declare every file the control touches; SQ-50 hit
  the scope wall on a production file it was only going to revert and restore.
- The `/code-review` skill's finders are refused by the board hook; the skill's agent runs every angle
  itself and still finds things the bound reviews and the board's whole-branch review missed (the
  streamed-abort gap was its catch). Keep both passes.
- A reviewer marks a finding "blocks"; the orchestrator decides. Twice this wave the cost of the fix
  outweighed the risk and the claim was narrowed in D104 with the dissent recorded.
- A resumed agent after a gateway 502 carries on from its transcript; relaunching would have lost a
  whole-branch review in progress.

## Do this next

1. `/reload-plugins` (sidequest 5.3.1 is installed but the last session ran 5.2.1), then
   `git switch main && git pull` and `dotnet build; dotnet test` (expect 996).
2. Next wave candidates, from `main`: #15 item 7 (the LAN `--listen` step; it raises the Windows
   Firewall prompt mid-run, so it needs a design that does not block the smoke), #14 (the remaining
   unit and TestServer gaps), #16 (CI end to end on the fake backend; needs an ARM64 runner or a
   CI-only x64 build). Cut `wave/<name>`, set the board's `integrationBranch`, one ticket per logical
   change, a bound cross-family review on every code candidate before it integrates, the whole-branch
   review and `/code-review` before the PR, and the D105 docs ticket before the push.
3. October 1 is the first date an Aion Instruct package can run here. When it lands: a
   `--backend phi-silica` smoke under the registry override, a re-measure of the usable window and of
   `GetUsablePromptLength`, and a note on whether the package goes through the in-box workload host
   (which would sidestep D70) or Windows ML. The registry path and the "velocity key" are still
   unpublished.
4. Still open with no owner: the serena concurrent-worktree defect report on
   Eigenwise/eigenwise-toolshed (executors in worktrees must not use serena's tools until it lands),
   and the hook-timeout report (58 `PreToolUse:Bash` timeouts in the 2026-09-12 to 15 window; compare
   on 5.3.1 before filing).
