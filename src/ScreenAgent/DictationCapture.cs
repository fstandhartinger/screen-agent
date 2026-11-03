using System.Diagnostics;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenAgent.Core;
namespace ScreenAgent;
// WASAPI shared-mode microphone at 16 kHz mono PCM16. The capture callback only copies the block into a bounded queue (5 minutes of audio); a writer task does the
// disk write (with its fsync), the chunker and the logging, so a slow disk or a virus scan cannot make the device drop samples. There is no length limit here.
internal sealed class DictationCapture : IDisposable
{
    const int MaxQueued=3000;   // ~100 ms blocks: 5 minutes
    readonly record struct Item(byte[]? Data,double MissingSeconds);
    WasapiCapture capture=null!;
    bool dead;   // the capture ended on its own and was not reopened
    readonly TaskCompletionSource<bool> stopped=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object gate=new();
    readonly Channel<Item> queue;
    readonly Task writer;
    readonly Stopwatch wall=new();
    readonly GapWatch gaps=new();
    long bytesIn,dropped;
    bool stopping;
    public DictationRecording Recording {get;}
    // The raw blocks, for the live captions (runs on the writer task, not the capture thread).
    public event Action<byte[],int>? Block;
    // Audio that never arrived (seconds missing so far, recording seconds when noticed) and blocks dropped because the queue was full.
    public event Action<double,double>? Gap;
    // Windows ended the capture on its own (microphone unplugged, driver reset, device switched): exception type and recording seconds. Raised on the thread that created the capture.
    public event Action<string,double>? Lost;
    // Exception type when the capture ended on its own or did not stop in time; the recording up to then is intact.
    public string? CaptureError {get;private set;}
    public long DroppedBlocks=>Interlocked.Read(ref dropped);
    public int Restarts {get;private set;}
    public DictationCapture(DictationRecording recording)
    {
        Recording=recording;
        queue=Channel.CreateBounded<Item>(new BoundedChannelOptions(MaxQueued){FullMode=BoundedChannelFullMode.DropWrite,SingleReader=true},_=>Interlocked.Increment(ref dropped));
        Open();
        writer=Task.Run(Drain);
    }
    WasapiCapture Open()
    {
        var prev=capture;var c=new WasapiCapture();c.WaveFormat=new WaveFormat(16000,16,1);capture=c;   // events of any other instance are ignored
        c.DataAvailable+=(s,e)=>
        {
            if(!ReferenceEquals(s,capture)||e.BytesRecorded<=0)return;
            var copy=new byte[e.BytesRecorded];Buffer.BlockCopy(e.Buffer,0,copy,0,copy.Length);
            long total=Interlocked.Add(ref bytesIn,copy.Length);
            double? missing=gaps.Check(wall.Elapsed.TotalSeconds,total/(double)Wav.BytesPerSecond);
            queue.Writer.TryWrite(new(copy,0));
            if(missing is {} m)queue.Writer.TryWrite(new(null,m));
        };
        c.RecordingStopped+=(s,e)=>
        {
            if(!ReferenceEquals(s,capture))return;   // the instance we replaced
            bool wanted;lock(gate)wanted=stopping;
            if(!wanted){dead=true;CaptureError=e.Exception?.GetType().Name??"CaptureEnded";Lost?.Invoke(CaptureError,Recording.Seconds);}
            else{if(e.Exception!=null)CaptureError=e.Exception.GetType().Name;stopped.TrySetResult(true);}
        };
        if(!wall.IsRunning)wall.Start();
        try{c.StartRecording();}
        catch(Exception){capture=prev;try{c.Dispose();}catch(Exception ex){Diag.Fail("dictation-capture-open-dispose",ex);}throw;}   // the caller logs it
        return c;
    }
    async Task Drain()
    {
        await foreach(var it in queue.Reader.ReadAllAsync())
        {
            try
            {
                if(it.Data is {} d){Recording.Append(d);Block?.Invoke(d,d.Length);}
                else Gap?.Invoke(it.MissingSeconds,Recording.Seconds);
            }
            catch(Exception ex){Diag.Fail("dictation-writer",ex);}   // one bad block never stops the writer
        }
    }
    // Tries to open the default microphone again after Lost, into the same recording (the gap stays a gap). false: not possible.
    public bool TryRestart()
    {
        lock(gate)
        {
            if(stopping)return false;
            try
            {
                var old=capture;Open();Restarts++;dead=false;
                try{old.Dispose();}catch(Exception ex){Diag.Fail("dictation-capture-dispose-old",ex);}
                return true;
            }
            catch(Exception ex){Diag.Fail("dictation-capture-restart",ex);return false;}
        }
    }
    // Stops the microphone and the recording; the last chunk (null: none left) is for the job.
    public async Task<Chunk?> Finish()
    {
        lock(gate){if(!stopping){stopping=true;try{capture.StopRecording();}catch(Exception ex){CaptureError??=ex.GetType().Name;stopped.TrySetResult(true);}}}   // kept as CaptureError; the manager logs it at stop
        if(!dead)try{await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));}catch(TimeoutException){CaptureError??="StopTimeout";}   /* recorded: DictationManager logs capture-stopped with it */
        queue.Writer.TryComplete();
        try{await writer.WaitAsync(TimeSpan.FromSeconds(30));}catch(TimeoutException){CaptureError??="DrainTimeout";}   /* the writer is still at it: Stop() below ends the recording, later blocks are dropped */
        return Recording.Stop();
    }
    // The same without waiting for the UI thread (the app closes): the writer is joined for a moment, the recording is closed with a valid header.
    public Chunk? Halt()
    {
        lock(gate){if(!stopping){stopping=true;try{capture.StopRecording();}catch(Exception ex){CaptureError??=ex.GetType().Name;}}}   // kept as CaptureError; the manager logs it
        queue.Writer.TryComplete();
        try{writer.Wait(TimeSpan.FromSeconds(2));}catch(Exception ex){CaptureError??=ex.GetType().Name;}   // a writer that is not done after 2 s is abandoned; the header fix below still runs
        return Recording.Stop();
    }
    public void Dispose(){lock(gate){stopping=true;}try{capture.Dispose();}catch(Exception ex){Diag.Fail("dictation-capture-dispose",ex);}queue.Writer.TryComplete();Recording.Dispose();}
}
