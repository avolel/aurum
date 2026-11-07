using System.Net;
using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Infrastructure.Pricing.Macro;
using Aurum.App.Infrastructure.Pricing.Sources;
using Microsoft.AspNetCore.WebUtilities;

namespace Aurum.Api.Tests.Modules.Macro;

/// <summary>
/// Parsing and request shape for <see cref="FredClient"/>, with no network. The two saved files
/// are real FRED answers from 2026-11-07, kept because <c>output_type=4</c>'s shape is the part
/// least likely to match the docs.
/// </summary>
public class FredClientTests
{
    private const string ApiKey = "abcdefghijklmnopqrstuvwxyz012345";
    private static readonly Uri BaseUrl = new("https://api.stlouisfed.org/fred/");
    private static readonly DateOnly From = new(2026, 1, 1);

    private const string ReleaseBody = """
        {"count":2,"offset":0,"limit":100000,"observations":[
          {"realtime_start":"2026-02-11","realtime_end":"2026-03-10","date":"2026-01-01","value":"325.252"},
          {"realtime_start":"2026-03-11","realtime_end":"9999-12-31","date":"2026-02-01","value":"."}]}
        """;

    private const string MarketBody = """
        {"count":1,"offset":0,"limit":100000,"observations":[
          {"realtime_start":"2026-10-29","realtime_end":"2026-10-29","date":"2026-10-28","value":"4.12"}]}
        """;

    /// <summary>The client over the real auth handler, recording the URI that left it.</summary>
    private static (FredClient Client, Func<Uri> Sent) Build(HttpStatusCode status, string body)
    {
        Uri? sent = null;
        var stub = new CountingHandler(r =>
        {
            sent = r.RequestUri;
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        });
        var auth = new QueryKeyAuthHandler(ApiKey) { InnerHandler = stub };
        return (new FredClient(new HttpClient(auth) { BaseAddress = BaseUrl }), () => sent!);
    }

    private static string Saved(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Modules", "Macro", name));

    [Fact]
    public void Parse_ReleaseSeries_KeepsValueAndPublishDate()
    {
        var rows = FredClient.Parse(ReleaseBody, FredSeries.Cpi);

        Assert.Equal(new FredObservation(new DateOnly(2026, 1, 1), 325.252m, new DateOnly(2026, 2, 11)), rows[0]);
    }

    [Fact]
    public void Parse_DotValue_IsNullAndRowIsKept()
    {
        var rows = FredClient.Parse(ReleaseBody, FredSeries.Cpi);

        Assert.Equal(2, rows.Count);
        Assert.Null(rows[1].Value);
    }

    [Fact]
    public void Parse_MarketSeries_LeavesReleasedOnEmpty()
    {
        var rows = FredClient.Parse(MarketBody, FredSeries.Treasury10Y);

        Assert.Null(Assert.Single(rows).ReleasedOn);
    }

    [Fact]
    public void Parse_CountLargerThanRowsReturned_Throws()
    {
        var truncated = ReleaseBody.Replace("\"count\":2", "\"count\":3");

        Assert.ThrowsAny<Exception>(() => FredClient.Parse(truncated, FredSeries.Cpi));
    }

    [Fact]
    public void Parse_MalformedBody_Throws()
    {
        Assert.ThrowsAny<Exception>(() => FredClient.Parse("<html>maintenance</html>", FredSeries.Cpi));
    }

    [Fact]
    public void Parse_SavedCpiInitialRelease_ReadsFirstPublishedValues()
    {
        var rows = FredClient.Parse(Saved("fred-cpiaucsl-initial-release.json"), FredSeries.Cpi);

        Assert.Equal(21, rows.Count);
        Assert.Equal(new FredObservation(new DateOnly(2025, 1, 1), 319.086m, new DateOnly(2025, 2, 12)), rows[0]);
        // October 2025 was never published (the shutdown); FRED keeps the period as ".".
        Assert.Null(Assert.Single(rows, r => r.ObservedOn == new DateOnly(2025, 10, 1)).Value);
    }

    [Fact]
    public void Parse_SavedTreasury10Y_KeepsHolidaysAsNull()
    {
        var rows = FredClient.Parse(Saved("fred-dgs10.json"), FredSeries.Treasury10Y);

        Assert.All(rows, r => Assert.Null(r.ReleasedOn));
        Assert.Null(Assert.Single(rows, r => r.ObservedOn == new DateOnly(2025, 12, 25)).Value);
    }

    [Fact]
    public async Task GetObservations_ReleaseSeries_AsksForInitialReleases()
    {
        var (client, sent) = Build(HttpStatusCode.OK, ReleaseBody);

        await client.GetObservationsAsync(FredSeries.Cpi, From, CancellationToken.None);

        var query = QueryHelpers.ParseQuery(sent().Query);
        Assert.Equal("/fred/series/observations", sent().AbsolutePath);
        Assert.Equal("CPIAUCSL", query["series_id"]);
        Assert.Equal("4", query["output_type"]);
        Assert.Equal("json", query["file_type"]);
        Assert.Equal("2026-01-01", query["observation_start"]);
        Assert.Equal(ApiKey, query["api_key"]);
    }

    [Fact]
    public async Task GetObservations_ReleaseSeries_SearchesEveryVintage()
    {
        var (client, sent) = Build(HttpStatusCode.OK, ReleaseBody);

        await client.GetObservationsAsync(FredSeries.Cpi, From, CancellationToken.None);

        // Left at the default (today), FRED answers output_type=4 with 400 "No vintage dates exist".
        var query = QueryHelpers.ParseQuery(sent().Query);
        Assert.Equal("1776-07-04", query["realtime_start"]);
        Assert.Equal("9999-12-31", query["realtime_end"]);
    }

    [Fact]
    public async Task GetObservations_MarketSeries_UsesTheDefaultOutputType()
    {
        var (client, sent) = Build(HttpStatusCode.OK, MarketBody);

        await client.GetObservationsAsync(FredSeries.Treasury10Y, From, CancellationToken.None);

        var query = QueryHelpers.ParseQuery(sent().Query);
        Assert.False(query.ContainsKey("output_type"));
        Assert.False(query.ContainsKey("realtime_start"));
    }

    [Fact]
    public async Task GetObservations_BadRequest_ThrowsWithoutTheKey()
    {
        var (client, _) = Build(HttpStatusCode.BadRequest, """{"error_code":400,"error_message":"Bad Request.  The value for variable api_key is not registered."}""");

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => client.GetObservationsAsync(FredSeries.Cpi, From, CancellationToken.None));

        Assert.Contains("CPIAUCSL", ex.Message);
        Assert.DoesNotContain(ApiKey, ex.ToString());
    }
}
