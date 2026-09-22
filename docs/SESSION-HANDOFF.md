# Session handoff, 2026-09-22: the chunk8-leftovers wave is merged, `main` is `5b34d4f`, no wave open

The chunk8-leftovers wave closed #24, #27, #28, #17 and #33 in 23 commits over `main` and merged as
PR #40 (`5b34d4f`, 2026-09-22) after CI passed on the tip. The wave's last code commit is `949ed25`;
996 tests. The final smoke on that commit passed all steps, with 0 skipped, 7 informational,
`first-generation RPC retry: 0`, client disconnect drain followed by the next request in 879 ms,
readiness capabilities for sampling options, system prompt context, prompt-length preflight and
cancellation, and final health `ok`. The board's `integrationBranch` is back on `main`; every ticket
is done. #15 was reopened a second time after the merge: a commit message on PR #39 had quoted the PR
body wording that closed it the first time, and GitHub applied the keyword inside the quotation. The
#24 landing comment is posted.

## What the wave taught

A review filed after integration bound to the integrated hash, so reviews should bind before integration or explicitly review the hash. The board refused rework on a review-bound candidate, so a rejected candidate was superseded by a fresh repair ticket. A negative control had to stay inside its ticket's declared files. The stale-timeout guard remained deliberately untested because the fake-clock arrangement never reached it. The streamed abort required a hard-route fix after the first three drain rounds missed cancellation hidden by a `CancellationToken.None` read. The empty tool-call fence stayed content because stripping it would hide the model's answer.

## Do this next

`git switch main && git pull`, then `dotnet build; dotnet test` (expect 996). Close idle WSL, Edge
WebView and Claude sessions before the next hardware run: the machine reached memory pressure at the
end of this wave and the harness killed a background smoke run for it. Never put a closing verb before
an issue number that stays open, in a PR body or a commit message, and never quote such a phrase with a
real number either. October 1 is the first date an Aion Instruct package can run on this machine (a
`--backend phi-silica` smoke under the registry override, a re-measure of the usable window). The
next-wave candidates are #15 item 7, #14 and #16; #2 and #11 wait on Microsoft.
