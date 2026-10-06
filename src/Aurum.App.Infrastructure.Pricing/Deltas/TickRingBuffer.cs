namespace Aurum.App.Infrastructure.Pricing.Deltas;

/// <summary>
/// A fixed-size, time-ordered list of <see cref="Sample"/>s for one symbol. When full, appending
/// overwrites the oldest entry.
/// </summary>
/// <remarks>
/// <para>Not thread-safe. The delta engine holds one per symbol behind that symbol's lock; nothing
/// else should touch it.</para>
///
/// <para>Strictly increasing <see cref="Sample.ObservedAt"/> is the invariant everything else rests
/// on: it is what makes <see cref="LastIndexAtOrBefore"/> a binary search. So
/// <see cref="TryAppend"/> refuses a sample at or before the newest and never re-sorts. A re-sorted
/// buffer would be in order but would no longer be what the app actually observed (D-17).</para>
///
/// <para>Indexes are <em>logical</em>: 0 is the oldest held sample and <c>Count - 1</c> the newest,
/// wherever they sit in the array. Logical position <c>i</c> is array slot
/// <c>(start + i) % capacity</c>.</para>
/// </remarks>
internal sealed class TickRingBuffer
{
    private readonly Sample[] _items;

    // Array slot of the oldest held sample. Moves only once the buffer is full.
    private int _start;
    private int _count;

    public TickRingBuffer(int capacity)
    {
        // Two, not one: a buffer that can hold a single price can never bracket any window, so it
        // would boot clean and answer "no answer" forever.
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        _items = new Sample[capacity];
    }

    public int Capacity => _items.Length;

    public int Count => _count;

    /// <summary>The sample at logical position <paramref name="index"/>; 0 is the oldest.</summary>
    public Sample this[int index]
    {
        get
        {
            // Checked against Count, not the array length: slots past Count hold default(Sample) or
            // an overwritten value, and silently returning either would be a made-up price.
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
            return _items[(_start + index) % _items.Length];
        }
    }

    /// <summary>
    /// Appends <paramref name="sample"/> if it is strictly newer than the newest held sample.
    /// </summary>
    /// <returns>
    /// <c>false</c> when the sample was dropped as out of order. Counting and logging the drop is the
    /// caller's job, because warm-up appends through here too and must not count.
    /// </returns>
    public bool TryAppend(Sample sample)
    {
        // At-or-before, not just before: an equal timestamp is a second price for an instant the
        // buffer already holds, and keeping both would make "last at or before t" ambiguous.
        if (_count > 0 && sample.ObservedAt <= this[_count - 1].ObservedAt)
        {
            return false;
        }

        if (_count < _items.Length)
        {
            _items[(_start + _count) % _items.Length] = sample;
            _count++;
        }
        else
        {
            // Full: the newest takes the oldest's slot, and the oldest moves up one.
            _items[_start] = sample;
            _start = (_start + 1) % _items.Length;
        }

        return true;
    }

    /// <summary>
    /// The logical index of the last sample observed at or before <paramref name="time"/>, or -1
    /// when every held sample is newer (or the buffer is empty).
    /// </summary>
    /// <remarks>
    /// Last at-or-before, never first-after. First-after silently shrinks the window: with samples
    /// eight hours apart, a "one-hour move" would be measured from a price inside the hour and still
    /// be labelled one hour. Returning the index rather than the sample lets the caller walk the
    /// range from start to end for the sample count and volatility.
    /// </remarks>
    public int LastIndexAtOrBefore(DateTimeOffset time)
    {
        var lo = 0;
        var hi = _count - 1;
        var found = -1;

        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (this[mid].ObservedAt <= time)
            {
                // A candidate; a later one may also qualify, so keep looking right.
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }
}
