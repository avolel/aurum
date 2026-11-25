using System.Text.Json.Nodes;

namespace Aurum.App.Infrastructure.AI.Ollama;

// Ollama's request and reply bodies for /api/chat, sent and read as snake_case.

/// <param name="KeepAlive">A number of seconds or a duration string; see OllamaClient.KeepAliveValue.</param>
internal sealed record ChatRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    bool Stream,
    bool Think,
    JsonNode Format,
    object KeepAlive,
    ModelSettings Options);

internal sealed record ChatMessage(string Role, string Content);

internal sealed record ModelSettings(int NumCtx, double Temperature, int Seed);

/// <summary>Only the fields the client checks. Ollama sends more, and they're ignored.</summary>
internal sealed record ChatResponse(ChatMessage? Message, string? DoneReason, int PromptEvalCount);
