using NAudio.CoreAudioApi;
using NAudio.Wave;
namespace ScreenAgent;
internal sealed class DictationCapture : IDisposable
{
    readonly WasapiCapture capture;
    readonly MemoryStream buffer=new();
    readonly WaveFileWriter writer;
    readonly TaskCompletionSource<bool> stopped=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object gate=new();
    bool stopping;
    public DictationCapture()
    {
        capture=new WasapiCapture();capture.WaveFormat=new WaveFormat(16000,16,1);
        writer=new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(buffer),capture.WaveFormat);
        capture.DataAvailable+=(_,e)=>{lock(gate){if(buffer.Length<16000*2*120)writer.Write(e.Buffer,0,e.BytesRecorded);}};
        capture.RecordingStopped+=(_,e)=>{if(e.Exception!=null)stopped.TrySetException(e.Exception);else stopped.TrySetResult(true);};
        capture.StartRecording();
    }
    public async Task<byte[]> Finish()
    {
        lock(gate){if(!stopping){stopping=true;capture.StopRecording();}}
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        lock(gate){writer.Flush();writer.Dispose();return buffer.ToArray();}
    }
    public void Dispose(){capture.Dispose();writer.Dispose();buffer.Dispose();}
}
