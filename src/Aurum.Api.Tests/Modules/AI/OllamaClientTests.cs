using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Aurum.App.Infrastructure.AI.Ollama;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aurum.Api.Tests.Modules.AI;

/// <summary>
/// <see cref="OllamaClient.ChatAsync{T}"/> through <c>Program.AddOllama</c>, the production
/// registration, with only the network swapped for a stub. The lock and retry tests need the real
/// resilience handler, because the subject is where the lock sits relative to it.
/// </summary>
public class OllamaClientTests
{
    private const int Window = 16384;
    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>A stand-in answer shape. Public so the strict serializer can build it.</summary>
    public sealed record TestAnswer(string Summary, int Score);

    private static readonly Dictionary<string, string?> Settings = new()
    {
        ["Ollama:BaseUrl"] = "http://ollama.test:11434",
        ["Ollama:Jobs:Write:Model"] = "gemma4:12b-it-qat",
        ["Ollama:Jobs:Write:ContextWindow"] = Window.ToString(),
        ["Ollama:Jobs:Check:Model"] = "gemma4:12b-it-qat",
        ["Ollama:Jobs:Check:ContextWindow"] = Window.ToString(),
        ["Ollama:Jobs:Embed:Model"] = "nomic-embed-text",
        ["Ollama:Jobs:Embed:ContextWindow"] = "2048",
        ["Ollama:KeepAlive"] = "-1",
        ["Ollama:RequestTimeout"] = "00:00:10",
        ["Ollama:MaxAttempts"] = "2",
        ["Ollama:Temperature"] = "0",
        ["Ollama:Seed"] = "42",
    };

