using Aurum.App.SharedKernel.Common;

namespace Aurum.Api.Tests.Common;

public class CursorTests
{
    [Fact]
    public void Encode_ThenTryDecode_ReturnsTheSameCursor()
    {
        // Sub-millisecond ticks: Postgres keeps microseconds, so the cursor must too.
        var cursor = new Cursor(new DateTimeOffset(2026, 10, 12, 9, 30, 15, TimeSpan.Zero).AddTicks(1234560), 987654321);

        Assert.True(Cursor.TryDecode(cursor.Encode(), out var decoded));
        Assert.Equal(cursor, decoded);
    }

    [Fact]
    public void Encode_UsesOnlyQueryStringSafeCharacters()
    {
        var encoded = new Cursor(DateTimeOffset.UnixEpoch, long.MaxValue).Encode();

        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("bm8tcGlwZQ")]                 // "no-pipe"
    [InlineData("MjAyNi0xMC0xMnwtMQ")]         // "2026-10-12|-1": not round-trip format, negative id
    public void TryDecode_WithMalformedInput_ReturnsFalse(string value)
    {
        Assert.False(Cursor.TryDecode(value, out _));
    }
}
