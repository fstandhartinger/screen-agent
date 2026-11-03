using System.Text.RegularExpressions;
namespace ScreenAgent.Core;
// Florian's decision (2 Nov 2026): no app-specific fences by default. The only thing left is what the user puts into
// BlockProcesses / BlockDomains / BlockTitlePatterns in config.json; all three are empty by default and then nothing is looked up per window.
public sealed class PrivacyPolicy
{
    public string[] ExtraProcesses { get; init; } = [];
    public string[] ExtraDomains { get; init; } = [];
    public string[] ExtraTitlePatterns { get; init; } = [];
    // False with the default config: no window enumeration, no UI Automation walk per frame.
    public bool HasLists => ExtraProcesses.Length>0||ExtraDomains.Length>0||ExtraTitlePatterns.Length>0;
    // A browser address is only read for windows other than the foreground one when a domain list exists.
    public bool NeedsAddress(string process) => ExtraDomains.Length>0&&Browser(process);
    public static bool Browser(string process) => new[]{"chrome","msedge","firefox"}.Contains(process, StringComparer.OrdinalIgnoreCase);
    // An unreadable address never blocks: the list is the user's, not a fence.
    public bool Blocked(string process,string title,string? url=null)
    {
        if(ExtraProcesses.Any(p=>process.StartsWith(p,StringComparison.OrdinalIgnoreCase))) return true;
        if(ExtraTitlePatterns.Any(p=>TitleMatches(title,p))) return true;
        return Uri.TryCreate(url,UriKind.Absolute,out var uri)&&(uri.Scheme=="https"||uri.Scheme=="http")&&
            ExtraDomains.Any(d=>uri.Host.Equals(d,StringComparison.OrdinalIgnoreCase)||uri.Host.EndsWith("."+d,StringComparison.OrdinalIgnoreCase));
    }
    static bool TitleMatches(string title,string pattern)
    {
        try{return Regex.IsMatch(title,pattern,RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(50));}
        catch(Exception e) when(e is ArgumentException or RegexMatchTimeoutException){return title.Contains(pattern,StringComparison.OrdinalIgnoreCase);}   /* bad pattern: plain substring */
    }
    // Lock screen, sign-in and UAC consent run on the secure desktop; there is simply no frame. Start menu, Alt-Tab and the shell are ordinary windows.
    public static bool LockedHost(string process) => new[]{"LockApp","LogonUI","consent"}.Contains(process,StringComparer.OrdinalIgnoreCase);
}
