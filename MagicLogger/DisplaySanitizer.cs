using System.Text;
using System.Text.RegularExpressions;

namespace MagicLogger;

public static partial class DisplaySanitizer
{
    [GeneratedRegex(@"[\uE000-\uF8FF](?:(?![\uE000-\uF8FF]).)*[\uE000-\uF8FF]", RegexOptions.Singleline)]
    private static partial Regex PrivateUseMarker();

    [GeneratedRegex(@"(?i)(?:\[|【)?(?:oaicite|memcite|filecite|turn\d+(?:search|fetch|view|file)\d+)(?:\]|】)?")]
    private static partial Regex NamedMarker();

    [GeneratedRegex(@"[ \t]+(?=\r?$)", RegexOptions.Multiline)]
    private static partial Regex TrailingWhitespace();

    [GeneratedRegex(@"\n{4,}")]
    private static partial Regex ExcessBlankLines();

    public static string Clean(string? rawText)
    {
        if (string.IsNullOrEmpty(rawText)) return string.Empty;

        var withoutMarkers = PrivateUseMarker().Replace(rawText, string.Empty);
        withoutMarkers = NamedMarker().Replace(withoutMarkers, string.Empty);

        var builder = new StringBuilder(withoutMarkers.Length);
        foreach (var rune in withoutMarkers.EnumerateRunes())
        {
            var value = rune.Value;
            if (value is '\r' or '\n' or '\t' || !Rune.IsControl(rune))
                builder.Append(rune.ToString());
        }

        var normalized = builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        normalized = TrailingWhitespace().Replace(normalized, string.Empty);
        return ExcessBlankLines().Replace(normalized, "\n\n\n").Trim();
    }
}
