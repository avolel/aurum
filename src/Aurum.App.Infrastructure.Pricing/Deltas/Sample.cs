namespace Aurum.App.Infrastructure.Pricing.Deltas;

/// <summary>
/// One observed price as the delta engine stores it: when it was observed, the mid, and which
/// source it came from.
/// </summary>
/// <remarks>
/// A struct so the buffer is one array with no per-entry allocation: 40 bytes padded, about 340 KB
/// per symbol at the default. No bid, ask or <c>ReceivedAt</c>, to stay small (D-17).
/// </remarks>
/// <param name="SourceOrdinal">
/// A per-process number for the source code, handed out the first time each code is seen. Only
/// equality between two ordinals means anything; the values can differ across restarts.
/// </param>
public readonly record struct Sample(DateTimeOffset ObservedAt, decimal Mid, byte SourceOrdinal);
