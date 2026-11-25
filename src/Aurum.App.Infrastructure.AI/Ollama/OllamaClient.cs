using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Microsoft.Extensions.Options;

namespace Aurum.App.Infrastructure.AI.Ollama;

/// <summary>
/// A singleton, so its lock is the only one. The GPU runs one answer at a time; a second
/// request would queue inside Ollama, where our time limit would already be running.
/// </summary>
internal sealed class OllamaClient(IHttpClientFactory httpFactory, IOptions<OllamaOptions> options) : IOllamaClient
{
    /// <summary>Ollama's wire format. Not strict: Ollama adds reply fields between versions.</summary>
    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    ///  Chat with Ollama, returning a typed answer. The answer is checked for completeness and deserialized.
    /// </summary>   
    public async Task<OllamaAnswer<T>> ChatAsync<T>(OllamaJob job, string system, string user, CancellationToken ct)
    {
        var settings = options.Value;
        var jobSettings = settings.Jobs[job];

        var request = new ChatRequest(
            jobSettings.Model,
            [new("system", system), new("user", user)],
            Stream: false,
            Think: false,
            Format: AnswerSchema<T>.Value,
            KeepAlive: KeepAliveValue(settings.KeepAlive),
            Options: new(jobSettings.ContextWindow, settings.Temperature, settings.Seed));

        var reply = await PostAsync<ChatResponse>("api/chat", request, ct);
        return Check<T>(reply, jobSettings, settings);
    }

    /// <summary>
    /// One vector for <paramref name="text"/>, from the Embed job's model. Text longer than the
    /// window fails with Ollama's 400 instead of being cut quietly.
    /// </summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var settings = options.Value;
        var jobSettings = settings.Jobs[OllamaJob.Embed];

        var request = new EmbedRequest(
            jobSettings.Model,
            text,
            Truncate: false,
            KeepAlive: KeepAliveValue(settings.KeepAlive),
            Options: new(jobSettings.ContextWindow, settings.Temperature, settings.Seed));

        var reply = await PostAsync<EmbedResponse>("api/embed", request, ct);

        // One input, so exactly one vector. Anything else would be stored against the wrong text.
        if (reply.Embeddings is not [{ Length: > 0 } vector])
        {
            throw new OllamaAnswerException(
                $"Expected one vector, got {reply.Embeddings?.Length ?? 0}.", rawJson: null);
        }

        return vector;
    }

    /// <summary>
    /// The job's model as Ollama has it now. Read on every call, not kept: an <c>ollama pull</c>
    /// while the app runs changes the digest. No lock: both GETs answer at once even mid-answer.
    /// </summary>
    public async Task<OllamaModelInfo> GetModelInfoAsync(OllamaJob job, CancellationToken ct)
    {
        var model = options.Value.Jobs[job].Model;
        var http = httpFactory.CreateClient(OllamaOptions.HttpClientName);

        TagsResponse tags;
        using (var response = await http.GetAsync("api/tags", ct))
        {
            tags = await ReadAsync<TagsResponse>(response, "api/tags", ct);
        }

        var wanted = WithTag(model);
        var found = tags.Models?.FirstOrDefault(m => m.Name == wanted)
            ?? throw new OllamaModelMissingException(model);

        VersionResponse version;
        using (var response = await http.GetAsync("api/version", ct))
        {
            version = await ReadAsync<VersionResponse>(response, "api/version", ct);
        }

        return new(model, found.Digest, version.Version);
    }

    /// <summary>
    /// Ollama lists an untagged pull as "name:latest". The tag is the part after the last '/',
    /// so a registry port ("host:5000/name") isn't mistaken for one.
    /// </summary>
    private static string WithTag(string model) =>
        model[(model.LastIndexOf('/') + 1)..].Contains(':') ? model : $"{model}:latest";

    /// <summary>
    /// Every POST goes through here, so chat and embed share the one lock. Waiting for it is bounded
    /// only by ct. The handler's time limits start once it is held, and its retries run inside it, so
    /// a retry can't let a waiter in first.
    /// </summary>
    private async Task<TResponse> PostAsync<TResponse>(string path, object body, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var http = httpFactory.CreateClient(OllamaOptions.HttpClientName);
            using var response = await http.PostAsJsonAsync(path, body, Wire, ct);
            return await ReadAsync<TResponse>(response, path, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<TResponse> ReadAsync<TResponse>(HttpResponseMessage response, string path, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            // Ollama explains itself in the body ("model not found"), which the status alone doesn't.
            var error = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"Ollama {path} returned {(int)response.StatusCode}: {error}", null, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<TResponse>(Wire, ct)
            ?? throw new OllamaAnswerException($"Ollama {path} returned an empty body.", rawJson: null);
    }

    private static OllamaAnswer<T> Check<T>(ChatResponse reply, OllamaJobOptions job, OllamaOptions settings)
    {
        var raw = reply.Message?.Content ?? "";

        // Ollama cuts an over-long prompt from the front without saying so. A full window is the only sign.
        if (reply.PromptEvalCount >= job.ContextWindow)
        {
            throw new OllamaAnswerException(
                $"The prompt used {reply.PromptEvalCount} tokens of a {job.ContextWindow}-token window, "
              + "so Ollama probably cut part of it.", raw);
        }

        // "length" means the answer hit the output limit and stopped part-way.
        if (reply.DoneReason != "stop")
        {
            throw new OllamaAnswerException(
                $"The answer ended with done_reason '{reply.DoneReason}', not 'stop', so it is incomplete.", raw);
        }

        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(raw, JsonSerializerOptions.Strict);
        }
        catch (JsonException ex)
        {
            throw new OllamaAnswerException($"The answer doesn't fit {typeof(T).Name}: {ex.Message}", raw, ex);
        }

        // Strict refuses a null field, but a bare "null" answer still comes back as null.
        if (value is null)
        {
            throw new OllamaAnswerException($"The answer was null, not a {typeof(T).Name}.", raw);
        }

        return new(value, raw, job.Model, job.ContextWindow, settings.Temperature, settings.Seed, reply.PromptEvalCount);
    }

    /// <summary>
    /// A number goes out as seconds (-1 = forever). Ollama parses a string as a Go duration,
    /// which needs a unit, so "-1" as a string would be refused.
    /// </summary>
    private static object KeepAliveValue(string keepAlive) =>
        double.TryParse(keepAlive, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : keepAlive;

    /// <summary>
    /// Built once per answer type from the same options that read the answer, so the shape
    /// Ollama is told and the shape that's accepted can't drift apart.
    /// </summary>
    internal static class AnswerSchema<T>
    {
        public static readonly JsonNode Value = JsonSchemaExporter.GetJsonSchemaAsNode(
            JsonSerializerOptions.Strict,
            typeof(T),
            // Otherwise the top level is "object or null", and the model may answer null.
            new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
    }
}
