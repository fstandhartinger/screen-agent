using System.Net.Http;
using ScreenAgent.Core;
namespace ScreenAgent;
// The command channel side of the Main Agent prompt: acknowledge at once, run, report the outcome. The text never goes back to the Desk.
internal sealed class PromptChannel
{
    readonly DeviceClient client;
    readonly Func<string,CancellationToken,Task<PromptResult>> run;
    readonly Action<string,Dictionary<string,object?>> log;
    readonly Action<string,string> fallback;
    readonly Func<string,bool> sessionOk;
    readonly PromptGate gate=new();
    public PromptChannel(DeviceClient client,Func<string,CancellationToken,Task<PromptResult>> run,Action<string,Dictionary<string,object?>> log,Action<string,string> fallback,Func<string,bool> sessionOk)
    {this.client=client;this.run=run;this.log=log;this.fallback=fallback;this.sessionOk=sessionOk;}
    public async Task Handle(string sid,PromptRequest request,CancellationToken ct)
    {
        var start=gate.Begin(request.Id);
        if(start==PromptStart.Repeat)return;
        string url="api/device/session/"+Uri.EscapeDataString(sid)+"/commands/"+Uri.EscapeDataString(request.Id)+"/result";
        await Report(url,new{state="received"});
        log("main.prompt.received",new(){["chars"]=request.Text.Length});
        async Task Fail(string code)
        {
            log("main.prompt.done",new(){["code"]=code,["chars"]=0});fallback(code,request.Text);
            await Report(url,new{state="failed",code,landed=(bool?)null,chars=0});
        }
        if(start==PromptStart.Busy){await Fail(PromptCodes.Busy);return;}
        try
        {
            if(!sessionOk(sid)||ct.IsCancellationRequested){await Fail(PromptCodes.NoSession);return;}
            var result=await run(request.Text,ct);
            bool ok=PromptCodes.Ok(result.Code);
            if(!ok)fallback(result.Code,request.Text);
            await Report(url,new{state=ok?"done":"failed",code=result.Code,landed=result.Landed,chars=result.Chars});
        }
        catch{await Report(url,new{state="failed",code=PromptCodes.Interrupted,landed=(bool?)null,chars=0});}
        finally{gate.End();}
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
