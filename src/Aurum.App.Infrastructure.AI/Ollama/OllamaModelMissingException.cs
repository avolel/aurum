namespace Aurum.App.Infrastructure.AI.Ollama;

/// <summary>A configured model isn't in Ollama's list, so it was never pulled or was removed.</summary>
public sealed class OllamaModelMissingException(string model)
    : Exception($"Ollama has no model '{model}'. Run `ollama pull {model}`.")
{
    public string Model { get; } = model;
}
