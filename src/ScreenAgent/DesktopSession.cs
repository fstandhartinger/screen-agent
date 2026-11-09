using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows;
namespace ScreenAgent;
// Test/debug transport only: a stand-in signed-in voice session drives the exact production command parser and executor.
internal sealed class DesktopSession:IDisposable
{
    readonly ConcurrentDictionary<string,TaskCompletionSource<JsonElement>> replies=new();
    readonly ComputerOverlay overlay;readonly KeyboardHook hook;readonly Timer beats;readonly string sid="desktop-debug";
    public ComputerAgent Agent {get;}
    public string? RunId {get;private set;}
    int seq;
    public DesktopSession(Application app,string log)
    {
        overlay=app.Dispatcher.Invoke(()=>new ComputerOverlay());
        hook=new KeyboardHook(new HotkeyBindings(new AppConfig().Hotkeys));
        Agent=new ComputerAgent((url,body)=>
        {
            if(url.EndsWith("/result")){string id=url.Split('/')[^2];if(replies.TryRemove(id,out var reply))reply.TrySetResult(JsonSerializer.SerializeToElement(body));}return Task.FromResult(true);
        },(_,_)=>{},new InputGate(),new ComputerPolicy(){Unrestricted=true},overlay,hook,()=>sid,()=>true,()=>true,()=>false,null,new ChromeCdp.ConnectOptions("",false),log,app.Dispatcher);
        beats=new Timer(_=>{if(RunId is {} run)Send(run,"beat");},null,1000,1000);
    }
    void Send(string run,string op,string? id=null,string? tool=null,object? args=null,string? goal=null)
    {
        var wire=JsonSerializer.SerializeToElement(new{computer=new{id=id??("c"+Guid.NewGuid().ToString("N")[..16]),run,seq=Interlocked.Increment(ref seq),op,tool,args,goal}});
        var command=ComputerWire.Parse(wire,out string why)??throw new InvalidOperationException(why);Agent.Handle(sid,command);
    }
    async Task<JsonElement> Command(string op,string? tool=null,object? args=null,string? goal=null)
    {
        string id="c"+Guid.NewGuid().ToString("N")[..16];var tcs=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);replies[id]=tcs;
        try{Send(RunId??throw new InvalidOperationException("Zuerst /start aufrufen."),op,id,tool,args,goal);return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(610));}finally{replies.TryRemove(id,out _);}
    }
    public async Task<JsonElement> Start(string goal){if(RunId is not null)await Command("stop");RunId="r"+Guid.NewGuid().ToString("N")[..16];return await Command("start",goal:goal);}
    public Task<JsonElement> Act(string tool,object? args=null)=>Command("action",tool,args??new{});
    public void Kill()=>Agent.Run?.Kill("hotkey");
    public async Task Stop(){if(RunId is not null){await Command("stop");RunId=null;}}
    public void Dispose(){beats.Dispose();Agent.Dispose();hook.Dispose();Application.Current.Dispatcher.Invoke(()=>overlay.Hide());}
}
