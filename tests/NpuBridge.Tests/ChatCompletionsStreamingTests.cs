using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NpuBridge.Api;
using NpuBridge.Backends;
using NpuBridge.Backends.Fake;
using NpuBridge.Configuration;

namespace NpuBridge.Tests;

/// <summary>
/// The server-sent-event shape of <c>POST /v1/chat/completions</c>, asserted on the raw wire text
/// rather than on a deserialized convenience view: the framing (<c>data: </c> prefix, blank-line
/// separator, the literal <c>[DONE]</c> terminator) is as much of the contract as the JSON inside it,
/// and a typed reader would hide a broken frame.
///
/// The fake backend raises its progress callback on a thread-pool thread by default, which is the
/// point: if the endpoint wrote to the HTTP response from that callback instead of handing deltas
/// through a channel, a reply long enough to span many deltas would come back interleaved or short,
/// and <see cref="Concatenated_deltas_reproduce_the_generated_text_exactly"/> would fail.
/// </summary>
public class ChatCompletionsStreamingTests
{
    private const string Path = "/v1/chat/completions";

    /// <summary>Long enough to span many deltas and to make an ordering or interleaving bug visible.</summary>
    private static IReadOnlyList<string> LongReply { get; } = FakeBackend.Tokenize(string.Join(
        ' ',
        Enumerable.Range(0, 200).Select(i => $"token{i.ToString(CultureInfo.InvariantCulture)}")));

