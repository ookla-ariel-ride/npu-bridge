using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NpuBridge.Api;
using NpuBridge.Backends;
using NpuBridge.Backends.Fake;
using NpuBridge.Configuration;
using NpuBridge.Prompting;

namespace NpuBridge.Tests;

/// <summary>
/// The server-sent-event shape of <c>POST /v1/completions</c>: the <c>text_completion</c> counterpart
/// of <see cref="ChatCompletionsStreamingTests"/>. Every scheduler-wiring, keep-alive, cancel-drain-
/// dispose and D77-null rule that file pins is the exact same code running here (chunk 8 task 3), so
/// this file is about what is actually different -- there is no role chunk (a text completion has no
/// role) and every chunk's <c>object</c> is <c>text_completion</c>, the same string the non-streamed
/// reply carries, unlike the chat shape's separate <c>chat.completion.chunk</c>.
/// </summary>
public class CompletionsStreamingTests
{
    private const string Path = "/v1/completions";

    /// <summary>
    /// <c>/v1/completions</c> cannot truncate through its public one-user-turn request shape, so this
    /// pins the endpoint's exact first-frame hook as a unit: it applies the settled count before the
    /// first frame, and <see cref="SseStream"/> invokes it once even when the stream writes more frames.
    /// </summary>
    [Fact]
    public async Task The_completions_before_headers_hook_is_applied_once_before_its_first_frame()
    {
        var backend = new FakeBackend();
        await backend.InitializeAsync(CancellationToken.None);
        var messages = new ChatMessage[]
        {
            new("user", ChatMessageContent.FromText("old question"), null, null),
            new("assistant", ChatMessageContent.FromText("old answer"), null, null),
            new("user", ChatMessageContent.FromText("new question"), null, null),
        };
        var request = new ChatCompletionRequest("fake", messages, true, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);
        var rendered = PromptTemplate.Render(messages, nativeSystemPromptSupported: false);
        var prepared = new PreparedChatRequest(
            "test-request",
            "fake",
            request,
            backend,
            rendered,
            NativeSystem: null,
            NativeSystemTokens: 0,
            Sampling: null,
            Limits: OutputLimits.None,
            PromptChars: rendered.Prompt.Length);
        using var cache = new ContextCache(0);
        var session = new ConversationSession(prepared, cache, new BridgeOptions { TruncateHistory = true }, NullLogger.Instance);

        Assert.True(session.TryDropOldestExchange());
        session.Acquire(new BackendCallTracker()).Lease!.Dispose();

        var http = new DefaultHttpContext();
        await using var body = new MemoryStream();
        http.Response.Body = body;
        var hookCalls = 0;
        var beforeHeaders = CompletionsStreamEndpoint.CreateBeforeHeadersHook(session, http.Response);
        var sse = new SseStream(http.Response, () =>
        {
            hookCalls++;
            beforeHeaders();
        });

        await sse.WriteAsync("data: first\n\n", CancellationToken.None);
        await sse.WriteAsync("data: second\n\n", CancellationToken.None);

        Assert.Equal(1, hookCalls);
        Assert.Equal("2", http.Response.Headers[ConversationSession.TruncatedTurnsHeader].ToString());
        Assert.Equal("data: first\n\ndata: second\n\n", System.Text.Encoding.UTF8.GetString(body.ToArray()));
        Assert.Equal(1, backend.ContextsCreated);
        Assert.Equal(1, backend.ContextsDisposed);
    }

