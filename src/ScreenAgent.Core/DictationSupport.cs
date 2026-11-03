namespace ScreenAgent.Core;
// The live captions of ONE dictation. The caption host feeds every dictation that is still live (or ended a moment ago, for answers that are on their way) its own
// stitcher, so the fallback text of a failed chunk is never what he said to the agent afterwards or in the next dictation.
public sealed class DictationCaptions
{
    readonly CaptionStitcher stitch=new();readonly object gate=new();
    long until=long.MaxValue;
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
