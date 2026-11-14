namespace ScreenAgent.Core;
// The live captions of ONE dictation. The caption host feeds every dictation that is still live (or ended a moment ago, for answers that are on their way) its own
// stitcher, so the fallback text of a failed chunk is never what he said to the agent afterwards or in the next dictation.
public sealed class DictationCaptions(string id="",long startedMs=0)
{
    readonly CaptionStitcher stitch=new();readonly object gate=new();
    long until=long.MaxValue;
    // v0.1.16: what the subtitle of this dictation showed (CaptionHost holds its own lock around these) and the raw windows for a refinement keep.
    public string Id {get;}=id;
    public long StartedMs {get;}=startedMs;
    public LiveCaption Live {get;}=new();
    public CaptionLog Log {get;}=new();
    public List<(long Ms,double AudioEnd,string Text)> Windows {get;}=[];
    public int Seq;
    public void Add(string window){lock(gate)stitch.Add(window);}
    public int Mark(){lock(gate)return stitch.Mark();}
    public string Slice(int from,int to){lock(gate)return stitch.Slice(from,to);}
    public string Text{get{lock(gate)return stitch.Text;}}
    // The dictation ended at `now`: answers for audio sent up to graceMs later still belong to it.
    public void End(long now,long graceMs)=>Interlocked.Exchange(ref until,now+graceMs);
    public bool Accepts(long now)=>now<=Interlocked.Read(ref until);
}
// Audio that never arrived: the capture delivers fewer seconds than the wall clock passed (a stalled device or callback, a full queue). Returns the seconds missing
// so far when that grew by more than the tolerance since the last report.
public sealed class GapWatch(double toleranceSeconds=0.5)
{
    double reported;
    public double? Check(double wallSeconds,double audioSeconds)
    {
        double missing=wallSeconds-audioSeconds;
        if(missing<=reported+toleranceSeconds)return null;
        reported=missing;return missing;
    }
}

// The end of a dictation's subtitle for the trace (caption.summary): the live text and the typed text side by side, and the measures of CaptionLog.
public static class CaptionSummary
{
    // The measures (cheap, under the host's lock); the live text comes out for the comparison and the text parts, which run outside the lock.
    public static Dictionary<string,object?> Build(DictationCaptions d,string typed,long nowMs,out string live)
    {
        var log=d.Log;live=string.Join(' ',d.Live.Display());
        return new()
        {
            ["id"]=d.Id,["ms"]=nowMs-d.StartedMs,["windows"]=d.Live.Windows,["appended"]=d.Live.Appended,["updates"]=log.Updates,
            ["firstShownMs"]=log.FirstShownMs,["changed"]=log.Changed,["changedWindow"]=log.ChangedBy['w'],["changedPolish"]=log.ChangedBy['p'],["changedChunk"]=log.ChangedBy['c'],
            ["changedLate"]=log.ChangedLate,["correctionAgeMs"]=log.MedianCorrectionAgeMs,["repeatsShown"]=log.RepeatsShown,["repeatsLive"]=Disfluency.Repeats(StitchWords.Split(live)),
            ["repeatsRaw"]=Disfluency.Repeats(d.Live.Words),["repeatsTyped"]=Disfluency.Repeats(StitchWords.Split(typed)),
            ["polishAsked"]=log.PolishAsked,["polishUsed"]=log.PolishUsed,["polishDropped"]=log.PolishDropped,["polishFailed"]=log.PolishFailed,["chunks"]=log.Chunks,["chunksPlaced"]=log.ChunksPlaced,
            ["__live"]=live
        };
    }
    // Live against typed text: on the last 1,500 words of each (the alignment is quadratic; a 60-minute dictation must not cost seconds and hundreds of MB).
    public static void AddComparison(Dictionary<string,object?> data,string live,string typed)
    {
        static string Last(string t){var w=StitchWords.Split(t);return w.Length<=1500?t:string.Join(' ',w[^1500..]);}
        var (missing,extra,finalWords,examples)=CaptionLog.Compare(Last(live),Last(typed));
        data["liveWords"]=StitchWords.Split(live).Length;data["typedWords"]=StitchWords.Split(typed).Length;data["missingInTyped"]=missing;data["extraInTyped"]=extra;data["diffs"]=examples;
        if(finalWords<StitchWords.Split(typed).Length)data["comparedWords"]=finalWords;
    }
    // The two texts themselves in parts of at most 3,500 characters (the Desk keeps an event of at most 8 KB): caption.live {id, n, of, transcript} and
    // caption.typed {id, n, of, text}; at most 12 parts each (a longer dictation keeps its end).
    public static List<(string Type,Dictionary<string,object?> Data)> Texts(string id,string live,string typed)
    {
        var outp=new List<(string,Dictionary<string,object?>)>();
        void Parts(string type,string key,string text)
        {
            var parts=new List<string>();
            for(int i=0;i<text.Length;i+=3500)parts.Add(text.Substring(i,Math.Min(3500,text.Length-i)));
            if(parts.Count>12)parts=parts[^12..];
            for(int n=0;n<parts.Count;n++)outp.Add((type,new(){["id"]=id,["n"]=n+1,["of"]=parts.Count,[key]=parts[n]}));
        }
        Parts("caption.live","transcript",live);
        Parts("caption.typed","text",typed);
        return outp;
    }
}
