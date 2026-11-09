using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
public class ChromeCancellationTests
{
    static JsonElement J(object value)=>JsonSerializer.SerializeToElement(value);
    sealed class Peer:IDisposable
    {
        readonly HttpListener listener=new();readonly CancellationTokenSource life=new();WebSocket? remote;
        public CdpSocket Socket=null!;public volatile bool Stop;public readonly List<JsonElement> Requests=[];
        public Func<JsonElement,object?> Reply=_=>new{};
        public async Task Start(bool read=true)
        {
            using var port=new TcpListener(IPAddress.Loopback,0);port.Start();int n=((IPEndPoint)port.LocalEndpoint).Port;port.Stop();
            listener.Prefixes.Add($"http://127.0.0.1:{n}/");listener.Start();
            var connect=CdpSocket.Connect(new Uri($"ws://127.0.0.1:{n}/"),()=>Stop,(_,_)=>{},life.Token);
            var context=await listener.GetContextAsync();remote=(await context.AcceptWebSocketAsync(null)).WebSocket;Socket=await connect;
            if(read)_=Read();
        }
        async Task Read()
        {
            try
            {
                var buf=new byte[65536];
                while(!life.IsCancellationRequested)
                {
                    using var ms=new MemoryStream();WebSocketReceiveResult r;
                    do{r=await remote!.ReceiveAsync(buf,life.Token);if(r.MessageType==WebSocketMessageType.Close)return;ms.Write(buf,0,r.Count);}while(!r.EndOfMessage);
                    using var doc=JsonDocument.Parse(ms.ToArray());var q=doc.RootElement.Clone();lock(Requests)Requests.Add(q);
                    var result=Reply(q);if(result is null)continue;
                    await remote!.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new{id=q.GetProperty("id").GetInt32(),result}),WebSocketMessageType.Text,true,life.Token);
                }
            }
            catch(Exception) when(life.IsCancellationRequested||remote?.State!=WebSocketState.Open){ }
        }
        public bool Saw(string method){lock(Requests)return Requests.Any(r=>r.GetProperty("method").GetString()==method);}
        public void Dispose(){life.Cancel();Socket?.Abort();Socket?.Dispose();remote?.Dispose();listener.Close();life.Dispose();}
    }
    [Fact] public async Task StopDuringReplyWaitCancelsInUnder100ms()
    {
        using var p=new Peer{Reply=_=>null};await p.Start();var call=p.Socket.Call("Browser.getVersion",timeoutMs:300000);
        for(int i=0;i<100&&!p.Saw("Browser.getVersion");i++)await Task.Delay(5);
        Assert.True(p.Saw("Browser.getVersion"));var sw=Stopwatch.StartNew();p.Stop=true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>call.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(sw.ElapsedMilliseconds<150,$"stop took {sw.ElapsedMilliseconds} ms");
    }
    [Fact] public async Task StopWhileQueuedForSendDoesNotSend()
    {
        using var p=new Peer();await p.Start();
        var gate=(SemaphoreSlim)typeof(CdpSocket).GetField("sending",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(p.Socket)!;
        await gate.WaitAsync();
        try{var call=p.Socket.Call("Browser.getVersion",timeoutMs:300000);await Task.Delay(30);p.Stop=true;await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>call.WaitAsync(TimeSpan.FromSeconds(1)));}
        finally{gate.Release();}
        await Task.Delay(50);Assert.False(p.Saw("Browser.getVersion"));
    }
    sealed class StalledTransport:WebSocket
    {
        bool aborted;
        public override WebSocketCloseStatus? CloseStatus=>null;
        public override string? CloseStatusDescription=>null;
        public override WebSocketState State=>aborted?WebSocketState.Aborted:WebSocketState.Open;
        public override string? SubProtocol=>null;
        public override void Abort()=>aborted=true;
        public override void Dispose()=>aborted=true;
        public override Task CloseAsync(WebSocketCloseStatus status,string? text,CancellationToken ct)=>Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status,string? text,CancellationToken ct)=>Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b,CancellationToken ct)=>throw new NotSupportedException();
        public override async Task SendAsync(ArraySegment<byte> b,WebSocketMessageType type,bool end,CancellationToken ct)=>await Task.Delay(Timeout.Infinite,ct);
    }
    [Fact] public async Task BackpressuredSendIsBoundedAndAbortsSocket()
    {
        // Deterministic backpressure: loopback buffers differ across Windows/Linux and may absorb a whole large message.
        using var transport=new StalledTransport();
        using var socket=(CdpSocket)Activator.CreateInstance(typeof(CdpSocket),BindingFlags.Instance|BindingFlags.NonPublic,null,
            new object[]{transport,(Func<bool>)(()=>false),(Action<string,Dictionary<string,object?>>)((_,_)=>{})},null)!;
        var sw=Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(()=>socket.Call("Input.insertText",new{text="test"},timeoutMs:100));
        Assert.True(sw.ElapsedMilliseconds<1000,$"send took {sw.ElapsedMilliseconds} ms");Assert.False(socket.Open);
    }
    static object Protocol(JsonElement q,bool editable=true,bool disabled=false,bool focus=true,bool stale=false)
    {
        string method=q.GetProperty("method").GetString()!;
        if(method=="Target.attachToTarget")return new{sessionId="session"};
        if(method=="Target.getTargets")return new{targetInfos=new[]{new{targetId="target",type="page",url="https://example.test",title="Test"}}};
        if(method=="Page.getFrameTree")return new{frameTree=new{frame=new{id="frame",loaderId="loader"}}};
        if(method=="Page.createIsolatedWorld")return new{executionContextId=1};
        if(method=="Runtime.evaluate")return new{result=new{value="visible"}};
        if(method=="Runtime.callFunctionOn")
        {
            string fn=q.GetProperty("params").GetProperty("functionDeclaration").GetString()!;
            object value=fn==ChromePageJs.Snapshot?new{g=1,n=1,title="Test",url="https://example.test",vw=800,vh=600,items=new[]{new{r=1,k="textbox",n="Field"}},cut=false}:
                fn==ChromePageJs.Locate?new{kind="input",name="Field",how="focus",dis=disabled,editable}:
                fn==ChromePageJs.FocusSelect||fn==ChromePageJs.FocusProof?new{a=focus,len=0}:
                fn==ChromePageJs.ReadValue?(stale?(object)new{e="stale"}:new{v="unchanged",len=9,pw=false}):new{ok=false};
            return new{result=new{value}};
        }
        return new{};
    }
    [Theory,InlineData(false,true,true,false,"not-editable",false),InlineData(false,false,true,false,"not-editable",false),InlineData(true,false,false,false,"no-focus",false),InlineData(true,false,true,false,"verify-failed",true),InlineData(true,false,true,true,"verify-failed",true)]
    public async Task FillRequiresEditableFocusAndReadback(bool editable,bool disabled,bool focus,bool stale,string rule,bool inserted)
    {
        using var p=new Peer{Reply=q=>Protocol(q,editable,disabled,focus,stale)};await p.Start();
        var state=new ChromeRunState();state.Alias("target");using var c=new ChromeFree(p.Socket,state,()=>p.Stop,(_,_)=>{});
        Assert.True((await c.Run("chrome_snapshot",J(new{tab="t1"}))).Ok);
        var r=await c.Run("chrome_fill",J(new{@ref="r1",text="supplied",enter=true}));
        Assert.False(r.Ok);Assert.Equal(rule,r.Rule);Assert.Equal(inserted,p.Saw("Input.insertText"));Assert.False(p.Saw("Input.dispatchKeyEvent"));
    }
    [Fact] public async Task AppendCannotVerifyAnUnchangedFieldThatAlreadyContainsTheText()
    {
        using var p=new Peer{Reply=q=>q.GetProperty("method").GetString()=="Runtime.callFunctionOn"&&q.GetProperty("params").GetProperty("functionDeclaration").GetString()==ChromePageJs.FocusSelect
            ?new{result=new{value=new{a=true,len=9}}}:Protocol(q)};await p.Start();
        var state=new ChromeRunState();state.Alias("target");using var c=new ChromeFree(p.Socket,state,()=>p.Stop,(_,_)=>{});
        await c.Run("chrome_snapshot",J(new{tab="t1"}));
        var r=await c.Run("chrome_fill",J(new{@ref="r1",text="unchanged",clear=false,enter=true}));
        Assert.False(r.Ok);Assert.Equal("verify-failed",r.Rule);Assert.True(p.Saw("Input.insertText"));Assert.False(p.Saw("Input.dispatchKeyEvent"));
    }
    [Theory,InlineData(true),InlineData(false)]
    public async Task StoppedDragReleasesOrAborts(bool releaseAnswers)
    {
        bool released=false;using var p=new Peer();p.Reply=q=>
        {
            if(q.GetProperty("method").GetString()=="Input.dispatchMouseEvent")
            {
                string type=q.GetProperty("params").GetProperty("type").GetString()!;
                if(type=="mousePressed")p.Stop=true;
                if(type=="mouseReleased"){released=true;if(!releaseAnswers)return null;}
            }
            return Protocol(q);
        };
        await p.Start();var state=new ChromeRunState();state.Alias("target");using var c=new ChromeFree(p.Socket,state,()=>p.Stop,(_,_)=>{});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>c.Mouse("target","drag",10,10,50,50,"left",1,0,0,600,""));
        Assert.True(released);Assert.Equal(releaseAnswers,p.Socket.Open);
    }
}