    /// <summary>
    /// The streamed endpoint gets the refusal after scheduler admission but before any SSE frame, so it
    /// keeps the ordinary HTTP status and OpenAI error envelope instead of committing event-stream.
    /// </summary>
    [Fact]
    public async Task An_over_length_prompt_is_a_plain_json_400_before_the_first_completions_frame()
    {
        var fake = new FakeBackend(new FakeBackendOptions { MaxPromptChars = 1 });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await PostStreamAsync(host);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("text/event-stream", response.Content.Headers.ContentType?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", body, StringComparison.Ordinal);
        Assert.Equal("context_length_exceeded", JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString());
        host.AssertNoLeak();
    }

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
    public async Task Every_chunk_is_a_text_completion_object_and_concatenated_deltas_reproduce_the_text()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["Hello", " world"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chunks = await ReadChunksAsync(await PostStreamAsync(host));

        Assert.All(chunks, c => Assert.Equal("text_completion", c.GetProperty("object").GetString()));

        var texts = chunks
            .Select(c => c.GetProperty("choices")[0].GetProperty("text").GetString()!)
            .ToList();
        Assert.Equal("Hello world", string.Concat(texts));
    }

    [Fact]
    public async Task Id_created_and_model_are_identical_across_every_chunk()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["a", "b", "c"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chunks = await ReadChunksAsync(await PostStreamAsync(host));

        Assert.Single(chunks.Select(c => c.GetProperty("id").GetString()).Distinct());
        Assert.Single(chunks.Select(c => c.GetProperty("created").GetInt64()).Distinct());
        Assert.Single(chunks.Select(c => c.GetProperty("model").GetString()).Distinct());
    }

    [Fact]
    public async Task The_last_chunk_carries_the_finish_reason_and_logprobs_is_an_explicit_null()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["hi"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chunks = await ReadChunksAsync(await PostStreamAsync(host));

        var finishChunks = chunks.Where(c => c.GetProperty("choices")[0].GetProperty("finish_reason").ValueKind != JsonValueKind.Null).ToList();
        var last = Assert.Single(finishChunks);
        Assert.Equal("stop", last.GetProperty("choices")[0].GetProperty("finish_reason").GetString());

        // D77: every chunk's logprobs is a written null, on every choice, not only the last.
        Assert.All(chunks, c => Assert.Equal(JsonValueKind.Null, c.GetProperty("choices")[0].GetProperty("logprobs").ValueKind));
    }

    /// <summary>
    /// Fix round 1, finding 6: the previous version of this test only checked that a usage chunk with
    /// empty <c>choices</c> existed somewhere in the body, which a usage chunk emitted <em>first</em>
    /// would also satisfy. It must be the trailing chunk -- every content-bearing chunk comes before it.
    /// </summary>
    [Fact]
    public async Task Include_usage_adds_one_trailing_chunk_with_empty_choices_and_null_usage_before_it()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["abcd"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chunks = await ReadChunksAsync(await PostStreamAsync(host, includeUsage: true));

        var usageChunk = Assert.Single(chunks, c => c.GetProperty("choices").GetArrayLength() == 0);
        Assert.True(usageChunk.GetProperty("usage").GetProperty("completion_tokens").GetInt32() > 0);

        // The ordering the test's name claims: the usage chunk is last, not merely present -- a usage
        // chunk emitted first would also have satisfied the Assert.Single above.
        Assert.Equal(usageChunk.GetRawText(), chunks[^1].GetRawText());

        foreach (var chunk in chunks.Where(c => c.GetProperty("choices").GetArrayLength() > 0))
        {
            Assert.Equal(JsonValueKind.Null, chunk.GetProperty("usage").ValueKind);
        }
    }

    [Fact]
    public async Task Max_tokens_cuts_the_stream_exactly_as_it_does_on_the_chat_shape()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["0123", "4567", "89ab"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chunks = await ReadChunksAsync(await PostStreamAsync(host, maxTokens: 2));

        var text = string.Concat(chunks.Select(c => c.GetProperty("choices")[0].GetProperty("text").GetString()));
        Assert.Equal("01234567", text);

        var finish = chunks.Select(c => c.GetProperty("choices")[0].GetProperty("finish_reason"))
            .Single(f => f.ValueKind != JsonValueKind.Null);
        Assert.Equal("length", finish.GetString());
        host.AssertNoLeak();
    }

    [Fact]
    public async Task A_stop_string_truncates_the_stream_and_is_excluded_from_the_output()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["hello", " world", " END", " more"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var chunks = await ReadChunksAsync(await PostStreamAsync(host, stop: "END"));

        var text = string.Concat(chunks.Select(c => c.GetProperty("choices")[0].GetProperty("text").GetString()));
        Assert.Equal("hello world ", text);

        var finish = chunks.Select(c => c.GetProperty("choices")[0].GetProperty("finish_reason"))
            .Single(f => f.ValueKind != JsonValueKind.Null);
        Assert.Equal("stop", finish.GetString());
        host.AssertNoLeak();
    }

    [Fact]
    public async Task The_stream_leaves_no_context_leak()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["hi"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        await PostStreamAsync(host);

        host.AssertNoLeak();
        Assert.Equal(1, host.Cache.Count);
    }

    [Fact]
    public async Task A_cancelled_completions_stream_drain_warns_periodically_until_it_completes()
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
            new BridgeOptions { Backend = BackendKind.Fake, DrainWarningSeconds = 60 },
            time: clock,
            loggerProvider: capture);

