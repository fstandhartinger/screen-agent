using System.Net.Http;
using ScreenAgent.Core;
namespace ScreenAgent;
// The command channel side of the Main Agent prompt: acknowledge at once, run, report the outcome. The text never goes back to the Desk.
internal sealed class PromptChannel
{
    readonly DeviceClient client;
    readonly Func<string,CancellationToken,PromptDeadline,Task<PromptResult>> run;
    readonly Action<string,Dictionary<string,object?>> log;
    readonly Action<string,string,int> fallback;
    readonly Func<string,bool> sessionOk;
    readonly PromptGate gate=new();
    readonly InputGate input;
    public PromptChannel(DeviceClient client,Func<string,CancellationToken,PromptDeadline,Task<PromptResult>> run,Action<string,Dictionary<string,object?>> log,Action<string,string,int> fallback,Func<string,bool> sessionOk,InputGate? input=null)
    {this.client=client;this.run=run;this.log=log;this.fallback=fallback;this.sessionOk=sessionOk;this.input=input??new InputGate();}
    // A log that throws must never keep a prompt from running or the gate from opening again.
    void Log(string type,Dictionary<string,object?> data){try{log(type,data);}catch{ }}
    void Fallback(string code,string text,int chars){try{fallback(code,text,chars);}catch{ }}
    public async Task Handle(string sid,PromptRequest request,CancellationToken ct)
    {
        var deadline=PromptDeadline.FromNow();   // the Desk's long poll counts from here
        var start=gate.Begin(request.Id);
        if(start==PromptStart.Repeat)return;
        bool inputHeld=false;
        try
        {
            string url="api/device/session/"+Uri.EscapeDataString(sid)+"/commands/"+Uri.EscapeDataString(request.Id)+"/result";
            await Report(url,new{state="received"});
            Log("main.prompt.received",new(){["chars"]=request.Text.Length});
            async Task Fail(string code)
            {
                Log("main.prompt.done",new(){["code"]=code,["chars"]=0});Fallback(code,request.Text,0);
                await Report(url,new{state="failed",code,landed=(bool?)null,chars=0});
            }
            if(start==PromptStart.Busy){await Fail(PromptCodes.Busy);return;}
            try
            {
                if(!sessionOk(sid)||ct.IsCancellationRequested){await Fail(PromptCodes.NoSession);return;}
                // A dictation being injected right now finishes first (3 s at most), then the prompt starts; otherwise busy.
                inputHeld=await input.BeginPrompt(TimeSpan.FromSeconds(3));
                if(!inputHeld){await Fail(PromptCodes.Busy);return;}
                using var limit=CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter((int)Math.Max(1,deadline.Left(Environment.TickCount64)));
                var result=await run(request.Text,limit.Token,deadline);
                bool ok=PromptCodes.Ok(result.Code);
                if(!ok)Fallback(result.Code,request.Text,result.Code==PromptCodes.Interrupted?result.Chars:0);
                await Report(url,new{state=ok?"done":"failed",code=result.Code,landed=result.Landed,chars=result.Chars});
            }
            catch{await Report(url,new{state="failed",code=PromptCodes.Interrupted,landed=(bool?)null,chars=0});}
        }
        finally{if(inputHeld)input.EndPrompt();if(start==PromptStart.Accepted)gate.End();}   // a busy answer must not open the gate of the prompt that is running
    }
    async Task Report(string url,object body)
    {
        for(int attempt=0;attempt<2;attempt++)
        {
            try
            {
                using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var reply=await client.Raw(HttpMethod.Post,url,body,cts.Token);
                if(reply.Ok||reply.Status is 404 or 409)return;
            }
            catch{ /* one retry */ }
            await Task.Delay(500);
        }
    }
}
