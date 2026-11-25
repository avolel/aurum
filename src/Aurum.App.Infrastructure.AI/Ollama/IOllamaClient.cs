namespace Aurum.App.Infrastructure.AI.Ollama;

/// <summary>The one way into the local model. One request at a time, whoever calls.</summary>
public interface IOllamaClient
{
    /// <summary>
    /// Asks <paramref name="job"/>'s model for an answer shaped like <typeparamref name="T"/>.
    /// Throws <see cref="OllamaAnswerException"/> when the answer doesn't fit or was cut off;
    /// that is not retried here.
    /// </summary>
    Task<OllamaAnswer<T>> ChatAsync<T>(OllamaJob job, string system, string user, CancellationToken ct);

    /// <summary>
    /// One vector from the Embed job's model. Text too long for its window throws
    /// <see cref="HttpRequestException"/> (Ollama's 400) rather than being cut.
    /// </summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct);

    /// <summary>
    /// The job's model name, its digest and Ollama's version, read fresh each call.
    /// Throws <see cref="OllamaModelMissingException"/> when the model isn't pulled.
    /// </summary>
    Task<OllamaModelInfo> GetModelInfoAsync(OllamaJob job, CancellationToken ct);
}

/// <summary>Which model produced an answer, exactly: a re-pull under the same name changes the digest.</summary>
public sealed record OllamaModelInfo(string Model, string Digest, string OllamaVersion);

/// <summary>The checked answer, plus the settings that produced it, so a run can be repeated.</summary>
/// <param name="PromptTokens">Ollama's prompt_eval_count, so callers can see how close the window was.</param>
public sealed record OllamaAnswer<T>(
    T Value,
    string RawJson,
    string Model,
    int ContextWindow,
    double Temperature,
    int Seed,
    int PromptTokens);
