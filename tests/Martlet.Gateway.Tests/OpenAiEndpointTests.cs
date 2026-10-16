using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Martlet.Avatar.Audio2Face.Remote;
using Martlet.Core.Access;
using Martlet.Core.Contracts;
using Martlet.Gateway.Ollama;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Martlet.Gateway.Tests;

// NOT AI: a controlled HTTP fixture stands in for the host's loopback Ollama servers. The tests check the OpenAI-compatible
// endpoint's wire format, its translation to Ollama's native API, its keys, its priority and its front door.
public sealed class OpenAiEndpointTests
{
    private const string CardA = "GPU-1a2b3c4d-0000-1111-2222-333344445555";
    private const string CardB = "GPU-9f8e7d6c-0000-1111-2222-333344445555";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    /// <summary>One Ollama server's native API: /api/chat (streamed or whole; held after the first chunk until
    /// <see cref="Release"/> when asked), /api/generate and /api/embed. Every request body is kept.</summary>
    private sealed class FixtureOllama : IAsyncDisposable
    {
        private readonly WebApplication app;
        private TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<(string Path, JsonElement Body)> Requests { get; } = new();
        internal TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FirstSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Uri Endpoint { get; private set; } = null!;
        internal bool Held { get; set; }

        private FixtureOllama(WebApplication app) => this.app = app;

        internal void Release() => release.TrySetResult();

        internal IEnumerable<JsonElement> Bodies(string path) => Requests.Where(r => r.Path == path).Select(r => r.Body);

        internal static async Task<FixtureOllama> StartAsync(string reply)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var fake = new FixtureOllama(app);
            async Task<JsonElement> Read(HttpContext context, string path)
            {
                using var document = await JsonDocument.ParseAsync(context.Request.Body);
                var body = document.RootElement.Clone();
                fake.Requests.Enqueue((path, body));
                return body;
            }
            app.MapPost("/api/chat", async context =>
            {
                var body = await Read(context, "/api/chat");
                var half = reply.Length / 2;
                if (!body.GetProperty("stream").GetBoolean())
                {
                    await context.Response.WriteAsJsonAsync(new
                    {
                        model = body.GetProperty("model").GetString(), message = new { role = "assistant", content = reply },
                        done = true, done_reason = "stop", prompt_eval_count = 12, eval_count = 7
                    });
                    return;
                }
                context.Response.ContentType = "application/x-ndjson";
                await context.Response.WriteAsync(Line(reply[..half], false));
                await context.Response.Body.FlushAsync();
                fake.FirstSent.TrySetResult();
                if (fake.Held)
                {
                    try { await fake.release.Task.WaitAsync(context.RequestAborted); }
                    catch (OperationCanceledException)
                    {
                        fake.Aborted.TrySetResult();
                        return;
                    }
                }
                await context.Response.WriteAsync(Line(reply[half..], false) + Line("", true));
            });
            app.MapPost("/api/generate", async context =>
            {
                await Read(context, "/api/generate");
                await context.Response.WriteAsJsonAsync(new { response = reply, done = true, done_reason = "length", prompt_eval_count = 3, eval_count = 4 });
            });
            app.MapPost("/api/embed", async context =>
            {
                await Read(context, "/api/embed");
                await context.Response.WriteAsJsonAsync(new { embeddings = new[] { new[] { 0.5f, -0.25f } }, prompt_eval_count = 2 });
            });
            await app.StartAsync();
            fake.Endpoint = new Uri(app.Urls.First() + "/");
            return fake;
        }

        private static string Line(string text, bool done) => done
            ? JsonSerializer.Serialize(new { message = new { role = "assistant", content = "" }, done, done_reason = "stop", prompt_eval_count = 9, eval_count = 5 }) + "\n"
            : JsonSerializer.Serialize(new { message = new { role = "assistant", content = text }, done }) + "\n";

