using System.Net.Http;
using ScreenAgent.Core;
namespace ScreenAgent;
// One place for everything worth explaining afterwards: hotkey events, frame decisions, ids seen, session lifecycle, dictation and captions.
// Goes to a small rolling file (%LOCALAPPDATA%\ScreenAgent\logs) and, in batches every 5 s, to the Desk: the trace of the running session, and without a session
// (dictation mostly runs outside one) the device log. Events wait in a file queue (trace-queue.jsonl: 1000 events, 24 h) until the Desk took them, so a lost
// connection or a restart does not lose them. Counts, ids, file paths and short labels only: never images, OCR text, window titles, audio or transcripts (see TraceSanitizer).
internal sealed class Telemetry : IDisposable
{
    static readonly string Folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScreenAgent","logs");
    readonly TraceQueue queue=new(Path.Combine(Folder,"trace-queue.jsonl"));
    readonly RollingLog log=new(Folder);
    readonly DeviceClient client;
    readonly bool upload;
    readonly Func<string?> sid;
    readonly System.Threading.Timer timer;
    int flushing;
    long deviceRetryAt;
    public Telemetry(AppConfig config,DeviceClient client,Func<string?> sid)
    {
        this.client=client;this.sid=sid;upload=config.TraceUpload;
        queue.Load();
        timer=new(_=>_=Flush(sid()),null,5000,5000);
    }
    public void Event(string type,Dictionary<string,object?>? data=null)
    {
        var clean=TraceSanitizer.Clean(data);var e=new TraceEvent(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),type,clean);
        log.Write(e);
        if(upload)queue.Add(type,clean,e.ts);
    }
    public async Task Flush(string? session)
    {
        if(!upload||!client.Paired||Interlocked.Exchange(ref flushing,1)==1)return;
        try
        {
            for(int round=0;round<10;round++)
            {
                var batch=queue.Peek(100);
                if(batch.Count==0)break;
                var body=new{events=batch.Select(e=>new{ts=e.ts,type=e.type,data=e.data}).ToArray()};
                try
                {
                    using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    bool taken=false;
                    if(session is not null)
                    {
                        using var reply=await client.Raw(HttpMethod.Post,"api/device/session/"+Uri.EscapeDataString(session)+"/trace",body,cts.Token);
                        taken=reply.Ok;
                        // 404/409: no such session (yet), try the device log below. 429/5xx/401/403: later. Any other 4xx is a bad request we must not repeat.
                        if(!taken&&reply.Status is not (404 or 409)){if(reply.Status is >=400 and <500 and not (401 or 403 or 429)){queue.Remove(batch.Count);continue;}break;}
                    }
                    // No session, or the gateway does not know ours (yet): the device log.
                    if(!taken&&Environment.TickCount64>=deviceRetryAt)
                    {
                        using var reply=await client.Raw(HttpMethod.Post,"api/device/log",body,cts.Token);
                        taken=reply.Ok;
                        if(!reply.Ok){deviceRetryAt=Environment.TickCount64+60000;if(reply.Status is 400 or 413){queue.Remove(batch.Count);continue;}}
                    }
                    if(!taken)break;
                    queue.Remove(batch.Count);
                }
                catch{deviceRetryAt=Environment.TickCount64+15000;break;}
            }
        }
        finally{queue.Save();Interlocked.Exchange(ref flushing,0);}
    }
    public void Dispose(){timer.Dispose();queue.Save();}
}
