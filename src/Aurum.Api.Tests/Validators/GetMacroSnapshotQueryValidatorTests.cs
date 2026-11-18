using Aurum.App.Application.Macro.Queries;
using Aurum.App.Application.Macro.Validators;
using Microsoft.Extensions.Time.Testing;

namespace Aurum.Api.Tests.Validators;

public class GetMacroSnapshotQueryValidatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly GetMacroSnapshotQueryValidator _validator = new(new FakeTimeProvider(Now));

    [Fact]
    public async Task ValidateAsync_WithNoAsOf_Passes()
    {
        var result = await _validator.ValidateAsync(new GetMacroSnapshotQuery(AsOf: null));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_WithAsOfInThePast_Passes()
    {
        var result = await _validator.ValidateAsync(new GetMacroSnapshotQuery(new DateTimeOffset(2008, 9, 15, 13, 30, 0, TimeSpan.Zero)));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_WithAsOfExactlyNow_Passes()
    {
        var result = await _validator.ValidateAsync(new GetMacroSnapshotQuery(Now));

        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_WithAsOfOneTickInTheFuture_Fails()
    {
        var result = await _validator.ValidateAsync(new GetMacroSnapshotQuery(Now.AddTicks(1)));

        var error = Assert.Single(result.Errors);
        Assert.Equal(nameof(GetMacroSnapshotQuery.AsOf), error.PropertyName);
    }
}
