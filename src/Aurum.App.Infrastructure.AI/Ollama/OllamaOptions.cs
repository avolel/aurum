using System.ComponentModel.DataAnnotations;

namespace Aurum.App.Infrastructure.AI.Ollama;

/// <summary>What each request to Ollama is told to use, plus the client's time limits.</summary>
public sealed class OllamaOptions
{
    public const string SectionName = "Ollama";

    /// <summary>The named HttpClient. Named, not typed, so the singleton client's lock is the only one.</summary>
    internal const string HttpClientName = "Ollama";

    /// <summary>
    /// The wait before each retry. Constant and without jitter, so <see cref="TotalTimeout"/> is exact.
    /// </summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    [Required] public Uri? BaseUrl { get; set; }

    /// <summary>One entry per <see cref="OllamaJob"/>. The validator refuses a missing one.</summary>
    public Dictionary<OllamaJob, OllamaJobOptions> Jobs { get; set; } = [];

    /// <summary>Sent on every request. "-1" keeps the model loaded forever.</summary>
    [Required(AllowEmptyStrings = false)] public string KeepAlive { get; set; } = "-1";

    /// <summary>
    /// Per attempt. A cold start took 26 s and one answer about 30 s, so the standard
    /// handler's 10 s default would time out every answer.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; }

    /// <summary>The first try plus retries. At least 2: the standard handler can't turn retries off.</summary>
    public int MaxAttempts { get; set; }

    public double Temperature { get; set; }

    public int Seed { get; set; }

    /// <summary>
    /// Every attempt's full time limit plus the waits between them. Program.cs uses this as the
    /// handler's total timeout, so the last attempt is never cut short.
    /// </summary>
    internal TimeSpan TotalTimeout => RequestTimeout * MaxAttempts + RetryDelay * (MaxAttempts - 1);
}

public sealed class OllamaJobOptions
{
    [Required(AllowEmptyStrings = false)] public string Model { get; set; } = "";

    /// <summary>Sent as num_ctx. Ollama cuts longer prompts without saying so.</summary>
    public int ContextWindow { get; set; }
}

public enum OllamaJob
{
    Write,
    Check,
    Embed,
}
