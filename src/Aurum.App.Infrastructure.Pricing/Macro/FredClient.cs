using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Aurum.App.SharedKernel.Constants;

namespace Aurum.App.Infrastructure.Pricing.Macro;

/// <summary>
/// Reads observations from FRED's <c>series/observations</c> and publish dates from
/// <c>release/dates</c>. Fetches only; <see cref="FredImporter"/> saves.
/// </summary>
/// <remarks>
/// The API key is added by <c>QueryKeyAuthHandler</c> below this client, so nothing here can log
/// or throw it. Not behind <c>QuotaHandler</c>: FRED has no monthly allowance (D-22).
/// </remarks>
public class FredClient(HttpClient http)
{

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<IReadOnlyList<FredObservation>> GetObservationsAsync(
        FredSeries series, DateOnly from, CancellationToken ct)
    {
        var query = new Dictionary<string, string?>
        {
            ["series_id"] = series.Code,
            ["file_type"] = "json",
            ["observation_start"] = from.ToString(FredConstants.DateFormat, CultureInfo.InvariantCulture),
        };

        if (series.IsRelease)
        {
            query["output_type"] = "4"; // first published value only (D-22)
            // FRED's whole range. The default window is today only, and output_type=4 then answers 400.
            query["realtime_start"] = "1776-07-04";
            query["realtime_end"] = "9999-12-31";
        }

        return Parse(await GetAsync("series/observations", query, series.Code, ct), series);
    }

    /// <summary>The release's publish dates from <paramref name="from"/> on, oldest first.</summary>
    public async Task<IReadOnlyList<DateOnly>> GetReleaseDatesAsync(
        FredRelease release, DateOnly from, CancellationToken ct)
    {
        var query = new Dictionary<string, string?>
        {
            ["release_id"] = release.FredId.ToString(CultureInfo.InvariantCulture),
            ["file_type"] = "json",
            ["realtime_start"] = from.ToString(FredConstants.DateFormat, CultureInfo.InvariantCulture),
            // Without it FRED lists only dates that already have figures, so no future dates.
            ["include_release_dates_with_no_data"] = "true",
        };

        return ParseReleaseDates(await GetAsync("release/dates", query, release.Code, ct), release);
    }

    private async Task<string> GetAsync(string path, Dictionary<string, string?> query, string what, CancellationToken ct)
    {
        // Relative, so BaseAddress must end in a slash or "/fred" is replaced.
        using var response = await http.GetAsync(QueryHelpers.AddQueryString(path, query), ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"FRED answered {(int)response.StatusCode} for {what}: {ErrorMessage(body)}",
                inner: null,
                response.StatusCode);
        }

        return body;
    }

    /// <summary>Pure, so parsing tests without HTTP.</summary>
    internal static IReadOnlyList<DateOnly> ParseReleaseDates(string json, FredRelease release)
    {
        var page = JsonSerializer.Deserialize<ReleaseDatesPage>(json, Json)
            ?? throw new InvalidDataException($"FRED returned an empty body for release {release.Code}.");

        if (page.ReleaseDates is null)
        {
            throw new InvalidDataException($"FRED's response for release {release.Code} has no release_dates array.");
        }

        if (page.Count > page.ReleaseDates.Count)
        {
            throw new InvalidDataException(
                $"FRED reported {page.Count} dates for release {release.Code} but returned {page.ReleaseDates.Count}.");
        }

        return page.ReleaseDates.Select(d => ParseDate(d.Date)).ToList();
    }

    /// <summary>Pure, so parsing and the failure rules test without HTTP.</summary>
    internal static IReadOnlyList<FredObservation> Parse(string json, FredSeries series)
    {
        var page = JsonSerializer.Deserialize<ObservationsPage>(json, Json)
            ?? throw new InvalidDataException($"FRED returned an empty body for {series.Code}.");

        if (page.Observations is null)
        {
            throw new InvalidDataException($"FRED's response for {series.Code} has no observations array.");
        }

        // A second page would mean rows dropped without a word; none of these series comes close.
        if (page.Count > page.Observations.Count)
        {
            throw new InvalidDataException(
                $"FRED reported {page.Count} observations for {series.Code} but returned {page.Observations.Count}.");
        }

        return page.Observations
            .Select(o => new FredObservation(
                ParseDate(o.Date),
                o.Value == FredConstants.MissingValue ? null : decimal.Parse(o.Value, NumberStyles.Number, CultureInfo.InvariantCulture),
                // For output_type=4, realtime_start is the day the value was first published.
                series.IsRelease ? ParseDate(o.RealtimeStart) : null))
            .ToList();
    }

    private static DateOnly ParseDate(string value) =>
        DateOnly.ParseExact(value, FredConstants.DateFormat, CultureInfo.InvariantCulture);

    // FRED's own message names the problem ("series does not exist"); fall back if the body isn't its JSON.
    private static string ErrorMessage(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<ErrorBody>(body, Json)?.ErrorMessage ?? "no error message";
        }
        catch (JsonException)
        {
            return "no error message";
        }
    }

    private sealed record ObservationsPage(int Count, List<Observation>? Observations);

    private sealed record Observation(string RealtimeStart, string Date, string Value);

    private sealed record ReleaseDatesPage(int Count, List<ReleaseDate>? ReleaseDates);

    private sealed record ReleaseDate(string Date);

    private sealed record ErrorBody(string? ErrorMessage);
}
