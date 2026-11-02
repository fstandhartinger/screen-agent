using System.Text.RegularExpressions;
namespace ScreenAgent.Core;
public static partial class ArtifactMatcher
{
    // Unicode letter/digit boundaries: underscores are valid delimiters, é is not.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])[aAäÄ@]\s?[#＃]\s?([0-9a-fA-FoOlLiI|sSgGzZqQ]{6})(?![\p{L}\p{N}])")]
    private static partial Regex Candidates();
    // Faint 11 px badges: the # reads as *, +, H, 4 or vanishes into "aff"/"at", and one gap can land inside the hex part. The registry rejects what is wrong,
    // so this list may be generous; it only runs on OCR text, never on anything a person typed.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])[aAäÄ@oe8]\s?(?:[#＃*+%HfFtT4]{1,2})\s?([0-9a-fA-FoOlLiI|sSgGzZqQ]{1,5}\s?[0-9a-fA-FoOlLiI|sSgGzZqQ]{1,5})(?![\p{L}\p{N}])")]
    private static partial Regex Loose();
    [GeneratedRegex("^[0-9a-fA-F]{6}$")] private static partial Regex Exact();
    public static string[] Find(string text) => Candidates().Matches(text).Cast<Match>()
        .Select(m => m.Groups[1].Value).OrderBy(s => Exact().IsMatch(s) ? 0 : 1)
        .Select(s => "a#" + string.Concat(s.Select(c => char.ToLowerInvariant(c) switch
        { 'o' => '0', 'l' or 'i' or '|' => '1', 's' => '5', 'g' or 'q' => '9', 'z' => '2', var x => x })))
        .Distinct(StringComparer.Ordinal).Take(20).ToArray();
    public static string[] FindLoose(string text) => Find(text).Concat(Loose().Matches(text).Cast<Match>()
        .Select(m => m.Groups[1].Value.Replace(" ","")).Where(s => s.Length==6).Select(s => "a#" + Normalize(s)))
        .Distinct(StringComparer.Ordinal).Take(60).ToArray();
    static string Normalize(string s) => string.Concat(s.Select(c => char.ToLowerInvariant(c) switch
        { 'o' => '0', 'l' or 'i' or '|' => '1', 's' => '5', 'g' or 'q' => '9', 'z' => '2', var x => x }));
}
