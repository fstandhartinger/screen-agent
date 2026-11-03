namespace ScreenAgent.Core;
// One dictation as it is recorded: every sample goes to the wav on disk first, then through the chunker. No length limit; after MaxMinutes the next file
// is started (and Warning says so). When the disk refuses, recording goes on in memory (DiskOk false) and Warning says that too.
// Append runs on the capture thread; ChunkReady fires from inside it.
public sealed class DictationRecording : IDisposable
{
    readonly DictationStore store;readonly Action<string,Dictionary<string,object?>> log;
    readonly long partBytes;
    readonly object gate=new();
    WavWriter? writer;
    readonly MemoryStream memory=new();
    readonly DictationChunker chunker;
    long samples;int peak;bool stopped;
    public DictationMeta Meta {get;}
    public bool DiskOk {get;private set;}=true;
    public int Peak=>peak;
    public long Samples=>samples;
    public double Seconds=>samples/(double)Wav.Rate;
    public event Action<Chunk>? ChunkReady;
    public event Action<string>? Warning;
    // The same warning when it happened in the constructor, before anyone could subscribe.
    public string? StartWarning {get;private set;}
    public DictationRecording(DictationStore store,string process,Action<string,Dictionary<string,object?>> log,double maxMinutes=60,DictationChunker? chunker=null,double headerFixSeconds=3)
    {
        this.store=store;this.log=log;partBytes=(long)(Math.Max(0.001,maxMinutes)*60*Wav.BytesPerSecond)/2*2;this.chunker=chunker??new();this.headerFixSeconds=headerFixSeconds;
        Meta=store.Begin(process);
        OpenPart();
        try{store.Save(Meta);}catch(Exception ex){FailDisk("meta",ex);}
    }
    readonly double headerFixSeconds;
    void OpenPart()
    {
        if(!DiskOk)return;
        try{writer=new WavWriter(store.NextFile(Meta),headerFixSeconds);}
        catch(Exception ex){FailDisk("open",ex);}
    }
    void FailDisk(string stage,Exception ex)
    {
        DiskOk=false;Meta.DiskOk=false;try{writer?.Dispose();}catch(Exception){ /* the writer is dropped anyway */ }writer=null;
        log("dictation.disk",new(){["id"]=Meta.Id,["stage"]=stage,["exType"]=ex.GetType().Name,["bytes"]=samples*2,["path"]=store.Folder});
        const string say="Diktat wird nicht auf der Festplatte gesichert · Aufnahme läuft im Speicher weiter";
        StartWarning??=say;Warning?.Invoke(say);
    }
    public void Append(ReadOnlySpan<byte> pcm)
    {
        pcm=pcm[..(pcm.Length&~1)];
        if(pcm.Length==0)return;
        IReadOnlyList<Chunk> chunks;
        lock(gate)
        {
            if(stopped)return;
            var s=System.Runtime.InteropServices.MemoryMarshal.Cast<byte,short>(pcm);
            peak=Math.Max(peak,AudioLevel.Peak(s));samples+=s.Length;
            if(writer is not null)
            {
                try
                {
                    writer.Append(pcm);
                    if(writer.PcmBytes>=partBytes)
                    {
                        writer.Dispose();writer=null;OpenPart();
                        log("dictation.limit",new(){["id"]=Meta.Id,["part"]=Meta.Files.Count,["minutes"]=Math.Round(partBytes/(double)Wav.BytesPerSecond/60,2)});
                        Warning?.Invoke($"Diktat läuft seit {Math.Round(partBytes/(double)Wav.BytesPerSecond/60,1)} Minuten · neue Datei begonnen, die Aufnahme geht weiter");
                    }
                }
                catch(Exception ex){FailDisk("write",ex);memory.Write(pcm);}
            }
            else memory.Write(pcm);
            chunks=chunker.Append(s);
        }
        foreach(var c in chunks)ChunkReady?.Invoke(c);
    }
    // Ends the recording: the files are closed with valid headers, the sidecar says "processing". The last chunk (null when none is left) goes to the job.
    public Chunk? Stop()
    {
        lock(gate)
        {
            if(stopped)return null;
            stopped=true;
            try{writer?.Dispose();}catch(Exception){ /* the writer is dropped anyway */ }writer=null;
            Meta.Seconds=Seconds;Meta.Ended=DateTimeOffset.UtcNow;Meta.State="processing";Meta.Bytes=DiskOk?store.BytesOf(Meta):memory.Length;
            try{store.Save(Meta);}catch(Exception){ /* the wav is on disk and recoverable without it */ }
            return chunker.Finish();
        }
    }
    // The whole recording again, in blocks (files in order, then whatever only reached memory): retry after a failure.
    public IEnumerable<short[]> Blocks()
    {
        foreach(var p in Meta.Paths.Where(File.Exists))foreach(var b in Wav.ReadBlocks(p))yield return b;
        if(memory.Length>0)yield return Wav.Samples(memory.ToArray());
    }
    public void Dispose(){lock(gate){stopped=true;try{writer?.Dispose();}catch(Exception){ /* the writer is dropped anyway */ }writer=null;}memory.Dispose();}
}
