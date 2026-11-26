using System.Text.Json;
using Aurum.App.Infrastructure.AI.Ollama;

namespace Aurum.Api.Tests.Infrastructure;

/// <summary>
/// An <see cref="IOllamaClient"/> that answers what a test set up and records every call, so no
/// test reaches a real Ollama (finding 8). The real client's wire is <c>OllamaClientTests</c>' subject.
/// </summary>
/// <remarks>
/// A set-up answer is given on every call until it's replaced. A call with nothing set up throws,
/// naming what's missing, rather than inventing an answer. Locked because a background worker can
/// call while the test thread sets up or reads.
/// </remarks>
public sealed class FakeOllamaClient : IOllamaClient
{
    public const string OllamaVersion = "0.0.0-fake";
    public const int ContextWindow = 16384;
    public const double Temperature = 0;
    public const int Seed = 42;
    public const int PromptTokens = 100;

    /// <param name="Job">Null for an embed, which has no job parameter.</param>
    /// <param name="Text">The chat's user message, or the embed's input.</param>
    public sealed record Call(string Method, OllamaJob? Job, string? System, string Text);

    private readonly Lock _gate = new();
    private readonly Dictionary<OllamaJob, Func<object>> _answers = [];
    private readonly Dictionary<string, float[]> _embeddings = [];
    private readonly List<Call> _calls = [];

    public IReadOnlyList<Call> Calls { get { lock (_gate) { return [.. _calls]; } } }

    /// <summary>The model name the fake reports for a job.</summary>
    public static string ModelFor(OllamaJob job) => $"fake-{job}".ToLowerInvariant();

    /// <summary>Every <c>ChatAsync</c> for <paramref name="job"/> answers <paramref name="value"/>.</summary>
    public void Answer<T>(OllamaJob job, T value) where T : notnull
    {
        lock (_gate) { _answers[job] = () => value; }
    }

    /// <summary>
    /// Every <c>ChatAsync</c> for <paramref name="job"/> throws <paramref name="exception"/>, for
    /// example an <see cref="OllamaAnswerException"/> for an answer that didn't fit.
    /// </summary>
    public void Fail(OllamaJob job, Exception exception)
    {
        lock (_gate) { _answers[job] = () => throw exception; }
    }

    /// <summary><c>EmbedAsync(text)</c> returns <paramref name="vector"/>.</summary>
    public void Embed(string text, float[] vector)
    {
        lock (_gate) { _embeddings[text] = vector; }
    }

    public async Task<OllamaAnswer<T>> ChatAsync<T>(OllamaJob job, string system, string user, CancellationToken ct)
    {
        // Like the real client: never done synchronously, and a failure surfaces at the await, not the call.
        await Task.Yield();

        Func<object>? answer;
        lock (_gate)
        {
            _calls.Add(new(nameof(ChatAsync), job, system, user));
            _answers.TryGetValue(job, out answer);
        }

        if (answer is null)
        {
            throw new InvalidOperationException($"FakeOllamaClient has no answer set up for {job}. Call Answer or Fail first.");
        }

        if (answer() is not T value)
        {
            throw new InvalidOperationException($"FakeOllamaClient's answer for {job} isn't a {typeof(T).Name}.");
        }

        // Serialized as the real client would read it, so RawJson round-trips through Strict.
        var raw = JsonSerializer.Serialize(value, JsonSerializerOptions.Strict);
        return new OllamaAnswer<T>(value, raw, ModelFor(job), ContextWindow, Temperature, Seed, PromptTokens);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        await Task.Yield();

        float[]? vector;
        lock (_gate)
        {
            _calls.Add(new(nameof(EmbedAsync), null, null, text));
            _embeddings.TryGetValue(text, out vector);
        }

        return vector
            ?? throw new InvalidOperationException($"FakeOllamaClient has no embedding set up for \"{text}\". Call Embed first.");
    }

    public Task<OllamaModelInfo> GetModelInfoAsync(OllamaJob job, CancellationToken ct) =>
        Task.FromResult(new OllamaModelInfo(ModelFor(job), $"sha256:fake-{job}".ToLowerInvariant(), OllamaVersion));

    public Task LoadAsync(OllamaJob job, CancellationToken ct)
    {
        lock (_gate) { _calls.Add(new(nameof(LoadAsync), job, null, "")); }
        return Task.CompletedTask;
    }
}