        public async ValueTask DisposeAsync()
        {
            Release();
            await app.DisposeAsync();
        }
    }

    /// <summary>A gateway with the conversation model's route (live, gemma4:e4b) and Deep thinking's (pool, qwen3:8b), each on its
    /// own fixture Ollama; the network's API keys; and the owner's desktop paired with it.</summary>
    private sealed class Lab : IAsyncDisposable
    {
        internal FixtureOllama Conversation { get; private init; } = null!;
        internal FixtureOllama Deep { get; private init; } = null!;
        internal GatewayTestHost Host { get; private set; } = null!;
        internal Audio2FaceHostConnection Connection { get; private set; } = null!;
        internal GatewayRequestSigner Signer { get; private set; } = null!;
        internal IssuedApiKey Shared { get; private set; } = null!;
        internal IssuedApiKey Equal { get; private set; } = null!;
        internal IssuedApiKey VoiceOnly { get; private set; } = null!;
        internal ApiKeyList Keys { get; private set; } = ApiKeyList.Empty;
        private readonly List<IAsyncDisposable> owned = [];

        internal static async Task<Lab> StartAsync(string hostId = "fixture-host", string[]? conversationGpus = null, string[]? deepGpus = null,
            string conversationModel = "gemma4:e4b", string deepModel = "qwen3:8b", ApiKeyList? keys = null)
        {
            var lab = new Lab
            {
                Conversation = await FixtureOllama.StartAsync("Hello from the conversation model."),
                Deep = await FixtureOllama.StartAsync("A plan for Sunday.")
            };
            var thinking = new OllamaRelayWorker(lab.Conversation.Endpoint, conversationModel);
            var deep = OllamaRelayWorker.DeepThinking(lab.Deep.Endpoint, deepModel);
            if (conversationGpus is not null) thinking.Route.PlaceOn(conversationGpus);
            if (deepGpus is not null) deep.Route.PlaceOn(deepGpus);
            lab.owned.AddRange([thinking, deep]);
            lab.Host = await GatewayTestHost.StartAsync(inferenceWorkers: [thinking, deep], hostId: hostId);
            var list = keys ?? ApiKeyList.Empty;
            if (keys is null)
            {
                var now = DateTimeOffset.UtcNow;
                (list, lab.Shared) = list.Create("Ana's laptop", OpenAiEndpoint.Scopes(false), null, "desktop-test", now);
                (list, lab.Equal) = list.Create("Ben's server", OpenAiEndpoint.Scopes(true), null, "desktop-test", now);
                (list, lab.VoiceOnly) = list.Create("Home Assistant", [ApiKeyScopes.Voice], null, "desktop-test", now);
            }
            lab.Keys = lab.Host.Server.MergeApiKeys(list);
            var credential = await lab.Host.PairAsync(GatewayRole.Voice, "desktop-raw");
            lab.Signer = new GatewayRequestSigner(lab.Host.Identity, credential, lab.Host.Clock);
            var card = lab.Host.OpenPairing(GatewayRole.Voice, "desktop-test");
            var pairing = await Audio2FaceHostClient.PairAsync(lab.Host.Origin.CanonicalOrigin, card.HostId, card.SpkiFingerprint,
                "desktop-test", card.PairingId, card.Token.Reveal());
            lab.Connection = new Audio2FaceHostConnection(pairing.Pairing, pairing.Secret, lab.Host.Clock);
            return lab;
        }

        internal void Share(IssuedApiKey shared, IssuedApiKey equal)
        {
            Shared = shared;
            Equal = equal;
        }

        internal Task<HttpResponseMessage> GetAsync(string path, string? key)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Host.Origin.CanonicalOrigin + path);
            if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return Host.Client.SendAsync(request).AsTask();
        }

        internal Task<HttpResponseMessage> PostAsync(string path, string? key, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Host.Origin.CanonicalOrigin + path)
            {
                Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
            if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return Host.Client.SendAsync(request).AsTask();
        }

        /// <summary>The owner's reply on Thinking's route, as the desktop streams it (8,192 context tokens).</summary>
        internal async Task<string> ReplyAsync()
        {
            var route = Assert.Single(await Connection.ReadRoutesAsync(), r => r.RouteId == HostRoute.OllamaChatRouteId);
            var text = new StringBuilder();
            await foreach (var delta in Connection.StreamChatAsync(route, Ids(), 1, Host.Clock.GetUtcNow().AddSeconds(30), null, [],
                "Are you there?", 0.7, 256, 8_192))
                text.Append(delta);
            return text.ToString();
        }

        /// <summary>The owner's think on Deep thinking's route (a pool request), streamed to the end.</summary>
        internal async Task<HttpResponseMessage> ThinkAsync()
        {
            var routes = await Connection.ReadRoutesAsync();
            var route = Assert.Single(routes, r => r.RouteId == Martlet.Core.Settings.SelfHostSetup.DeepThinkingRouteId);
            var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["protocol_version"] = new Dictionary<string, int> { ["major"] = 2, ["minor"] = 0 },
                ["route_id"] = route.RouteId, ["contract_id"] = route.ContractId, ["contract_version"] = route.ContractVersion,
                ["destination_id"] = route.DestinationId, ["worker_id"] = route.WorkerId, ["adapter_version"] = route.AdapterVersion,
                ["model_id"] = route.ModelId, ["model_revision"] = route.ModelRevision, ["model_sha256"] = route.ModelSha256,
                ["artifact_identity_sha256"] = route.ArtifactIdentitySha256,
                ["session_id"] = Guid.NewGuid(), ["turn_id"] = Guid.NewGuid(), ["request_id"] = Guid.NewGuid(), ["epoch"] = 1,
                ["deadline_utc"] = Host.Clock.GetUtcNow().AddSeconds(60).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["payload"] = new Dictionary<string, object>
                {
                    ["input"] = "Think it over.", ["temperature"] = 0.7, ["maximum_output_tokens"] = 256, ["maximum_context_tokens"] = 8_192
                }
            });
            return await Host.Client.SendAsync(Host.SignedPost(route.Path, GatewayRole.Voice, Signer, body));
        }

        public async ValueTask DisposeAsync()
        {
            Connection?.Dispose();
            if (Host is not null) await Host.DisposeAsync();
            foreach (var item in owned) await item.DisposeAsync();
            await Conversation.DisposeAsync();
            await Deep.DisposeAsync();
        }
    }

    private static CorrelationIds Ids() => new() { SessionId = Guid.NewGuid(), TurnId = Guid.NewGuid(), RequestId = Guid.NewGuid() };

    private static object Chat(string model, bool stream = false, string text = "Hi!") => new Dictionary<string, object>
    {
        ["model"] = model, ["stream"] = stream, ["messages"] = new[] { new { role = "user", content = text } }
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"Expected {status}, got {response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string type)
    {
        var root = await JsonAsync(response, status);
        var error = root.GetProperty("error");
        Assert.Equal(type, error.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        return error;
    }

    private static async IAsyncEnumerable<JsonElement?> EventsAsync(HttpResponseMessage response)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        while (await reader.ReadLineAsync().WaitAsync(Wait) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var data = line[6..];
            if (data == "[DONE]")
            {
                yield return null;
                yield break;
            }
            yield return JsonDocument.Parse(data).RootElement.Clone();
        }
    }

    [Fact]
    public async Task Models_lists_the_hosts_own_models_to_an_openai_key_and_refuses_other_callers_in_openai_form()
    {
        await using var lab = await Lab.StartAsync();
        var models = await JsonAsync(await lab.GetAsync("/v1/models", lab.Shared.Token), HttpStatusCode.OK);
        Assert.Equal("list", models.GetProperty("object").GetString());
        var ids = models.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(["gemma4:e4b", "qwen3:8b"], ids);
        var one = await JsonAsync(await lab.GetAsync("/v1/models/qwen3%3A8b", lab.Shared.Token), HttpStatusCode.OK);
        Assert.Equal("model", one.GetProperty("object").GetString());
        Assert.Equal(["fixture-host"], one.GetProperty("martlet_hosts").EnumerateArray().Select(h => h.GetString()!));

        await ErrorAsync(await lab.GetAsync("/v1/models", null), HttpStatusCode.Unauthorized, "authentication_error");
        await ErrorAsync(await lab.GetAsync("/v1/models", "martlet_" + new string('a', 66)), HttpStatusCode.Unauthorized, "authentication_error");
        var scope = await ErrorAsync(await lab.GetAsync("/v1/models", lab.VoiceOnly.Token), HttpStatusCode.Forbidden, "permission_error");
        Assert.Equal("key.scope", scope.GetProperty("code").GetString());
        await ErrorAsync(await lab.GetAsync("/v1/models/nope", lab.Shared.Token), HttpStatusCode.NotFound, "not_found_error");
        // An openai key reaches nothing else of the host.
        using var other = await lab.GetAsync("/martlet/v1/status", lab.Shared.Token);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        // The owner's computers may list the models with their signed requests (the Share page does).
        using var signed = await lab.Host.Client.SendAsync(lab.Host.SignedGet("/v1/models", GatewayRole.Voice, lab.Signer));
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
    }

    [Fact]
    public async Task A_chat_runs_on_the_hosts_own_model_with_the_owners_context_size_and_keeps_it_loaded()
    {
        await using var lab = await Lab.StartAsync();
        Assert.Equal("Hello from the conversation model.", await lab.ReplyAsync());
        var owner = lab.Conversation.Bodies("/api/chat").Last();
        var ownerContext = owner.GetProperty("options").GetProperty("num_ctx").GetInt32();

        var answer = await JsonAsync(await lab.PostAsync("/v1/chat/completions", lab.Shared.Token, new Dictionary<string, object>
        {
            ["model"] = "gemma4:e4b", ["temperature"] = 0.2, ["max_tokens"] = 64, ["stop"] = "END",
            ["messages"] = new object[]
            {
                new { role = "developer", content = "Be brief." },
                new { role = "user", content = new object[] { new { type = "text", text = "What is this?" },
                    new { type = "image_url", image_url = new { url = "data:image/png;base64,iVBORw0KGgo=" } } } }
            }
        }), HttpStatusCode.OK);
        Assert.Equal("chat.completion", answer.GetProperty("object").GetString());
        var choice = answer.GetProperty("choices")[0];
        Assert.Equal("Hello from the conversation model.", choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        Assert.Equal(19, answer.GetProperty("usage").GetProperty("total_tokens").GetInt32());

        var native = lab.Conversation.Bodies("/api/chat").Last();
        Assert.Equal("gemma4:e4b", native.GetProperty("model").GetString());
        Assert.False(native.GetProperty("stream").GetBoolean());
        Assert.Equal(-1, native.GetProperty("keep_alive").GetInt32());
        var options = native.GetProperty("options");
        // The owner's context size, so Ollama never loads the model again for a shared request.
        Assert.Equal(ownerContext, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(64, options.GetProperty("num_predict").GetInt32());
        Assert.Equal(["END"], options.GetProperty("stop").EnumerateArray().Select(s => s.GetString()!));
        var messages = native.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("iVBORw0KGgo=", messages[1].GetProperty("images")[0].GetString());

        // A picture from a web address is refused: the host never fetches anything for a request.
        var web = await ErrorAsync(await lab.PostAsync("/v1/chat/completions", lab.Shared.Token, new Dictionary<string, object>
        {
            ["model"] = "gemma4:e4b",
            ["messages"] = new[] { new { role = "user", content = new object[] { new { type = "image_url", image_url = new { url = "https://example.com/a.png" } } } } }
        }), HttpStatusCode.BadRequest, "invalid_request_error");
        Assert.Equal("messages", web.GetProperty("param").GetString());
    }

    [Fact]
    public async Task A_streamed_chat_arrives_as_server_sent_events_and_completions_and_embeddings_translate()
    {
        await using var lab = await Lab.StartAsync();
        using var streamed = await lab.PostAsync("/v1/chat/completions", lab.Shared.Token, new Dictionary<string, object>
        {
            ["model"] = "qwen3:8b", ["stream"] = true, ["stream_options"] = new { include_usage = true },
            ["messages"] = new[] { new { role = "user", content = "Plan my Sunday." } }
        });
        Assert.Equal(HttpStatusCode.OK, streamed.StatusCode);
        Assert.Equal("text/event-stream", streamed.Content.Headers.ContentType?.MediaType);
        Assert.Equal("fixture-host", streamed.Headers.GetValues("X-Martlet-Host").Single());
        var text = new StringBuilder();
        string? finish = null;
        var usage = 0;
        var done = false;
        await foreach (var item in EventsAsync(streamed))
        {
            if (item is not { } chunk) { done = true; break; }
            Assert.Equal("chat.completion.chunk", chunk.GetProperty("object").GetString());
            if (chunk.TryGetProperty("usage", out var counted)) usage = counted.GetProperty("total_tokens").GetInt32();
            foreach (var choice in chunk.GetProperty("choices").EnumerateArray())
            {
                if (choice.GetProperty("delta").TryGetProperty("content", out var piece)) text.Append(piece.GetString());
                if (choice.GetProperty("finish_reason").ValueKind == JsonValueKind.String) finish = choice.GetProperty("finish_reason").GetString();
            }
        }
        Assert.True(done);
        Assert.Equal("A plan for Sunday.", text.ToString());
        Assert.Equal("stop", finish);
        Assert.Equal(14, usage);

        var completion = await JsonAsync(await lab.PostAsync("/v1/completions", lab.Equal.Token,
            new { model = "qwen3:8b", prompt = "Once upon a time" }), HttpStatusCode.OK);
        Assert.Equal("text_completion", completion.GetProperty("object").GetString());
        Assert.Equal("length", completion.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.Equal("Once upon a time", lab.Deep.Bodies("/api/generate").Single().GetProperty("prompt").GetString());

        var embedding = await JsonAsync(await lab.PostAsync("/v1/embeddings", lab.Shared.Token,
            new { model = "qwen3:8b", input = "hello" }), HttpStatusCode.OK);
        Assert.Equal([0.5f, -0.25f], embedding.GetProperty("data")[0].GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()));
    }

    [Fact]
    public async Task Requests_name_a_model_of_the_network_once_and_need_the_openai_scope()
    {
        await using var lab = await Lab.StartAsync();
        var missing = await ErrorAsync(await lab.PostAsync("/v1/chat/completions", lab.Shared.Token, Chat("llama-on-the-cloud")),
            HttpStatusCode.NotFound, "not_found_error");
        Assert.Equal("model_not_found", missing.GetProperty("code").GetString());
        // Two "model" names: Martlet would check one and Ollama run the other, so the request is refused.
        await ErrorAsync(await lab.PostAsync("/v1/chat/completions", lab.Shared.Token,
            """{"model":"gemma4:e4b","model":"other:70b","messages":[{"role":"user","content":"Hi"}]}"""),
            HttpStatusCode.BadRequest, "invalid_request_error");
        await ErrorAsync(await lab.PostAsync("/v1/chat/completions", lab.VoiceOnly.Token, Chat("gemma4:e4b")), HttpStatusCode.Forbidden, "permission_error");
        await ErrorAsync(await lab.PostAsync("/v1/chat/completions", null, Chat("gemma4:e4b")), HttpStatusCode.Unauthorized, "authentication_error");
        Assert.Empty(lab.Conversation.Bodies("/api/chat"));
    }

    [Fact]
    public async Task The_owners_reply_stops_a_shared_request_on_the_same_worker_and_takes_its_slot()
    {
        await using var lab = await Lab.StartAsync(conversationGpus: [CardA], deepGpus: [CardB]);
        lab.Conversation.Held = true;
        using var theirs = await lab.PostAsync("/v1/chat/completions", lab.Shared.Token, Chat("gemma4:e4b", stream: true));
        Assert.Equal(HttpStatusCode.OK, theirs.StatusCode);
        var events = EventsAsync(theirs).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        await lab.Conversation.FirstSent.Task.WaitAsync(Wait);

        // The owner's reply needs the worker's only slot: the shared request ends with an error event and [DONE], its Ollama
        // request is dropped, and the reply runs.
        lab.Conversation.Held = false;
        var reply = lab.ReplyAsync();
        string? code = null;
        while (await events.MoveNextAsync() && events.Current is { } chunk)
            if (chunk.TryGetProperty("error", out var error)) code = error.GetProperty("code").GetString();
        Assert.Equal("owner_needs_it", code);
        await lab.Conversation.Aborted.Task.WaitAsync(Wait);
        Assert.Equal("Hello from the conversation model.", await reply.WaitAsync(Wait));
    }

    [Fact]
    public async Task A_shared_request_waits_for_the_owners_background_work_unless_its_key_has_equal_priority()
    {
        await using var lab = await Lab.StartAsync(conversationGpus: [CardA], deepGpus: [CardA]);
        lab.Deep.Held = true;
        using var think = await lab.ThinkAsync();
        Assert.Equal(HttpStatusCode.OK, think.StatusCode);
        await lab.Deep.FirstSent.Task.WaitAsync(Wait);

        // The owner's think (pool work) uses card A: a key of rank "your Martlet first" is turned away from the other model on it.
        using var refused = await lab.PostAsync("/v1/chat/completions", lab.Shared.Token, Chat("gemma4:e4b"));
        var busy = await ErrorAsync(refused, HttpStatusCode.TooManyRequests, "rate_limit_error");
        Assert.Equal("model_busy", busy.GetProperty("code").GetString());
        Assert.Equal("5", refused.Headers.RetryAfter?.ToString());
        Assert.Empty(lab.Conversation.Bodies("/api/chat"));

        // A key with equal priority runs beside the owner's background work.
        var answer = await JsonAsync(await lab.PostAsync("/v1/chat/completions", lab.Equal.Token, Chat("gemma4:e4b")), HttpStatusCode.OK);
        Assert.Equal("Hello from the conversation model.", answer.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        lab.Deep.Release();
    }

    [Fact]
    public async Task The_front_door_lists_the_networks_models_and_sends_a_model_it_lacks_to_the_host_that_has_it()
    {
        await using var front = await Lab.StartAsync("front-door", conversationModel: "alpha:1b", deepModel: "alpha-think:2b");
        await using var gpu = await Lab.StartAsync("gpu-box", conversationModel: "beta:7b", deepModel: "beta-think:14b", keys: front.Keys);
        gpu.Share(front.Shared, front.Equal);
        front.Host.Server.UseOpenAiPeers(() =>
            [(gpu.Host.Identity.HostId, gpu.Host.Origin.CanonicalOrigin, gpu.Host.Identity.SpkiFingerprint)]);

        var models = await JsonAsync(await front.GetAsync("/v1/models", front.Shared.Token), HttpStatusCode.OK);
        var hosts = models.GetProperty("data").EnumerateArray().ToDictionary(m => m.GetProperty("id").GetString()!,
            m => m.GetProperty("martlet_hosts")[0].GetString());
        Assert.Equal("front-door", hosts["alpha:1b"]);
        Assert.Equal("gpu-box", hosts["beta:7b"]);

        using var sent = await front.PostAsync("/v1/chat/completions", front.Shared.Token, Chat("beta:7b"));
        var answer = await JsonAsync(sent, HttpStatusCode.OK);
        Assert.Equal("gpu-box", sent.Headers.GetValues("X-Martlet-Host").Single());
        Assert.Equal("Hello from the conversation model.", answer.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Single(gpu.Conversation.Bodies("/api/chat"));
        Assert.Empty(front.Conversation.Bodies("/api/chat"));

        // A host asked by another host answers with its own models only, so a request is never sent on twice.
        var request = new HttpRequestMessage(HttpMethod.Get, gpu.Host.Origin.CanonicalOrigin + "/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", front.Shared.Token);
        request.Headers.Add(OpenAiEndpoint.ForwardedHeader, "front-door");
        var own = await JsonAsync(await gpu.Host.Client.SendAsync(request), HttpStatusCode.OK);
        Assert.DoesNotContain(own.GetProperty("data").EnumerateArray(), m => m.GetProperty("id").GetString()!.StartsWith("alpha", StringComparison.Ordinal));
    }

    [Fact]
    public void Chat_translation_keeps_tools_and_turns_tool_calls_back_into_openai_form()
    {
        using var request = JsonDocument.Parse("""
            {"model":"qwen3:8b","reasoning_effort":"none","response_format":{"type":"json_object"},
             "tools":[{"type":"function","function":{"name":"weather","parameters":{"type":"object"}}}],
             "messages":[{"role":"user","content":"Weather?"},
                         {"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"weather","arguments":"{\"city\":\"Oslo\"}"}}]},
                         {"role":"tool","tool_call_id":"call_1","content":"Sunny"}]}
            """);
        var translation = OllamaOpenAi.Chat(request.RootElement, "qwen3:8b", 8_192);
        using var native = JsonDocument.Parse(translation.Body);
        var root = native.RootElement;
        Assert.Equal("/api/chat", translation.NativePath);
        Assert.False(root.GetProperty("think").GetBoolean());
        Assert.Equal("json", root.GetProperty("format").GetString());
        Assert.Equal("weather", root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("Oslo", root.GetProperty("messages")[1].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetProperty("city").GetString());
        Assert.Equal("weather", root.GetProperty("messages")[2].GetProperty("tool_name").GetString());

        using var answer = JsonDocument.Parse("""{"message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"weather","arguments":{"city":"Bergen"}}}]},"done":true,"done_reason":"stop"}""");
        using var openai = JsonDocument.Parse(translation.Finish(answer.RootElement));
        var choice = openai.RootElement.GetProperty("choices")[0];
        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
        var call = choice.GetProperty("message").GetProperty("tool_calls")[0];
        Assert.Equal("function", call.GetProperty("type").GetString());
        Assert.Equal("""{"city":"Bergen"}""", call.GetProperty("function").GetProperty("arguments").GetString());
        Assert.StartsWith("call_", call.GetProperty("id").GetString(), StringComparison.Ordinal);
    }
}
