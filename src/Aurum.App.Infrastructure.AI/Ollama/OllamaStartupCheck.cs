using Aurum.App.Logging;
using Microsoft.Extensions.Hosting;

namespace Aurum.App.Infrastructure.AI.Ollama;

/// <summary>
/// Once at startup: checks every job's model is pulled, then loads each one, so the first price
/// move doesn't wait for a cold start (26 s measured). Runs in the background and never throws:
/// prices must keep working when the AI is down.
/// </summary>
internal sealed class OllamaStartupCheck(
    IOllamaClient ollama,
    IAppLogService<OllamaStartupCheck> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Caught here because an exception out of ExecuteAsync stops the whole host by default.
            await logger.LogErrorAsync("OllamaStartupCheckFailed", ex,
                details: $"Ollama couldn't be checked or loaded: {ex.Message}",
                logType: LogType.SystemLog, ct: CancellationToken.None);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var jobs = Enum.GetValues<OllamaJob>();
        var found = new List<(OllamaJob Job, OllamaModelInfo Info)>();

        foreach (var job in jobs)
        {
            try
            {
                found.Add((job, await ollama.GetModelInfoAsync(job, ct)));
            }
            catch (OllamaModelMissingException ex)
            {
                // The message names the model and says "ollama pull <name>".
                await logger.LogErrorAsync("OllamaModelMissing", ex,
                    details: $"{job}: {ex.Message}", logType: LogType.SystemLog, ct: CancellationToken.None);
            }
        }

        // Load nothing unless every model is there: the AI can't work with a job missing anyway.
        if (found.Count < jobs.Length)
        {
            return;
        }

        foreach (var job in jobs)
        {
            await ollama.LoadAsync(job, ct);
        }

        var models = string.Join(", ", found.Select(f => $"{f.Job} {f.Info.Model} ({f.Info.Digest})"));
        await logger.LogAsync(LogType.SystemLog, "OllamaModelsLoaded",
            $"Ollama {found[0].Info.OllamaVersion}: {models}", 200, ct: CancellationToken.None);
    }
}
