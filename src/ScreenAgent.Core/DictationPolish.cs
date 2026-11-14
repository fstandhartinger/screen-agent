namespace ScreenAgent.Core;
// v0.1.12 (Florian, 9 Nov 2026): a fast model with the glossary of his world cleans up what was heard (misheard names, fillers, full stops in the
// middle of a sentence, speaking grammar, spoken self-corrections) and stays close to his words. The gateway does it per chunk already; this is
// the rule for the one extra pass over the whole dictation, and the guard that keeps a clean-up that lost his words from replacing the text.
public static class DictationPolish
{
    static readonly HashSet<string> Fillers=new(StringComparer.OrdinalIgnoreCase){"äh","ähh","ähm","ähmm","öh","öhm","hm","hmm","mhm","ehm","uh","uhm"};   // not "um": a German word
    // Several chunks (their seams and corrections across them) or a chunk whose clean-up failed: one pass over the whole text. One clean chunk is done already.
    public static bool Wanted(DictationOutcome o)=>o.Complete&&!string.IsNullOrWhiteSpace(o.Text)&&(o.Chunks>=2||o.Degraded);
    // v0.1.13: which pass the text gets at the gateway. "final": several chunks or a degraded one (seams, corrections across chunks), with light structure when
    // format is on. "format": one clean chunk that is long enough to deserve line breaks (the gateway only places breaks, the words stay). null: nothing.
    public const int FormatWords=45;
    public static string? Mode(DictationOutcome o,bool polish,bool format)
    {
        if(!o.Complete||string.IsNullOrWhiteSpace(o.Text))return null;
        if(polish&&Wanted(o))return "final";
        return format&&Words(o.Text)>=FormatWords?"format":null;
    }
    // How long he waits for it at most: the windows run side by side at the gateway, so it hardly grows with the length.
    public static TimeSpan Budget(string text)=>TimeSpan.FromMilliseconds(Math.Min(9000,4000+text.Length*1.5));
    public static int Words(string? text)=>(text??"").Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries)
        .Select(StitchWords.Key).Count(k=>k.Length>0&&!Fillers.Contains(k));
    // His words stay: fillers and spoken corrections may go, but not a quarter of the content, and nothing may be added on a large scale.
    public static bool Accept(string before,string? after)
    {
        if(string.IsNullOrWhiteSpace(after))return false;
        int a=Words(before),b=Words(after);
        return b>=0.75*a&&b<=1.3*a+4;
    }
}