        var responseTask = host.Client.PostAsJsonAsync(Path,
            Body(model: "fake", stream: true, includeUsage: null, maxTokens: 1, stop: null));

        await TestWait.UntilAsync(() => fake.CancellationsObserved == 1);

        await TestWait.UntilAsync(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(60));
            return capture.Records.Count(r => r.Level == LogLevel.Warning
                && r.Message.Contains("generation drain is still waiting", StringComparison.Ordinal)) == 1;
        });
        var warning = Assert.Single(capture.Records, r => r.Level == LogLevel.Warning
            && r.Message.Contains("generation drain is still waiting", StringComparison.Ordinal));
        Assert.StartsWith("req=chatcmpl-", warning.Message, StringComparison.Ordinal);
        Assert.Contains("shape=completions-stream", warning.Message, StringComparison.Ordinal);
        Assert.Contains("after 60s", warning.Message, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromSeconds(60));
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
        Assert.Contains("shape=completions-stream", completed.Message, StringComparison.Ordinal);
        Assert.Contains("after 120s", completed.Message, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(2, capture.Records.Count(r => r.Level == LogLevel.Warning
            && r.Message.Contains("generation drain is still waiting", StringComparison.Ordinal)));
        host.AssertNoLeak();
    }

    /// <summary>
    /// Task 3b review fix round 1, Finding 2: this endpoint's own <c>catch (Exception ex) when
    /// (aborted.IsCancellationRequested)</c> clause and its cancel-drain-settle <c>finally</c>
    /// (<c>CompletionsStreamEndpoint.cs</c>) were not part of the extraction -- they are per-endpoint
    /// copies chat's suite never touches -- so a client disconnect had zero coverage on
    /// <c>/v1/completions</c> even though <c>StreamingPipeline</c> now covers the wire framing. Ported
    /// from <see cref="ChatCompletionsStreamingTests.A_client_that_disconnects_mid_stream_is_drained_before_the_context_is_disposed"/>
    /// unchanged apart from the request body: gate-based throughout (D54), no wall-clock assertion.
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
                Content = JsonContent.Create(Body(model: "fake", stream: true, includeUsage: null, maxTokens: null, stop: null)),
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
    /// per-request line reports http=0 rather than a status nobody received. Ported from
    /// <see cref="ChatCompletionsStreamingTests.A_disconnected_client_is_logged_as_http_0_and_leaks_no_context"/>
    /// (task 3b review fix round 1, Finding 2) unchanged apart from the request body.
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
            Content = JsonContent.Create(Body(model: "fake", stream: true, includeUsage: null, maxTokens: null, stop: null)),
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

        // The completions endpoint's request id carries the same chatcmpl- prefix chat's does
        // (docs/FUTURE.md), so the log line's prefix check is unchanged from the ported test.
        var line = Assert.Single(capture.Records, r => r.Message.StartsWith("req=chatcmpl-", StringComparison.Ordinal));
        Assert.Contains("http=0", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(capture.Records, r => r.Level >= LogLevel.Error);
    }

    /// <summary>Long enough to span many deltas, same as the chat shape's copy.</summary>
    private static IReadOnlyList<string> LongReply { get; } = FakeBackend.Tokenize(string.Join(
        ' ',
        Enumerable.Range(0, 200).Select(i => $"token{i.ToString(CultureInfo.InvariantCulture)}")));

    private static object Body(string? model, bool? stream, bool? includeUsage, int? maxTokens, string? stop) => new
    {
        model,
        stream,
        stream_options = includeUsage is null ? null : new { include_usage = includeUsage },
        prompt = "say hi",
        max_tokens = maxTokens,
        stop,
    };

    private static Task<HttpResponseMessage> PostStreamAsync(
        BridgeTestHost host, string model = "fake", bool? includeUsage = null, int? maxTokens = null, string? stop = null) =>
        host.Client.PostAsJsonAsync(Path, Body(model, stream: true, includeUsage, maxTokens, stop));

    /// <summary>Parses the raw SSE body into the JSON payload of every <c>data:</c> frame but <c>[DONE]</c>.</summary>
    private static async Task<List<JsonElement>> ReadChunksAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.EndsWith("data: [DONE]\n\n", body, StringComparison.Ordinal);

        var chunks = Sse.Chunks(body);
        Assert.NotEmpty(chunks);
        return chunks;
    }
}
