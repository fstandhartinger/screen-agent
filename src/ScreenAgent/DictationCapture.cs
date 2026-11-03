using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenAgent.Core;
namespace ScreenAgent;
// WASAPI shared-mode microphone at 16 kHz mono PCM16. Every block goes straight into the DictationRecording (disk first); there is no length limit here.
internal sealed class DictationCapture : IDisposable
{
    readonly WasapiCapture capture;
    readonly TaskCompletionSource<bool> stopped=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object gate=new();
    bool stopping;
    public DictationRecording Recording {get;}
    // The raw blocks, for the live captions (runs on the capture thread).
    public event Action<byte[],int>? Block;
    // Exception type when Windows ended the capture on its own (microphone unplugged, driver reset); the recording up to then is intact.
    public string? CaptureError {get;private set;}
    public DictationCapture(DictationRecording recording)
    {
        Recording=recording;
        capture=new WasapiCapture();capture.WaveFormat=new WaveFormat(16000,16,1);
        capture.DataAvailable+=(_,e)=>{recording.Append(e.Buffer.AsSpan(0,e.BytesRecorded));Block?.Invoke(e.Buffer,e.BytesRecorded);};
        capture.RecordingStopped+=(_,e)=>{if(e.Exception!=null)CaptureError=e.Exception.GetType().Name;stopped.TrySetResult(true);};
        capture.StartRecording();
    }
    // Stops the microphone and the recording; the last chunk (null: none left) is for the job.
    public async Task<Chunk?> Finish()
    {
        lock(gate){if(!stopping){stopping=true;capture.StopRecording();}}
        try{await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));}catch(TimeoutException){CaptureError??="StopTimeout";}   /* recorded: DictationManager logs capture-stopped with it */
        return Recording.Stop();
    }
    public void Dispose(){try{capture.Dispose();}catch(Exception ex){Diag.Fail("dictation-capture-dispose",ex);}Recording.Dispose();}
}
