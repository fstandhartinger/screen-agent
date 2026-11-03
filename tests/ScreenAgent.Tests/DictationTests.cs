using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
public class DictationTests
{
    const int R=Wav.Rate;
    // Synthetic speech: a 220 Hz tone in bursts (amplitude 3000) with a little noise; silence = noise only.
    static short[] Tone(double sec,double amp=3000,int seed=1){var rnd=new Random(seed);var s=new short[(int)(sec*R)];for(int i=0;i<s.Length;i++)s[i]=(short)(amp*Math.Sin(2*Math.PI*220*i/R)+rnd.Next(-60,60));return s;}
    static short[] Quiet(double sec,int noise=60,int seed=2){var rnd=new Random(seed);var s=new short[(int)(sec*R)];for(int i=0;i<s.Length;i++)s[i]=(short)rnd.Next(-noise,noise+1);return s;}
    static short[] Cat(params short[][] p)=>p.SelectMany(x=>x).ToArray();
    static List<Chunk> Run(short[] audio,int blockMs=100,DictationChunker? c=null)
    {
        c??=new();var list=new List<Chunk>();int block=R*blockMs/1000;
        for(int i=0;i<audio.Length;i+=block)list.AddRange(c.Append(audio.AsSpan(i,Math.Min(block,audio.Length-i))));
        if(c.Finish() is {} last)list.Add(last);
        return list;
    }
    [Fact] public void UnderHardLimitWithoutPauseIsOneChunk()
    {
        var c=new DictationChunker();var audio=Tone(50);
        for(int i=0;i<audio.Length;i+=R/10)Assert.Empty(c.Append(audio.AsSpan(i,Math.Min(R/10,audio.Length-i))));
        var last=c.Finish()!;Assert.Equal(0,last.Index);Assert.True(last.Final);Assert.Equal(audio.Length,last.Samples.Length);
    }
    [Fact] public void CutsAtThePauseAfterTwentySecondsWhileHeStillTalks()
    {
        var audio=Cat(Tone(30),Quiet(0.5),Tone(30));var c=new DictationChunker();var early=new List<Chunk>();
        for(int i=0;i<audio.Length;i+=R/10)early.AddRange(c.Append(audio.AsSpan(i,Math.Min(R/10,audio.Length-i))));
        var first=Assert.Single(early);                       // out before the recording ended
        Assert.InRange(first.Seconds,30,31);Assert.False(first.Final);Assert.Equal(0,first.OverlapSamples);
        var last=c.Finish()!;Assert.Equal(audio.Length,first.Samples.Length+last.Samples.Length);Assert.Equal(1,last.Index);
    }
    [Fact] public void ShortPausesAndPausesBeforeTwentySecondsDoNotCut()
    {
        Assert.Single(Run(Cat(Tone(25),Quiet(0.2),Tone(20))));
        Assert.Single(Run(Cat(Tone(10),Quiet(0.6),Tone(40))));
    }
    [Fact] public void TheEarliestPauseInTheWindowWins()
    {
        var chunks=Run(Cat(Tone(25),Quiet(0.5),Tone(14),Quiet(0.5),Tone(14)));
        Assert.Equal(2,chunks.Count);Assert.InRange(chunks[0].Seconds,25,26);
    }
    [Fact] public void HardCutAt55SecondsOverlapsHalfASecond()
    {
        var audio=Tone(70);var chunks=Run(audio);
        Assert.Equal(2,chunks.Count);Assert.Equal(55*R,chunks[0].Samples.Length);Assert.Equal(0,chunks[0].OverlapSamples);
        Assert.Equal(R/2,chunks[1].OverlapSamples);Assert.Equal(audio.Length-55*R+R/2,chunks[1].Samples.Length);
        Assert.True(chunks[0].Samples.TakeLast(R/2).SequenceEqual(chunks[1].Samples.Take(R/2)));   // the same half second twice
    }
    [Fact] public void NoSampleIsLostWhateverTheBlockSize()
    {
        var audio=Cat(Tone(22),Quiet(0.4),Tone(60),Quiet(0.5),Tone(5));
        foreach(int ms in new[]{20,100,1000,7000})
        {
            var chunks=Run(audio,ms);
            Assert.Equal(audio.Length,chunks.Sum(c=>c.Samples.Length-c.OverlapSamples));
            Assert.Equal(Enumerable.Range(0,chunks.Count),chunks.Select(c=>c.Index));
        }
        Assert.Equal(Run(audio,20).Select(c=>c.Samples.Length),Run(audio,1000).Select(c=>c.Samples.Length));
    }
    [Fact] public void PausesInANoisyRoomStillCount()
    {
        var chunks=Run(Cat(Tone(30),Quiet(0.5,250),Tone(10)));   // hiss at RMS ~145, speech at ~2100
        Assert.Equal(2,chunks.Count);
    }
    [Theory][InlineData(100,"too-short")][InlineData(4799,"too-short")][InlineData(16000,null)]
    public void GateRejectsTooShort(int samples,string? why)=>Assert.Equal(why,DictationGate.Reject(samples,3000));
    [Fact] public void GateRejectsDigitalSilence(){Assert.Equal("silent",DictationGate.Reject(R*5,0));Assert.Equal("silent",DictationGate.Reject(R*5,3));Assert.Null(DictationGate.Reject(R*5,400));}

