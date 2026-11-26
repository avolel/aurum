using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Aurum.App.Infrastructure.AI.Ollama;

// Ollama's request and reply bodies, sent and read as snake_case.

/// <param name="Format">The answer schema. Null only on a load, which has no answer, and then left out.</param>
/// <param name="KeepAlive">A number of seconds or a duration string; see OllamaClient.KeepAliveValue.</param>
internal sealed record ChatRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    bool Stream,
    bool Think,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonNode? Format,
    object KeepAlive,
    ModelSettings Options);

internal sealed record ChatMessage(string Role, string Content);

internal sealed record ModelSettings(int NumCtx, double Temperature, int Seed);

/// <summary>Only the fields the client checks. Ollama sends more, and they're ignored.</summary>
internal sealed record ChatResponse(ChatMessage? Message, string? DoneReason, int PromptEvalCount);

/// <param name="Truncate">Always false: a cut text would embed as if it were the whole one.</param>
internal sealed record EmbedRequest(
    string Model,
    string Input,
    bool Truncate,
    object KeepAlive,
    ModelSettings Options);

internal sealed record EmbedResponse(float[][]? Embeddings);

/// <summary>GET /api/tags: every pulled model. Only the name and digest are read.</summary>
internal sealed record TagsResponse(IReadOnlyList<TagModel>? Models);

internal sealed record TagModel(string Name, string Digest);

/// <summary>GET /api/version.</summary>
internal sealed record VersionResponse(string Version);
