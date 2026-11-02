namespace ScreenAgent.Core;
public enum FrameReason { Start, Interval, Foreground, Fresh }
public enum FrameVerdict { Wait, Send, Deduped }
public readonly record struct FrameDecision(FrameVerdict Verdict,FrameReason Reason);
// Decides when a screenshot is taken and whether it goes out: one at the start, then every interval and at once on a window switch,
// but only when the picture changed against the last frame that was SENT (ChangeDetector). A skipped, failed, rejected or capped frame
// never becomes the reference. A fresh request from the voice agent always sends. MinGapMs keeps us below the gateway's 30 frames/min.
// A change that has to wait for the gap makes the next capture due as soon as the gap is over, so the agent's picture is at most one interval old.
public sealed class FramePlanner(int intervalMs=4000,int minGapMs=2000)
{
    bool started,pending;
    long lastCaptureAt,lastSentAt,lastSentForeground;
    byte[]? lastSent;
    public int IntervalMs=>intervalMs;
    public bool Started=>started;
    // A video or animation changes every picture: after 6 sends in a row without a quiet one the gap doubles, up to 16 s, so the hourly budget is not gone in minutes. A quiet frame resets it. A direct question ignores it.
    int busyRun;
    long Gap=>busyRun<6?minGapMs:Math.Min(16000,minGapMs*(1L<<Math.Min(3,busyRun/6)));
    public void Reset(){started=false;pending=false;busyRun=0;lastCaptureAt=lastSentAt=lastSentForeground=0;lastSent=null;}
    // Is it time to look at the screen at all? Cheap: no capture needed to answer.
    public bool CaptureDue(long now,long foreground,bool fresh)=>fresh||!started||foreground!=lastSentForeground&&now-lastCaptureAt>=minGapMs||pending&&now-lastSentAt>=Gap||now-lastCaptureAt>=intervalMs;
    public void Captured(long now,long foreground){lastCaptureAt=now;}
    public FrameDecision Decide(long now,byte[] grid,long foreground,bool fresh)
    {
        if(fresh)return new(FrameVerdict.Send,FrameReason.Fresh);
        if(!started||lastSent is null)return new(FrameVerdict.Send,FrameReason.Start);
        bool gap=now-lastSentAt>=Gap;
        FrameReason reason;
        if(foreground!=lastSentForeground)reason=FrameReason.Foreground;
        else if(ChangeDetector.Changed(lastSent,grid))reason=FrameReason.Interval;
        else{pending=false;busyRun=0;return new(FrameVerdict.Deduped,FrameReason.Interval);}
        pending=!gap;
        return new(gap?FrameVerdict.Send:FrameVerdict.Wait,reason);
    }
    // Only a frame the gateway accepted is the new reference.
    public void Sent(long now,byte[] grid,long foreground){started=true;pending=false;busyRun++;lastSentAt=now;lastSent=grid;lastSentForeground=foreground;}
}
// JPEG sizing: at most 1600 px wide, then a quality ladder until the file fits the gateway's 300 KB limit (aim 150-300 KB).
public static class FrameSizing
{
    public const int MaxWidth=1600,MaxBytes=300*1024;
    public static readonly int[] Qualities=[80,70,60,50,40];
    public static (int W,int H) Fit(int width,int height,int maxWidth=MaxWidth)
    {
        if(width<=0||height<=0)return (1,1);
        if(width<=maxWidth)return (width,height);
        return (maxWidth,Math.Max(1,(int)Math.Round(height*(double)maxWidth/width)));
    }
    // After a quality step is exhausted, shrink by 15 % and start the ladder again.
    public static (int W,int H) Shrink(int w,int h)=>(Math.Max(64,(int)(w*.85)),Math.Max(64,(int)(h*.85)));
}
