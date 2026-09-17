# Session handoff, 2026-09-16: both waves merged, `main` is `9ebca06`, no wave open

Supersedes the 2026-09-15 handoff. One session merged PR #37 (`leftovers`), ran the smoke wave through
its whole-branch review, fix round and two hardware runs, merged it as PR #38, and closed out the board.

## State

- `main` and `origin/main` are `9ebca06` (PR #38, merged 2026-09-16). PR #37 merged the day before as
  `8e9444e` and closed #19, #22, #25, #26, #34 and #35. The board's `integrationBranch` is `main`,
  `worktreeBase` is `local-main`, and every ticket (SQ-1 to SQ-41) is done.
- 980 tests. CI ran them on PR #38: `Passed: 980, Failed: 0, Skipped: 0, Total: 980`.
- The last clean hardware smoke ran on `edaf254`, the wave's last code commit, 2026-09-15:
  `All steps passed (0 skipped, 7 informational)`, `first-generation RPC retry: 0`, 41 steps, 34 pass.
  Pins baseline: `#15 D16 D24 D37 D38 D44 D45 D50 D51 D52 D53 D55 D66 D67 D69 D71 D72 D73 D77 D79 D80
  D83 D84 D87 D91 D97`. The JSON summary carries the full commit hash. The run on `3c429ab` earlier
  that day had the same counts and pins line. D103 has the wave's rulings.
- PR #38 closed #15 by accident: its body said "this PR does not close #15" and GitHub reads that as a
  closing keyword. #15 was reopened the same minute with an explanation, then given its landing
  comment. It stays open for item 7 (the LAN `--listen` step raises the Windows Firewall prompt) and
  the manual checklist. #14 has a comment for the gated disconnect tests; the rest of its list is open.
- The machine is on Dev build 29667 (`260905-1914`), not the 29648 the older docs name. Phi Silica
  passed both smoke runs on it. The D70 Aion blocker was measured on 29648 and has not been re-checked
  here; the re-check is a `--backend aion` start and one `/healthz` read.
- Serena works again. A plugin update on 2026-09-13 rewrote the serena plugin's `.mcp.json` and dropped
  its `--python 3.12`; the pin now lives in `~/.claude/settings.json` as `env.UV_PYTHON = "3.12"`.
- Toolshed: sidequest 5.1.22, quartermaster 0.11.1, observability 0.7.32, model-gateway 0.51.2, all
  updated between waves. The hand-placed amd64 Collector binary survived the observability bump.

## What this session taught

- A whole-branch review filed after the candidates are integrated binds to a commit hash, not to a
  candidate; file it before integrating next time, or against the hash.
- Two findings reversed on evidence: the review's proposal to demote the chat-path system-prompt step
  to informational lost to the hardware run, which returned exactly `PONG` at all three sites, so the
  assertion stayed hard and gained punctuation tolerance instead (F2, D103). The review's F3 premise
  (an existing `appsettings.local.json` beside the exe) does not hold on this machine: the step wrote
  and removed the file.
- In a PR body, never put a closing verb directly before an issue number unless the merge should
  close it. Write "leaves #15 open".
- The board CLI's `list` said "No tickets" while the MCP listed 36; the MCP is the authority.
- A GPT executor writes a serviceable docs commit from a brief that carries every fact; it still needs
  a prose pass afterwards (this handoff's predecessor read like a ticket).

## Do this next

1. `git switch main && git pull`; `dotnet build; dotnet test` (expect 980).
2. Optional and cheap: `--backend aion` on build 29667, read `/healthz`, and record the outcome on
   issue #2 either way. Do not go further than that one read if it still fails (D70).
3. Rule on #33 (tell the model to answer in prose when no tool applies, measure with
   `tool-probe.ps1`, then decide whether to strip an empty fence).
4. Report the serena concurrent-worktree defect on Eigenwise/eigenwise-toolshed; it is still the reason
   executors in Sidequest worktrees must not use serena's tools.
5. Next wave candidates: #24 and #17 (scheduler and `/healthz`), #27 and #28 (the streaming drain),
   #15 item 7, #14 and #16 (coverage). Cut `wave/<name>` from `main`, set `integrationBranch`, one
   ticket per logical change, a bound cross-family review on every code candidate before it
   integrates, and the whole-branch review before the PR.
6. October 1 is the first date an Aion Instruct package can run on this machine. When it lands, the
   work is a `--backend phi-silica` smoke under the registry override, a re-measure of the usable
   window and of `GetUsablePromptLength`, and a note on whether the package goes through the in-box
   workload host or Windows ML.
