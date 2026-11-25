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
}

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
