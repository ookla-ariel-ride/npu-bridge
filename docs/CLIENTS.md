# Client setup

Four ways to talk to the bridge: two agent tools, `curl`, and the Python `openai` client. Base URL
for all of them is `http://127.0.0.1:5273/v1` (or whatever `--listen` was given). There is no
authentication: send any string as the API key, since most client libraries refuse to start
without one.

Read this after the README's request-parameter and tool-calling sections; this document is about
wiring a specific client to them, not about what the wire does. Where a client's behaviour depends
on a fact measured on hardware, the number is here rather than repeated from `docs/DECISIONS.md`.

## Endpoints

| Endpoint | Purpose |
|---|---|
| `POST /v1/chat/completions` | chat, streaming and non-streaming |
| `POST /v1/completions` | the legacy prompt-in, text-out shape (see the `curl` section below) |
| `GET /v1/models`, `GET /v1/models/{id}` | the served model id, for a client's own discovery step |
| `GET /healthz` | 200 once ready, 503 otherwise; a 503 `degraded` means the model loaded but recent generations failed, so back off instead of retrying hot; also `queue_depth`, `queue_capacity` and the context-cache counters |
| `POST /debug/generate`, `POST /debug/tokenize` | loopback-only diagnostics, not part of the OpenAI-shaped surface a client should call |

README's own "Endpoints" section has the full detail on each; this list exists so a client's
health check or model-discovery step is pointed at something real before reading further.

## Conversations, the context cache, and what happens on overflow

