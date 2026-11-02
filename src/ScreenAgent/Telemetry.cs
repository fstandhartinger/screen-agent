using System.Net.Http;
using ScreenAgent.Core;
namespace ScreenAgent;
// One place for everything worth explaining afterwards: hotkey events, frame decisions, ids seen, session lifecycle.
// Goes to a small rolling file (%LOCALAPPDATA%\ScreenAgent\logs) and, in batches every 5 s, to the Desk trace of the running session.
// Counts, ids and short labels only: never images, OCR text, window titles or transcripts (see TraceSanitizer).
internal sealed class Telemetry : IDisposable
{
    readonly TraceBuffer buffer=new();
    readonly RollingLog log=new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScreenAgent","logs"));
    readonly DeviceClient client;
    readonly bool upload;
    readonly Func<string?> sid;
    readonly System.Threading.Timer timer;
    int flushing;
    public Telemetry(AppConfig config,DeviceClient client,Func<string?> sid)
    {
        this.client=client;this.sid=sid;upload=config.TraceUpload;
        timer=new(_=>_=Flush(sid()),null,5000,5000);
    }
    public void Event(string type,Dictionary<string,object?>? data=null)
    {
        var clean=TraceSanitizer.Clean(data);var e=new TraceEvent(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),type,clean);
        log.Write(e);
        if(upload)buffer.Add(type,clean,e.ts);
    }
    public async Task Flush(string? session)
    {
        if(!upload||session is null||!client.Paired||Interlocked.Exchange(ref flushing,1)==1)return;
        try
        {
            var batch=buffer.Take();
            if(batch.Count==0)return;
            try
            {
                using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var reply=await client.Raw(HttpMethod.Post,"api/device/session/"+Uri.EscapeDataString(session)+"/trace",new{events=batch.Select(e=>new{ts=e.ts,type=e.type,data=e.data}).ToArray()},cts.Token);
                // 409/404: the session is gone or not registered yet. Keep the batch for the next session id; anything else is a bad request we must not repeat.
                if(!reply.Ok&&reply.Status is 404 or 409 or 429 or >=500)buffer.Requeue(batch);
            }
            catch{buffer.Requeue(batch);}
        }
        finally{Interlocked.Exchange(ref flushing,0);}
    }
    public void Dispose(){timer.Dispose();}
}
