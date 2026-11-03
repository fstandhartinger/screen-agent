using System.Diagnostics;
using System.Threading.Channels;
namespace ScreenAgent.Core;
// What the sender needs for one request. Only = the whole dictation is this one chunk (no `chunk` field on the wire: same request as always).
public sealed record ChunkRequest(string DictationId,Chunk Chunk,bool Only,string FieldBefore);
// Status: 2xx ok; 0 no connection; -1 timeout; otherwise the HTTP status. Providers like "groq:ok,openai:503".
public sealed record ChunkReply(int Status,string Text="",string[]? Providers=null,bool Degraded=false,double Seconds=0,long Ms=0,string? Code=null,string? Failure=null)
{
    public bool Ok=>Status is >=200 and <300;
    public bool Network=>Status==0;
    public bool Retryable=>Status is -1 or 408 or 425 or 429 or >=500;
    public string Reason=>Status switch
    {
        0=>"Keine Verbindung zum Desk",-1=>"Zeitüberschreitung beim Desk",401 or 403=>"Gerät nicht mehr verbunden",413=>"Aufnahme für eine Anfrage zu lang",429=>"Zu viele Anfragen",
        >=500=>$"Transkription nicht erreichbar (HTTP {Status})",_=>$"Desk lehnt ab (HTTP {Status})"
    };
}
public delegate Task<ChunkReply> ChunkSender(ChunkRequest request,CancellationToken ct);
public sealed record DictationOutcome(string Text,int Chunks,int Failed,bool Degraded,bool FromFallback,string? FailReason,int FailedStatus,long Ms)
{
    public bool Complete=>Failed==0;
}
// Sends the chunks of one dictation one at a time, in order, in the background: each with up to 2 retries (1 s, 3 s) for timeouts, 429 and 5xx;
// a lost connection does not use up retries, it waits and goes on (LivePatience while he talks, StopPatience after he stopped). A chunk that stays
// failed is replaced by its fallback (the live caption text) and flagged; the others carry on. Nothing is injected here.
public sealed class DictationJob
{
    readonly string id;readonly ChunkSender send;readonly Action<string,Dictionary<string,object?>> log;
    readonly Func<TimeSpan,CancellationToken,Task> delay;
    readonly Channel<(Chunk C,Func<string?> Fallback)> queue=Channel.CreateUnbounded<(Chunk,Func<string?>)>();
    readonly List<string> parts=[];readonly List<bool> overlapped=[];
    readonly Task consumer;readonly CancellationToken ct;
    readonly string prior;
    readonly Stopwatch clock=Stopwatch.StartNew();
    int enqueued,done,failed,lastFailedStatus;
    bool degraded,fromFallback,stopping,only;
    long stopAt;string? failReason;
    public TimeSpan LivePatience {get;init;}=TimeSpan.FromMinutes(10);
    public TimeSpan StopPatience {get;init;}=TimeSpan.FromSeconds(45);
    public int Enqueued=>enqueued;
    public int Done=>done;
    public event Action? Progress;
    public DictationJob(string id,string fieldBefore,ChunkSender send,Action<string,Dictionary<string,object?>> log,CancellationToken ct,Func<TimeSpan,CancellationToken,Task>? delay=null)
    {
        this.id=id;this.send=send;this.log=log;this.ct=ct;prior=fieldBefore;this.delay=delay??((t,c)=>Task.Delay(t,c));
        consumer=Task.Run(Run);
    }
    public void Enqueue(Chunk chunk,Func<string?>? fallback=null){Interlocked.Increment(ref enqueued);queue.Writer.TryWrite((chunk,fallback??(()=>null)));}
    // Stop: the last chunk (null = nothing left), then wait until every chunk is answered or given up.
    public async Task<DictationOutcome> Complete(Chunk? last,Func<string?>? fallback=null)
    {
        if(last is not null){only=enqueued==0;Enqueue(last,fallback);}
        stopAt=clock.ElapsedMilliseconds;stopping=true;queue.Writer.TryComplete();
        await consumer;
        string text;lock(parts)text=DictationJoin.Join(parts,overlapped);
        return new(text,enqueued,failed,degraded,fromFallback,failReason,lastFailedStatus,clock.ElapsedMilliseconds);
    }
    async Task Run()
    {
        try
        {
            await foreach(var (chunk,fallback) in queue.Reader.ReadAllAsync(ct))
            {
                var result=await Send(chunk);
                string? text=result?.Text;bool ov=chunk.OverlapSamples>0;
                if(result is null||!result.Ok)
                {
                    failed++;lastFailedStatus=result?.Status??0;failReason??=result?.Reason??"Abgebrochen";
                    text=fallback();fromFallback|=!string.IsNullOrWhiteSpace(text);ov=false;
                }
                else degraded|=result.Degraded;
                lock(parts){parts.Add(text??"");overlapped.Add(ov);}
                done++;Progress?.Invoke();
            }
        }
        catch(OperationCanceledException){failReason??="Abgebrochen";failed++;}   /* kept as failReason; the manager logs it */
    }
    string FieldBefore()
    {
        string joined;lock(parts)joined=DictationJoin.Join(parts,overlapped);
        var all=prior.Length>0&&joined.Length>0?prior+" "+joined:prior+joined;
        return all.Length<=500?all:all[^500..];
    }
    async Task<ChunkReply?> Send(Chunk chunk)
    {
        var request=new ChunkRequest(id,chunk,only&&chunk.Final&&chunk.Index==0,FieldBefore());
        int attempt=0;long waitStart=clock.ElapsedMilliseconds;int networkWaits=0;
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;var t0=clock.ElapsedMilliseconds;ChunkReply reply;
            try{reply=await send(request,ct);}
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch(OperationCanceledException){reply=new(-1,Failure:"TimeoutException");}   /* in the reply; the job logs it */
            catch(HttpRequestException ex){reply=new(0,Failure:ex.GetType().Name);}   /* in the reply; the job logs it */
            catch(Exception ex){reply=new(0,Failure:ex.GetType().Name);}   /* in the reply; the job logs it */
            log("dictation.chunk",new(){["id"]=id,["index"]=chunk.Index,["bytes"]=chunk.Samples.Length*2+Wav.HeaderBytes,["seconds"]=Math.Round(chunk.Seconds,1),["ms"]=clock.ElapsedMilliseconds-t0,["status"]=reply.Status,["attempt"]=attempt,["providers"]=string.Join(',',reply.Providers??[]),["code"]=reply.Code??"",["exType"]=reply.Failure??"",["degraded"]=reply.Degraded,["overlap"]=chunk.OverlapSamples>0});
            if(reply.Ok)return reply;
            if(reply.Network)
            {
                attempt--;   // a lost connection costs no attempt
                long deadline=stopping?Math.Min(waitStart+(long)LivePatience.TotalMilliseconds,stopAt+(long)StopPatience.TotalMilliseconds):waitStart+(long)LivePatience.TotalMilliseconds;
                if(clock.ElapsedMilliseconds>=deadline)return reply;
                networkWaits++;await delay(TimeSpan.FromSeconds(Math.Min(15,Math.Pow(2,Math.Min(networkWaits,4)))),ct);continue;
            }
            if(!reply.Retryable||attempt>=3)return reply;
            await delay(TimeSpan.FromSeconds(attempt==1?1:3),ct);
        }
    }
}
