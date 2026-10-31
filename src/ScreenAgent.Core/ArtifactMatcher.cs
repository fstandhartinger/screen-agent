using System.Text.RegularExpressions;
namespace ScreenAgent.Core;
public static partial class ArtifactMatcher
{
    // Unicode letter/digit boundaries: underscores are valid delimiters, é is not.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])[aAäÄ@]\s?[#＃]\s?([0-9a-fA-FoOlLiI|sSgGzZqQ]{6})(?![\p{L}\p{N}])")]
    private static partial Regex Candidates();
    [GeneratedRegex("^[0-9a-fA-F]{6}$")] private static partial Regex Exact();
    public static string[] Find(string text) => Candidates().Matches(text).Cast<Match>()
        .Select(m => m.Groups[1].Value).OrderBy(s => Exact().IsMatch(s) ? 0 : 1)
        .Select(s => "a#" + string.Concat(s.Select(c => char.ToLowerInvariant(c) switch
        { 'o' => '0', 'l' or 'i' or '|' => '1', 's' => '5', 'g' or 'q' => '9', 'z' => '2', var x => x })))
        .Distinct(StringComparer.Ordinal).Take(20).ToArray();
}
