using System.Text.Json;
using Aurum.App.Infrastructure.AI.Ollama;
using Aurum.App.Infrastructure.AI.Ollama.Answers;

namespace Aurum.Api.Tests.Modules.AI;

/// <summary>
/// The answer shapes as Ollama sees them (the schema) and as the client reads them back
/// (<see cref="JsonSerializerOptions.Strict"/>, the options <c>OllamaClient</c> checks with).
/// </summary>
public class OllamaAnswersTests
{
    private const string GoodExplanation = """
        {
          "Summary": "Gold rose as the dollar fell after a soft CPI print.",
          "MainCause": { "Cause": "InflationData", "Confidence": 70 },
          "OtherCauses": [ { "Cause": "DollarStrength", "Confidence": 40 } ],
          "ArticleNumbers": [ 1, 3, 4 ]
        }
        """;

    private static ExplanationAnswer? Read(string json) =>
        JsonSerializer.Deserialize<ExplanationAnswer>(json, JsonSerializerOptions.Strict);

    [Fact]
    public void ExplanationAnswer_Schema_ListsEveryCauseByName()
    {
        var schema = OllamaClient.AnswerSchema<ExplanationAnswer>.Value;

        var mainCause = schema["properties"]!["MainCause"]!["properties"]!["Cause"]!["enum"]!;
        var otherCause = schema["properties"]!["OtherCauses"]!["items"]!["properties"]!["Cause"]!["enum"]!;

        Assert.Equal(Enum.GetNames<Cause>(), mainCause.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(Enum.GetNames<Cause>(), otherCause.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void AnswerSchemas_HaveNoRefs()
    {
        // The exporter writes a "$ref" for a type that contains itself. Nobody has checked that
        // Ollama's format grammar follows one, so every shape stays written out in full.
        Assert.DoesNotContain("$ref", OllamaClient.AnswerSchema<ExplanationAnswer>.Value.ToJsonString());
        Assert.DoesNotContain("$ref", OllamaClient.AnswerSchema<CheckAnswer>.Value.ToJsonString());
    }

    [Fact]
    public void ExplanationAnswer_ValidAnswer_Reads()
    {
        var answer = Read(GoodExplanation)!;

        Assert.Equal(new CauseAnswer(Cause.InflationData, 70), answer.MainCause);
        Assert.Equal([new CauseAnswer(Cause.DollarStrength, 40)], answer.OtherCauses);
        Assert.Equal([1, 3, 4], answer.ArticleNumbers);
    }

    [Theory]
    [InlineData("\"InflationData\"", "99")]          // a number no category has
    [InlineData("\"InflationData\"", "\"1\"")]       // a number written as a string
    [InlineData("\"InflationData\"", "\"Tariffs\"")] // a name that isn't a category
    public void ExplanationAnswer_CauseThatIsNotACategory_Throws(string valid, string invalid)
    {
        Assert.Throws<JsonException>(() => Read(GoodExplanation.Replace(valid, invalid)));
    }

    [Fact]
    public void CheckAnswer_ValidAnswer_Reads()
    {
        var answer = JsonSerializer.Deserialize<CheckAnswer>(
            """{ "Score": 82, "GivesAdvice": false, "Reason": "Both cited articles mention the CPI miss." }""",
            JsonSerializerOptions.Strict);

        Assert.Equal(new CheckAnswer(82, false, "Both cited articles mention the CPI miss."), answer);
    }
}
