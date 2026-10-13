using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Aurum.App.SharedKernel.Common;

/// <summary>One page of a time-ordered list. <see cref="NextCursor"/> is null on the last page.</summary>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>The last <c>(time, Id)</c> a page returned; the next page starts after it.</summary>
/// <remarks>
/// Sent to callers as an opaque string so nobody builds one by hand and depends on its format.
/// Base64url, not base64: a <c>+</c> in a query string arrives as a space.
/// </remarks>
public readonly record struct Cursor(DateTimeOffset Time, long Id)
{
    public string Encode() =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{Time:O}|{Id}")));

    public static bool TryDecode(string value, out Cursor cursor)
    {
        cursor = default;

        string text;
        try
        {
            text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value));
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = text.Split('|');
        if (parts.Length != 2
            || !DateTimeOffset.TryParseExact(parts[0], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return false;
        }

        cursor = new Cursor(time, id);
        return true;
    }
}
