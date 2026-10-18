namespace Palanca;

internal static class Text
{
    /// <summary>
    /// Trims <paramref name="value"/> and returns it when it has 1 to <paramref name="maxLength"/> characters and no
    /// control characters, other than line breaks (stored as <c>\n</c>) when <paramref name="allowLineBreaks"/> is set.
    /// </summary>
    public static string? Clean(string? value, int maxLength, bool allowLineBreaks = false)
    {
        var trimmed = value?.ReplaceLineEndings("\n").Trim();
        return string.IsNullOrEmpty(trimmed)
            || trimmed.Length > maxLength
            || trimmed.Any(c => char.IsControl(c) && !(allowLineBreaks && c == '\n'))
            ? null
            : trimmed;
    }
}
