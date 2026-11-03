using System.Text;
namespace ScreenAgent.Core;
// 16 kHz, 16-bit mono WAV, the one format the app records and the gateway takes. Canonical 44-byte header.
public static class Wav
{
    public const int Rate=16000,HeaderBytes=44,BytesPerSecond=Rate*2;
    public static byte[] Header(long dataBytes)
    {
        var h=new byte[HeaderBytes];
        void Put(int at,string s)=>Encoding.ASCII.GetBytes(s).CopyTo(h,at);
        void U32(int at,long v)=>BitConverter.GetBytes((uint)Math.Clamp(v,0,uint.MaxValue)).CopyTo(h,at);
        Put(0,"RIFF");U32(4,36+dataBytes);Put(8,"WAVE");Put(12,"fmt ");U32(16,16);BitConverter.GetBytes((ushort)1).CopyTo(h,20);BitConverter.GetBytes((ushort)1).CopyTo(h,22);
        U32(24,Rate);U32(28,BytesPerSecond);BitConverter.GetBytes((ushort)2).CopyTo(h,32);BitConverter.GetBytes((ushort)16).CopyTo(h,34);Put(36,"data");U32(40,dataBytes);
        return h;
    }
    public static byte[] FromPcm(ReadOnlySpan<byte> pcm){var w=new byte[HeaderBytes+pcm.Length];Header(pcm.Length).CopyTo(w,0);pcm.CopyTo(w.AsSpan(HeaderBytes));return w;}
    public static byte[] FromSamples(ReadOnlySpan<short> s)=>FromPcm(System.Runtime.InteropServices.MemoryMarshal.AsBytes(s));
    public static short[] Samples(ReadOnlySpan<byte> pcm)=>System.Runtime.InteropServices.MemoryMarshal.Cast<byte,short>(pcm[..(pcm.Length&~1)]).ToArray();
    public static double Seconds(long pcmBytes)=>pcmBytes/(double)BytesPerSecond;
    // Looks at a file as the recorder (or a crash) left it: fixes the two size fields when they do not match the file, drops a trailing odd byte.
    // Pcm = bytes of audio after the header; Repaired = the header was wrong and is right now; Readable = it is a 16 kHz mono PCM16 file with a data chunk at 36.
    public sealed record Check(bool Readable,bool Repaired,long Pcm,string? Problem=null);
    public static Check Repair(string path)
    {
        try
        {
            using var f=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.Read);
            if(f.Length<HeaderBytes)return new(false,false,0,f.Length==0?"empty":"no-header");
            var h=new byte[HeaderBytes];f.ReadExactly(h);
            bool ok=Encoding.ASCII.GetString(h,0,4)=="RIFF"&&Encoding.ASCII.GetString(h,8,4)=="WAVE"&&Encoding.ASCII.GetString(h,12,4)=="fmt "&&Encoding.ASCII.GetString(h,36,4)=="data"
                &&BitConverter.ToUInt16(h,20)==1&&BitConverter.ToUInt16(h,22)==1&&BitConverter.ToUInt32(h,24)==Rate&&BitConverter.ToUInt16(h,34)==16;
            if(!ok)return new(false,false,0,"format");
            long pcm=(f.Length-HeaderBytes)&~1L;
            bool good=BitConverter.ToUInt32(h,40)==pcm&&BitConverter.ToUInt32(h,4)==36+pcm&&f.Length==HeaderBytes+pcm;
            if(good)return new(true,false,pcm);
            f.SetLength(HeaderBytes+pcm);f.Position=0;f.Write(Header(pcm));f.Flush(true);
            return new(true,true,pcm);
        }
        catch(Exception ex){return new(false,false,0,ex.GetType().Name);}
    }
    // PCM of a repaired file in blocks, so a one-hour recording never has to sit in memory as one array.
    public static IEnumerable<short[]> ReadBlocks(string path,int blockSamples=Rate)
    {
        using var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        f.Position=HeaderBytes;var buf=new byte[blockSamples*2];int n;
        while((n=f.Read(buf,0,buf.Length))>0){n&=~1;if(n==0)break;var s=new short[n/2];System.Buffer.BlockCopy(buf,0,s,0,n);yield return s;}
    }
}
// Appends PCM to a WAV on disk from the first sample. The header is fixed up every FixEverySeconds, so a crash or power loss leaves a file that plays
// (and Wav.Repair fixes whatever the last seconds left behind).
public sealed class WavWriter : IDisposable
{
    readonly FileStream file;
    long data,fixedAt;
    readonly long fixEveryBytes;
    public string Path {get;}
    public long PcmBytes=>data;
    public WavWriter(string path,double fixEverySeconds=3)
    {
        Path=path;fixEveryBytes=(long)(fixEverySeconds*Wav.BytesPerSecond);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        file=new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read);
        file.Write(Wav.Header(0));file.Flush(true);
    }
    public void Append(ReadOnlySpan<byte> pcm)
    {
        file.Write(pcm);data+=pcm.Length;file.Flush();
        if(data-fixedAt>=fixEveryBytes)FixHeader();
    }
    public void FixHeader()
    {
        long at=file.Position;file.Position=0;file.Write(Wav.Header(data));file.Position=at;file.Flush(true);fixedAt=data;
    }
    public void Dispose(){try{FixHeader();}catch{ /* the file is still repairable on the next start */ }file.Dispose();}
}
