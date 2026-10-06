namespace Aurum.App.Infrastructure.Pricing.Deltas;

/// <summary>
/// A fixed-size, time-ordered list of <see cref="Sample"/>s for one symbol. When full, appending
/// overwrites the oldest entry.
/// </summary>
/// <remarks>
/// <para>Not thread-safe: the delta engine guards each one with its symbol's lock.</para>
/// <para>Strictly increasing <see cref="Sample.ObservedAt"/> is what makes the binary search work, so
/// out-of-order samples are refused, never re-sorted (D-17).</para>
/// <para>Indexes are logical: 0 is the oldest held sample, array slot <c>(start + i) % capacity</c>.</para>
/// </remarks>
internal sealed class TickRingBuffer
{
    private readonly Sample[] _items;

    // Array slot of the oldest held sample. Moves only once the buffer is full.
    private int _start;
    private int _count;

    public TickRingBuffer(int capacity)
    {
        // A single price can never bracket a window.
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
            // Against Count, not the array: slots past it would be a made-up price.
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
            return _items[(_start + index) % _items.Length];
        }
    }

    /// <summary>
    /// Appends <paramref name="sample"/> if it is strictly newer than the newest held sample.
    /// </summary>
    /// <returns>
    /// <c>false</c> when dropped as out of order. The caller counts drops; warm-up must not.
    /// </returns>
    public bool TryAppend(Sample sample)
    {
        // Equal times are refused too, or "last at or before t" would be ambiguous.
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
    /// Never first-after, which would quietly shrink the window (D-17). Returns the index so the
    /// caller can walk the range.
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
