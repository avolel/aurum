using Aurum.App.Infrastructure.Pricing.Deltas;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// The delta engine's per-symbol ring buffer. No container, no clock, sub-second.
/// </summary>
public class TickRingBufferTests
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static Sample At(int minutes, decimal mid = 4_000m) =>
        new(Start.AddMinutes(minutes), mid, SourceOrdinal: 0);

    private static TickRingBuffer Filled(int capacity, params int[] minutes)
    {
        var buffer = new TickRingBuffer(capacity);
        foreach (var m in minutes)
        {
            Assert.True(buffer.TryAppend(At(m)));
        }

        return buffer;
    }

    [Fact]
    public void Out_of_order_and_equal_timestamps_are_refused_and_leave_the_buffer_unchanged()
    {
        var buffer = Filled(4, 0, 10);

        Assert.False(buffer.TryAppend(At(5)));
        Assert.False(buffer.TryAppend(At(10, mid: 4_100m))); // same instant, different price

        Assert.Equal(2, buffer.Count);
        Assert.Equal(At(10), buffer[1]);
    }

    /// <summary>
    /// Pins "last at or before", not "first after". Flipping the comparison or the direction the
    /// search continues in turns this red.
    /// </summary>
    [Theory]
    [InlineData(-1, -1)] // before everything: no starting price
    [InlineData(0, 0)]   // exact match on the oldest
    [InlineData(25, 2)]  // between 20 and 30: 20, not 30
    [InlineData(30, 3)]  // exact match in the middle is included
    [InlineData(99, 4)]  // after everything: the newest
    public void Search_returns_the_last_sample_at_or_before_the_time(int minutes, int expected)
    {
        var buffer = Filled(8, 0, 10, 20, 30, 40);

        Assert.Equal(expected, buffer.LastIndexAtOrBefore(Start.AddMinutes(minutes)));
    }

    [Fact]
    public void Empty_buffer_finds_nothing()
    {
        Assert.Equal(-1, new TickRingBuffer(4).LastIndexAtOrBefore(Start));
    }

    /// <summary>
    /// Ten samples into four slots wraps the start position twice, so logical index 0 sits mid-array.
    /// A search that indexed the array directly instead of through the start offset would land on
    /// an overwritten slot here and nowhere else.
    /// </summary>
    [Fact]
    public void Buffer_wraps_and_search_still_finds_the_start()
    {
        var buffer = Filled(4, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9);

        Assert.Equal(4, buffer.Count);
        Assert.Equal([At(6), At(7), At(8), At(9)], Enumerable.Range(0, buffer.Count).Select(i => buffer[i]));

        Assert.Equal(-1, buffer.LastIndexAtOrBefore(Start.AddMinutes(5))); // overwritten: gone, not found
        Assert.Equal(1, buffer.LastIndexAtOrBefore(Start.AddMinutes(7.5)));
    }

    [Fact]
    public void Indexing_past_count_throws_rather_than_returning_an_empty_slot()
    {
        var buffer = Filled(4, 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[1]);
    }

    [Fact]
    public void A_buffer_too_small_to_bracket_a_window_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TickRingBuffer(1));
    }
}
