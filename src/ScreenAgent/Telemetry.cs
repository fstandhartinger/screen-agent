using System.Net.Http;
using ScreenAgent.Core;
namespace ScreenAgent;
// One place for everything worth explaining afterwards: hotkey events, frame decisions, ids seen, session lifecycle, dictation and captions.
// Goes to a small rolling file (%LOCALAPPDATA%\ScreenAgent\logs) and, in batches every 5 s, to the Desk: the trace of the running session, and without a session
// (dictation mostly runs outside one) the device log. Events wait in a file queue (trace-queue.jsonl: 1000 events, 24 h) until the Desk took them, so a lost
// connection or a restart does not lose them. Counts, ids, file paths and short labels only: never images, OCR text, window titles, audio or transcripts (see TraceSanitizer).
internal sealed class Telemetry : IDisposable
{
    internal static readonly string Folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScreenAgent","logs");
    readonly TraceQueue queue;
    readonly RollingLog log;
    readonly DeviceClient client;
    readonly bool upload,textLog;
    readonly Func<string?> sid;
    readonly System.Threading.Timer timer;
    int flushing;
    long deviceRetryAt,flushFailLogged;
    long batchNo;
    public Telemetry(AppConfig config,DeviceClient client,Func<string?> sid,string? folder=null,TimeSpan? flushInterval=null)
    {
        string storage=folder??Folder;queue=new(Path.Combine(storage,"trace-queue.jsonl"));log=new(storage);
        this.client=client;this.sid=sid;upload=config.TraceUpload;textLog=config.CaptionLog;
        queue.Load();Diag.Attach(this);
        var interval=flushInterval??TimeSpan.FromSeconds(5);
        timer=new(_=>_=Flush(sid()),null,interval,interval);
    }
    public void Event(string type,Dictionary<string,object?>? data=null)
    {
        var clean=TraceSanitizer.Clean(data);var e=new TraceEvent(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),type,clean);
        log.Write(e);
        if(upload)queue.Add(type,clean,e.ts);
    }
    // v0.1.16: caption text for the refinement rounds (CaptionSummary, CaptionLog). Only with TraceUpload and CaptionLog on, only caption.* types, only to the Desk.
    public void Text(string type,Dictionary<string,object?> data)
    {
        if(!upload||!textLog||!type.StartsWith("caption.",StringComparison.Ordinal))return;
        long ts=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        log.Write(new(ts,type,new(){["chars"]=data.Values.OfType<string>().Sum(s=>s.Length)}));   // locally only that it was sent
        queue.Add(type,TraceSanitizer.CleanText(data),ts);
    }
    // After a crash the next start loads the queue file; this keeps it current (an unpaired device never reaches the flush below).
    public void Persist()=>queue.Save();
    // Local only (the upload is what failed), at most once a minute.
    void FlushFailed(string stage,int status,long ms,int count,Exception? ex=null)
    {
        long now=Environment.TickCount64;if(now-flushFailLogged<60000&&flushFailLogged!=0)return;flushFailLogged=now;
        var data=new Dictionary<string,object?>{["stage"]=stage,["status"]=status,["ms"]=ms,["events"]=count,["queued"]=queue.Count};
        if(ex is not null)Redact.Of(ex,data);
        log.Write(new(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),"telemetry.flush.fail",TraceSanitizer.Clean(data)));
    }
    public async Task Flush(string? session)
    {
        if(!upload)return;
        if(!client.Paired){queue.Save();return;}
        if(Interlocked.Exchange(ref flushing,1)==1)return;
        try
        {
            for(int round=0;round<10;round++)
            {
                var batch=queue.Peek(99); // the client.batch header is the hundredth event
                if(batch.Count==0)break;
                // Every batch says which app and session it came from (first event, and as fields the gateway may ignore).
                var head=new{ts=batch[0].ts,type="client.batch",data=new Dictionary<string,object?>{["app"]=AppInfo.Version,["commit"]=AppInfo.Commit,["session"]=session??"",["n"]=++batchNo,["dropped"]=queue.Dropped}};
                // main.* remains readable in the local log; upload it under the gateway's accepted client namespace, including queued older events.
                var body=new{app=AppInfo.Version,commit=AppInfo.Commit,session=session??"",events=batch.Select(e=>new{ts=e.ts,type=e.type.StartsWith("main.",StringComparison.Ordinal)?"client."+e.type:e.type,data=e.data}).Prepend(head).ToArray()};
                var clock=System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    bool taken=false;
                    if(session is not null)
                    {
                        using var reply=await client.Raw(HttpMethod.Post,"api/device/session/"+Uri.EscapeDataString(session)+"/trace",body,cts.Token);
                        taken=reply.Ok;
                        // 404/409: no such session (yet), try the device log below. 429/5xx/401/403: later. Any other 4xx is a bad request we must not repeat.
                        if(!taken&&reply.Status is not (404 or 409)){FlushFailed("trace",reply.Status,clock.ElapsedMilliseconds,batch.Count);if(reply.Status is >=400 and <500 and not (401 or 403 or 429)){queue.Remove(batch.Count);continue;}break;}
                    }
                    // No session, or the gateway does not know ours (yet): the device log.
                    if(!taken&&Environment.TickCount64>=deviceRetryAt)
                    {
                        using var reply=await client.Raw(HttpMethod.Post,"api/device/log",body,cts.Token);
                        taken=reply.Ok;
                        if(!reply.Ok){FlushFailed("device-log",reply.Status,clock.ElapsedMilliseconds,batch.Count);deviceRetryAt=Environment.TickCount64+60000;if(reply.Status is 400 or 413){queue.Remove(batch.Count);continue;}}
                    }
                    if(!taken)break;
                    queue.Remove(batch.Count);
                }
                catch(Exception ex){deviceRetryAt=Environment.TickCount64+15000;FlushFailed("send",0,clock.ElapsedMilliseconds,batch.Count,ex);break;}
            }
        }
        finally{queue.Save();Interlocked.Exchange(ref flushing,0);}
    }
    public void Dispose(){Diag.Detach(this);timer.Dispose();queue.Save();}
}