    private static ServiceProvider BuildProvider(OllamaStub transport)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(Settings).Build());
        services.AddLogging();

        Program.AddOllama(services);

        // Replace only what touches a socket; the resilience handler stays as Program.cs builds it.
        services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => transport));
        return services.BuildServiceProvider();
    }

    private static HttpResponseMessage Reply(string content, int promptTokens = 100, string doneReason = "stop") =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                model = "gemma4:12b-it-qat",
                message = new { role = "assistant", content },
                done = true,
                done_reason = doneReason,
                prompt_eval_count = promptTokens,
                eval_count = 20,
            }),
        };

    private const string GoodAnswer = """{"Summary":"Gold rose on a weaker dollar.","Score":7}""";

    [Fact]
    public async Task ChatAsync_SendsEverySettingOnEveryRequest()
    {
        var transport = new OllamaStub((_, _) => Task.FromResult(Reply(GoodAnswer)));
        await using var provider = BuildProvider(transport);
        var client = provider.GetRequiredService<IOllamaClient>();

        var answer = await client.ChatAsync<TestAnswer>(OllamaJob.Write, "be brief", "why did gold move?", Ct);
        await client.ChatAsync<TestAnswer>(OllamaJob.Check, "be strict", "check this", Ct);

        Assert.Equal(new TestAnswer("Gold rose on a weaker dollar.", 7), answer.Value);
        Assert.Equal(2, transport.Bodies.Count);

        foreach (var body in transport.Bodies)
        {
            Assert.Equal("gemma4:12b-it-qat", (string?)body["model"]);
            Assert.Equal(Window, (int?)body["options"]?["num_ctx"]);
            Assert.Equal(0d, (double?)body["options"]?["temperature"]);
            Assert.Equal(42, (int?)body["options"]?["seed"]);
            // A number, not "-1": Ollama reads a string as a Go duration, which needs a unit.
            Assert.Equal(-1d, (double?)body["keep_alive"]);
            Assert.False((bool?)body["stream"]);
            Assert.False((bool?)body["think"]);

            // The schema comes from TestAnswer: both fields required, nothing else allowed.
            var format = body["format"]!;
            Assert.Equal("object", (string?)format["type"]);
            Assert.Equal(["Summary", "Score"], format["required"]!.AsArray().Select(n => (string?)n));
            Assert.False((bool?)format["additionalProperties"]);
        }

        Assert.Equal("system", (string?)transport.Bodies[0]["messages"]?[0]?["role"]);
        Assert.Equal("why did gold move?", (string?)transport.Bodies[0]["messages"]?[1]?["content"]);
    }

    [Fact]
    public async Task ChatAsync_AnswerMissingARequiredField_Throws()
    {
        var transport = new OllamaStub((_, _) => Task.FromResult(Reply("""{"Summary":"no score"}""")));
        await using var provider = BuildProvider(transport);

        var ex = await Assert.ThrowsAsync<OllamaAnswerException>(() =>
            provider.GetRequiredService<IOllamaClient>().ChatAsync<TestAnswer>(OllamaJob.Write, "s", "u", Ct));

        Assert.Equal("""{"Summary":"no score"}""", ex.RawJson);
        // Not retried: an answer that doesn't fit is item 6's decision.
        Assert.Single(transport.Bodies);
    }

    [Fact]
    public async Task ChatAsync_AnswerWithAnUnknownField_Throws()
    {
        var transport = new OllamaStub((_, _) =>
            Task.FromResult(Reply("""{"Summary":"x","Score":1,"Confidence":0.9}""")));
        await using var provider = BuildProvider(transport);

        await Assert.ThrowsAsync<OllamaAnswerException>(() =>
            provider.GetRequiredService<IOllamaClient>().ChatAsync<TestAnswer>(OllamaJob.Write, "s", "u", Ct));
    }

    [Fact]
    public async Task ChatAsync_PromptFillsTheContextWindow_Throws()
    {
        // A valid answer, so only the token count can fail it.
        var transport = new OllamaStub((_, _) => Task.FromResult(Reply(GoodAnswer, promptTokens: Window)));
        await using var provider = BuildProvider(transport);

        var ex = await Assert.ThrowsAsync<OllamaAnswerException>(() =>
            provider.GetRequiredService<IOllamaClient>().ChatAsync<TestAnswer>(OllamaJob.Write, "s", "u", Ct));

        Assert.Contains($"{Window}-token window", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatAsync_AnswerCutOffAtTheOutputLimit_Throws()
    {
        var transport = new OllamaStub((_, _) => Task.FromResult(Reply(GoodAnswer, doneReason: "length")));
        await using var provider = BuildProvider(transport);

        await Assert.ThrowsAsync<OllamaAnswerException>(() =>
            provider.GetRequiredService<IOllamaClient>().ChatAsync<TestAnswer>(OllamaJob.Write, "s", "u", Ct));
    }

    [Fact]
    public async Task ChatAsync_TwoCallersAtOnce_OnlyOneRequestInFlight()
    {
        var release = new TaskCompletionSource();
        var transport = new OllamaStub(async (_, user) =>
        {
            if (user == "first")
            {
                await release.Task;
            }

            return Reply(GoodAnswer);
        });
        await using var provider = BuildProvider(transport);
        var client = provider.GetRequiredService<IOllamaClient>();

        var first = client.ChatAsync<TestAnswer>(OllamaJob.Write, "s", "first", Ct);
        await transport.WaitForRequestsAsync(1);
        var second = client.ChatAsync<TestAnswer>(OllamaJob.Write, "s", "second", Ct);

        // Long enough for the second request to reach the stub if the lock let it through.
        await Task.Delay(250);
        Assert.Single(transport.Bodies);

        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(["first", "second"], transport.Users);
        Assert.Equal(1, transport.MaxInFlight);
    }

    /// <remarks>Pays the real 2 s retry delay: <c>RetryDelay</c> is a constant, not a setting.</remarks>
    [Fact]
    public async Task ChatAsync_ServerErrorThenSuccess_RetriesOnceAndKeepsTheLock()
    {
        var release = new TaskCompletionSource();
        var firstAttempt = true;
        var transport = new OllamaStub(async (_, user) =>
        {
            if (user == "first" && firstAttempt)
            {
                firstAttempt = false;
                await release.Task;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return Reply(GoodAnswer);
        });
        await using var provider = BuildProvider(transport);
        var client = provider.GetRequiredService<IOllamaClient>();

        var first = client.ChatAsync<TestAnswer>(OllamaJob.Write, "s", "first", Ct);
        await transport.WaitForRequestsAsync(1);
        var second = client.ChatAsync<TestAnswer>(OllamaJob.Write, "s", "second", Ct);

        // The 500 goes back while "second" is waiting. If the lock were inside the retry, "second"
        // would get in during the 2 s delay before the retry.
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(["first", "first", "second"], transport.Users);
    }

    /// <summary>
    /// Answers asynchronously, so a test can hold a request open, and records what was in flight.
    /// </summary>
    private sealed class OllamaStub(Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private readonly List<JsonNode> _bodies = [];
        private int _inFlight;
        private int _maxInFlight;

        public IReadOnlyList<JsonNode> Bodies { get { lock (_gate) { return [.. _bodies]; } } }

        /// <summary>Each request's user message, in the order they arrived.</summary>
        public IReadOnlyList<string?> Users => [.. Bodies.Select(b => (string?)b["messages"]?[1]?["content"])];

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public async Task WaitForRequestsAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Bodies.Count < count)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Expected {count} requests, saw {Bodies.Count}.");
                }

                await Task.Delay(10);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            lock (_gate)
            {
                _bodies.Add(body);
                _maxInFlight = Math.Max(_maxInFlight, ++_inFlight);
            }

            try
            {
                return await respond(request, (string?)body["messages"]?[1]?["content"]);
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight--;
                }
            }
        }
    }
}
