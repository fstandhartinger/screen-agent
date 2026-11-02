using System.Numerics;
namespace ScreenAgent.Core;
// 64-bit difference hash: the screen is reduced to a 9x8 luma grid, each bit says "this cell is brighter than its right neighbour".
public static class DHash
{
    public const int Width=9,Height=8;
    public static ulong Compute(ReadOnlySpan<float> luma)
    {
        if(luma.Length!=Width*Height)throw new ArgumentException("9x8 grid expected");
        ulong hash=0;
        for(int y=0;y<Height;y++)for(int x=0;x<Width-1;x++)if(luma[y*Width+x]>luma[y*Width+x+1])hash|=1UL<<(y*(Width-1)+x);
        return hash;
    }
    public static int Distance(ulong a,ulong b)=>BitOperations.PopCount(a^b);
}
public enum FrameReason { Start, Interval, Foreground, Fresh }
public enum FrameVerdict { Wait, Send, Deduped }
public readonly record struct FrameDecision(FrameVerdict Verdict,FrameReason Reason);
// Decides when a screenshot is taken and whether it goes out: one at the start, then every interval and at once on a window switch,
// but only when the picture changed (hash distance above the threshold). A fresh request from the voice agent always sends.
// MinGapMs keeps us below the gateway's 30 frames/min.
public sealed class FramePlanner(int intervalMs=4000,int threshold=4,int minGapMs=2000)
{
    bool started;
    long lastCaptureAt,lastSentAt;
    ulong lastHash;
    long lastForeground;
    public int IntervalMs=>intervalMs;
    public int Threshold=>threshold;
    public bool Started=>started;
    public void Reset(){started=false;lastCaptureAt=lastSentAt=0;lastHash=0;lastForeground=0;}
    // Is it time to look at the screen at all? Cheap: no capture needed to answer.
    public bool CaptureDue(long now,long foreground,bool fresh)=>fresh||!started||foreground!=lastForeground&&now-lastCaptureAt>=minGapMs||now-lastCaptureAt>=intervalMs;
    public void Captured(long now,long foreground){lastCaptureAt=now;lastForeground=foreground;}
    public FrameDecision Decide(long now,ulong hash,long foreground,bool fresh)
    {
        if(fresh)return new(FrameVerdict.Send,FrameReason.Fresh);
        if(!started)return new(FrameVerdict.Send,FrameReason.Start);
        bool gap=now-lastSentAt>=minGapMs;
        if(foreground!=lastSentForeground)return gap?new(FrameVerdict.Send,FrameReason.Foreground):new(FrameVerdict.Wait,FrameReason.Foreground);
        if(DHash.Distance(hash,lastHash)>threshold)return gap?new(FrameVerdict.Send,FrameReason.Interval):new(FrameVerdict.Wait,FrameReason.Interval);
        return new(FrameVerdict.Deduped,FrameReason.Interval);
    }
    long lastSentForeground;
    public void Sent(long now,ulong hash,long foreground){started=true;lastSentAt=now;lastHash=hash;lastSentForeground=foreground;}
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
