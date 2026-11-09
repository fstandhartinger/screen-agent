using ScreenAgent.Core;
namespace ScreenAgent;
internal sealed class ScreenTextSender
{
    readonly ScreenTextSource source;readonly DeviceClient client;readonly Func<bool> enabled;
    readonly Action<string,Dictionary<string,object?>> log;
    readonly Func<long> clock;
    readonly SemaphoreSlim gate=new(1,1);
    string? previous;long posted;int capturing,failureLogged;
    public ScreenTextSender(ScreenTextSource source,DeviceClient client,Func<bool> enabled,Action<string,Dictionary<string,object?>> log,Func<long>? clock=null){this.source=source;this.client=client;this.enabled=enabled;this.log=log;this.clock=clock??(()=>Environment.TickCount64);}
    // Called on the hotkey path: only queue work. The microphone never waits for capture or HTTP.
    public Task Start(string reason,CancellationToken ct,string? reusedOcr=null,long? frameVersion=null,IntPtr expectedWindow=default)
    {
        if(!enabled())return Task.CompletedTask;
        return Task.Run(()=>Run(reason,ct,reusedOcr,frameVersion,expectedWindow));
    }
    internal async Task Run(string reason,CancellationToken ct,string? reusedOcr=null,long? frameVersion=null,IntPtr expectedWindow=default)
    {
        if(!enabled()||expectedWindow!=IntPtr.Zero&&Native.GetForegroundWindow()!=expectedWindow)return;
        bool owns=Interlocked.CompareExchange(ref capturing,1,0)==0;
        // A dictation start must not be lost behind a foreground capture already in flight.
        if(!owns&&reason!="dictation")return;
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(6));
            string? earlyText=null;
            var result=await source.Capture(reason,deadline.Token,reusedOcr,async r=>
            {
                if(expectedWindow!=IntPtr.Zero&&r.Window!=expectedWindow)return;
                await Send(r,reason,deadline.Token,frameVersion);earlyText=r.Text;
            });
            if(result is not null&&(expectedWindow==IntPtr.Zero||result.Window==expectedWindow))
                await Send(result,reason,deadline.Token,frameVersion,earlyText is not null&&result.Text!=earlyText);
        }
        catch{ /* A hint failure never interrupts the microphone or shows a notice. */ Failure(reason);}
        finally{if(owns)Interlocked.Exchange(ref capturing,0);}
    }
    internal async Task Send(ScreenTextResult result,string reason,CancellationToken ct,long? frameVersion=null,bool enrichment=false)
    {
        if(!enabled()||result.Text.Length==0||!source.Current(result))return;
        await gate.WaitAsync(ct);
        try
        {
            long now=clock(),age=now-posted;
            if(!enrichment&&previous is not null&&!ScreenTextLogic.Changed(previous,result.Text)&&age<(reason=="dictation"?20000:60000))return;
            if(!enabled()||!source.Current(result))return;
            var body=new Dictionary<string,object?>{["reason"]=reason,["process"]=result.Process,["title"]=result.Title,["text"]=ScreenTextLogic.Tidy(result.Text)};
            if(reason=="session"&&frameVersion is not null)body["frameVersion"]=frameVersion;
            // Remember attempted payloads too: an older/offline gateway must never cause a retry loop.
            previous=result.Text;posted=now;
            int status=await client.PostScreenText(body,ct);
            if(status==200||Interlocked.Exchange(ref failureLogged,1)==0)
                log("screen.text",new(){["reason"]=reason,["chars"]=result.Text.Length,["lines"]=result.Text.Count(c=>c=='\n')+1,["uiaMs"]=result.UiaMs,["ocrMs"]=result.OcrMs,["ms"]=result.Ms,["status"]=status});
        }
        finally{gate.Release();}
    }
    void Failure(string reason){if(Interlocked.Exchange(ref failureLogged,1)==0)log("screen.text",new(){["reason"]=reason,["chars"]=0,["lines"]=0,["uiaMs"]=0,["ocrMs"]=0,["ms"]=0,["status"]=0});}
}
