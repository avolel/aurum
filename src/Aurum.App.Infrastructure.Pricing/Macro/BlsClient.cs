using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Aurum.App.SharedKernel.Constants;

namespace Aurum.App.Infrastructure.Pricing.Macro;

/// <summary>
/// Reads one year of BLS figures for several series in one <c>POST</c>. Used only for the month just
/// released, on its release day; FRED supplies everything else (D-23). Fetches only.
/// </summary>
/// <remarks>
/// BLS takes the key in the body, so <c>QueryKeyAuthHandler</c> can't add it. BLS also copies the key
/// into its refusal message, so the answer is scrubbed before anything reads it.
/// </remarks>
public class BlsClient(HttpClient http, IOptions<BlsOptions> options)
{
    private const string KeyPlaceholder = "[key]";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <param name="year">The figure's year: January's release of December figures passes the previous year.</param>
    public async Task<IReadOnlyList<BlsObservation>> GetObservationsAsync(
        IReadOnlyList<BlsSeries> series, int year, CancellationToken ct)
    {
        var key = options.Value.ApiKey;
        var request = new BlsRequest(
            series.Select(s => s.Code).ToList(),
            year.ToString(CultureInfo.InvariantCulture),
            year.ToString(CultureInfo.InvariantCulture),
            key);

        // Relative, so BaseAddress must end in a slash or "/v2" is replaced.
        using var response = await http.PostAsJsonAsync("timeseries/data/", request, ct);
        var body = (await response.Content.ReadAsStringAsync(ct)).Replace(key, KeyPlaceholder);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"BLS answered {(int)response.StatusCode}: {body}", inner: null, response.StatusCode);
        }

        return Parse(body, series);
    }

    /// <summary>Pure, so parsing and the failure rules test without HTTP.</summary>
    internal static IReadOnlyList<BlsObservation> Parse(string json, IReadOnlyList<BlsSeries> requested)
    {
        var answer = JsonSerializer.Deserialize<BlsAnswer>(json, Json)
            ?? throw new InvalidDataException("BLS returned an empty body.");

        // BLS answers 200 with a refusal in the body (bad key, daily limit reached).
        if (answer.Status != BlsConstants.SuccessStatus)
        {
            throw new InvalidDataException($"BLS answered {answer.Status}: {string.Join(" ", answer.Message ?? [])}");
        }

        var rows = new List<BlsObservation>();
        foreach (var series in requested)
        {
            var found = answer.Results?.Series?.FirstOrDefault(s => s.SeriesId == series.Code)
                ?? throw new InvalidDataException($"BLS's answer has no {series.Code}.");

            rows.AddRange(found.Data
                .Where(d => d.Period != BlsConstants.AnnualAveragePeriod)
                .Select(d => new BlsObservation(
                    series,
                    // "M10" is October.
                    new DateOnly(int.Parse(d.Year, CultureInfo.InvariantCulture), int.Parse(d.Period[1..], CultureInfo.InvariantCulture), 1),
                    d.Value == BlsConstants.MissingValue ? null : decimal.Parse(d.Value, NumberStyles.Number, CultureInfo.InvariantCulture))));
        }

        return rows;
    }

    private sealed record BlsRequest(
        [property: JsonPropertyName("seriesid")] List<string> SeriesId,
        [property: JsonPropertyName("startyear")] string StartYear,
        [property: JsonPropertyName("endyear")] string EndYear,
        [property: JsonPropertyName("registrationkey")] string RegistrationKey);

    private sealed record BlsAnswer(string Status, List<string>? Message, BlsResults? Results);

    private sealed record BlsResults(List<BlsSeriesData>? Series);

    private sealed record BlsSeriesData(string SeriesId, List<BlsDataPoint> Data);

    private sealed record BlsDataPoint(string Year, string Period, string Value);
}
