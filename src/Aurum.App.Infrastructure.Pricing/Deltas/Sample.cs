namespace Aurum.App.Infrastructure.Pricing.Deltas;

/// <summary>
/// One observed price as the delta engine stores it: when it was observed, the mid, and which
/// source it came from.
/// </summary>
/// <remarks>
/// <para>A struct so the ring buffer is one contiguous array with no per-entry allocation. Padded
/// to 40 bytes (16 + 16 + 1, aligned to 8), so the default 8,640 entries are about 340 KB per
/// symbol — not the 200 KB the original spec quoted.</para>
///
/// <para>No bid, ask or <c>ReceivedAt</c>. That is what keeps it small, and it is also why the
/// latest-quote cache cannot be warmed from this buffer (D-17).</para>
/// </remarks>
/// <param name="SourceOrdinal">
/// A per-process number for the source code, handed out the first time each code is seen. Only
/// equality between two ordinals means anything; the values can differ across restarts.
/// </param>
public readonly record struct Sample(DateTimeOffset ObservedAt, decimal Mid, byte SourceOrdinal);
