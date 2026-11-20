using System.Text.RegularExpressions;

namespace ScreenAgent.Core;

public static partial class StartupCard
{
    // A paired footer carries one identity. Use its exact artifact ID so the server verifies its binding.
    [GeneratedRegex(@"(?<![\p{L}\p{N}_])a\s*#\s*([0-9a-f]{6})(?![\p{L}\p{N}_])(?:\s*[·•]\s*Karte\s+([1-9]\d{0,11})(?![\p{L}\p{N}_]))?|(?<![\p{L}\p{N}_])(?:Karte(?:[\s-]*Nr\.?)?|Card(?:\s*(?:No\.?|Nr\.?))?)\s*#?\s*([1-9]\d{0,11})(?![\p{L}\p{N}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex References();

    // 20 Nov 2026 (Florian): a SELECTION may hold only the bare card number ("20081") or the bare artifact id ("c029b6", "a#c029b6").
    [GeneratedRegex(@"^\s*#?\s*([1-9]\d{3,11})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex BareNumber();
    [GeneratedRegex(@"^\s*(?:a\s*#\s*)?([0-9a-f]{6})\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BareArtifact();

    /// <summary>Like <see cref="Find"/>, plus a selection that is only a card number or only an artifact id.</summary>
    public static string? FindSelected(string selected, out bool ambiguous)
    {
        var found = Find(selected, out ambiguous);
        if (found is not null || ambiguous) return found;
        var n = BareNumber().Match(selected);
        if (n.Success) return "card:" + n.Groups[1].Value;
        var a = BareArtifact().Match(selected);
        // six hex digits only count as an artifact id when they contain a letter (a pure number is a card number)
        if (a.Success && a.Groups[1].Value.Any(char.IsLetter)) return "a#" + a.Groups[1].Value.ToLowerInvariant();
        return null;
    }

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
