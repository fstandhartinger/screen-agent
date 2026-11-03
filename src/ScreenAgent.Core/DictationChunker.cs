namespace ScreenAgent.Core;
public sealed record Chunk(int Index,short[] Samples,bool Final,int OverlapSamples)
{
    public double Seconds=>Samples.Length/(double)Core.Wav.Rate;
    public byte[] WavBytes=>Core.Wav.FromSamples(Samples);
}
public static class AudioLevel
{
    public const int Frame=Wav.Rate*30/1000;   // 30 ms
    public static double Rms(ReadOnlySpan<short> s){if(s.Length==0)return 0;double sum=0;foreach(var v in s)sum+=(double)v*v;return Math.Sqrt(sum/s.Length);}
    public static int Peak(ReadOnlySpan<short> s){int p=0;foreach(var v in s){int a=v==short.MinValue?32768:Math.Abs((int)v);if(a>p)p=a;}return p;}
}
// Before any provider call: a recording this short or this dead is not worth a request (and not an error either).
public static class DictationGate
{
    public const double MinSeconds=0.3;
    public const int SilentPeak=8;
    public static string? Reject(long samples,int peak)=>samples<MinSeconds*Wav.Rate?"too-short":peak<=SilentPeak?"silent":null;
}
// Splits a running recording for the gateway (60 s per request). Cuts at the first pause of Gap length after MinSeconds (so chunks go out while he still talks);
// with none by HardSeconds it cuts anyway and the next chunk starts OverlapSeconds earlier, which DictationJoin removes by word overlap again.
// Silence = a 30 ms frame whose RMS is under a threshold adapted to the room (about 2.5 x the quiet 10th percentile of the chunk so far).
public sealed class DictationChunker(double minSeconds=20,double hardSeconds=55,double overlapSeconds=0.5,double gapSeconds=0.3)
{
    readonly List<short> buf=[];
    readonly List<float> rms=[];
    int index,overlap,framesDone;
    int GapFrames=>Math.Max(1,(int)Math.Round(gapSeconds*1000/30));
    public int Emitted=>index;
    public long Samples {get;private set;}
    // The room's quiet level decides what a pause is: never below 150 (no hiss counts as speech) and never above 1500 (loud speech never counts as pause).
    public double Threshold()
    {
        if(rms.Count==0)return 300;
        var sorted=rms.OrderBy(x=>x).ToArray();double floor=sorted[sorted.Length/10];
        return Math.Clamp(floor*2.5,150,1500);
    }
    public IReadOnlyList<Chunk> Append(ReadOnlySpan<short> samples)
    {
        buf.AddRange(samples.ToArray());Samples+=samples.Length;
        var ready=new List<Chunk>();
        while(true)
        {
            while((framesDone+1)*AudioLevel.Frame<=buf.Count){rms.Add((float)AudioLevel.Rms(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buf).Slice(framesDone*AudioLevel.Frame,AudioLevel.Frame)));framesDone++;}
            var (cut,hard)=FindCut();
            if(cut<0)break;
            ready.Add(Cut(cut,hard));
        }
        return ready;
    }
    (int At,bool Hard) FindCut()
    {
        int minFrame=(int)(minSeconds*1000/30),hardSamples=(int)(hardSeconds*Wav.Rate);
        if(framesDone>=minFrame)
        {
            double thr=Threshold();int need=GapFrames;
            // Re-scan from the first frame that could still belong to a run: cheap, chunks are at most a minute.
            int run=0,start=-1;
            for(int i=Math.Max(0,minFrame-need);i<framesDone;i++)
            {
                if(rms[i]<thr){if(run==0)start=i;run++;}else run=0;
                if(run>=need)
                {
                    int cutFrame=Math.Max(start+need/2,minFrame);
                    if(cutFrame<=i)return (cutFrame*AudioLevel.Frame,false);   // inside the run, and the run is long enough to see
                }
            }
        }
        return buf.Count>=hardSamples?(hardSamples,true):(-1,false);
    }
    Chunk Cut(int at,bool hard)
    {
        var samples=buf.GetRange(0,at).ToArray();
        var c=new Chunk(index++,samples,false,overlap);
        int keepFrom=hard?Math.Max(0,at-(int)(overlapSeconds*Wav.Rate)):at;
        buf.RemoveRange(0,keepFrom);overlap=at-keepFrom;
        rms.Clear();framesDone=0;
        return c;
    }
    // The rest, at stop. Null when nothing was recorded after the last cut (apart from overlap).
    public Chunk? Finish()
    {
        if(buf.Count<=overlap&&index>0)return null;
        var c=new Chunk(index++,buf.ToArray(),true,overlap);
        buf.Clear();rms.Clear();framesDone=0;overlap=0;
        return c;
    }
}
