using System.Text.Json;
namespace ScreenAgent.Core;
// The microphone level for the waveform: filled from the audio thread, read by the 25 fps overlay timer.
public sealed class LevelRing(int capacity=64)
{
    public const int SliceBytes=Wav.BytesPerSecond*40/1000;   // one bar = 40 ms
    readonly double[] values=new double[capacity];
    int next,count;
    public void Add(double rms){lock(values){values[next]=rms;next=(next+1)%values.Length;if(count<values.Length)count++;}}
    // The audio of one block, cut into bars.
    public void AddPcm(ReadOnlySpan<byte> pcm)
    {
        for(int at=0;at+1<pcm.Length;at+=SliceBytes)
        {
            var slice=pcm.Slice(at,Math.Min(SliceBytes,pcm.Length-at)&~1);
            if(slice.Length>0)Add(AudioLevel.Rms(System.Runtime.InteropServices.MemoryMarshal.Cast<byte,short>(slice)));
        }
    }
    public double[] Last(int n)
    {
        lock(values)
        {
            int take=Math.Min(n,count);var o=new double[take];
            for(int i=0;i<take;i++)o[i]=values[(next-take+i+values.Length*2)%values.Length];
            return o;
        }
    }
    // Speech sits far below full scale, so the square root keeps normal talking clearly visible and a loud word from filling everything.
    public static double BarHeight(double rms,double min,double max)=>Math.Clamp(min+(max-min)*Math.Sqrt(Math.Clamp(rms/6000,0,1)),min,max);
}
// One word of the dictionary as the Desk returns it.
public sealed record DictionaryEntry(string Term,string[] ReplaceFrom,string Source)
{
    public static DictionaryEntry[] Parse(string json)
    {
        using var document=JsonDocument.Parse(json);var root=document.RootElement;
        if(root.ValueKind==JsonValueKind.Object&&(root.TryGetProperty("items",out var items)||root.TryGetProperty("terms",out items)))root=items;
        if(root.ValueKind!=JsonValueKind.Array)return [];
        var o=new List<DictionaryEntry>();
        foreach(var e in root.EnumerateArray())
        {
            if(e.ValueKind==JsonValueKind.String){o.Add(new(e.GetString()!,[],""));continue;}
            if(e.ValueKind!=JsonValueKind.Object||!e.TryGetProperty("term",out var t)||t.ValueKind!=JsonValueKind.String)continue;
            string[] from=e.TryGetProperty("replace_from",out var f)&&f.ValueKind==JsonValueKind.Array?f.EnumerateArray().Where(v=>v.ValueKind==JsonValueKind.String).Select(v=>v.GetString()!).ToArray():[];
            o.Add(new(t.GetString()!,from,e.TryGetProperty("source",out var s)&&s.ValueKind==JsonValueKind.String?s.GetString()!:""));
        }
        return o.ToArray();
    }
    // "Karpati, Karpaty,, Karpati" -> the two forms.
    public static string[] SplitForms(string text)=>text.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct().ToArray();
}
// What the tray and the history window show of a stored dictation.
public static class DictationHistory
{
    public static string? TextOf(DictationMeta m)=>!string.IsNullOrWhiteSpace(m.Text)?m.Text:string.IsNullOrWhiteSpace(m.PartialText)?null:m.PartialText;
    // Newest first: everything that was really said and is still on disk. A discarded or still running one is not listed.
    public static List<DictationMeta> Recent(IEnumerable<DictationMeta> all,int max=50)=>all.Where(m=>m.State is "done" or "failed").OrderByDescending(m=>m.Started).Take(max).ToList();
    public static string Line(DictationMeta m)
    {
        string? t=TextOf(m);
        string head=$"{m.Started.ToLocalTime():dd.MM. HH:mm} · {(int)(m.Seconds/60)}:{(int)m.Seconds%60:00}";
        if(t is null)return head+(m.State=="failed"?" · nicht verarbeitet":"");
        t=t.ReplaceLineEndings(" ");
        return head+" · "+(t.Length<=26?t:t[..26]+"…");
    }
}