Send the whole conversation on every call, the way OpenAI clients already do. A conversation that
extends one the bridge already answered reuses its model context and sends only the new turns, so a
follow-up in the same chat answers faster than a fresh one would. When a conversation grows past
what fits (Phi Silica's usable window is 3,581 tokens on an empty context), nothing is dropped
silently: the bridge answers HTTP 400 with code `context_length_exceeded`. Starting the bridge with
`--truncate-history` changes that to dropping the oldest exchange instead, and the response then
carries `x-npu-bridge-truncated-turns: N` so a client can tell it happened. The mechanics and the
measured timings are in README's "Conversations and the context cache" section; the client-facing
version of the fact is: a long-running agent conversation gets a 400 once it outgrows the window
unless `--truncate-history` is set, and a client that wants to know whether history was dropped
should check for that header rather than assume its transcript survived intact.

## Running it where a client can rely on it staying up

How the bridge starts matters if a client is going to depend on it being there: Phi Silica needs
Windows package identity, and only a logon task can carry that (`NpuBridge.exe task install`,
elevated), not a Windows service, since a service is launched by path and never gets identity.
`aion` and `fake` use a service instead (`NpuBridge.exe service install`). Registering the wrong
verb for the backend in use means the bridge process exists but never reports itself ready.

## The five things that catch every client on the first try

**`model` must be the id `/v1/models` lists, not the client's default.** OpenAI clients ship
defaulted to `gpt-4o` or similar; sent here, that is a 404 with code `model_not_found` (D77). Point
the client at `phi-silica`, `fake` or `aion-instruct`, matched case-insensitively, and the reply
always names whichever one actually served it.

**`usage` is real tokens on one backend and an estimate on the others.** Phi Silica counts with the
vendored Phi-3.5-mini tokenizer, measured to agree with the runtime's own vocabulary at every prompt
boundary tried (D80). Aion and the fake backend divide characters by four (D44). A client that logs
cost or context usage from `usage.prompt_tokens` is reading a real count on Phi Silica and a rough
one everywhere else; do not average the two together.

**`max_tokens` is a cut, not a generation setting.** Neither Windows API takes a token budget, so the
bridge lets the model generate and stops consuming its output at the limit, cancelling the
generation under it (D53). The model is not told to wrap up early: every token up to the cut was
really generated, cut and `stop` strings included. A `max_tokens` chosen to control latency behaves
as intended, but a request that finishes at exactly the cap is not evidence the model was about to
stop there anyway.

**Tool calling is emulated, and only the easy case is measured.** There is no native function calling
on either backend; the bridge injects an instruction block into the system prompt and parses the
reply back into `tool_calls` (D83). The hardware measurement is 20 out of 20 correct calls, but for
one tool with one required string argument, which is the easy end of the range PLAN scoped this
feature for. Nothing has been measured yet with ten-plus tools, nested argument schemas, or a long
agent system prompt competing for the same context window, tracked as issue #21. An agent tool
that leans on tool calling for its core loop should treat this bridge as unproven for that case until
it is run against the tool set in question.

**A hand-rolled SSE reader that assumes every line is `data:` or blank will misparse this stream.**
Between the first byte and the first token, and again if a reply buffers for tool-call detection, the
bridge sends `: keep-alive\n\n` comment lines to hold the connection open (a legal SSE comment,
starting with a colon). OpenAI's own server never sends one, so a reader written against OpenAI's
literal output and not the SSE spec can choke on a line it was not expecting. The Python `openai`
library and `eventsource-parser`-based readers already skip comment lines correctly; this is a trap
for a reader written by hand.

## Concurrency and 429s (new in chunk 8)

One generation runs on the model at a time. A second request while one is running waits on a bounded
queue rather than getting a second, independent context; the queue holds `--queue-capacity` requests
beyond the one running (default 4). Past that, the bridge answers HTTP 429 with a `Retry-After`
header (whole seconds, the queue depth times a rolling average generation time, floored at 1) and the
OpenAI error body `error.type: "rate_limit_error"`, `error.code: "queue_full"`. Measured on Phi
Silica with `--queue-capacity 1`: three requests fired together, one admitted (HTTP 200), two
rejected (HTTP 429, `Retry-After: 1`), and a separate two-request run showed the queue actually
holding a second request rather than racing it: `/healthz`'s `queue_depth` peaked at 1 while both
were in flight, and the second's context session did not start until the first's had ended (D51's
ordering, one level up).

For an agent loop that fires requests in a burst (OpenCode's and Hermes's own case, per the README),
this matters twice over: a burst larger than `--queue-capacity` gets 429s, and a client that treats
429 as a fatal error rather than a signal to wait and retry will fail requests a slower client would
have gotten to. The `openai` Python library retries a 429 automatically, twice by default
(`DEFAULT_MAX_RETRIES = 2`), honouring `Retry-After` for the backoff delay; check that whatever HTTP
client sits under a given agent tool's OpenAI provider does the same, since
`Retry-After` is what makes the retry correct rather than a busy-loop. `/healthz` exposes
`queue_depth` and `queue_capacity` if a supervisor process wants to watch pressure directly rather
than wait for a 429.

## OpenCode

> **Unverified on this machine as of 2026-09-12.** OpenCode's native ARM64 build (1.18.30) refuses
> every command, and Windows ARM64 is the only platform the Phi Silica backend exists on.
> The workaround (the x64 build under emulation, or pinning 1.18.29), kept in a machine-setup note
> outside this repository, makes OpenCode start; nothing in this section has yet been exercised against it. The
> verified client on this machine is Hermes, below.

OpenCode is one of the two agent tools this bridge exists to serve, and the one where tool calling
and burst concurrency both matter most: an agent loop calls tools repeatedly in one conversation and
can issue several requests close together.

OpenCode's own docs (`opencode.ai/docs/providers`, `opencode.ai/docs/config`) describe a custom
OpenAI-compatible provider as an entry under `provider` in `opencode.json`, using the
`@ai-sdk/openai-compatible` package and a `baseURL`:

```jsonc
{
  // Field names under "provider.<id>" per opencode.ai/docs/providers at the time this was written;
  // not verified against a running OpenCode instance, and they have moved between versions before
  // (some write-ups use "package"/"settings" for what current docs call "npm"/"options"). Check
  // opencode.ai/docs/config against the installed version. The baseURL and model id below are the
  // two facts this document can vouch for.
  "$schema": "https://opencode.ai/config.json",
  "provider": {
    "npu-bridge": {
      "npm": "@ai-sdk/openai-compatible",
      "name": "npu-bridge",
      "options": {
        "baseURL": "http://127.0.0.1:5273/v1",
        "apiKey": "not-used"
      },
      "models": {
        "phi-silica": {
          "name": "Phi Silica (NPU)"
        }
      }
    }
  },
  "model": "npu-bridge/phi-silica"
}
```

Whatever the exact keys turn out to be for the installed version, the same two warnings from above
apply directly: OpenCode's agent loop calls
tools in every turn of a real task, which is exactly the untested shape (issue #21), and issuing
several tool-result follow-ups quickly is exactly the burst pattern the queue and its 429s exist for.
Start with a small task and one tool before trusting a long agent run to this backend.

## Hermes

**Verified against a running instance on 2026-09-12.** The earlier guess in this document — that
"Hermes" meant `NousResearch/hermes-agent` — was wrong. The tool installed on this machine is
**Hermes Agent v0.21.2** (`hermes --version` reports `2026.9.11`, upstream `1c671bea`, a git
install under `%LOCALAPPDATA%\hermes`), a far larger agent than the guess assumed: its subcommand
surface includes `whatsapp`, `slack`, `kanban`, `lsp`, `memory-graph` and `computer-use`. Treat the
configuration below as the verified one and ignore any write-up based on the old identification.

Configuration is a `model` block in the config file that `hermes config path` prints
(`%LOCALAPPDATA%\hermes\config.yaml` here, **not** `~/.hermes/config.yaml`):

```yaml
model:
  default: phi-silica
  provider: custom
  base_url: http://127.0.0.1:5273/v1
  api_key: not-used
```

`provider: custom` also reads `OPENAI_BASE_URL` and `OPENAI_API_KEY` from the environment, and
`HERMES_HOME` redirects the whole config root — which together let you point Hermes at the bridge
for a one-off run without touching your real configuration:

```powershell
$env:HERMES_HOME = 'C:\some\scratch\hermes-home'   # fresh config root
# write the model block above to $env:HERMES_HOME\config.yaml
hermes -t clarify -z "What is 2+2? Answer with just the number." --safe-mode --cli
```

That exact command answered `` `4` `` off the NPU in about 13 s of process wall-clock, measured at
the shell around the whole `hermes` invocation rather than around the bridge request.

### The tool schemas are what will not fit

This is the thing to understand before pointing Hermes at this bridge for real work. Phi Silica's
usable window is **3,581 tokens**, and Hermes's tool-schema JSON for its full toolset is about
**37 KB on the wire — roughly 10,000 tokens on its own**, nearly three times the entire window. Because tool
emulation (D83) renders that block into the **system text**, a full-toolset Hermes does not merely
overflow; it crosses the threshold where `CreateContext` throws, and the request comes back as a
502 rather than a clean 400 (issue #29). Hermes then retries it three times, so one impossible
request costs 30–40 s of NPU time.

Measured on 2026-09-12, same prompt each time:

| Hermes configuration | Tools on the wire | Result |
|---|---|---|
| Default config, in a repo | 23 (37.1 KB) | 502 `backend_error` after 3 retries, ~37 s |
| Fresh config root, empty cwd, all toolsets | 23 (37.1 KB) | 502 `backend_error` after 3 retries, ~32 s |
| Fresh config root, `-t clarify` | 1 | **answered correctly, ~13 s** |
| Default config in a repo, `-t clarify` | 1 | **answered correctly, ~13 s** |

The tool counts and byte figures are read from Hermes's own captured request dumps, which is why they
differ from `hermes prompt-size`: that command reports every toolset's schema regardless of `-t` and
regardless of Hermes's own tool-search tiering, so it says 25 tools and ~40 KB where the wire carries
23 and 37.1 KB. Neither failing request set `stream`, so the bridge's buffered streaming path was not
exercised by these runs (issue #31).

So the binding constraint is the **toolset**, not the working directory and not the system prompt:
restricting tools with `-t` is what makes Hermes work here, and the `AGENTS.md`/cwd context tier
turned out not to be what pushed it over. Use `hermes prompt-size` to see the breakdown before a
run — it is offline and makes no API call — but note it reports the schema size for *all* toolsets
regardless of `-t`, so it overstates what a `-t`-restricted run actually sends.

Expect a 429 (not a hang) if a burst outruns `--queue-capacity`.

## `curl`

Non-streaming:

```powershell
curl.exe http://127.0.0.1:5273/v1/chat/completions `
  -H "Content-Type: application/json" `
  -d '{"model":"phi-silica","messages":[{"role":"user","content":"Say hello."}]}'
```

Streaming (note the `-N`, so `curl` does not buffer the response before printing it):

```powershell
curl.exe -N http://127.0.0.1:5273/v1/chat/completions `
  -H "Content-Type: application/json" `
  -d '{"model":"phi-silica","stream":true,"messages":[{"role":"user","content":"Say hello."}]}'
```

A stream looks like this on the wire for the request above, which does not ask for usage: the
keep-alive comment before the first token, one `chat.completion.chunk` per delta with no `usage`
field at all, and `data: [DONE]` at the end. Add `"stream_options":{"include_usage":true}` to the
request and every chunk before the last carries `"usage": null` instead of omitting the field, with
the real counts on one further chunk after the finish chunk, empty `choices` included (see the
Python example below, which turns it on).

```
: keep-alive

data: {"id":"chatcmpl-...","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null,"logprobs":null}],"model":"phi-silica"}

data: {"id":"chatcmpl-...","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null,"logprobs":null}],"model":"phi-silica"}

data: {"id":"chatcmpl-...","object":"chat.completion.chunk","choices":[{"index":0,"delta":{},"finish_reason":"stop","logprobs":null}],"model":"phi-silica"}

data: [DONE]
```

`POST /v1/completions` (the legacy shape, chunk 8) takes the same body with a `prompt` string
instead of `messages`, and returns `object: "text_completion"` with `choices[].text` instead of
`choices[].message`:

```powershell
curl.exe http://127.0.0.1:5273/v1/completions `
  -H "Content-Type: application/json" `
  -d '{"model":"phi-silica","prompt":"Say hello."}'
```

Both endpoints share every phase past reading the request body (the context cache, the scheduler,
the client-side cut), so everything in this document about `usage`, `max_tokens` and 429s applies to
`/v1/completions` unchanged. Two differences worth knowing: `tools` does not exist on this endpoint
at all (not merely ignored: omit it), and its `id` field is stamped `chatcmpl-...`, the same
allocator chat completions use, rather than OpenAI's own `cmpl-...` prefix; a client that parses the
id's prefix to tell the two endpoints' responses apart will be wrong on this bridge. `echo`,
`best_of`, `suffix`, the legacy integer `logprobs`, and `logit_bias` are accepted and logged once as
ignored; none of the five does anything, `echo: true` included.

## Python `openai` client

```python
from openai import OpenAI

client = OpenAI(base_url="http://127.0.0.1:5273/v1", api_key="not-used")

reply = client.chat.completions.create(
    model="phi-silica",
    messages=[{"role": "user", "content": "Say hello."}],
)
print(reply.choices[0].message.content)
```

Streaming, with usage on the final chunk:

```python
stream = client.chat.completions.create(
    model="phi-silica",
    messages=[{"role": "user", "content": "Say hello."}],
    stream=True,
    stream_options={"include_usage": True},
)
for chunk in stream:
    if chunk.choices and chunk.choices[0].delta.content:
        print(chunk.choices[0].delta.content, end="", flush=True)
    if chunk.usage:
        print(f"\n{chunk.usage.prompt_tokens} prompt, {chunk.usage.completion_tokens} completion")
```

The library's own SSE reader already treats `: keep-alive` as a comment and skips it, so nothing
special is needed to handle it; the warning above is for a reader written from scratch, not for this
client. A 429 does not normally reach calling code: the library retries it for you, up to
`DEFAULT_MAX_RETRIES` (2) times, sleeping for this bridge's own `Retry-After` value between
attempts (confirmed in `openai-python`'s `_constants.py` and the retry logic in
`_base_client.py`). `openai.RateLimitError` only surfaces once those retries are exhausted, or
after `max_retries=0` is passed to turn retrying off; an agent script written directly against this
SDK gets the same protection OpenCode's and Hermes's own OpenAI providers get for free, and only
needs to catch `RateLimitError` to handle the exhausted case.

## Where these facts come from

`docs/DECISIONS.md` D44, D53, D77, D80 and D83 carry the reasoning and the measurements behind the
`usage`, `max_tokens` and tool-calling notes above; the queue numbers came from `scripts/smoke.ps1`
against Phi Silica on this machine (`.superpowers/sdd/chunk-8-plan/task-4-report.md`).

The **Hermes** section was verified against a running Hermes Agent v0.21.2 on 2026-09-12 (issue #21):
its identity, its config path, the `OPENAI_BASE_URL`/`HERMES_HOME` overrides, the four-row toolset
table and the `` `4` `` answer are all measured, not documented-and-assumed. The COMException
behaviour behind the 502 rows is issue #29. The **OpenCode** section remains unverified: its ARM64
build does not start here without the x64-emulation workaround, and no run
against the bridge has been made since; treat it as a starting point to check against OpenCode's
current documentation rather than a working integration.
