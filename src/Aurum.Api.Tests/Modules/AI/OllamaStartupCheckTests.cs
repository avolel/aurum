using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Infrastructure.AI.Ollama;
using Aurum.App.Logging;
using Microsoft.Extensions.Logging;
using Moq;

namespace Aurum.Api.Tests.Modules.AI;

/// <summary>
/// Runs the check the way the host does, <c>StartAsync</c> then awaiting <c>ExecuteTask</c>, so a
/// throw that would stop the host fails the test. The client is mocked: its wire is
/// <see cref="OllamaClientTests"/>' subject.
/// </summary>
public class OllamaStartupCheckTests
{
    private readonly Mock<IOllamaClient> _ollama = new();
    private readonly ListAppLog<OllamaStartupCheck> _log = new();

    public OllamaStartupCheckTests()
    {
        _ollama.Setup(o => o.GetModelInfoAsync(It.IsAny<OllamaJob>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OllamaJob job, CancellationToken _) => new OllamaModelInfo($"{job}-model", $"sha-{job}", "0.40.9"));
    }

    private async Task RunAsync()
    {
        var check = new OllamaStartupCheck(_ollama.Object, _log);
        await check.StartAsync(CancellationToken.None);
        await check.ExecuteTask!;
    }

    [Fact]
    public async Task ExecuteAsync_EveryModelPulled_LoadsEachJobAndLogsLoaded()
    {
        await RunAsync();

        foreach (var job in Enum.GetValues<OllamaJob>())
        {
            _ollama.Verify(o => o.LoadAsync(job, It.IsAny<CancellationToken>()), Times.Once);
        }

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(("OllamaModelsLoaded", LogLevel.Information, LogType.SystemLog), (entry.Action, entry.Level, entry.LogType));
        // The digests and version are what make a later answer traceable to the exact model.
        Assert.Contains("0.40.9", entry.Details);
        Assert.Contains("Embed Embed-model (sha-Embed)", entry.Details);
    }

    [Fact]
    public async Task ExecuteAsync_MissingModel_LogsErrorAndDoesNotThrow()
    {
        _ollama.Setup(o => o.GetModelInfoAsync(OllamaJob.Embed, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OllamaModelMissingException("nomic-embed-text"));

        await RunAsync();

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(("OllamaModelMissing", LogLevel.Error, LogType.SystemLog), (entry.Action, entry.Level, entry.LogType));
        Assert.Contains("ollama pull nomic-embed-text", entry.Details);
        // Nothing is loaded while a job's model is missing.
        _ollama.Verify(o => o.LoadAsync(It.IsAny<OllamaJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_OllamaUnreachable_LogsErrorAndDoesNotThrow()
    {
        _ollama.Setup(o => o.GetModelInfoAsync(It.IsAny<OllamaJob>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused (ollama:11434)"));

        await RunAsync();

        var entry = Assert.Single(_log.Entries);
        Assert.Equal(("OllamaStartupCheckFailed", LogLevel.Error), (entry.Action, entry.Level));
        Assert.IsType<HttpRequestException>(entry.Exception);
    }

    [Fact]
    public async Task ExecuteAsync_LoadFails_LogsErrorAndDoesNotThrow()
    {
        // Both models fitting in 12 GB together is unchecked (D-6); a refused load lands here.
        _ollama.Setup(o => o.LoadAsync(OllamaJob.Embed, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Ollama api/embed returned 500: out of memory"));

        await RunAsync();

        var entry = Assert.Single(_log.Entries);
        Assert.Equal("OllamaStartupCheckFailed", entry.Action);
        Assert.Contains("out of memory", entry.Details);
    }
}
