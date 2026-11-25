namespace Aurum.App.Infrastructure.AI.Ollama;

/// <summary>Ollama answered, but the answer can't be used: wrong shape, or cut off.</summary>
public sealed class OllamaAnswerException(string message, string? rawJson, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>What the model sent, kept so a failed answer can be read later.</summary>
    public string? RawJson { get; } = rawJson;
}
