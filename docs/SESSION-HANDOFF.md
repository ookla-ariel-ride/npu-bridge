# Session handoff, 2026-09-21: chunk8-leftovers wave ready for its PR

The chunk8-leftovers wave closed #24, #27, #28, #17 and #33 in 21 commits over `main`. The code tip is `949ed25` on `wave/chunk8-leftovers`; 996 tests passed at the end of the wave. The final smoke on that tip passed all steps, with 0 skipped, 7 informational, `first-generation RPC retry: 0`, client disconnect drain followed by the next request in 879 ms, readiness capabilities for sampling options, system prompt context, prompt-length preflight and cancellation, and final health `ok`. The PR had not yet been opened, and the board's `integrationBranch` was still the wave branch.

## What the wave taught

A review filed after integration bound to the integrated hash, so reviews should bind before integration or explicitly review the hash. The board refused rework on a review-bound candidate, so a rejected candidate was superseded by a fresh repair ticket. A negative control had to stay inside its ticket's declared files. The stale-timeout guard remained deliberately untested because the fake-clock arrangement never reached it. The streamed abort required a hard-route fix after the first three drain rounds missed cancellation hidden by a `CancellationToken.None` read. The empty tool-call fence stayed content because stripping it would hide the model's answer.

## Do this next

Push `wave/chunk8-leftovers` and open its PR with `closes #24`, `closes #27`, `closes #28`, `closes #17` and `closes #33`. Never put a closing verb before an issue that stays open. Let CI run, merge the PR, then run `git switch main && git pull` and repoint the board's `integrationBranch` to `main`. Close idle WSL, Edge WebView and Claude sessions before the next hardware run because the machine reached memory pressure at the end of this wave. October 1 is the first date an Aion Instruct package can run on this machine. The next-wave candidates are #15 item 7, #14 and #16.
