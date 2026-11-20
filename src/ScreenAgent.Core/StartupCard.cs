using System.Text.RegularExpressions;

namespace ScreenAgent.Core;

public static partial class StartupCard
{
    // A paired footer carries one identity. Use its exact artifact ID so the server verifies its binding.
    [GeneratedRegex(@"(?<![\p{L}\p{N}_])a\s*#\s*([0-9a-f]{6})(?![\p{L}\p{N}_])(?:\s*[·•]\s*Karte\s+([1-9]\d{0,11})(?![\p{L}\p{N}_]))?|(?<![\p{L}\p{N}_])Karte\s+([1-9]\d{0,11})(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex References();

    public static string? Find(string text, out bool ambiguous)
    {
        var matches = References().Matches(text).Cast<Match>().ToArray();
        var paired = matches.Where(m => m.Groups[2].Success).ToLookup(m => m.Groups[2].Value, m => m.Groups[1].Value.ToLowerInvariant());
        var refs = matches.SelectMany(m => m.Groups[1].Success
            ? new[] { "a#" + m.Groups[1].Value.ToLowerInvariant() }
            : paired.Contains(m.Groups[3].Value)
                ? paired[m.Groups[3].Value].Select(aid => "a#" + aid)
                : new[] { "card:" + m.Groups[3].Value }).Distinct().Take(2).ToArray();
        ambiguous = refs.Length > 1;
        return refs.Length == 1 ? refs[0] : null;
    }
}
