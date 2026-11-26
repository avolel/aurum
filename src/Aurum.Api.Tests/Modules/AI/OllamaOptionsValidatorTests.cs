using Aurum.App.Infrastructure.AI.Ollama;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aurum.Api.Tests.Modules.AI;

/// <summary>
/// The Ollama settings, bound and checked the way Program.cs does it, so a broken setting fails
/// at startup and not on item 6's first request.
/// </summary>
public class OllamaOptionsValidatorTests
{
    /// <summary>Settings that pass, so each test fails only on the rule it names.</summary>
    private static Dictionary<string, string?> Valid() => new()
    {
        ["Ollama:BaseUrl"] = "http://localhost:11434",
        ["Ollama:Jobs:Write:Model"] = "gemma4:12b-it-qat",
        ["Ollama:Jobs:Write:ContextWindow"] = "16384",
        ["Ollama:Jobs:Check:Model"] = "gemma4:12b-it-qat",
        ["Ollama:Jobs:Check:ContextWindow"] = "16384",
        ["Ollama:Jobs:Embed:Model"] = "nomic-embed-text",
        ["Ollama:Jobs:Embed:ContextWindow"] = "2048",
        ["Ollama:RequestTimeout"] = "00:03:00",
        ["Ollama:MaxAttempts"] = "2",
    };

    [Fact]
    public void Validate_ValidSettings_BindsEveryJob()
    {
        var options = Resolve(Valid());

        // Also proves the enum dictionary keys bind from configuration text.
        Assert.Equal("nomic-embed-text", options.Jobs[OllamaJob.Embed].Model);
        Assert.Equal(16384, options.Jobs[OllamaJob.Write].ContextWindow);
    }

    [Fact]
    public void Validate_MissingJob_Fails()
    {
        var values = Valid();
        values.Remove("Ollama:Jobs:Check:Model");
        values.Remove("Ollama:Jobs:Check:ContextWindow");

        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(values));
        Assert.Contains("Ollama:Jobs:Check is missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ZeroContextWindow_Fails()
    {
        var values = Valid();
        values["Ollama:Jobs:Embed:ContextWindow"] = "0";

        var ex = Assert.Throws<OptionsValidationException>(() => Resolve(values));
        Assert.Contains("Ollama:Jobs:Embed:ContextWindow must be positive", ex.Message, StringComparison.Ordinal);
    }

    private static OllamaOptions Resolve(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddOptions<OllamaOptions>()
            .BindConfiguration(OllamaOptions.SectionName)
            .ValidateDataAnnotations();
        services.AddSingleton<IValidateOptions<OllamaOptions>, OllamaOptionsValidator>();

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<OllamaOptions>>().Value;
    }
}
