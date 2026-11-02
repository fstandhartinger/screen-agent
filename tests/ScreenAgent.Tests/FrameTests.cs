using ScreenAgent.Core;
using System.Text.Json;
using Xunit;
namespace ScreenAgent.Tests;
public class FrameTests
{
    const int W=ChangeDetector.Width,H=ChangeDetector.Height;
    // A quiet page: light grey with a faint texture, 320x180 like the real downsample.
    static byte[] Page(){var g=new byte[W*H];for(int i=0;i<g.Length;i++)g[i]=(byte)(200+(i*7)%5);return g;}
    static byte[] With(byte[] g,int x0,int y0,int w,int h,int delta){var c=(byte[])g.Clone();for(int y=y0;y<y0+h;y++)for(int x=x0;x<x0+w;x++)c[y*W+x]=(byte)Math.Clamp(c[y*W+x]+delta,0,255);return c;}
    // A 1920x1080 screenshot as BGRA, white, to run the real downsample on.
    static byte[] Screen(int w=1920,int h=1080){var p=new byte[w*h*4];Array.Fill(p,(byte)255);return p;}
    // Ink in logical 1920-wide pixels, drawn k times larger on a bigger screen (glyphs scale with the display, they do not turn into finer noise).
    static void Ink(byte[] p,int w,int x0,int y0,int bw,int bh,int seed,double density,double k)
    {
        var rnd=new Random(seed);
        for(int ly=0;ly<bh;ly++)for(int lx=0;lx<bw;lx++)if(rnd.NextDouble()<density)
            for(int y=(int)((y0+ly)*k);y<(int)((y0+ly+1)*k);y++)for(int x=(int)((x0+lx)*k);x<(int)((x0+lx+1)*k);x++){int i=(y*w+x)*4;p[i]=p[i+1]=p[i+2]=0;}
    }
    [Fact] public void IdenticalPicturesAndQuietNoiseAreUnchanged()
    {
        var a=Page();Assert.False(ChangeDetector.Changed(a,(byte[])a.Clone()));
        var noise=(byte[])a.Clone();for(int i=0;i<noise.Length;i++)noise[i]=(byte)(noise[i]+(i%3)*5);   // up to 10 levels everywhere: below the level
        Assert.False(ChangeDetector.Changed(a,noise));
        Assert.False(ChangeDetector.Changed(a,With(a,0,0,W,H,15)));   // a slow brightness drift
    }
    [Fact] public void ANumberChangingInACornerCounts()
    {
        var a=Page();
        Assert.True(ChangeDetector.Changed(a,With(a,290,6,7,3,60)));      // 5 digits: ~7x3 grid pixels
        Assert.True(ChangeDetector.Changed(a,With(a,4,170,5,2,-70)));     // bottom-left corner, smaller
        Assert.True(ChangeDetector.Changed(a,With(a,0,0,3,3,-90)));       // right in the corner
    }
    [Fact] public void ANumberOnATileBorderStillCounts()
    {
        var a=Page();   // tiles are 20 px wide: x=16..22 straddles the border at 20, y=17..19 the one at 20
        Assert.True(ChangeDetector.Changed(a,With(a,16,17,7,3,60)));
    }
    [Fact] public void ABlinkingCaretDoesNotCount()
    {
        var a=Page();
        Assert.False(ChangeDetector.Changed(a,With(a,100,50,1,4,-90)));   // 1 px wide, 4 grid px tall
        Assert.False(ChangeDetector.Changed(a,With(a,100,50,2,3,-90)));   // worst case: 2 px wide, 3 tall
    }
    [Fact] public void AWholeNewPageCounts()
    {
        var a=Page();Assert.True(ChangeDetector.Changed(a,With(a,0,0,W,H,-80)));Assert.True(ChangeDetector.ChangedTiles(a,With(a,0,0,W,H,-80))>100);
    }
    [Fact] public void SparseSingleDotsDoNotCount()
    {
        var a=Page();var b=(byte[])a.Clone();for(int y=0;y<H;y+=10)for(int x=0;x<W;x+=10)b[y*W+x]=0;   // 1 dot per 10x10: at most 4 in a tile
        Assert.False(ChangeDetector.Changed(a,b));
    }
    [Fact] public void TheGridMustBeTheRightSize()=>Assert.Throws<ArgumentException>(()=>ChangeDetector.Changed(new byte[10],new byte[10]));
    [Fact] public void DownsampleAveragesTheArea()
    {
        var p=Screen(640,360);var g=ChangeDetector.FromBgra(p,640,360,640*4);
        Assert.Equal(W*H,g.Length);Assert.All(g,v=>Assert.Equal(255,(int)v));   // white: the three weights add up to 256
        for(int y=0;y<360;y++)for(int x=0;x<2;x++){int i=(y*640+x)*4;p[i]=p[i+1]=p[i+2]=0;}   // a 2 px wide black bar on the left = the first grid column
        g=ChangeDetector.FromBgra(p,640,360,640*4);Assert.Equal(0,g[0]);Assert.True(g[1]>250);
        Assert.Throws<ArgumentException>(()=>ChangeDetector.FromBgra(new byte[10],640,360,640*4));
    }
    [Fact] public void RealResolutionFiveDigitNumberCountsAndCaretDoesNot()
    {
        foreach(var (w,h) in new[]{(1920,1080),(2560,1440),(3840,2160)})
        {
            double k=w/1920.0;
            var empty=Screen(w,h);var one=Screen(w,h);var other=Screen(w,h);
            Ink(one,w,1500,60,40,14,1,.45,k);Ink(other,w,1500,60,40,14,2,.45,k);
            var gOne=ChangeDetector.FromBgra(one,w,h,w*4);var gOther=ChangeDetector.FromBgra(other,w,h,w*4);
            Assert.True(ChangeDetector.Changed(gOne,gOther),$"12345 -> 67890 at {w}x{h}");
            Assert.True(ChangeDetector.Changed(ChangeDetector.FromBgra(empty,w,h,w*4),gOne),$"number appears at {w}x{h}");
            foreach(int caretWidth in new[]{1,2})
            {
                var caret=Screen(w,h);Ink(caret,w,700,500,caretWidth,18,3,1.0,k);
                Assert.False(ChangeDetector.Changed(ChangeDetector.FromBgra(empty,w,h,w*4),ChangeDetector.FromBgra(caret,w,h,w*4)),$"caret {caretWidth}px at {w}x{h}");
            }
        }
    }
    [Fact] public void FirstFrameIsStartThenUnchangedScreenIsDeduped()
    {
        var p=new FramePlanner();var a=Page();
        Assert.True(p.CaptureDue(0,1,false));
        var d=p.Decide(0,a,1,false);Assert.Equal((FrameVerdict.Send,FrameReason.Start),(d.Verdict,d.Reason));
        p.Captured(0,1);p.Sent(0,a,1);
        Assert.False(p.CaptureDue(1000,1,false));
        Assert.True(p.CaptureDue(4000,1,false));
        Assert.Equal(FrameVerdict.Deduped,p.Decide(4000,(byte[])a.Clone(),1,false).Verdict);
        Assert.Equal(FrameVerdict.Deduped,p.Decide(4000,With(a,100,50,1,4,-90),1,false).Verdict);   // a caret
    }
    [Fact] public void ChangedPictureSendsAsInterval()
    {
        var p=new FramePlanner();var a=Page();p.Captured(0,1);p.Sent(0,a,1);
        var d=p.Decide(4000,With(a,290,6,7,3,60),1,false);Assert.Equal((FrameVerdict.Send,FrameReason.Interval),(d.Verdict,d.Reason));
    }
    [Fact] public void ASkippedFrameNeverBecomesTheReference()
    {
        var p=new FramePlanner();var a=Page();var b=With(a,290,6,7,3,60);
        p.Captured(0,1);p.Sent(0,a,1);
        Assert.Equal(FrameVerdict.Send,p.Decide(4000,b,1,false).Verdict);   // b seen but not sent (locked, rejected, capped ...): no Sent call
        Assert.Equal(FrameVerdict.Send,p.Decide(8000,b,1,false).Verdict);   // so the agent is still owed b
        p.Sent(8000,b,1);Assert.Equal(FrameVerdict.Deduped,p.Decide(12000,b,1,false).Verdict);
    }
    [Fact] public void AfterAChangeTheAgentsPictureIsAtMostOneIntervalOld()
    {
        // A tick every 100 ms like the app; the screen changes at t0, any t0. The frame goes out at the first capture at or after the change.
        for(long t0=0;t0<9000;t0+=250)
        {
            var p=new FramePlanner();var a=Page();var b=With(a,290,6,7,3,60);
            p.Captured(0,1);p.Sent(0,a,1);long sentAt=-1;
            for(long t=100;t<20000&&sentAt<0;t+=100)
            {
                if(!p.CaptureDue(t,1,false))continue;
                p.Captured(t,1);var d=p.Decide(t,t>=t0?b:a,1,false);
                if(d.Verdict==FrameVerdict.Send){sentAt=t;p.Sent(t,t>=t0?b:a,1);}
            }
            Assert.InRange(sentAt-Math.Max(t0,1),0,4000);
        }
    }
    [Fact] public void AChangeThatHadToWaitForTheGapIsCapturedAgainAtOnceNotAFullIntervalLater()
    {
        var p=new FramePlanner();var a=Page();var b=With(a,290,6,7,3,60);
        p.Sent(0,a,1);p.Captured(0,1);
        // foreground switches at 2000, frame sent; the picture changes right after, at 2100: capture at 2100 only waits (gap 2000 since the send)
        p.Captured(2000,2);p.Sent(2000,a,2);
        Assert.False(p.CaptureDue(2050,2,false));
        p.Captured(2100,2);Assert.Equal(FrameVerdict.Wait,p.Decide(2100,b,2,false).Verdict);
        Assert.False(p.CaptureDue(3999,2,false));Assert.True(p.CaptureDue(4000,2,false));   // sent at 2000 + gap, not at 2100 + interval
        p.Captured(4000,2);Assert.Equal(FrameVerdict.Send,p.Decide(4000,b,2,false).Verdict);
    }
    [Fact] public void ForegroundChangeIsDueAtOnceAndSendsEvenWithSamePicture()
    {
        var p=new FramePlanner();var a=Page();p.Captured(0,1);p.Sent(0,a,1);
        Assert.False(p.CaptureDue(1000,2,false));        // too soon: rate limit
        Assert.True(p.CaptureDue(2000,2,false));
        var d=p.Decide(2000,a,2,false);Assert.Equal((FrameVerdict.Send,FrameReason.Foreground),(d.Verdict,d.Reason));
        Assert.Equal(FrameVerdict.Wait,p.Decide(1000,a,2,false).Verdict);
    }
    [Fact] public void FreshRequestAlwaysSendsAndIsAlwaysDue()
    {
        var p=new FramePlanner();var a=Page();p.Captured(0,1);p.Sent(0,a,1);
        Assert.True(p.CaptureDue(10,1,true));
        var d=p.Decide(10,a,1,true);Assert.Equal((FrameVerdict.Send,FrameReason.Fresh),(d.Verdict,d.Reason));
    }
    [Fact] public void ResetStartsOverWithAStartFrame()
    {
        var p=new FramePlanner();var a=Page();p.Captured(0,1);p.Sent(0,a,1);p.Reset();
        Assert.True(p.CaptureDue(1,1,false));Assert.Equal(FrameReason.Start,p.Decide(1,a,1,false).Reason);
    }
    [Fact] public void NeverMoreThanThirtyFramesPerMinute()
    {
        var p=new FramePlanner();int sent=0;byte[] a=Page(),b=With(a,0,0,W,H,-80);long fg=0;
        for(long t=0;t<60000;t+=100){fg=t/300;var h=(t/100)%2==0?a:b;if(p.CaptureDue(t,fg,false)){p.Captured(t,fg);if(p.Decide(t,h,fg,false).Verdict==FrameVerdict.Send){p.Sent(t,h,fg);sent++;}}}
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

public class ReportingTests
{
    [Fact] public void DedupedIsLoggedOncePerRunAndThenEveryThirtySeconds()
    {
        var log=new RunLog();int lines=0;
        for(long t=0;t<=125000;t+=4000)if(log.ShouldLog("deduped",t))lines++;
        Assert.Equal(4,lines);   // at 0, 32 s, 64 s and 96 s (ticks are 4 s apart, the next one after 30 s)
    }
    [Fact] public void ANewRunLogsAgain()
    {
        var log=new RunLog();Assert.True(log.ShouldLog("deduped",0));Assert.False(log.ShouldLog("deduped",4000));
        log.Break();Assert.True(log.ShouldLog("deduped",8000));Assert.True(log.ShouldLog("locked",9000));Assert.False(log.ShouldLog("locked",12000));
    }
    [Fact] public void ClearAndPrivateBodiesCarryOnlyTheContractsWords()
    {
        foreach(var why in new[]{"locked","paused","stop","error"})Assert.Equal(why,DevicePayloads.Why(why)["why"]);
        Assert.Empty(DevicePayloads.Why("config-list"));Assert.Empty(DevicePayloads.Why(null));
        Assert.Equal("{\"why\":\"locked\"}",JsonSerializer.Serialize(DevicePayloads.Why("locked")));
    }
    [Fact] public void IdsFromOcrAndFromVisionAreOneList()
    {
        Assert.Equal(["a1b2c3","332179","0000ff"],DevicePayloads.Merge(["a#a1b2c3","a#332179"],["332179","a#0000FF","nonsense"]).Select(x=>x));
        Assert.Empty(DevicePayloads.Merge([],[]));
    }
    [Fact] public void CropHasFifteenPercentMarginPlusStrokeAndStaysOnTheMonitor()
    {
        var b=HighlightGeometry.Crop(100,300,200,260,1920,1080)!;
        Assert.Equal(new Box(100-35,200-35,200+70+1,60+70+1),b);   // margin = ceil(200*.15)+5 = 35
        Assert.Equal(new Box(0,0,40+11+1,30+11+1),HighlightGeometry.Crop(0,40,0,30,1920,1080));   // margin 11
        var edge=HighlightGeometry.Crop(1900,1919,1060,1079,1920,1080)!;Assert.Equal(1920,edge.x+edge.w);Assert.Equal(1080,edge.y+edge.h);
        Assert.Null(HighlightGeometry.Crop(5000,5100,5000,5100,1920,1080));
    }
}
