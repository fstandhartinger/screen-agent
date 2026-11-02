using ScreenAgent.Core;
using System.Text.Json;
using Xunit;
namespace ScreenAgent.Tests;
public class FrameTests
{
    static float[] Grid(Func<int,int,float> f){var g=new float[72];for(int y=0;y<8;y++)for(int x=0;x<9;x++)g[y*9+x]=f(x,y);return g;}
    [Fact] public void FlatScreenHashesToZeroAndGradientToAllOnes()
    {
        Assert.Equal(0UL,DHash.Compute(Grid((_,_)=>.5f)));
        Assert.Equal(ulong.MaxValue,DHash.Compute(Grid((x,_)=>1-x/9f)));
        Assert.Equal(0UL,DHash.Compute(Grid((x,_)=>x/9f)));
        Assert.Throws<ArgumentException>(()=>DHash.Compute(new float[10]));
    }
    [Fact] public void DistanceCountsDifferingBits()
    {
        Assert.Equal(0,DHash.Distance(5,5));Assert.Equal(64,DHash.Distance(0,ulong.MaxValue));Assert.Equal(2,DHash.Distance(0b1010,0));
    }
    [Fact] public void TinyChangeStaysUnderTheThresholdBigChangeDoesNot()
    {
        var a=DHash.Compute(Grid((x,y)=>((x*7+y*3)%5)/5f));
        var tiny=DHash.Compute(Grid((x,y)=>x==3&&y==2?.9f:((x*7+y*3)%5)/5f));
        var other=DHash.Compute(Grid((x,y)=>((x*3+y*11)%7)/7f));
        Assert.True(DHash.Distance(a,tiny)<=4);Assert.True(DHash.Distance(a,other)>4);
    }
    [Fact] public void FirstFrameIsStartThenUnchangedScreenIsDeduped()
    {
        var p=new FramePlanner();
        Assert.True(p.CaptureDue(0,1,false));
        var d=p.Decide(0,10,1,false);Assert.Equal((FrameVerdict.Send,FrameReason.Start),(d.Verdict,d.Reason));
        p.Captured(0,1);p.Sent(0,10,1);
        Assert.False(p.CaptureDue(1000,1,false));
        Assert.True(p.CaptureDue(4000,1,false));
        Assert.Equal(FrameVerdict.Deduped,p.Decide(4000,10,1,false).Verdict);
        Assert.Equal(FrameVerdict.Deduped,p.Decide(4000,10^3UL,1,false).Verdict);   // 2 bits: under the threshold
    }
    [Fact] public void ChangedPictureSendsAsInterval()
    {
        var p=new FramePlanner();p.Captured(0,1);p.Sent(0,0,1);
        var d=p.Decide(4000,0xFFFF,1,false);Assert.Equal((FrameVerdict.Send,FrameReason.Interval),(d.Verdict,d.Reason));
    }
    [Fact] public void ForegroundChangeIsDueAtOnceAndSendsEvenWithSamePicture()
    {
        var p=new FramePlanner();p.Captured(0,1);p.Sent(0,7,1);
        Assert.False(p.CaptureDue(1000,2,false));        // too soon: rate limit
        Assert.True(p.CaptureDue(2000,2,false));
        var d=p.Decide(2000,7,2,false);Assert.Equal((FrameVerdict.Send,FrameReason.Foreground),(d.Verdict,d.Reason));
        Assert.Equal(FrameVerdict.Wait,p.Decide(1000,7,2,false).Verdict);
    }
    [Fact] public void FreshRequestAlwaysSendsAndIsAlwaysDue()
    {
        var p=new FramePlanner();p.Captured(0,1);p.Sent(0,7,1);
        Assert.True(p.CaptureDue(10,1,true));
        var d=p.Decide(10,7,1,true);Assert.Equal((FrameVerdict.Send,FrameReason.Fresh),(d.Verdict,d.Reason));
    }
    [Fact] public void ResetStartsOverWithAStartFrame()
    {
        var p=new FramePlanner();p.Captured(0,1);p.Sent(0,7,1);p.Reset();
        Assert.True(p.CaptureDue(1,1,false));Assert.Equal(FrameReason.Start,p.Decide(1,7,1,false).Reason);
    }
    [Fact] public void NeverMoreThanThirtyFramesPerMinute()
    {
        var p=new FramePlanner();int sent=0;ulong h=0;long fg=0;
        for(long t=0;t<60000;t+=100){fg=t/300;h=~h;if(p.CaptureDue(t,fg,false)){p.Captured(t,fg);if(p.Decide(t,h,fg,false).Verdict==FrameVerdict.Send){p.Sent(t,h,fg);sent++;}}}
        Assert.InRange(sent,1,30);
    }
    [Theory][InlineData(3840,2160,1600,900)][InlineData(1600,900,1600,900)][InlineData(1366,768,1366,768)][InlineData(2560,1440,1600,900)]
    public void FitsWidth(int w,int h,int ew,int eh)=>Assert.Equal((ew,eh),FrameSizing.Fit(w,h));
    [Fact] public void ShrinkNeverGoesBelowAFloor(){var s=(W:100,H:100);for(int i=0;i<30;i++)s=FrameSizing.Shrink(s.W,s.H);Assert.Equal((64,64),s);}
    [Fact] public void TraceNeverCarriesTextImagesOrLongStrings()
    {
        var c=TraceSanitizer.Clean(new(){["decision"]="sent",["bytes"]=1234,["ocrText"]="secret",["jpegBase64"]="x",["windowTitle"]="Bank",["process"]=new string('p',500),["ids"]=new[]{"a1b2c3"}})!;
        Assert.Equal("sent",c["decision"]);Assert.Equal(1234,c["bytes"]);Assert.False(c.ContainsKey("ocrText"));Assert.False(c.ContainsKey("jpegBase64"));Assert.False(c.ContainsKey("windowTitle"));
        Assert.Equal(120,((string)c["process"]!).Length);Assert.Equal(new[]{"a1b2c3"},(string[])c["ids"]!);
    }
    [Fact] public void BufferDropsOldestAndRequeuesInOrder()
    {
        var b=new TraceBuffer(3);for(int i=0;i<5;i++)b.Add("e"+i);
        Assert.Equal(2,b.Dropped);var batch=b.Take(2);Assert.Equal(["e2","e3"],batch.Select(e=>e.type));
        b.Requeue(batch);Assert.Equal(["e2","e3","e4"],b.Take(10).Select(e=>e.type));
    }
    [Fact] public void LogRollsAtTheLimitAndKeepsTwoFiles()
    {
        string dir=Path.Combine(Path.GetTempPath(),"sa-log-"+Guid.NewGuid());
        try
        {
            var log=new RollingLog(dir,2000);
            for(int i=0;i<200;i++)log.Write(new(1,"frame.sent",new(){["n"]=i}));
            Assert.True(File.Exists(log.Current)&&File.Exists(log.Previous));
            Assert.Equal(2,Directory.GetFiles(dir).Length);
            Assert.All(new[]{log.Current,log.Previous},f=>Assert.True(new FileInfo(f).Length<=2000));
            string last=File.ReadAllLines(log.Current).Last();Assert.Equal(199,JsonDocument.Parse(last).RootElement.GetProperty("data").GetProperty("n").GetInt32());
        }
        finally{try{Directory.Delete(dir,true);}catch{}}
    }
}