    [Fact] public void JoinRemovesTheOverlapOnlyWhereTheCutWasHard()
    {
        Assert.Equal("wir treffen uns morgen um zehn Uhr",DictationJoin.Join(["wir treffen uns morgen um","um zehn Uhr"],[false,true]));
        Assert.Equal("das ist sehr sehr gut",DictationJoin.Join(["das ist sehr","sehr gut"],[false,false]));
        Assert.Equal("Das ist, sehr gut.",DictationJoin.Join(["Das ist, sehr","sehr gut."],[false,true]));
        Assert.Equal("a b c d",DictationJoin.Join(["a b","","c d"],[false,false,true]));
        Assert.Equal("eins zwei",DictationJoin.Join(["eins","zwei"],[false,true]));   // no shared word: nothing removed
    }
    [Fact] public void StitchGrowsFromRollingWindowsAndLastWordsMayChange()
    {
        var truth="wir treffen uns morgen um zehn Uhr im Büro und besprechen dann den neuen Plan für die Woche".Split(' ');
        var s=new CaptionStitcher();
        for(int end=3;end<=truth.Length;end++)
        {
            var window=truth[Math.Max(0,end-6)..end].ToArray();
            if(end<truth.Length)window[^1]=window[^1]+"x";      // the last word of a window is a guess
            if(end>6)window[0]=window[0][1..];                   // and the first may be cut
            s.Add(string.Join(' ',window));
        }
        s.Add(string.Join(' ',truth[^6..]));
        Assert.Equal(string.Join(' ',truth),s.Text);
    }
    [Fact] public void StitchAppendsAfterALongPauseAndSlicesByMarks()
    {
        var s=new CaptionStitcher();s.Add("guten Morgen zusammen");int m=s.Mark();s.Add("jetzt kommt ein ganz neuer Satz");
        Assert.Equal("guten Morgen zusammen jetzt kommt ein ganz neuer Satz",s.Text);
        Assert.Equal("guten Morgen zusammen",s.Slice(0,m));Assert.Equal("jetzt kommt ein ganz neuer Satz",s.Slice(m,s.Count));
        Assert.Equal("ganz neuer Satz",s.Last(18));
    }
    [Fact] public void WavWriterKeepsTheHeaderValidAfterACrash()
    {
        string dir=Directory.CreateTempSubdirectory().FullName;string p=Path.Combine(dir,"a.wav"),copy=Path.Combine(dir,"crash.wav");
        var w=new WavWriter(p,3);var pcm=new byte[R*2];                       // 1 s per append
        for(int i=0;i<4;i++)w.Append(pcm);                                    // header fixed after 3 s
        File.Copy(p,copy);                                                    // the process dies here: no Dispose
        using(var f=File.OpenRead(copy)){var h=new byte[44];f.ReadExactly(h);Assert.Equal(3*R*2,BitConverter.ToInt32(h,40));}   // the header was fixed up 3 s in, the 4th second is only in the body
        var c=Wav.Repair(copy);Assert.True(c.Readable);Assert.True(c.Repaired);Assert.Equal(4L*R*2,c.Pcm);
        Assert.False(Wav.Repair(copy).Repaired);
        w.Dispose();Assert.False(Wav.Repair(p).Repaired);
        Assert.Equal(4,Wav.ReadBlocks(p).Sum(b=>b.Length)/R);
    }
    [Fact] public void RepairRejectsGarbageAndCutsAnOddByte()
    {
        string dir=Directory.CreateTempSubdirectory().FullName;
        File.WriteAllBytes(Path.Combine(dir,"x.wav"),new byte[10]);Assert.False(Wav.Repair(Path.Combine(dir,"x.wav")).Readable);
        var odd=Wav.FromPcm(new byte[1001]);File.WriteAllBytes(Path.Combine(dir,"o.wav"),odd);
        var c=Wav.Repair(Path.Combine(dir,"o.wav"));Assert.True(c.Readable);Assert.True(c.Repaired);Assert.Equal(1000,c.Pcm);
    }
}
