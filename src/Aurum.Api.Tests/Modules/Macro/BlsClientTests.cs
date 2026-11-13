using System.Net;
using System.Text.Json;
using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Infrastructure.Pricing.Macro;
using Microsoft.Extensions.Options;

namespace Aurum.Api.Tests.Modules.Macro;

/// <summary>
/// Parsing and request shape for <see cref="BlsClient"/>, with no network. <c>bls-2026.json</c> is
/// a real BLS answer for all four series from 2026-11-11, the day after October's CPI release.
/// </summary>
public class BlsClientTests
{
    private const string ApiKey = "abcdefghijklmnopqrstuvwxyz012345";
    private static readonly Uri BaseUrl = new("https://api.bls.gov/publicAPI/v2/");

    private const string CpiBody = """
        {"status":"REQUEST_SUCCEEDED","responseTime":10,"message":[],"Results":{"series":[
          {"seriesID":"CUSR0000SA0","data":[
            {"year":"2025","period":"M13","periodName":"Annual","value":"321.943","footnotes":[{}]},
            {"year":"2025","period":"M11","periodName":"November","value":"324.122","footnotes":[{}]},
            {"year":"2025","period":"M10","periodName":"October","value":"-","footnotes":[{}]}]}]}}
        """;

    // BLS's real refusal: HTTP 200, and the key copied into the message.
    private const string RefusalBody = $$$"""
        {"status":"REQUEST_NOT_PROCESSED","responseTime":0,"message":["The key:{{{ApiKey}}} provided by the User is invalid. Please provide a proper key for the operation to be successful"],"Results":{}}
        """;

    /// <summary>The client over a stub, recording the request that left it.</summary>
    private static (BlsClient Client, Func<HttpRequestMessage> Sent, Func<string> SentBody) Build(string body)
    {
        HttpRequestMessage? sent = null;
        string? sentBody = null;
        var stub = new CountingHandler(r =>
        {
            sent = r;
            sentBody = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        var options = Options.Create(new BlsOptions { BaseUrl = BaseUrl, ApiKey = ApiKey });
        return (new BlsClient(new HttpClient(stub) { BaseAddress = BaseUrl }, options), () => sent!, () => sentBody!);
    }

    private static string Saved(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Modules", "Macro", name));

    [Fact]
    public void Parse_SavedAnswer_ReadsOctoberCpi()
    {
        var rows = BlsClient.Parse(Saved("bls-2026.json"), BlsSeries.All);

        // The same as FRED's first published value.
        Assert.Equal(337.476m, Assert.Single(rows, r => r.Series == BlsSeries.Cpi && r.ObservedOn == new DateOnly(2026, 10, 1)).Value);
    }

    [Fact]
    public void Parse_SavedAnswer_ReadsOctoberCoreCpi()
    {
        var rows = BlsClient.Parse(Saved("bls-2026.json"), BlsSeries.All);

        Assert.Equal(339.9m, Assert.Single(rows, r => r.Series == BlsSeries.CoreCpi && r.ObservedOn == new DateOnly(2026, 10, 1)).Value);
    }

    [Fact]
    public void Parse_DashValue_IsNullAndRowIsKept()
    {
        var rows = BlsClient.Parse(CpiBody, [BlsSeries.Cpi]);

        Assert.Null(Assert.Single(rows, r => r.ObservedOn == new DateOnly(2025, 10, 1)).Value);
    }

    [Fact]
    public void Parse_AnnualAverage_IsSkipped()
    {
        var rows = BlsClient.Parse(CpiBody, [BlsSeries.Cpi]);

        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.Value == 321.943m);
    }

    [Fact]
    public void Parse_RequestNotProcessed_ThrowsWithBlsMessage()
    {
        var ex = Assert.ThrowsAny<Exception>(() => BlsClient.Parse(RefusalBody, [BlsSeries.Cpi]));

        Assert.Contains("REQUEST_NOT_PROCESSED", ex.Message);
        Assert.Contains("provided by the User is invalid", ex.Message);
    }

    [Fact]
    public void Parse_RequestedSeriesMissing_Throws()
    {
        var ex = Assert.ThrowsAny<Exception>(() => BlsClient.Parse(CpiBody, [BlsSeries.Cpi, BlsSeries.CoreCpi]));

        Assert.Contains("CUSR0000SA0L1E", ex.Message);
    }

    [Fact]
    public async Task GetObservations_SendsEverySeriesTheYearAndTheKey()
    {
        var (client, sent, sentBody) = Build(Saved("bls-2026.json"));

        await client.GetObservationsAsync(BlsSeries.All, 2026, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, sent().Method);
        Assert.Equal("/publicAPI/v2/timeseries/data/", sent().RequestUri!.AbsolutePath);
        var body = JsonDocument.Parse(sentBody()).RootElement;
        Assert.Equal(
            ["CUSR0000SA0", "CUSR0000SA0L1E", "CES0000000001", "LNS14000000"],
            body.GetProperty("seriesid").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("2026", body.GetProperty("startyear").GetString());
        Assert.Equal("2026", body.GetProperty("endyear").GetString());
        Assert.Equal(ApiKey, body.GetProperty("registrationkey").GetString());
    }

    [Fact]
    public async Task GetObservations_ErrorStatus_ThrowsWithoutTheKey()
    {
        var (client, _, _) = Build(RefusalBody);

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => client.GetObservationsAsync([BlsSeries.Cpi], 2026, CancellationToken.None));

        Assert.Contains("provided by the User is invalid", ex.Message);
        Assert.DoesNotContain(ApiKey, ex.ToString());
    }
}
