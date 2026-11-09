using System.Text.RegularExpressions;
namespace ScreenAgent.Core;
// Memory-only speech context. Keep UIA spelling and line boundaries; never send secret-looking values.
public static class ScreenTextLogic
{
    static readonly Regex Space=new(@"\s+",RegexOptions.Compiled,TimeSpan.FromMilliseconds(50));
    static readonly Regex Code=new(@"(?i)(\b(?:code|pin|otp|einmalcode|sicherheitscode)\s*[:=]\s*)\d{6}\b",RegexOptions.Compiled,TimeSpan.FromMilliseconds(50));
    static string Key(string line)=>string.Concat(line.Where(c=>!char.IsWhiteSpace(c))).ToUpperInvariant();
    public static string Tidy(string? raw)
    {
        if(string.IsNullOrWhiteSpace(raw))return "";
        try
        {
            raw=new string(raw.Where(c=>!char.IsControl(c)||c is '\n' or '\r' or '\t').ToArray());
            raw=Code.Replace(SecretText.Redact(SecretFilter.Redact(raw)),"$1"+SecretText.Hidden);
            var lines=new List<string>();var seen=new HashSet<string>(StringComparer.Ordinal);int size=0;
            foreach(var part in raw.Replace("\r\n","\n").Replace('\r','\n').Split('\n'))
            {
                string line=Space.Replace(part," ").Trim();
                if(line.Length<3||!line.Any(char.IsLetter)||!seen.Add(line))continue;
                int next=size+line.Length+(lines.Count>0?1:0);if(next>6000)break;
                lines.Add(line);size=next;
            }
            return string.Join("\n",lines);
        }
        catch(RegexMatchTimeoutException){ /* Fail closed: speech context is optional. */ return "";}
    }
    public static string Merge(string? uia,string? ocr)
    {
        var first=Tidy(uia).Split('\n',StringSplitOptions.RemoveEmptyEntries).ToList();
        var keys=first.Select(Key).ToList();
        foreach(var line in Tidy(ocr).Split('\n',StringSplitOptions.RemoveEmptyEntries))
        {
            string key=Key(line);if(keys.Any(k=>k.Contains(key,StringComparison.Ordinal)))continue;
            first.Add(line);keys.Add(key);
        }
        return Tidy(string.Join("\n",first));
    }
    public static bool Changed(string? previousText,string text)
    {
        var before=Tidy(previousText).Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(Key).ToHashSet();
        var after=Tidy(text).Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(Key).ToHashSet();
        int total=Math.Max(before.Count,after.Count);
        return total>0&&before.Intersect(after).Count()<total*.9;
    }
}
