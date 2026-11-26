using System.Text.Json;
using Aurum.App.Infrastructure.AI.Ollama;
using Aurum.App.Infrastructure.AI.Ollama.Answers;

namespace Aurum.Api.Tests.Infrastructure;

/// <summary>Pins what item 6's tests will rely on the stand-in to do.</summary>
public class FakeOllamaClientTests
{
    private static CancellationToken Ct => CancellationToken.None;

    private static readonly ExplanationAnswer Explanation = new(
        "Gold rose as the dollar fell.", new(Cause.DollarStrength, 70), [], [1, 2, 3]);

    [Fact]
    public async Task ChatAsync_AnswerSetUp_ReturnsItAndRecordsTheCall()
    {
        var fake = new FakeOllamaClient();
        fake.Answer(OllamaJob.Write, Explanation);

        var answer = await fake.ChatAsync<ExplanationAnswer>(OllamaJob.Write, "system", "why?", Ct);

        Assert.Same(Explanation, answer.Value);
        Assert.Equal(FakeOllamaClient.ModelFor(OllamaJob.Write), answer.Model);
        Assert.Equal(new FakeOllamaClient.Call("ChatAsync", OllamaJob.Write, "system", "why?"), Assert.Single(fake.Calls));

        // RawJson is what the real client would have accepted: causes by name, read back strictly.
        Assert.Contains("\"DollarStrength\"", answer.RawJson);
        Assert.Equal(Explanation.MainCause, JsonSerializer.Deserialize<ExplanationAnswer>(answer.RawJson, JsonSerializerOptions.Strict)!.MainCause);
    }

    [Fact]
    public async Task ChatAsync_NothingSetUp_ThrowsAtTheAwaitNotTheCall()
    {
        var fake = new FakeOllamaClient();

        // Like the real client, the failure is in the task, so code that awaits later still sees it.
        var task = fake.ChatAsync<ExplanationAnswer>(OllamaJob.Check, "system", "check", Ct);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Contains("Check", ex.Message);
    }

    [Fact]
    public async Task ChatAsync_FailSetUp_ThrowsTheGivenException()
    {
        var fake = new FakeOllamaClient();
        var failure = new OllamaAnswerException("The answer doesn't fit ExplanationAnswer.", "{}");
        fake.Fail(OllamaJob.Write, failure);

        var ex = await Assert.ThrowsAsync<OllamaAnswerException>(
            () => fake.ChatAsync<ExplanationAnswer>(OllamaJob.Write, "system", "why?", Ct));
        Assert.Same(failure, ex);
    }

    [Fact]
    public async Task EmbedAsync_ReturnsTheVectorSetUpForThatTextOnly()
    {
        var fake = new FakeOllamaClient();
        fake.Embed("gold rose", [0.1f, 0.2f]);

        var vector = await fake.EmbedAsync("gold rose", Ct);

        Assert.Equal([0.1f, 0.2f], vector);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fake.EmbedAsync("gold fell", Ct));
    }
}