    [Fact]
    public async Task The_response_carries_the_three_streaming_headers()
    {
        await using var host = await BridgeTestHost.StartAsync(new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] }));

        var response = await PostStreamAsync(host);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", Assert.Single(response.Headers.GetValues("Cache-Control")));
        Assert.Equal("no", Assert.Single(response.Headers.GetValues("X-Accel-Buffering")));
    }

    [Fact]
    public async Task The_first_chunk_opens_the_assistant_message_and_every_chunk_is_a_completion_chunk()
    {
        await using var host = await BridgeTestHost.StartAsync(new FakeBackend(new FakeBackendOptions { Responder = _ => ["Hello", " world"] }));

        var chunks = await ReadChunksAsync(await PostStreamAsync(host));

        var first = chunks[0];
        var delta = first.GetProperty("choices")[0].GetProperty("delta");
        Assert.Equal("assistant", delta.GetProperty("role").GetString());
        Assert.Equal(string.Empty, delta.GetProperty("content").GetString());

        Assert.All(chunks, c => Assert.Equal("chat.completion.chunk", c.GetProperty("object").GetString()));
    }

    [Fact]
    public async Task Concatenated_deltas_reproduce_the_generated_text_exactly()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => LongReply });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chunks = await ReadChunksAsync(await PostStreamAsync(host));

        // Every content delta, in wire order, minus the empty one the role chunk carries.
        var deltas = chunks
            .Where(c => c.GetProperty("choices").GetArrayLength() > 0)
            .Select(c => c.GetProperty("choices")[0].GetProperty("delta"))
            .Where(d => d.TryGetProperty("content", out _))
            .Select(d => d.GetProperty("content").GetString()!)
            .ToList();

        Assert.Equal(string.Concat(LongReply), string.Concat(deltas));

        // Nothing lost and nothing duplicated: one content chunk per delta, plus the role chunk's empty
        // one. Asserted on the count as well as the concatenation, because a dropped delta and a
        // duplicated neighbour would cancel out in the text alone.
        Assert.Equal(LongReply.Count + 1, deltas.Count);
        Assert.Equal(LongReply, deltas.Skip(1));
    }

    [Fact]
    public async Task Id_created_and_model_are_identical_across_every_chunk()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => LongReply });
        await using var host = await BridgeTestHost.StartAsync(fake);

        // The id is matched case-insensitively and the reply carries the served spelling (D77).
        var chunks = await ReadChunksAsync(await PostStreamAsync(host, model: "FAKE", includeUsage: true));

        var id = chunks[0].GetProperty("id").GetString();
        var created = chunks[0].GetProperty("created").GetInt64();

        Assert.StartsWith("chatcmpl-", id, StringComparison.Ordinal);
        Assert.True(created > 0);
        Assert.All(chunks, c =>
        {
            Assert.Equal(id, c.GetProperty("id").GetString());
            Assert.Equal(created, c.GetProperty("created").GetInt64());
            Assert.Equal("fake", c.GetProperty("model").GetString());
        });
    }

    [Fact]
    public async Task Only_the_last_choices_chunk_carries_a_finish_reason()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["a", "b", "c"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chunks = await ReadChunksAsync(await PostStreamAsync(host));

        var withChoices = chunks.Where(c => c.GetProperty("choices").GetArrayLength() > 0).ToList();
        var last = withChoices[^1];

        Assert.Equal("stop", last.GetProperty("choices")[0].GetProperty("finish_reason").GetString());

        // Present and null on every earlier chunk: the schema requires the key on each choice.
        Assert.All(withChoices.Take(withChoices.Count - 1), c =>
            Assert.Equal(JsonValueKind.Null, c.GetProperty("choices")[0].GetProperty("finish_reason").ValueKind));
    }

    [Fact]
    public async Task The_body_ends_with_the_done_sentinel()
    {
        await using var host = await BridgeTestHost.StartAsync(new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] }));

        var body = await (await PostStreamAsync(host)).Content.ReadAsStringAsync();

        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);
        Assert.Single(Lines(body), l => string.Equals(l, "data: [DONE]", StringComparison.Ordinal));
    }

    /// <summary>
    /// Same prompt, same generation, both response shapes: the usage chunk must report exactly the
    /// numbers the JSON reply reports, or a client that adds up either one gets a different answer for
    /// the same work.
    /// </summary>
    [Fact]
    public async Task Include_usage_appends_one_usage_chunk_matching_the_json_paths_estimate()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["abcde"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chunks = await ReadChunksAsync(await PostStreamAsync(host, includeUsage: true));

        var usageChunks = chunks.Where(c => c.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object).ToList();
        var usageChunk = Assert.Single(usageChunks);

        // Exactly one, it is the last chunk before [DONE], and it carries no choices. Every other
        // chunk carries "usage": null, as OpenAI's do once usage was asked for.
        Assert.Equal(JsonValueKind.Object, chunks[^1].GetProperty("usage").ValueKind);
        Assert.Equal(0, usageChunk.GetProperty("choices").GetArrayLength());
        Assert.All(chunks.Take(chunks.Count - 1), c => Assert.Equal(JsonValueKind.Null, c.GetProperty("usage").ValueKind));

        var json = await host.Client.PostAsJsonAsync(Path, Body(model: "fake", stream: false));
        var jsonUsage = JsonDocument.Parse(await json.Content.ReadAsStringAsync()).RootElement.GetProperty("usage");

        var usage = usageChunk.GetProperty("usage");
        Assert.Equal(jsonUsage.GetProperty("prompt_tokens").GetInt32(), usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(jsonUsage.GetProperty("completion_tokens").GetInt32(), usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(jsonUsage.GetProperty("total_tokens").GetInt32(), usage.GetProperty("total_tokens").GetInt32());

        // Pinned against the documented chars/4 estimate too, so a change on both paths at once still
        // has to be deliberate. Prompt "say hi" is 6 chars -> 2; output "abcde" is 5 chars -> 2.
        Assert.Equal(2, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(2, usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal(4, usage.GetProperty("total_tokens").GetInt32());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Without_include_usage_no_chunk_carries_usage(bool? includeUsage)
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["abcde"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chunks = await ReadChunksAsync(await PostStreamAsync(host, includeUsage: includeUsage));

        Assert.DoesNotContain(chunks, c => c.TryGetProperty("usage", out _));
        Assert.All(chunks, c => Assert.Equal(1, c.GetProperty("choices").GetArrayLength()));
    }

    [Fact]
    public async Task A_streamed_request_creates_exactly_one_context_and_accounts_for_it()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => LongReply });
        var host = await BridgeTestHost.StartAsync(fake);

        await PostStreamAsync(host);

        // Complete and uncut: the context is in the cache rather than disposed (chunk 5), and
        // shutdown releases it.
        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(1, host.Cache.Count);
        host.AssertNoLeak();

        await host.DisposeAsync();
        Assert.Equal(1, fake.ContextsDisposed);
        Assert.Equal(0, fake.ActiveContexts);
    }

    [Fact]
    public async Task The_stream_emits_the_same_per_request_log_line_the_json_path_does()
    {
        var capture = new CapturingLoggerProvider();
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["abcde"] });
        await using var host = await BridgeTestHost.StartAsync(fake, loggerProvider: capture);

        await PostStreamAsync(host);

        var line = Assert.Single(capture.Records, r => r.Message.StartsWith("req=chatcmpl-", StringComparison.Ordinal));
        Assert.Contains("backend=fake", line.Message, StringComparison.Ordinal);
        Assert.Contains("prompt_chars=6", line.Message, StringComparison.Ordinal);
        Assert.Contains("tokens=2", line.Message, StringComparison.Ordinal);
        Assert.Contains("status=Complete", line.Message, StringComparison.Ordinal);
        Assert.Contains("finish=stop", line.Message, StringComparison.Ordinal);
        Assert.Contains("http=200", line.Message, StringComparison.Ordinal);

        // The category is chosen on purpose by the streaming phase rather than inherited from whatever
        // logger the shared preparation phase happened to be handed.
        Assert.Equal("NpuBridge.Api.ChatCompletionsStreamEndpoint", line.Category);
    }

    /// <summary>
    /// Preparation fails before a single byte is written, so the status line is still the server's to
    /// set: a streamed request that never reaches generation gets the ordinary JSON error with the
    /// ordinary status, not a 200 stream carrying an error frame.
    /// </summary>
    [Fact]
    public async Task A_validation_failure_with_stream_true_is_a_plain_json_400()
    {
        var fake = new FakeBackend();
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            stream = true,
            n = 2,
            messages = new[] { new { role = "user", content = "say hi" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("data:", body, StringComparison.Ordinal);

        var error = JsonDocument.Parse(body).RootElement.GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("n", error.GetProperty("param").GetString());
        Assert.Equal(0, fake.ContextsCreated);
    }

    [Fact]
    public async Task A_not_ready_backend_with_stream_true_is_a_plain_json_503()
    {
        var gate = new TaskCompletionSource();
        var fake = new FakeBackend(new FakeBackendOptions { InitGate = gate });
        await using var host = await BridgeTestHost.StartAsync(fake, waitForReady: false);

        var response = await host.Client.PostAsJsonAsync(Path, Body(model: "fake", stream: true));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("data:", body, StringComparison.Ordinal);
        Assert.Equal("server_error", JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal(0, fake.ContextsCreated);

        gate.SetResult();
        await host.Lifecycle.Initialization;
    }

    /// <summary>
    /// The system-prompt placement decision is preparation's, so it must reach the backend unchanged on
    /// the streaming path: the same rendering assertion <see cref="ChatRequestPreparationTests"/> makes
    /// for the JSON path.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_streaming_path_honours_the_system_prompt_placement(bool nativeSystem)
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Capabilities = nativeSystem ? BackendCapabilities.SystemPromptContext : BackendCapabilities.None,
            Responder = _ => ["ok"],
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            stream = true,
            messages = new object[]
            {
                new { role = "system", content = "be terse" },
                new { role = "user", content = "hi" },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(fake.Calls);
        if (nativeSystem)
        {
            Assert.Contains("be terse", call.SystemPrompt!, StringComparison.Ordinal);
            Assert.DoesNotContain("be terse", call.Prompt, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(call.SystemPrompt);
            Assert.StartsWith("be terse", call.Prompt, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Every frame is <c>data: &lt;payload&gt;</c> followed by a blank line, and nothing else appears
    /// between them. A stray write -- for example one made from the callback thread while the request's
    /// own task was mid-frame -- shows up here as a line that is neither.
    /// </summary>
    [Fact]
    public async Task Every_line_in_the_body_is_a_data_frame_or_a_blank_separator()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => LongReply });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var body = await (await PostStreamAsync(host, includeUsage: true)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("\r", body, StringComparison.Ordinal);
        var raw = body.Split('\n');

        // "data: x", "", "data: y", "", ..., "" -- pairs, so an odd number of pieces after the split.
        Assert.Equal(1, raw.Length % 2);
        for (var i = 0; i < raw.Length - 1; i += 2)
        {
            Assert.StartsWith("data: ", raw[i], StringComparison.Ordinal);
            Assert.Equal(string.Empty, raw[i + 1]);
        }

        Assert.Equal(string.Empty, raw[^1]);
    }

    // ---- failure paths, cancellation, keep-alive ------------------------------------------------

    /// <summary>
    /// The status line is spent once a chunk has gone out, so a backend failure after the first token
    /// has to travel inside the stream. What it must not do is end as <c>stop</c>: before this existed,
    /// every non-completed status was reported to the client as a normal end of message.
    /// </summary>
    [Fact]
    public async Task A_backend_error_after_the_first_token_becomes_an_error_event_then_the_done_marker()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["one ", "two ", "three"],
            FailAfterTokens = 2,
            FailureStatus = GenerationStatus.Error,
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await PostStreamAsync(host);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        // The stream ends, rather than hanging: the error event is the last thing before [DONE].
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);
        var error = ErrorEvent(body);
        Assert.Equal("server_error", error.GetProperty("type").GetString());
        Assert.Contains("failed to generate", error.GetProperty("message").GetString(), StringComparison.Ordinal);

        // The two deltas that did get generated were streamed first, and nothing claimed a normal finish.
        Assert.Contains("one ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"finish_reason\":\"", body, StringComparison.Ordinal); // no stop, length or filter; the null on a role chunk is not a finish
        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(1, fake.ContextsDisposed);
    }

    /// <summary>
    /// The event's payload is not merely error-shaped: it is byte-for-byte the body the JSON path
    /// returns for the same failure, because both come from the same mapping.
    /// </summary>
    [Fact]
    public async Task The_error_event_body_is_exactly_the_body_the_json_path_returns()
    {
        var options = () => new FakeBackendOptions
        {
            Responder = _ => ["one ", "two"],
            FailAfterTokens = 1,
            FailureStatus = GenerationStatus.Error,
        };

        await using var streamHost = await BridgeTestHost.StartAsync(new FakeBackend(options()));
        var streamed = ErrorPayload(await (await PostStreamAsync(streamHost)).Content.ReadAsStringAsync());

        await using var jsonHost = await BridgeTestHost.StartAsync(new FakeBackend(options()));
        var json = await jsonHost.Client.PostAsJsonAsync(Path, Body(model: "fake", stream: false));

        Assert.Equal(HttpStatusCode.BadGateway, json.StatusCode);
        Assert.Equal(await json.Content.ReadAsStringAsync(), streamed);
    }

    /// <summary>
    /// A backend that throws rather than returning a status, after the headers are committed. Same
    /// treatment, and the <c>backend_error</c> code the JSON path uses survives into the stream.
    /// </summary>
    [Fact]
    public async Task A_backend_that_throws_mid_stream_becomes_an_error_event_with_the_backend_error_code()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["one ", "two"],
            FailAfterTokens = 1,
            FailureException = new InvalidOperationException("runtime went away"),
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var body = await (await PostStreamAsync(host)).Content.ReadAsStringAsync();

        var error = ErrorEvent(body);
        Assert.Equal("backend_error", error.GetProperty("code").GetString());
        Assert.Contains("InvalidOperationException", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);
        Assert.Equal(1, fake.ContextsDisposed);
    }

    /// <summary>
    /// The other side of the boundary. Nothing has been written when the failure is discovered, so the
    /// status line is still the server's to set and the client gets the ordinary 502 — not a 200 stream
    /// carrying an error frame.
    /// </summary>
    [Fact]
    public async Task A_backend_failure_before_the_first_token_is_a_plain_json_502()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["never"],
            FailAfterTokens = 0,
            FailureStatus = GenerationStatus.Error,
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await PostStreamAsync(host);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("data:", body, StringComparison.Ordinal);
        Assert.Equal("server_error", JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("type").GetString());

        // The context still existed and was still released.
        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(1, fake.ContextsDisposed);
    }

    /// <summary>
    /// The defect this replaces: an over-length prompt used to reach the client as HTTP 200, an empty
    /// reply and <c>finish_reason: "stop"</c> — the model reported as having answered when it refused.
    /// The verdict arrives before a single byte is written, so it is the real 400 the JSON path returns.
    /// </summary>
    [Fact]
    public async Task An_over_length_prompt_is_the_same_400_the_json_path_returns_and_never_a_stop()
    {
        var fake = new FakeBackend(new FakeBackendOptions { MaxPromptChars = 1 });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await PostStreamAsync(host);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("data:", body, StringComparison.Ordinal);
        Assert.DoesNotContain("stop", body, StringComparison.Ordinal);

        var error = JsonDocument.Parse(body).RootElement.GetProperty("error");
        Assert.Equal("context_length_exceeded", error.GetProperty("code").GetString());
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());

        var json = await host.Client.PostAsJsonAsync(Path, Body(model: "fake", stream: false));
        Assert.Equal(await json.Content.ReadAsStringAsync(), body);
        Assert.Equal(HttpStatusCode.BadRequest, json.StatusCode);
    }

    /// <summary>
    /// The same condition once the headers are gone: a keep-alive comment has already committed 200, so
    /// the refusal has to travel as an error event. Either way the answer is an error — <c>stop</c> is
    /// not a reachable outcome for a prompt that did not fit.
    ///
    /// "After a keep-alive" is arranged, not raced: the generation waits at a start gate this test opens
    /// only once it holds the response headers, and the headers can only have come from a keep-alive
    /// comment because nothing else has been written. The delays below decide how long the test takes,
    /// never what it asserts.
    /// </summary>
    [Fact]
    public async Task An_over_length_prompt_discovered_after_a_keep_alive_is_an_error_event_not_a_stop()
    {
        // No preflight. On a backend that has one the verdict is known before a byte goes out and
        // the answer is the 400 of the test above (chunk 5). Only a backend that can say "too long"
        // solely by failing the generation (Aion, D70) can still deliver it after a keep-alive.
        var start = new TaskCompletionSource();
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Capabilities = BackendCapabilities.SamplingOptions | BackendCapabilities.SystemPromptContext | BackendCapabilities.Cancellation,
            MaxPromptChars = 1,
            StartGate = start,
        });
        await using var host = await BridgeTestHost.StartAsync(
            fake,
            keepAliveInterval: TimeSpan.FromSeconds(30),
            firstKeepAliveDelay: TimeSpan.FromMilliseconds(20));

        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = JsonContent.Create(Body(model: "fake", stream: true)),
        };
        var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, guard.Token);

        // Headers in hand, so the keep-alive has gone out and the response shape is settled. Only now
        // may the prompt-length verdict happen; the guard token is a deadlock guard, not a measurement.
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        start.SetResult();
        var body = await response.Content.ReadAsStringAsync(guard.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(": keep-alive", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"finish_reason\":\"", body, StringComparison.Ordinal); // no stop, length or filter; the null on a role chunk is not a finish
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);

        var error = ErrorEvent(body);
        Assert.Equal("context_length_exceeded", error.GetProperty("code").GetString());
    }

    /// <summary>
    /// Content filtering is not a failure and never was: the generation ran, the answer was withheld,
    /// and the client is told so with a finish reason rather than an error event. Same as the JSON path.
    /// </summary>
    [Theory]
    [InlineData(GenerationStatus.ContentFiltered)]
    [InlineData(GenerationStatus.BlockedByPolicy)]
    public async Task Content_filtering_still_ends_as_a_successful_content_filter_finish(GenerationStatus status)
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["one ", "two ", "three"],
            FailAfterTokens = 2,
            FailureStatus = status,
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await PostStreamAsync(host);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("\"error\"", body, StringComparison.Ordinal);

        var withChoices = Sse.Chunks(body)
            .Where(c => c.GetProperty("choices").GetArrayLength() > 0)
            .ToList();
        Assert.Equal("content_filter", withChoices[^1].GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    /// <summary>
    /// While the first token is still being waited for, the response has to show signs of life or a
    /// proxy will close it. The interval is injected in milliseconds here; it is fifteen seconds in
    /// production, and a test that waited that long would not survive its first slow-suite complaint.
    /// </summary>
    [Fact]
    public async Task Keep_alive_comments_fill_the_wait_for_the_first_token_and_stop_once_it_arrives()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["one ", "two"],
            FirstTokenDelay = TimeSpan.FromMilliseconds(300),
        });
        await using var host = await BridgeTestHost.StartAsync(fake, keepAliveInterval: TimeSpan.FromMilliseconds(20));

        var body = await (await PostStreamAsync(host)).Content.ReadAsStringAsync();
        var lines = body.Split('\n');

        Assert.True(lines.Count(l => string.Equals(l, ": keep-alive", StringComparison.Ordinal)) >= 2,
            $"expected repeated keep-alive comments while the first token was delayed, got:\n{body}");

        // Every one of them precedes the first real chunk: they fill the wait and then stop.
        var lastKeepAlive = Array.FindLastIndex(lines, l => string.Equals(l, ": keep-alive", StringComparison.Ordinal));
        var firstFrame = Array.FindIndex(lines, l => l.StartsWith("data: ", StringComparison.Ordinal));
        Assert.True(lastKeepAlive < firstFrame, $"a keep-alive followed the first chunk:\n{body}");

        // And the stream is otherwise exactly the ordinary one.
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);
        Assert.Equal("one two", string.Concat(Sse.Chunks(body)
            .Select(c => c.GetProperty("choices")[0].GetProperty("delta"))
            .Where(d => d.TryGetProperty("content", out _))
            .Select(d => d.GetProperty("content").GetString())));
    }

    /// <summary>
    /// An empty reply that arrived slowly: keep-alive comments have already started the stream, but they
    /// are comments, not the assistant message. The role chunk still has to open it before the finish
    /// chunk closes it, or a client has a finish_reason for a message it was never told began.
    ///
    /// The generation is held at a start gate until the headers have been read, so the comment precedes
    /// the empty reply by construction rather than because one delay was set shorter than another.
    /// </summary>
    [Fact]
    public async Task An_empty_reply_after_a_keep_alive_still_opens_with_the_role_chunk()
    {
        var start = new TaskCompletionSource();
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => [],
            StartGate = start,
        });
        await using var host = await BridgeTestHost.StartAsync(
            fake,
            keepAliveInterval: TimeSpan.FromSeconds(30),
            firstKeepAliveDelay: TimeSpan.FromMilliseconds(20));

        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = JsonContent.Create(Body(model: "fake", stream: true)),
        };
        var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, guard.Token);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        start.SetResult();

        var body = await response.Content.ReadAsStringAsync(guard.Token);
        var chunks = Sse.Chunks(body);

        Assert.Contains(": keep-alive", body, StringComparison.Ordinal);
        Assert.Equal(2, chunks.Count);
        Assert.Equal("assistant", chunks[0].GetProperty("choices")[0].GetProperty("delta").GetProperty("role").GetString());
        Assert.Equal("stop", chunks[1].GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    /// <summary>
    /// The first comment is on a shorter clock than the ones after it, and for a different reason: the
    /// gap between comments is about proxy idle timeouts, but the wait before the *first* one is how long
    /// a client goes without response headers, and clients time that out sooner (httpx allows five
    /// seconds by default). Here the first is due at 50 ms and the second not for another thirty
    /// seconds, so the headers must arrive while the generation still has no token to show — which is an
    /// ordering, and is asserted as one. The backend is held at a gate this test releases only after it
    /// has the headers in hand, so no wall-clock bound is involved: if the handler waited for the first
    /// token instead of for the first keep-alive, the two would wait on each other and the send below
    /// would never return. Its cancellation token is a deadlock guard, not a measurement.
    /// </summary>
    [Fact]
    public async Task The_first_keep_alive_commits_the_headers_before_the_first_token()
    {
        var firstToken = new TaskCompletionSource();
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["ok"],
            FirstTokenGate = firstToken,
        });
        await using var host = await BridgeTestHost.StartAsync(
            fake,
            keepAliveInterval: TimeSpan.FromSeconds(30),
            firstKeepAliveDelay: TimeSpan.FromMilliseconds(50));

        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = JsonContent.Create(Body(model: "fake", stream: true)),
        };
        var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, guard.Token);

        // Headers, and a backend that has produced nothing: the first keep-alive committed them, not the
        // generation. The gate assertion guards this test's own construction — releasing it early would
        // turn the ordering back into a race.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.False(firstToken.Task.IsCompleted, "the first token was released before the headers were read");

        firstToken.SetResult();
        var body = await response.Content.ReadAsStringAsync(guard.Token);

        // Exactly one: the interval is thirty seconds away, so only the shorter first delay can have
        // produced a comment. The interval's own behaviour is covered by
        // Keep_alive_comments_fill_the_wait_for_the_first_token_and_stop_once_it_arrives.
        Assert.Equal(1, body.Split('\n').Count(l => string.Equals(l, ": keep-alive", StringComparison.Ordinal)));
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);
    }

    /// <summary>A first token that arrives before the interval elapses costs the client nothing extra.</summary>
    [Fact]
    public async Task A_prompt_first_token_produces_no_keep_alive_comment()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake, keepAliveInterval: TimeSpan.FromSeconds(30));

        var body = await (await PostStreamAsync(host)).Content.ReadAsStringAsync();

        Assert.DoesNotContain(": keep-alive", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cancelled stream still owns its context until the backend completes. The fake ignores the
    /// cancellation while both gates are closed, so advancing the injected clock proves that the shared
    /// drain watcher warns periodically without making the wait bounded.
    /// </summary>
    [Fact]
    public async Task A_cancelled_stream_drain_warns_periodically_until_it_completes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
        var capture = new CapturingLoggerProvider();
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["Hello", " world"],
            CancellationGate = gate,
            DeltaGate = gate,
            DeltaGateAfterTokens = 1,
        });
        await using var host = await BridgeTestHost.StartAsync(fake,
            new BridgeOptions { Backend = BackendKind.Fake, DrainWarningSeconds = 10 },
            time: clock,
            loggerProvider: capture);

        var responseTask = host.Client.PostAsJsonAsync(Path, new
        {
            model = "fake",
            stream = true,
            max_tokens = 1,
            messages = new[] { new { role = "user", content = "say hi" } },
        });

        await TestWait.UntilAsync(() => fake.CancellationsObserved == 1);

        clock.Advance(TimeSpan.FromSeconds(10));
        await TestWait.UntilAsync(() => capture.Records.Count(r => r.Level == LogLevel.Warning
            && r.Message.Contains("generation drain is still waiting", StringComparison.Ordinal)) == 1);
        var firstWarning = Assert.Single(capture.Records, r => r.Level == LogLevel.Warning
            && r.Message.Contains("generation drain is still waiting", StringComparison.Ordinal));
        Assert.StartsWith("req=chatcmpl-", firstWarning.Message, StringComparison.Ordinal);
        Assert.Contains("shape=stream", firstWarning.Message, StringComparison.Ordinal);
        Assert.Contains("after 10s", firstWarning.Message, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromSeconds(10));
        await TestWait.UntilAsync(() => capture.Records.Count(r => r.Level == LogLevel.Warning
            && r.Message.Contains("generation drain is still waiting", StringComparison.Ordinal)) == 2);

        gate.SetResult();
        using var response = await responseTask;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await TestWait.UntilAsync(() => capture.Records.Any(r => r.Level == LogLevel.Information
            && r.Message.Contains("generation drain completed", StringComparison.Ordinal)));
        var completed = Assert.Single(capture.Records, r => r.Level == LogLevel.Information
            && r.Message.Contains("generation drain completed", StringComparison.Ordinal));
        Assert.Contains("req=chatcmpl-", completed.Message, StringComparison.Ordinal);
        Assert.Contains("shape=stream", completed.Message, StringComparison.Ordinal);
        Assert.Contains("after 20s", completed.Message, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(2, capture.Records.Count(r => r.Level == LogLevel.Warning
            && r.Message.Contains("generation drain is still waiting", StringComparison.Ordinal)));
        host.AssertNoLeak();
    }

    /// <summary>
    /// The critical one. A client that disappears mid-stream makes the next response write throw, and
    /// that exception used to unwind straight into the <c>finally</c> that disposes the model context —
    /// while the generation was still running against it. On the real backends that is a use-after-free
    /// on a live WinRT handle, not merely an unobserved task.
    ///
    /// The fake here is given a cancellation gate, so its generation keeps running after the client is
    /// gone exactly as a runtime whose operation cannot be stopped on demand would. The handler must
    /// therefore sit in the drain, holding the context alive, until that generation ends.
    /// </summary>
    [Fact]
    public async Task A_client_that_disconnects_mid_stream_is_drained_before_the_context_is_disposed()
    {
        var gate = new TaskCompletionSource();
        FakeBackend fake = null!;
        var disposedDuringGeneration = false;

        IEnumerable<string> Tokens()
        {
            for (var i = 0; i < 200; i++)
            {
                // Read from inside the generation: if the context was released while this was still
                // producing, the handler disposed something the backend was still using.
                disposedDuringGeneration |= fake.ContextsDisposed > 0;
                yield return $"token{i.ToString(CultureInfo.InvariantCulture)} ";
            }
        }

        fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => Tokens(),
            TokenDelay = TimeSpan.FromMilliseconds(10),
            CancellationGate = gate,
        });

        var capture = new CapturingLoggerProvider();
        await using var host = await BridgeTestHost.StartAsync(fake, loggerProvider: capture);

        try
        {
            using var cts = new CancellationTokenSource();
            using var request = new HttpRequestMessage(HttpMethod.Post, Path)
            {
                Content = JsonContent.Create(Body(model: "fake", stream: true)),
            };

            var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            await using (var stream = await response.Content.ReadAsStreamAsync(cts.Token))
            {
                var buffer = new byte[128];
                Assert.True(await stream.ReadAsync(buffer, cts.Token) > 0);
            }

            // The client goes away mid-generation.
            await cts.CancelAsync();

            // http=0 is the handler saying there is nobody left to write to: it is done with the client
            // and is now in the drain. The generation has not finished, so the context must still exist.
            await TestWait.UntilAsync(() => capture.Records.Any(r => r.Message.Contains("http=0", StringComparison.Ordinal)));
            Assert.Equal(1, fake.ContextsCreated);
            Assert.Equal(0, fake.ContextsDisposed);
        }
        finally
        {
            gate.SetResult();
        }

        // Only now, once the generation can end, is the context released.
        await TestWait.UntilAsync(() => fake.ActiveContexts == 0);
        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(1, fake.ContextsDisposed);
        Assert.False(disposedDuringGeneration, "the context was disposed while the backend was still generating");

        // Nothing escaped as an unhandled request exception.
        Assert.DoesNotContain(capture.Records, r => r.Level >= LogLevel.Error);
    }

    /// <summary>
    /// The disconnect case for a generation that does stop when told to: the context balances, and the
    /// per-request line reports http=0 rather than a status nobody received.
    /// </summary>
    [Fact]
    public async Task A_disconnected_client_is_logged_as_http_0_and_leaks_no_context()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => LongReply,
            TokenDelay = TimeSpan.FromMilliseconds(10),
        });
        var capture = new CapturingLoggerProvider();
        await using var host = await BridgeTestHost.StartAsync(fake, loggerProvider: capture);

        using var cts = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = JsonContent.Create(Body(model: "fake", stream: true)),
        };

        var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using (var stream = await response.Content.ReadAsStreamAsync(cts.Token))
        {
            var buffer = new byte[128];
            Assert.True(await stream.ReadAsync(buffer, cts.Token) > 0);
        }

        await cts.CancelAsync();

        await TestWait.UntilAsync(() => fake.ActiveContexts == 0);
        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(1, fake.ContextsDisposed);

        var line = Assert.Single(capture.Records, r => r.Message.StartsWith("req=chatcmpl-", StringComparison.Ordinal));
        Assert.Contains("http=0", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(capture.Records, r => r.Level >= LogLevel.Error);
    }

    /// <summary>
    /// The one error event in the body, checked to be the last frame before the done marker: an error
    /// event that left the stream hanging, or that was followed by more chunks, fails here.
    /// </summary>
    private static string ErrorPayload(string body)
    {
        var payloads = Sse.Payloads(body);
        Assert.Equal("[DONE]", payloads[^1]);
        var frame = Assert.Single(payloads, p => p.Contains("\"error\"", StringComparison.Ordinal));
        Assert.Equal(payloads[^2], frame);
        return frame;
    }

    private static JsonElement ErrorEvent(string body) =>
        JsonDocument.Parse(ErrorPayload(body)).RootElement.GetProperty("error");

    /// <summary>
    /// The context is disposed even when cancelling the generation throws. The cancel that opens the
    /// finally was the one statement in the handler outside a <c>try</c>, and it stands immediately
    /// before the drain and the dispose: a throw there skipped both, leaking the live WinRT handle D43
    /// guarantees is released. Worse than the D51 defect it sits next to, which disposed too early
    /// rather than never.
    /// </summary>
    [Fact]
    public async Task A_throwing_cancellation_registration_still_drains_and_disposes_the_context()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["Hello", " world"],
            ThrowFromCancellationRegistration = true,
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await PostStreamAsync(host);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("[DONE]", body, StringComparison.Ordinal);

        // The generation ended Complete, so the context went into the cache -- the throwing
        // registration on the exit cancel changed nothing about that -- and nothing leaked.
        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(1, host.Cache.Count);
        host.AssertNoLeak();
    }

    /// <summary>
    /// A cancellation that is not the client's. Excluding <see cref="OperationCanceledException"/> from
    /// the second catch left the case "cancelled, but <c>RequestAborted</c> is not set" matching neither
    /// filter, so it escaped as an unhandled request exception mid-stream instead of being reported —
    /// which is what an adapter breaking the contract about swallowing the runtime's own cancellation
    /// produces, and the cut's linked token makes that reachable without the client going anywhere.
    /// </summary>
    [Fact]
    public async Task A_cancellation_that_is_not_the_clients_is_reported_rather_than_escaping()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["Hello", " world"],
            FailAfterTokens = 1,
            FailureException = new OperationCanceledException("adapter let the runtime's cancellation escape"),
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await PostStreamAsync(host);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"error\"", body, StringComparison.Ordinal);
        Assert.Contains("[DONE]", body, StringComparison.Ordinal);

        await TestWait.UntilAsync(() => fake.ActiveContexts == 0);
        Assert.Equal(1, fake.ContextsDisposed);
    }

    private static object Body(string? model, bool? stream, bool? includeUsage = null) => new
    {
        model,
        stream,
        stream_options = includeUsage is null ? null : new { include_usage = includeUsage },
        messages = new[] { new { role = "user", content = "say hi" } },
    };

    private static Task<HttpResponseMessage> PostStreamAsync(BridgeTestHost host, string model = "fake", bool? includeUsage = null) =>
        host.Client.PostAsJsonAsync(Path, Body(model, stream: true, includeUsage));

    /// <summary>Parses the raw SSE body into the JSON payload of every <c>data:</c> frame but <c>[DONE]</c>.</summary>
    private static async Task<List<JsonElement>> ReadChunksAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);

        var chunks = new List<JsonElement>();
        foreach (var line in Lines(body))
        {
            Assert.StartsWith("data: ", line, StringComparison.Ordinal);
            var payload = line["data: ".Length..];
            if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
            {
                continue;
            }

            chunks.Add(JsonDocument.Parse(payload).RootElement);
        }

        Assert.NotEmpty(chunks);
        return chunks;
    }

    private static IEnumerable<string> Lines(string body) =>
        body.Split('\n').Where(l => l.Length > 0);

    /// <summary>
    /// A non-positive interval disables keep-alive comments outright, which also removes the only
    /// thing that commits the headers before the first delta. Two proofs, neither a clock. The
    /// ordering: the generation has started and the client still has no headers. And the body: with
    /// a zero first delay the disabling branch is the only thing standing between the loop and
    /// <c>Task.Delay(TimeSpan.Zero)</c>, which completes at once, so were the branch missing the very
    /// first wait would write a comment before the gate could open; a negative interval would throw
    /// there instead. The negative row is deterministic. The zero row has one window: the gate is
    /// released once the backend has been called, which happens a few instructions before the handler
    /// enters its wait, so a delta that reached the channel in that gap would let a missing branch
    /// pass by a first wait that had already been satisfied. Closing it can now use the keep-alive
    /// delays driven by the injected <see cref="TimeProvider"/>.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_non_positive_keep_alive_interval_disables_the_comments_and_the_headers_wait_for_the_first_delta(int intervalMs)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["Hello"], FirstTokenGate = gate });
        await using var host = await BridgeTestHost.StartAsync(fake,
            keepAliveInterval: TimeSpan.FromMilliseconds(intervalMs),
            firstKeepAliveDelay: TimeSpan.Zero);

        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = JsonContent.Create(Body(model: "fake", stream: true)),
        };
        var send = host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        // The backend is generating, held at its gate, and nothing has committed the headers.
        await TestWait.UntilAsync(() => fake.Calls.Count == 1);
        Assert.False(send.IsCompleted, "the headers were committed before the first delta although keep-alives are disabled");

        gate.SetResult();
        using var response = await send;
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(": keep-alive", body, StringComparison.Ordinal);
        Assert.StartsWith("data: ", body, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);
        host.AssertNoLeak();
    }

    /// <summary>
    /// A non-positive first delay is accepted and a keep-alive still commits the headers. The negative
    /// row is the one with teeth: a negative span other than the infinite sentinel throws in
    /// <c>Task.Delay</c>, and without the fallback the request would fail before its first frame. What
    /// this cannot pin is the delay actually used: the code falls back to the interval, but a fallback
    /// to zero, or to any other non-negative span, would pass these assertions too. Pinning the value
    /// needs the keep-alive delays driven by the injected <see cref="TimeProvider"/>, which is now
    /// available.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_non_positive_first_keep_alive_delay_is_accepted_and_a_keep_alive_still_commits_the_headers(int firstDelayMs)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["Hello"], FirstTokenGate = gate });
        await using var host = await BridgeTestHost.StartAsync(fake,
            keepAliveInterval: TimeSpan.FromMilliseconds(20),
            firstKeepAliveDelay: TimeSpan.FromMilliseconds(firstDelayMs));

        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = JsonContent.Create(Body(model: "fake", stream: true)),
        };

        // Completes on the first keep-alive, while the backend is still held at its gate.
        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, guard.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        gate.SetResult();
        var body = await response.Content.ReadAsStringAsync(guard.Token);
        Assert.Contains(": keep-alive", body, StringComparison.Ordinal);
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);
        host.AssertNoLeak();
    }

    /// <summary>
    /// The exit once the client has gone. The stream is open, the generation failed, and nobody is
    /// there to read the error frame. <c>FailAsync</c> has two arms for that: skip the write when the
    /// abort has already been observed, or swallow the write's failure when it has not. Which one runs
    /// depends on when the host signals <c>RequestAborted</c> relative to the write, and TestServer
    /// completes the response pipe before it signals the token, so the branch taken is a race; the
    /// contract is not. This pins the contract: nothing reaches the client, the request ends without
    /// an exception escaping the pipeline, and the context is disposed. The window is opened on
    /// purpose: the per-request log line is written between classifying the failure and reporting it,
    /// and the capturing logger aborts the client the moment it sees it. The first delta is read off
    /// the wire before the failure is allowed to fire, because an aborted TestServer response discards
    /// whatever was still buffered.
    /// </summary>
    [Fact]
    public async Task A_failure_after_the_client_has_gone_reaches_nobody_and_still_disposes_the_context()
    {
        using var release = new ManualResetEventSlim();
        IEnumerable<string> Tokens()
        {
            yield return "one ";

            // Held until the test has read the first delta; the injected failure fires right after. A
            // stall is a broken test, never a scenario that quietly goes on without the read.
            if (!release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("the test never read the first delta off the wire");
            }

            yield return "two";
        }

        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => Tokens(),
            FailAfterTokens = 1,
            FailureStatus = GenerationStatus.Error,
            // The iterator's wait is synchronous, so it must run off the request's thread: the handler
            // starts the generation and then enters its first-delta wait, and a generation that blocked
            // before the handler got there would hold the first delta the test is waiting to read. The
            // fake's first-token delay is the one await before any delta that never completes
            // synchronously, so everything after it, the wait included, runs on the pool.
            FirstTokenDelay = TimeSpan.FromMilliseconds(1),
        });
        var capture = new CapturingLoggerProvider();
        await using var host = await BridgeTestHost.StartAsync(fake, loggerProvider: capture);

        using var cts = new CancellationTokenSource();
        capture.OnRecord = record =>
        {
            // The line the handler writes between classifying the Error and reporting it.
            if (record.Message.Contains("status=Error", StringComparison.Ordinal))
            {
                cts.Cancel();
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Path)
        {
            Content = JsonContent.Create(Body(model: "fake", stream: true)),
        };
        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var received = new System.Text.StringBuilder();
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var buffer = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
            {
                received.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
                if (received.ToString().Contains("one ", StringComparison.Ordinal))
                {
                    // The delta is in hand; now let the generation fail.
                    release.Set();
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
        {
            // The client's own cancellation, surfacing through the response stream.
        }

        // The request has run to the end of the pipeline: disposal alone would not say that.
        await TestWait.UntilAsync(() => host.Requests.Completed == 1);
        Assert.Empty(host.Requests.Escaped);
        Assert.Equal(1, fake.ContextsCreated);
        Assert.Equal(1, fake.ContextsDisposed);
        Assert.Equal(0, host.Cache.Count);

        // The failure was classified with the stream open (http=200 is "the status line was spent"),
        // and the client saw the delta and nothing after it.
        var line = Assert.Single(capture.Records, r => r.Message.Contains("status=Error", StringComparison.Ordinal));
        Assert.Contains("http=200", line.Message, StringComparison.Ordinal);
        Assert.Contains("one ", received.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"error\"", received.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("[DONE]", received.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(capture.Records, r => r.Level >= LogLevel.Error);
    }
}
