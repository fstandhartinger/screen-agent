using System.Text;
namespace ScreenAgent.Core;
// The sentences the app builds for an R2 ask. Page words (a field's value, a path, a recipient) are always quoted and marked as page text, with control characters and quote marks removed,
// so a page cannot make its text read like the question's own words (the voice model and he both hear it as a quotation).
public static class ComputerSpeech
{
    public const int FieldWords=15,MaxFields=6;
    static char Clean(char c)
    {
        if(char.IsControl(c)||c=='\u2028'||c=='\u2029')return ' ';
        return "\"„“”‚‘’«»".IndexOf(c)>=0?'\'':c;
    }
    // One quoted piece of page text: „…“, no control characters, no quote marks inside, at most `words` words.
    public const int WordChars=40;
    public static string Quote(string? s,int words=FieldWords)
    {
        // A key or token on the page ("Feld token: ghp_…") must not be read to him, nor travel to the voice vendor: secret-looking values come out as "ausgeblendet" first.
        var sb=new StringBuilder();
        foreach(char c in SecretText.Redact(s))sb.Append(Clean(c));
        var w=sb.ToString().Split(' ',StringSplitOptions.RemoveEmptyEntries);
        string t=string.Join(" ",w.Take(words).Select(x=>x.Length>WordChars?x[..WordChars]+"…":x))+(w.Length>words?" …":"");
        return "„"+t+"“";
    }
    // "Feld „to“: „chef@x.org“; Feld „subject“: „…“". Search boxes are left out by the caller; the first six non-empty fields, all of them quoted.
    public static string FormLine(IEnumerable<(string Name,string Value)> fields,IEnumerable<string>? recipients=null,bool messaging=false)
    {
        var all=fields.Where(f=>!string.IsNullOrWhiteSpace(f.Value)).ToList();
        var parts=all.Take(MaxFields).Select(f=>"Feld "+Quote(Labels.Clean(f.Name),4)+": "+Quote(f.Value)).ToList();
        var allRc=(recipients??[]).Where(r=>!string.IsNullOrWhiteSpace(r)).Distinct().ToList();
        var rc=allRc.Take(6).Select(r=>Quote(r,1)).ToList();
        var sb=new StringBuilder();
        if(parts.Count>0)sb.Append(" Seitentext, nicht von dir gesprochen: ").Append(string.Join("; ",parts)).Append('.');
        if(all.Count>MaxFields)sb.Append(" Und ").Append(all.Count-MaxFields).Append(" weitere Felder, die nicht vorgelesen werden.");
        if(rc.Count>0)sb.Append(" Empfänger laut Seite: ").Append(string.Join(", ",rc)).Append('.');
        if(allRc.Count>6)sb.Append(" Und ").Append(allRc.Count-6).Append(" weitere Empfänger.");
        if(messaging)sb.Append(" Hinweis: Auf dieser Seite können Empfänger oder Text unvollständig erkannt sein.");
        return sb.ToString();
    }
    // The part of a URL beyond the host, quoted and cut, so data in a path or a query is heard before he says yes.
    public static string PathPart(string url)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var u))return "";
        string p=u.AbsolutePath+u.Query+u.Fragment;
        if(p.Length<=1)return "";
        return " Pfad "+Quote(p.Length>120?p[..120]+"…":p,1);
    }
    // The browser window title is "<tab title> - Google Chrome" (a profile name may sit between): the tab title plus a short suffix, not any prefix of it.
    public static bool TabTitleMatches(string window,string tab)
    {
        if(tab.Length==0||!window.StartsWith(tab,StringComparison.Ordinal))return false;
        var rest=window[tab.Length..];
        return rest.Length==0||(rest.Length<=48&&(rest.StartsWith(" - ",StringComparison.Ordinal)||rest.StartsWith(" – ",StringComparison.Ordinal)||rest.StartsWith(" — ",StringComparison.Ordinal)));
    }
    /// <summary>The longest question the gateway accepts is 1200 characters; the app cuts its own text before that (the tail is the part about taint, so the cut takes the middle).</summary>
    public const int MaxSpoken=1100;
    public static string Fit(string s)=>s.Length<=MaxSpoken?s:s[..(MaxSpoken/2)]+" … "+s[^(MaxSpoken/2-5)..];
    public static string Cap(string s,int n)=>s.Length<=n?s:s[..Math.Max(0,n-1)]+"…";
}
// Which windows lie above a target window and cover part of it (z-order from the top, the target excluded).
public static class WindowCover
{
    public readonly record struct Box(int Left,int Top,int Right,int Bottom)
    {
        public bool Empty=>Right<=Left||Bottom<=Top;
        public bool Intersects(Box o)=>!Empty&&!o.Empty&&Left<o.Right&&o.Left<Right&&Top<o.Bottom&&o.Top<Bottom;
    }
    public static bool Covered(Box target,IEnumerable<Box> above)=>above.Any(b=>b.Intersects(target));
}
