using System.Text.Json.Serialization;

namespace Aurum.App.Infrastructure.AI.Ollama.Answers;

/// <summary>
/// FR-2.5's cause categories. Sent and read as names, so the schema lists every allowed value and
/// Ollama can only pick one of them.
/// </summary>
[JsonConverter(typeof(NamesOnlyEnumConverter<Cause>))]
public enum Cause
{
    FedAndRates,
    InflationData,
    DollarStrength,
    TreasuryYields,
    GeopoliticalRisk,
    CentralBankBuying,
    RiskAppetite,
    EconomicSurprise,
    ChartBreakout,
    GoldFundFlows,
}

/// <summary>
/// Reads an enum by name only. The default string converter also accepts a number, which would let
/// a cause like 99, one no category has, through the strict check.
/// </summary>
internal sealed class NamesOnlyEnumConverter<TEnum>()
    : JsonStringEnumConverter<TEnum>(namingPolicy: null, allowIntegerValues: false)
    where TEnum : struct, Enum;
