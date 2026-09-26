using Aurum.App.Infrastructure.Pricing;
using Aurum.App.Infrastructure.Pricing.Sources;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// The breaker's state machine. No container, no HTTP, sub-second.
/// </summary>
/// <remarks>
/// Every one of these is only writable because the circuit measures time by comparing two
/// <c>TimeProvider</c> reads rather than by holding a timer. That is half of D-14's argument
/// against Polly's breaker, whose break duration runs on the wall clock: the assertion in
/// <see cref="Circuit_closes_exactly_at_the_break_duration"/> has no equivalent there.
/// </remarks>
public class SourceCircuitTests
{
    private const string SourceCode = "goldapi.io";

    private static readonly DateTimeOffset Start = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static (SourceCircuit Circuit, FakeTimeProvider Clock) Build(
        int failureThreshold = 3, TimeSpan? breakDuration = null)
    {
        var clock = new FakeTimeProvider(Start);
        var options = new PriceFeedCircuitOptions
        {
            FailureThreshold = failureThreshold,
            BreakDuration = breakDuration ?? TimeSpan.FromMinutes(15),
        };

        return (new SourceCircuit(SourceCode, clock, options), clock);
    }

    [Fact]
    public void Circuit_opens_after_threshold_consecutive_failures()
    {
        var (circuit, _) = Build(failureThreshold: 3);

        circuit.RecordFailure("one");
        Assert.False(circuit.IsOpen());

        circuit.RecordFailure("two");
        Assert.False(circuit.IsOpen());

        circuit.RecordFailure("three");
        Assert.True(circuit.IsOpen());
    }

    [Fact]
    public void A_success_resets_the_consecutive_count()
    {
        var (circuit, _) = Build(failureThreshold: 3);

        circuit.RecordFailure("one");
        circuit.RecordFailure("two");
        circuit.RecordSuccess();
        circuit.RecordFailure("three");
        circuit.RecordFailure("four");

        // Four failures, but never three in a row. Cumulative counting is the bug you get by
        // forgetting the reset, and it opens here.
        Assert.False(circuit.IsOpen());
    }

    [Fact]
    public void Circuit_closes_exactly_at_the_break_duration()
    {
        var breakDuration = TimeSpan.FromMinutes(15);
        var (circuit, clock) = Build(failureThreshold: 1, breakDuration: breakDuration);

        circuit.RecordFailure("down");
        Assert.True(circuit.IsOpen());

        clock.Advance(breakDuration - TimeSpan.FromMilliseconds(1));
        Assert.True(circuit.IsOpen());

        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(circuit.IsOpen());
    }

    [Fact]
    public void One_failure_after_the_break_reopens_for_a_full_break()
    {
        var breakDuration = TimeSpan.FromMinutes(15);
        var (circuit, clock) = Build(failureThreshold: 3, breakDuration: breakDuration);

        circuit.RecordFailure("one");
        circuit.RecordFailure("two");
        circuit.RecordFailure("three");
        Assert.True(circuit.IsOpen());

        clock.Advance(breakDuration);
        Assert.False(circuit.IsOpen());

        // The refinement: expiry leaves the count at FailureThreshold - 1, so the next ordinary
        // attempt IS the probe and one failure is enough to re-open. Resetting the count to zero
        // instead would call a permanently dead provider three times every break — at this
        // threshold and a 5-minute cadence, every single poll, which is the un-broken behaviour
        // the circuit exists to stop.
        circuit.RecordFailure("still down");
        Assert.True(circuit.IsOpen());

        clock.Advance(breakDuration - TimeSpan.FromMilliseconds(1));
        Assert.True(circuit.IsOpen());
    }

    [Fact]
    public void A_failure_during_an_open_break_does_not_extend_it()
    {
        var breakDuration = TimeSpan.FromMinutes(15);
        var (circuit, clock) = Build(failureThreshold: 1, breakDuration: breakDuration);

        circuit.RecordFailure("down");

        clock.Advance(TimeSpan.FromMinutes(14));
        circuit.RecordFailure("still down");

        // Without the null guard on _openedAt this restamps the open time and the shutout never
        // ends while anything keeps failing it.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(circuit.IsOpen());
    }

    [Fact]
    public void Snapshot_reports_the_reason_and_the_end_of_the_break()
    {
        var breakDuration = TimeSpan.FromMinutes(15);
        var (circuit, _) = Build(failureThreshold: 1, breakDuration: breakDuration);

        circuit.RecordFailure("HTTP 503 Service Unavailable.");

        var snapshot = circuit.Snapshot();

        Assert.True(snapshot.IsOpen);
        Assert.Equal(SourceCode, snapshot.SourceCode);
        Assert.Equal("HTTP 503 Service Unavailable.", snapshot.LastFailureReason);
        Assert.Equal(Start, snapshot.LastFailureAt);
        Assert.Equal(Start + breakDuration, snapshot.OpenedUntil);
    }

    [Fact]
    public void Snapshot_of_a_closed_circuit_has_no_end_of_break()
    {
        var (circuit, _) = Build();

        var snapshot = circuit.Snapshot();

        Assert.False(snapshot.IsOpen);
        Assert.Null(snapshot.OpenedUntil);
        Assert.Null(snapshot.LastFailureReason);
    }

    [Fact]
    public void A_failure_reason_is_truncated_to_the_column_width()
    {
        var (circuit, _) = Build(failureThreshold: 1);

        circuit.RecordFailure(new string('x', 1000));

        // price_sources.LastFailureReason is nvarchar(512). An unbounded provider message throws
        // 22001 out of SaveChangesAsync and loses the tick fetched in the same unit of work.
        Assert.Equal(512, circuit.Snapshot().LastFailureReason!.Length);
    }

    [Fact]
    public void A_fresh_store_has_every_circuit_closed()
    {
        var clock = new FakeTimeProvider(Start);
        var store = new SourceCircuitStore(
            [new RegisteredPriceSource("goldapi.io"), new RegisteredPriceSource("api-ninjas")],
            clock,
            Options.Create(new PriceFeedCircuitOptions()));

        // The no-rehydrate rule, executable. A process that has just started has observed nothing,
        // so it must believe nothing — the opposite of the governor's durability rule one folder
        // over, and deliberately so (D-14).
        Assert.All(store.SnapshotAll(), snapshot =>
        {
            Assert.False(snapshot.IsOpen);
            Assert.Equal(0, snapshot.ConsecutiveFailures);
        });

        Assert.Equal(2, store.SnapshotAll().Count);
    }

    [Fact]
    public void An_unregistered_source_code_is_a_wiring_bug_not_a_new_circuit()
    {
        var store = new SourceCircuitStore(
            [new RegisteredPriceSource("goldapi.io")],
            new FakeTimeProvider(Start),
            Options.Create(new PriceFeedCircuitOptions()));

        // GetOrAdd would hide the bug behind a source that reports healthy forever — the same
        // failure shape as a switch fall-through fabricating a configuration (D-9).
        Assert.Throws<KeyNotFoundException>(() => store.For("never-registered"));
    }
}
