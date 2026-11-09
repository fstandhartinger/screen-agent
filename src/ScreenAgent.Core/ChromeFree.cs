using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
namespace ScreenAgent.Core;
// The free Chrome toolset's CDP client (general computer control, unrestricted mode): his running, signed-in Chrome, no scope and no confirm. Plain .NET, so the whole tool logic is
// exercised on Linux against a real Chrome (tests/ChromeFreeLiveTests) and on the Windows runner (--selftest-chrome). The guarded client (ChromeCdp.cs) is untouched and keeps its own allowlist.
public sealed class CdpError(string code,string message=""):Exception(message.Length==0?code:message){public string Code=>code;}
// A tool that cannot do what was asked: `Rule` is the machine reason, the message the German sentence with the way out.
public sealed class ChromeToolFail(string rule,string text,bool stale=false):Exception(text){public string Rule=>rule;public bool Stale=>stale;}
public sealed class CdpSocket:IDisposable
{
    readonly ClientWebSocket ws=new();
    readonly CancellationTokenSource life=new();
    readonly Dictionary<int,TaskCompletionSource<JsonElement>> waiting=[];
    readonly SemaphoreSlim sending=new(1,1);
    readonly Func<bool> stopped;
    readonly Action<string,Dictionary<string,object?>> log;
    int next;
    // (method, sessionId, params) on the reader thread: a handler must not block and must not await a Call inline.
    public event Action<string,string?,JsonElement>? Event;
    public bool Open=>ws.State==WebSocketState.Open&&!life.IsCancellationRequested;
    CdpSocket(Func<bool> stopped,Action<string,Dictionary<string,object?>> log){this.stopped=stopped;this.log=log;}
    // The handshake only completes once Chrome's allow prompt was answered (default profile); the caller runs the allow clicker meanwhile and bounds the wait with `ct`.
    public static async Task<CdpSocket> Connect(Uri uri,Func<bool> stopped,Action<string,Dictionary<string,object?>> log,CancellationToken ct)
    {
        var s=new CdpSocket(stopped,log);
        s.ws.Options.Proxy=null;   // loopback: without this the first connect waits about a second for the system's proxy auto-detection
        try{await s.ws.ConnectAsync(uri,ct);}
        catch{s.Dispose();throw;}
        _=Task.Run(s.ReadLoop);
        return s;
    }
    async Task ReadLoop()
    {
        var buf=new byte[64*1024];
        try
        {
            while(ws.State==WebSocketState.Open&&!life.IsCancellationRequested)
            {
                using var ms=new MemoryStream();WebSocketReceiveResult r;
                do{r=await ws.ReceiveAsync(buf,life.Token);if(r.MessageType==WebSocketMessageType.Close)return;ms.Write(buf,0,r.Count);if(ms.Length>64<<20)return;}while(!r.EndOfMessage);
                JsonDocument doc;try{doc=JsonDocument.Parse(ms.GetBuffer().AsMemory(0,(int)ms.Length));}catch(JsonException){continue;}   // a malformed frame is skipped; the call it belonged to times out and says so
                using(doc)
                {
                    var root=doc.RootElement;
                    if(root.TryGetProperty("id",out var id)&&id.TryGetInt32(out int n))
                    {
                        TaskCompletionSource<JsonElement>? tcs;lock(waiting){waiting.Remove(n,out tcs);}
                        if(tcs is null)continue;
                        if(root.TryGetProperty("error",out var err))tcs.TrySetException(new CdpError("cdp-error",(err.TryGetProperty("message",out var em)?em.GetString():"")??""));
                        else tcs.TrySetResult(root.TryGetProperty("result",out var res)?res.Clone():default);
                    }
                    else if(root.TryGetProperty("method",out var mm)&&Event is {} ev)
                    {
                        string? sid=root.TryGetProperty("sessionId",out var s)?s.GetString():null;
                        try{ev(mm.GetString()??"",sid,root.TryGetProperty("params",out var p)?p.Clone():default);}
                        catch(Exception e){log("computer.chrome.event_error",new(){["type"]=e.GetType().Name});}
                    }
                }
            }
        }
        catch(Exception e){if(!life.IsCancellationRequested)log("computer.chrome.closed",new(){["type"]=e.GetType().Name});}
        finally
        {
            try{life.Cancel();}catch(ObjectDisposedException){ }   // disposed while the reader was ending: nothing left to cancel
            lock(waiting){foreach(var t in waiting.Values)t.TrySetException(new CdpError("closed"));waiting.Clear();}
        }
    }
    // Throws CdpError("closed") when the socket is gone, TimeoutException when Chrome does not answer in time, OperationCanceledException when the run was stopped.
    public async Task<JsonElement> Call(string method,object? args=null,string? session=null,CancellationToken ct=default,int timeoutMs=10000,bool evenStopped=false)
    {
        if(!CdpMethods.OkFree(method))throw new CdpError("method-not-allowed",method);
        if(!evenStopped&&stopped())throw new OperationCanceledException("run stopped");
        if(!Open)throw new CdpError("closed");
        int id=Interlocked.Increment(ref next);var tcs=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(waiting)waiting[id]=tcs;
        var body=new Dictionary<string,object?>{["id"]=id,["method"]=method};
        if(args is not null)body["params"]=args;
        if(session is not null)body["sessionId"]=session;
        using var deadline=new CancellationTokenSource(timeoutMs);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct,life.Token,deadline.Token);
        int runCancelled=0;
        using var watch=new Timer(_=>{if(!evenStopped&&stopped()){Interlocked.Exchange(ref runCancelled,1);try{timeout.Cancel();}catch(ObjectDisposedException){ /* Call already ended. */ }}},null,0,25);
        bool inSend=false;
        try
        {
            await sending.WaitAsync(timeout.Token);
            try
            {
                if(!evenStopped&&stopped())throw new OperationCanceledException("run stopped");
                timeout.Token.ThrowIfCancellationRequested();
                inSend=true;
                await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(body),WebSocketMessageType.Text,true,timeout.Token);
                inSend=false;
            }
            finally{sending.Release();}
            return await tcs.Task.WaitAsync(timeout.Token);
        }
        catch(OperationCanceledException)
        {
            bool cancelled=ct.IsCancellationRequested||Volatile.Read(ref runCancelled)!=0||(!evenStopped&&stopped());
            bool closed=life.IsCancellationRequested;
            if(inSend)Abort(); // A partial protocol message must never be reused after its deadline.
            if(cancelled)throw new OperationCanceledException("run stopped",ct);
            if(deadline.IsCancellationRequested)throw new TimeoutException(method);
            if(closed)throw new CdpError("closed");
            throw new TimeoutException(method);
        }
        catch(Exception e) when(e is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            if(ct.IsCancellationRequested||(!evenStopped&&stopped()))throw new OperationCanceledException("run stopped");
            if(deadline.IsCancellationRequested){if(inSend)Abort();throw new TimeoutException(method);}
            throw new CdpError("closed",e.GetType().Name);
        }
        finally{lock(waiting)waiting.Remove(id);}
    }
    public void Abort(){life.Cancel();ws.Abort();}

    public void Dispose()
    {
        // Teardown is also the cleanup path after stop; do not wait for an unresponsive peer's close handshake.
        try{life.Cancel();ws.Abort();ws.Dispose();}catch(ObjectDisposedException){ /* Already disposed. */ }
    }
}
public sealed record ChromeShot(string Jpeg,int Width,int Height,double Scale,string Tab,bool Full,double CssWidth,double CssHeight);
// Text = German status or the data read. ObserveTab = the tab to picture after an acting tool. Shot = the picture of chrome_screenshot.
public sealed record ChromeResult(bool Ok,string Text,string Rule="free",bool Stale=false,string? Target=null,string? Domain=null,string? ObserveTab=null,ChromeShot? Shot=null);
// What outlives one connection within a run: the short tab names the model uses (t1, t2, ...), which tabs the run opened, and the ref and snapshot counters (a ref number is never reused in a run).
public sealed class ChromeRunState
{
    readonly Dictionary<string,int> alias=new(StringComparer.Ordinal);
    int nextAlias;
    public readonly HashSet<string> Own=new(StringComparer.Ordinal);
    public string? LastTab;
    public int NextRef=1,NextGen=1;
    public string Alias(string targetId){lock(alias){if(!alias.TryGetValue(targetId,out int n)){n=++nextAlias;alias[targetId]=n;}return "t"+n;}}
    public void Forget(string targetId){lock(alias)alias.Remove(targetId);Own.Remove(targetId);if(LastTab==targetId)LastTab=null;}
    // "t3", a full target id, or the unambiguous start of one (6+ characters).
    public string? Resolve(string name)
    {
        name=name.Trim().Trim('[',']');
        lock(alias)
        {
            if(name.Length>1&&name[0] is 't' or 'T'&&int.TryParse(name[1..],out int n))return alias.FirstOrDefault(a=>a.Value==n).Key;
            if(alias.ContainsKey(name))return name;
            if(name.Length>=6){var hit=alias.Keys.Where(k=>k.StartsWith(name,StringComparison.OrdinalIgnoreCase)).ToList();if(hit.Count==1)return hit[0];}
        }
        return null;
    }
}
public sealed class ChromeFree:IDisposable
{
    public static readonly string[] Tools=["chrome_tabs","chrome_open","chrome_navigate","chrome_activate","chrome_close","chrome_snapshot","chrome_click","chrome_fill","chrome_key","chrome_read","chrome_eval","chrome_screenshot","scroll"];
    // Tools marked A in the contract: they act, so the app attaches a fresh picture of the tab afterwards.
    public static bool Acts(string tool)=>tool is "chrome_open" or "chrome_navigate" or "chrome_activate" or "chrome_click" or "chrome_fill" or "chrome_key" or "scroll";
    const int InputMs=4000,MaxJpegChars=1_300_000;
    readonly CdpSocket socket;
    readonly ChromeRunState state;
    readonly Func<bool> stopped;
    readonly Action<string,Dictionary<string,object?>> log;
    readonly ConcurrentDictionary<string,string> sessions=new(StringComparer.Ordinal);       // targetId -> sessionId
    readonly ConcurrentDictionary<string,bool> pageEnabled=new(StringComparer.Ordinal);    // targetId -> Page.enable sent (dialog events)
    readonly ConcurrentDictionary<string,(string Loader,int Id)> worlds=new(StringComparer.Ordinal);   // targetId -> isolated world context of the document with that loader id
    readonly Dictionary<string,(int Gen,int Base,int Count)> snaps=new(StringComparer.Ordinal);   // the one live snapshot per tab
    readonly Dictionary<string,double> shotFactor=new(StringComparer.Ordinal);               // picture pixels per CSS pixel at scale 1, measured
    readonly ConcurrentQueue<string> dialogs=new();
    public string BrowserVersion {get;private set;}="";
    public bool Open=>socket.Open;
    public ChromeRunState State=>state;
    public ChromeFree(CdpSocket socket,ChromeRunState state,Func<bool> stopped,Action<string,Dictionary<string,object?>> log)
    {
        this.socket=socket;this.state=state;this.stopped=stopped;this.log=log;
        socket.Event+=OnEvent;
    }
    public async Task<string> Version(CancellationToken ct,int timeoutMs=3000)
    {
        var v=await socket.Call("Browser.getVersion",null,null,ct,timeoutMs);
        return BrowserVersion=v.TryGetProperty("product",out var p)?p.GetString()??"":"";
    }
    void OnEvent(string method,string? sid,JsonElement p)
    {
        if(method=="Page.javascriptDialogOpening")
        {
            string type=p.ValueKind==JsonValueKind.Object&&p.TryGetProperty("type",out var tp)?tp.GetString()??"":"";
            string msg=p.ValueKind==JsonValueKind.Object&&p.TryGetProperty("message",out var mp)?mp.GetString()??"":"";
            // A page dialog blocks every call to its tab. alert: closed. beforeunload: the page is left (the model asked to go). confirm / prompt: cancelled, never answered "yes" on its own.
            bool accept=type is "alert" or "beforeunload";
            log("computer.chrome.dialog",new(){["type"]=type,["chars"]=msg.Length});
            if(type!="beforeunload")dialogs.Enqueue("Die Seite zeigte einen Dialog ("+type+"): „"+ChromeJs.Cut(msg.Replace('\n',' '),200,out _)+"“. Er wurde "+(type=="alert"?"geschlossen":"abgebrochen")+".");
            _=Task.Run(async()=>{try{await socket.Call("Page.handleJavaScriptDialog",new{accept},sid,default,3000);}catch(Exception e) when(e is CdpError or TimeoutException or OperationCanceledException){ }});   // best effort: a dialog that stays open makes the next call time out, which is reported
        }
        else if(method=="Target.detachedFromTarget"&&p.ValueKind==JsonValueKind.Object&&p.TryGetProperty("sessionId",out var ds))
        {
            string gone=ds.GetString()??"";
            foreach(var kv in sessions)if(kv.Value==gone){sessions.TryRemove(kv.Key,out _);pageEnabled.TryRemove(kv.Key,out _);worlds.TryRemove(kv.Key,out _);}
        }
    }
    // ---- plumbing
    async Task Sleep(int ms,CancellationToken ct)
    {
        long end=Environment.TickCount64+ms;
        for(;;)
        {
            if(stopped())throw new OperationCanceledException("run stopped");
            long left=end-Environment.TickCount64;if(left<=0)return;
            await Task.Delay((int)Math.Min(left,50),ct);
        }
    }
    async Task<string> Session(string tab,bool page,CancellationToken ct)
    {
        if(!sessions.TryGetValue(tab,out var s))
        {
            JsonElement r;
            try{r=await socket.Call("Target.attachToTarget",new{targetId=tab,flatten=true},null,ct,4000);}
            catch(CdpError e) when(e.Code=="cdp-error"){state.Forget(tab);throw new ChromeToolFail("not-found","Den Tab gibt es nicht mehr. chrome_tabs zeigt die aktuellen.");}
            s=r.GetProperty("sessionId").GetString()!;sessions[tab]=s;
        }
        if(page&&pageEnabled.TryAdd(tab,true))
        {
            try{await socket.Call("Page.enable",null,s,ct,4000);}
            catch(Exception e) when(e is TimeoutException||e is CdpError{Code:"cdp-error"}){pageEnabled.TryRemove(tab,out _);}   // a tab that does not answer: its dialogs are then handled blind (Blind)
        }
        return s;
    }
    static bool SessionGone(CdpError e)=>e.Code=="cdp-error"&&(e.Message.Contains("ession",StringComparison.Ordinal)||e.Message.Contains("No target",StringComparison.Ordinal));
    static bool ContextGone(CdpError e)=>e.Code=="cdp-error"&&(e.Message.Contains("context",StringComparison.OrdinalIgnoreCase)||e.Message.Contains("Cannot find",StringComparison.Ordinal)||e.Message.Contains("navigated",StringComparison.OrdinalIgnoreCase));
    async Task<JsonElement> On(string tab,string method,object? args,CancellationToken ct,int timeoutMs=10000,bool page=true)
    {
        for(int i=0;;i++)
        {
            string s=await Session(tab,page,ct);
            try{return await socket.Call(method,args,s,ct,timeoutMs);}
            catch(CdpError e) when(i==0&&SessionGone(e)){sessions.TryRemove(tab,out _);pageEnabled.TryRemove(tab,out _);worlds.TryRemove(tab,out _);}   // the session was closed under us (tab reloaded in another process): attach again, once
        }
    }
    // A dialog that opened before we were attached sends no event: when a call times out, close it blind and say so.
    async Task<bool> Blind(string tab,CancellationToken ct)
    {
        try{await On(tab,"Page.handleJavaScriptDialog",new{accept=false},ct,2000);dialogs.Enqueue("Ein offener Dialog der Seite wurde abgebrochen.");return true;}
        catch(Exception e) when(e is CdpError{Code:"cdp-error"} or TimeoutException){return false;}   // no dialog was open (or the tab is silent): nothing was closed
    }
    async Task<int> World(string tab,CancellationToken ct)
    {
        // The world is only trusted for the very document it was made in (the main frame's loader id is read each time): context ids are small numbers per renderer process, and after
        // a navigation into another process a remembered id can be the id of a live context of ANOTHER frame (seen on the Windows runner: the snapshot came from an iframe).
        var ft=await On(tab,"Page.getFrameTree",null,ct,4000);var f=ft.GetProperty("frameTree").GetProperty("frame");
        string frame=S(f,"id"),loader=S(f,"loaderId");
        if(worlds.TryGetValue(tab,out var w)&&w.Loader==loader)return w.Id;
        var r=await On(tab,"Page.createIsolatedWorld",new{frameId=frame,worldName="screen-agent"},ct,4000);
        int id=r.GetProperty("executionContextId").GetInt32();worlds[tab]=(loader,id);return id;
    }
    // One compiled function in the tab's isolated world, result by value.
    async Task<JsonElement> InWorld(string tab,string fn,object?[] args,CancellationToken ct,int timeoutMs=8000)
    {
        for(int i=0;;i++)
        {
            int world=await World(tab,ct);
            JsonElement r;
            try
            {
                r=await On(tab,"Runtime.callFunctionOn",new Dictionary<string,object?>{["functionDeclaration"]=fn,["executionContextId"]=world,["returnByValue"]=true,["arguments"]=args.Select(a=>new Dictionary<string,object?>{["value"]=a}).ToArray()},ct,timeoutMs);
            }
            catch(CdpError e) when(i<2&&ContextGone(e)){worlds.TryRemove(tab,out _);continue;}   // the page navigated: its world is gone, a new one has no refs (they answer "stale")
            catch(TimeoutException) when(i==0)
            {
                if(!await Blind(tab,ct))throw;   // no dialog was in the way: the tab simply does not answer
                continue;
            }
            if(r.TryGetProperty("exceptionDetails",out var ex))throw new CdpError("page-function",Describe(ex));
            return r.GetProperty("result").TryGetProperty("value",out var v)?v.Clone():default;
        }
    }
    static string Describe(JsonElement ex)
    {
        string d=ex.TryGetProperty("exception",out var e)&&e.TryGetProperty("description",out var ds)&&ds.ValueKind==JsonValueKind.String?ds.GetString()??"":"";
        if(d.Length==0&&ex.TryGetProperty("exception",out var e2)&&e2.TryGetProperty("value",out var ev))d=ev.ValueKind==JsonValueKind.String?ev.GetString()??"":ev.GetRawText();
        if(d.Length==0)d=ex.TryGetProperty("text",out var t)?t.GetString()??"":"";
        return d;
    }
    static string S(JsonElement o,string n)=>o.ValueKind==JsonValueKind.Object&&o.TryGetProperty(n,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
    static double D(JsonElement o,string n)=>o.ValueKind==JsonValueKind.Object&&o.TryGetProperty(n,out var v)&&v.ValueKind==JsonValueKind.Number?v.GetDouble():0;
    static bool B(JsonElement o,string n)=>o.ValueKind==JsonValueKind.Object&&o.TryGetProperty(n,out var v)&&v.ValueKind==JsonValueKind.True;
    static string Cap(string? s,int n){s=new string((s??"").Where(c=>!char.IsControl(c)).ToArray());return ChromeJs.Cut(s,n,out bool cut)+(cut?"…":"");}
    static string? DomainOf(string url)=>Domains.HostOf(url) is {Length:>0} h?Domains.Etld1(h):null;
    string Name(string tab)=>"["+state.Alias(tab)+"]";
    string TabArg(JsonElement a)
    {
        string? given=ComputerWire.ArgString(a,"tab",200);
        if(string.IsNullOrWhiteSpace(given))return state.LastTab??throw new ChromeToolFail("bad-args","tab fehlt. chrome_tabs zeigt die Tabs, chrome_open öffnet einen eigenen.");
        string id=state.Resolve(given)??throw new ChromeToolFail("not-found","Tab "+Cap(given,40)+" gibt es nicht (mehr). chrome_tabs zeigt die aktuellen.");
        state.LastTab=id;return id;
    }
    (string Tab,int Gen,int N) RefArg(JsonElement a)
    {
        string given=(ComputerWire.ArgString(a,"ref",40)??"").Trim().Trim('[',']');
        if(given.StartsWith('r')||given.StartsWith('R'))given=given[1..];
        if(!int.TryParse(given,out int n))throw new ChromeToolFail("bad-args","ref fehlt oder ist kein Ref wie r12. chrome_snapshot liefert die Refs.");
        foreach(var kv in snaps)if(n>=kv.Value.Base&&n<kv.Value.Base+kv.Value.Count){state.LastTab=kv.Key;return (kv.Key,kv.Value.Gen,n);}
        throw Stale(n);
    }
    static ChromeToolFail Stale(int n)=>new("stale-ref","Ref r"+n+" gilt nicht mehr (neue Aufnahme oder die Seite hat gewechselt). Neuen chrome_snapshot holen.",true);
    async Task<List<(string Id,string Url,string Title,string Opener)>> Targets(CancellationToken ct)
    {
        var r=await socket.Call("Target.getTargets",null,null,ct,4000);var list=new List<(string,string,string,string)>();
        foreach(var t in r.GetProperty("targetInfos").EnumerateArray())
        {
            if(S(t,"type")!="page")continue;
            string url=S(t,"url");
            if(url.StartsWith("devtools://",StringComparison.Ordinal))continue;   // DevTools' own windows are not his pages
            list.Add((S(t,"targetId"),url,S(t,"title"),S(t,"openerId")));
        }
        return list;
    }
    async Task<(string Url,string Title)> Info(string tab,CancellationToken ct)
    {
        var t=(await Targets(ct)).FirstOrDefault(x=>x.Id==tab);
        return t.Id is null?("",""):(t.Url,t.Title);
    }
    // ---- dispatch: one tool call. Never throws for a tool-level problem; CdpError("closed") and a stop go up to the app (reconnect / killed).
    public async Task<ChromeResult> Run(string tool,JsonElement args,CancellationToken ct=default)
    {
        ChromeResult r;
        try
        {
            r=tool switch
            {
                "chrome_tabs"=>await TabsTool(ct),
                "chrome_open"=>await OpenTool(args,ct),
                "chrome_navigate"=>await NavigateTool(args,ct),
                "chrome_activate"=>await ActivateTool(args,ct),
                "chrome_close"=>await CloseTool(args,ct),
                "chrome_snapshot"=>await SnapshotTool(args,ct),
                "chrome_click"=>await ClickTool(args,ct),
                "chrome_fill"=>await FillTool(args,ct),
                "chrome_key"=>await KeyTool(args,ct),
                "chrome_read"=>await ReadTool(args,ct),
                "chrome_eval"=>await EvalTool(args,ct),
                "chrome_screenshot"=>await ShotTool(args,ct),
                "scroll"=>await ScrollTool(args,ct),
                _=>new(false,"Unbekanntes Werkzeug.","unknown-tool")
            };
        }
        catch(ChromeToolFail f){r=new(false,f.Message,f.Rule,f.Stale);}   // the tool's own refusal: it is the result
        catch(TimeoutException){r=new(false,"Der Tab antwortet nicht (im Hintergrund, minimiert oder hängt). chrome_activate holt ihn nach vorn; sonst die Desktop-Werkzeuge nehmen.","timeout");}   // reported to the model as the result (rule timeout)
        catch(CdpError e) when(e.Code is "cdp-error" or "page-function"){r=new(false,"Chrome hat den Schritt abgelehnt: "+Cap(e.Message,200),e.Code);}   // reported to the model as the result
        if(dialogs.IsEmpty)return r;
        var notes=new StringBuilder(r.Text);
        while(dialogs.TryDequeue(out var d))notes.Append('\n').Append(d);
        return r with{Text=notes.ToString()};
    }
    // ---- tabs
    async Task<ChromeResult> TabsTool(CancellationToken ct)
    {
        var tabs=await Targets(ct);
        // Which tab is in front of its window: document.visibilityState, asked in parallel, 300 ms each, at most 40 tabs (a sleeping tab does not answer and is listed without the mark).
        var probe=tabs.Take(40).Select(async t=>
        {
            string vis="";int win=0;
            try
            {
                var v=await On(t.Id,"Runtime.evaluate",new{expression="document.visibilityState",returnByValue=true},ct,300,page:false);
                vis=v.TryGetProperty("result",out var rr)?S(rr,"value"):"";
            }
            catch(Exception e) when(e is TimeoutException or CdpError{Code:"cdp-error"} or ChromeToolFail){ }   // no answer in 300 ms: listed without the mark
            try
            {
                var w=await socket.Call("Browser.getWindowForTarget",new{targetId=t.Id},null,ct,300);
                win=w.TryGetProperty("windowId",out var wi)&&wi.TryGetInt32(out int n)?n:0;
            }
            catch(Exception e) when(e is TimeoutException or CdpError{Code:"cdp-error"}){ }   // the window is a detail of the listing only
            return (t.Id,vis,win);
        }).ToList();
        var seen=(await Task.WhenAll(probe)).ToDictionary(x=>x.Id,x=>(x.vis,x.win));
        var windows=new List<int>();
        foreach(var x in seen.Values)if(x.win!=0&&!windows.Contains(x.win))windows.Add(x.win);
        var sb=new StringBuilder();
        sb.Append(tabs.Count).Append(tabs.Count==1?" Tab":" Tabs").Append(windows.Count>1?" in "+windows.Count+" Fenstern":"").Append(". aktiv = im Fenster vorn, eigen = von diesem Lauf geöffnet.\n");
        foreach(var t in tabs)
        {
            seen.TryGetValue(t.Id,out var p);
            sb.Append(Name(t.Id));
            if(p.vis=="visible")sb.Append(" aktiv");
            if(state.Own.Contains(t.Id))sb.Append(" eigen");
            sb.Append(" \"").Append(Cap(t.Title,80).Replace('"','\'')).Append("\" ").Append(Cap(t.Url,160));
            if(windows.Count>1&&p.win!=0)sb.Append(" · Fenster ").Append(windows.IndexOf(p.win)+1);
            if(p.vis is null or "")sb.Append(" · antwortet nicht");
            sb.Append('\n');
        }
        if(tabs.Count>40)sb.Append("(aktiv nur für die ersten 40 Tabs geprüft)\n");
        return new(true,sb.ToString(),Target:"tabs");
    }
    static string FixUrl(string url)
    {
        url=url.Trim();
        if(url.Length==0)return "about:blank";
        if(url.Contains("://",StringComparison.Ordinal)||url.StartsWith("about:",StringComparison.Ordinal)||url.StartsWith("data:",StringComparison.Ordinal)||url.StartsWith("chrome:",StringComparison.Ordinal)||url.StartsWith("file:",StringComparison.Ordinal))return url;
        return (url.StartsWith("localhost",StringComparison.Ordinal)||url.StartsWith("127.",StringComparison.Ordinal)?"http://":"https://")+url;
    }
    // readyState of the tab's current document; "" while the document is changing.
    async Task<(string Ready,string Url,string Title)> Ready(string tab,CancellationToken ct)
    {
        try
        {
            var r=await On(tab,"Runtime.evaluate",new{expression="document.readyState+'\\n'+location.href+'\\n'+document.title",returnByValue=true},ct,2000);
            var parts=(r.TryGetProperty("result",out var rr)?S(rr,"value"):"").Split('\n',3);
            return parts.Length==3?(parts[0],parts[1],parts[2]):("","","");
        }
        catch(Exception e) when(e is TimeoutException||e is CdpError{Code:"cdp-error"}){return ("","","");}   // the document is changing: the caller asks again
    }
    async Task<string> Loader(string tab,CancellationToken ct)
    {
        try{var ft=await On(tab,"Page.getFrameTree",null,ct,2000);return S(ft.GetProperty("frameTree").GetProperty("frame"),"loaderId");}
        catch(Exception e) when(e is TimeoutException||e is CdpError{Code:"cdp-error"}){return "";}   // the document is changing: the caller asks again
    }
    // Waits for the load: the wanted loader (or any other than `oldLoader`) must be the tab's document, then readyState complete. Returns (done, url, title).
    async Task<(bool Done,string Url,string Title)> WaitLoaded(string tab,string? wantLoader,string? oldLoader,int maxMs,bool notBlank,CancellationToken ct)
    {
        long t0=Environment.TickCount64;string url="",title="";bool switched=wantLoader is null&&oldLoader is null;
        while(Environment.TickCount64-t0<maxMs)
        {
            if(!switched)
            {
                string now=await Loader(tab,ct);
                switched=now.Length>0&&(wantLoader is not null?now==wantLoader:now!=oldLoader);
                if(!switched&&oldLoader is not null&&wantLoader is null&&Environment.TickCount64-t0>800)switched=true;   // back/forward inside one document (hash, pushState): the loader stays
                if(!switched){await Sleep(60,ct);continue;}
            }
            var (ready,u,t)=await Ready(tab,ct);
            if(u.Length>0){url=u;title=t;}
            if(ready=="complete"&&(!notBlank||u!="about:blank"||Environment.TickCount64-t0>1500))return (true,url,title);
            await Sleep(80,ct);
        }
        if(url.Length==0)(url,title)=await Info(tab,ct);
        return (false,url,title);
    }
    async Task<ChromeResult> OpenTool(JsonElement a,CancellationToken ct)
    {
        string url=FixUrl(ComputerWire.ArgString(a,"url",4000)??"");bool background=ComputerWire.ArgBool(a,"background");
        var r=await socket.Call("Target.createTarget",new{url,background},null,ct,8000);
        string id=r.GetProperty("targetId").GetString()!;state.Own.Add(id);state.LastTab=id;
        var (done,now,title)=await WaitLoaded(id,null,null,10000,url!="about:blank",ct);
        return new(true,"Eigener Tab "+Name(id)+" geöffnet"+(background?" (im Hintergrund)":"")+": \""+Cap(title,80)+"\" "+Cap(now.Length>0?now:url,200)+(done?"":" · lädt noch"),Target:title.Length>0?title:url,Domain:DomainOf(now.Length>0?now:url),ObserveTab:background?null:id);
    }
    async Task<ChromeResult> NavigateTool(JsonElement a,CancellationToken ct)
    {
        string tab=TabArg(a);string url=(ComputerWire.ArgString(a,"url",4000)??"").Trim();
        if(url.Length==0)throw new ChromeToolFail("bad-args","url fehlt: eine Adresse oder back, forward, reload.");
        string old=await Loader(tab,ct);string? want=null;
        switch(url.ToLowerInvariant())
        {
            case "back":case "forward":case "zurück":case "vor":
            {
                bool back=url.ToLowerInvariant() is "back" or "zurück";
                var h=await On(tab,"Page.getNavigationHistory",null,ct,4000);int cur=h.GetProperty("currentIndex").GetInt32();var entries=h.GetProperty("entries");
                int to=cur+(back?-1:1);
                if(to<0||to>=entries.GetArrayLength())throw new ChromeToolFail("no-history",back?"Es gibt keine Seite davor.":"Es gibt keine Seite danach.");
                await On(tab,"Page.navigateToHistoryEntry",new{entryId=entries[to].GetProperty("id").GetInt32()},ct,8000);
                break;
            }
            case "reload":case "neu laden":
                await On(tab,"Page.reload",null,ct,8000);break;
            default:
            {
                var r=await On(tab,"Page.navigate",new{url=FixUrl(url)},ct,20000);
                string err=S(r,"errorText");
                if(err.Length>0&&err!="net::ERR_ABORTED")return new(false,"Die Seite ließ sich nicht laden: "+Cap(err,120)+".","nav-error",ObserveTab:tab);
                want=S(r,"loaderId");
                if(want.Length==0){var here=await Ready(tab,ct);return new(true,"Auf der Seite gesprungen: "+Cap(here.Url,200),Target:here.Title,Domain:DomainOf(here.Url),ObserveTab:tab);}   // same document (a #anchor)
                break;
            }
        }
        var (done,now,title)=await WaitLoaded(tab,want,want is null?old:null,15000,false,ct);
        return new(true,(done?"Geladen":"Lädt noch (nach 15 s)")+" in "+Name(tab)+": \""+Cap(title,80)+"\" "+Cap(now,200),Target:title,Domain:DomainOf(now),ObserveTab:tab);
    }
    async Task<ChromeResult> ActivateTool(JsonElement a,CancellationToken ct)
    {
        string tab=TabArg(a);
        await socket.Call("Target.activateTarget",new{targetId=tab},null,ct,4000);
        try{await On(tab,"Page.bringToFront",null,ct,3000);}catch(TimeoutException){ }   // the target is activated; a tab that does not confirm is pictured (or reported) by the observe step
        var (url,title)=await Info(tab,ct);
        return new(true,"Tab "+Name(tab)+" ist vorn: \""+Cap(title,80)+"\"",Target:title,Domain:DomainOf(url),ObserveTab:tab);
    }
    async Task<ChromeResult> CloseTool(JsonElement a,CancellationToken ct)
    {
        string tab=TabArg(a);
        if(!state.Own.Contains(tab)&&!ComputerWire.ArgBool(a,"force"))
            throw new ChromeToolFail("not-own",Name(tab)+" ist ein Tab von Florian, kein eigener. Nur mit force:true schließen, wenn er genau das will.");
        string name=Name(tab);
        await socket.Call("Target.closeTarget",new{targetId=tab},null,ct,5000);
        sessions.TryRemove(tab,out _);pageEnabled.TryRemove(tab,out _);worlds.TryRemove(tab,out _);snaps.Remove(tab);shotFactor.Remove(tab);state.Forget(tab);
        return new(true,"Tab "+name+" geschlossen.",Target:"tab");
    }
    // Own tabs that are still open (for the log at the end of a run: the run is already over then, so this one read is made even though it is stopped).
    public async Task<int> OwnOpen(CancellationToken ct=default)
    {
        var r=await socket.Call("Target.getTargets",null,null,ct,1500,evenStopped:true);
        var ids=r.GetProperty("targetInfos").EnumerateArray().Select(t=>S(t,"targetId")).ToHashSet();
        return state.Own.Count(ids.Contains);
    }
    // Selftest only: the socket dies as if Chrome had dropped it.
    public void DropSocketForTest()=>socket.Dispose();
    // ---- snapshot
    async Task<ChromeResult> SnapshotTool(JsonElement a,CancellationToken ct)
    {
        string tab=TabArg(a);bool all=ComputerWire.ArgBool(a,"all");string? filter=ComputerWire.ArgString(a,"filter",200);
        int gen=state.NextGen++,first=state.NextRef;
        var r=await InWorld(tab,ChromePageJs.Snapshot,[gen,first,all],ct,15000);
        if(r.ValueKind!=JsonValueKind.Object)throw new CdpError("page-function","snapshot");
        int count=(int)D(r,"n");state.NextRef=first+count;snaps[tab]=(gen,first,count);
        var lines=ChromeOutline.Lines(r.TryGetProperty("items",out var items)?items:default);
        string title=S(r,"title"),url=S(r,"url");int vh=(int)D(r,"vh"),sm=(int)D(r,"sm");
        string text=ChromeOutline.Build(ChromeOutline.Header(state.Alias(tab),title,url,(int)D(r,"vw"),vh,(int)D(r,"sy"),sm),lines,filter,all,B(r,"cut"),more:sm>vh/2);
        if(B(r,"cut"))text+="\nSeitenbudget erreicht: "+S(r,"reason")+" ("+D(r,"visited")+" Knoten besucht).";
        return new(true,text,Target:title,Domain:DomainOf(url));
    }
    // ---- input
    static int ButtonMask(string b)=>b switch{"right"=>2,"middle"=>4,_=>1};
    static string Button(string? b)=>(b??"left").Trim().ToLowerInvariant() switch{"right" or "rechts"=>"right","middle" or "mitte"=>"middle",_=>"left"};
    async Task MouseClick(string tab,double x,double y,string button,int count,int mods,CancellationToken ct)
    {
        x=Math.Round(x,1);y=Math.Round(y,1);
        await On(tab,"Input.dispatchMouseEvent",new{type="mouseMoved",x,y,modifiers=mods},ct,InputMs);
        for(int i=1;i<=Math.Clamp(count,1,3);i++)
        {
            await On(tab,"Input.dispatchMouseEvent",new{type="mousePressed",x,y,button,buttons=ButtonMask(button),clickCount=i,modifiers=mods},ct,InputMs);
            await On(tab,"Input.dispatchMouseEvent",new{type="mouseReleased",x,y,button,buttons=0,clickCount=i,modifiers=mods},ct,InputMs);
        }
    }
    async Task Press(string tab,CdpKey k,CancellationToken ct)
    {
        var down=new Dictionary<string,object?>{["type"]=k.Text.Length>0?"keyDown":"rawKeyDown",["modifiers"]=k.Modifiers,["key"]=k.Key,["code"]=k.Code,["windowsVirtualKeyCode"]=k.Vk,["nativeVirtualKeyCode"]=k.Vk};
        if(k.Text.Length>0){down["text"]=k.Text;down["unmodifiedText"]=k.Text;}
        await On(tab,"Input.dispatchKeyEvent",down,ct,InputMs);
        await On(tab,"Input.dispatchKeyEvent",new Dictionary<string,object?>{["type"]="keyUp",["modifiers"]=k.Modifiers,["key"]=k.Key,["code"]=k.Code,["windowsVirtualKeyCode"]=k.Vk,["nativeVirtualKeyCode"]=k.Vk},ct,InputMs);
    }
    // Chrome drops input for a tab that is not shown in its window: the tab is brought to the front of ITS window first (the window itself is not raised). true = it had to be.
    async Task<bool> Show(string tab,CancellationToken ct)
    {
        try
        {
            var v=await On(tab,"Runtime.evaluate",new{expression="document.visibilityState",returnByValue=true},ct,1500);
            if((v.TryGetProperty("result",out var rr)?S(rr,"value"):"")!="hidden")return false;
            await On(tab,"Page.bringToFront",null,ct,3000);await Sleep(120,ct);
            return true;
        }
        catch(TimeoutException){return false;}   // a tab that does not even say whether it is shown: the input call itself reports the timeout
    }
    async Task<JsonElement> Locate(string tab,int gen,int n,string mode,CancellationToken ct)
    {
        var loc=await InWorld(tab,ChromePageJs.Locate,[n,gen,mode],ct);
        if(S(loc,"e")=="stale"||loc.ValueKind!=JsonValueKind.Object)throw Stale(n);
        return loc;
    }
    async Task<ChromeResult> ClickTool(JsonElement a,CancellationToken ct)
    {
        var (tab,gen,n)=RefArg(a);string button=Button(ComputerWire.ArgString(a,"button",12));bool twice=ComputerWire.ArgBool(a,"double");
        bool shown=await Show(tab,ct);
        var before=(await Targets(ct)).Select(t=>t.Id).ToHashSet();
        var loc=await Locate(tab,gen,n,"click",ct);
        string how=S(loc,"how"),name=S(loc,"name");
        if(how=="mouse")await MouseClick(tab,D(loc,"x"),D(loc,"y"),button,twice?2:1,0,ct);
        var sb=new StringBuilder(twice?"Doppelklick auf \"":"Geklickt: \"").Append(Cap(name,60).Replace('"','\'')).Append('"');
        if(how=="js")sb.Append(" (über element.click(): die Maus hätte ein anderes Element getroffen)");
        if(B(loc,"dis"))sb.Append(" · das Element ist deaktiviert");
        if(shown)sb.Append(" · der Tab wurde dafür in seinem Fenster nach vorn geholt");
        // a link with target=_blank or window.open: say which tab appeared; it is the run's own when it came from an own tab
        await Sleep(120,ct);
        var info=await Targets(ct);
        string see=tab;
        foreach(var t in info.Where(t=>!before.Contains(t.Id)))
        {
            if(state.Own.Contains(tab)&&t.Opener==tab)state.Own.Add(t.Id);
            sb.Append(" · neuer Tab ").Append(Name(t.Id)).Append(' ').Append(Cap(t.Url,120));
            if(t.Opener==tab)see=t.Id;   // the new tab is in front now: it is the one to picture (picturing the old one would pull it back to the front)
        }
        string url=info.FirstOrDefault(t=>t.Id==tab).Url??"";
        return new(true,sb.ToString(),Target:name,Domain:DomainOf(url),ObserveTab:see);
    }
    static string Norm(string s)=>s.Replace("\r\n","\n").Replace('\r','\n').Replace(' ',' ').TrimEnd('\n');
    async Task<ChromeResult> FillTool(JsonElement a,CancellationToken ct)
    {
        var (tab,gen,n)=RefArg(a);
        if(a.ValueKind!=JsonValueKind.Object||!a.TryGetProperty("text",out var tv)||tv.ValueKind!=JsonValueKind.String)throw new ChromeToolFail("bad-args","text fehlt.");
        string text=tv.GetString()??"";
        if(text.Length>20000)throw new ChromeToolFail("bad-args","text ist länger als 20000 Zeichen.");
        bool clear=!(a.TryGetProperty("clear",out var cv)&&cv.ValueKind==JsonValueKind.False),enter=ComputerWire.ArgBool(a,"enter");
        bool shown=await Show(tab,ct);
        var loc=await Locate(tab,gen,n,"fill",ct);
        string kind=S(loc,"kind"),name=S(loc,"name");
        if(B(loc,"dis")||!B(loc,"editable"))throw new ChromeToolFail("not-editable","Das Ziel ist deaktiviert oder nicht bearbeitbar; nichts eingegeben.");
        string url=(await Info(tab,ct)).Url;
        if(kind=="select")
        {
            var sel=await InWorld(tab,ChromePageJs.SelectOption,[n,gen,text],ct);
            if(S(sel,"e")=="stale")throw Stale(n);
            if(!B(sel,"ok"))
            {
                string options=sel.TryGetProperty("options",out var os)&&os.ValueKind==JsonValueKind.Array?string.Join(", ",os.EnumerateArray().Select(o=>"\""+Cap(o.GetString(),40)+"\"")):"";
                return new(false,"Die Auswahl hat keinen Eintrag \""+Cap(text,60)+"\". Es gibt: "+options,"not-found",ObserveTab:tab);
            }
            return new(true,"Ausgewählt: \""+Cap(S(sel,"label"),80)+"\"",Target:name,Domain:DomainOf(url),ObserveTab:tab);
        }
        if(S(loc,"how")=="mouse")await MouseClick(tab,D(loc,"x"),D(loc,"y"),"left",1,0,ct);
        var focus=await InWorld(tab,ChromePageJs.FocusSelect,[n,gen,clear],ct);
        if(S(focus,"e")=="stale")throw Stale(n);
        if(!B(focus,"a"))throw new ChromeToolFail("no-focus","Das Zielfeld hat keinen bestätigten Fokus; nichts eingegeben.");
        var inputProof=await InWorld(tab,ChromePageJs.FocusProof,[n,gen],ct);
        if(!B(inputProof,"a"))throw new ChromeToolFail("no-focus","Der Fokus hat gewechselt; nichts eingegeben.");
        int had=(int)D(focus,"len");
        if(text.Length>0)await On(tab,"Input.insertText",new{text},ct,Math.Max(InputMs,text.Length/2));
        else if(clear&&had>0)await Press(tab,new CdpKey(0,"Backspace","Backspace",8,""),ct);
        // read back before Enter (Enter may submit and take the field away)
        var back=await InWorld(tab,ChromePageJs.ReadValue,[n,gen],ct);
        string status;bool verified=false;
        if(S(back,"e")=="stale"||back.ValueKind!=JsonValueKind.Object||!back.TryGetProperty("len",out _))status="Der Feldinhalt ließ sich nicht prüfen; aktueller Inhalt unbekannt, kein Enter.";
        else
        {
            bool pw=B(back,"pw");int len=(int)D(back,"len");string now=pw?"":Norm(S(back,"v"));string want=Norm(text);
            bool same=pw?(clear?len==text.Length:len>=text.Length):(clear?now==want:now.Contains(want,StringComparison.Ordinal));
            if(!same&&clear&&kind is "input" or "textarea")
            {   // typed text did not land (a date or range field, or the page dropped it): set the value directly, with the events a framework listens to
                var setterProof=await InWorld(tab,ChromePageJs.FocusProof,[n,gen],ct);
                if(!B(setterProof,"a"))return new(false,"Der Fokus hat gewechselt; Feldinhalt nicht bestätigt, kein Enter.","no-focus",ObserveTab:tab);
                var set=await InWorld(tab,ChromePageJs.SetValue,[n,gen,text],ct);
                if(B(set,"ok"))
                {
                    back=await InWorld(tab,ChromePageJs.ReadValue,[n,gen],ct);
                    pw=B(back,"pw");len=(int)D(back,"len");now=pw?"":Norm(S(back,"v"));same=S(back,"e")==""&&back.TryGetProperty("len",out _)&&(pw?len==text.Length:now==want);
                }
            }
            verified=same;
            status=same?"eingetragen ("+text.Length+" Zeichen)":pw?"Das Feld hält jetzt "+len+" Zeichen (verdeckt), erwartet waren "+text.Length:"Das Feld enthält jetzt: \""+Cap(now,300)+"\" ("+now.Length+" Zeichen), nicht genau den Text";
        }
        if(!verified)return new(false,status,"verify-failed",Target:name,Domain:DomainOf(url),ObserveTab:tab);
        if(enter)
        {
            var proof=await InWorld(tab,ChromePageJs.FocusProof,[n,gen],ct);
            if(!B(proof,"a"))return new(false,"Der Fokus hat gewechselt; kein Enter gedrückt.","no-focus",ObserveTab:tab);
            await Press(tab,new CdpKey(0,"Enter","Enter",13,"\r"),ct);status+=" · Enter gedrückt";
        }
        if(shown)status+=" · der Tab wurde dafür in seinem Fenster nach vorn geholt";
        return new(true,"\""+Cap(name,60).Replace('"','\'')+"\": "+status,Target:name,Domain:DomainOf(url),ObserveTab:tab);
    }
    async Task<ChromeResult> KeyTool(JsonElement a,CancellationToken ct)
    {
        string tab=TabArg(a);
        var keys=CdpKeys.Parse(ComputerWire.ArgString(a,"key",200),out string bad);
        if(keys is null)throw new ChromeToolFail("bad-args","Taste \""+Cap(bad,40)+"\" verstehe ich nicht. Beispiele: enter, ctrl+a, shift+tab, esc, f5; mehrere mit Leerzeichen trennen.");
        if(keys.Count>50)throw new ChromeToolFail("bad-args","Höchstens 50 Tasten in einem Aufruf.");
        bool shown=await Show(tab,ct);
        foreach(var k in keys){await Press(tab,k,ct);if(keys.Count>1)await Sleep(20,ct);}
        string url=(await Info(tab,ct)).Url;
        return new(true,"Gedrückt in "+Name(tab)+": "+Cap(ComputerWire.ArgString(a,"key",200),80)+(shown?" · der Tab wurde dafür in seinem Fenster nach vorn geholt":""),Target:"taste",Domain:DomainOf(url),ObserveTab:tab);
    }
    async Task<(double W,double H)> Viewport(string tab,CancellationToken ct)
    {
        try
        {
            var r=await On(tab,"Runtime.evaluate",new{expression="innerWidth+'x'+innerHeight",returnByValue=true},ct,1500);
            var p=(r.TryGetProperty("result",out var rr)?S(rr,"value"):"").Split('x');
            if(p.Length==2&&double.TryParse(p[0],System.Globalization.CultureInfo.InvariantCulture,out double w)&&double.TryParse(p[1],System.Globalization.CultureInfo.InvariantCulture,out double h)&&w>0&&h>0)return (w,h);
        }
        catch(Exception e) when(e is TimeoutException||e is CdpError{Code:"cdp-error"}){ }   // unknown size: a point that is inside every window is used
        return (800,600);
    }
    async Task Wheel(string tab,double x,double y,double dx,double dy,int mods,CancellationToken ct)
    {
        try{await On(tab,"Input.dispatchMouseEvent",new{type="mouseWheel",x=Math.Round(x,1),y=Math.Round(y,1),deltaX=dx,deltaY=dy,modifiers=mods},ct,2500);}
        catch(TimeoutException)
        {   // no acknowledgement (a tab that is not painted): scroll the document itself
            await On(tab,"Runtime.evaluate",new{expression="(document.scrollingElement||document.documentElement).scrollBy({left:"+dx.ToString(System.Globalization.CultureInfo.InvariantCulture)+",top:"+dy.ToString(System.Globalization.CultureInfo.InvariantCulture)+",behavior:'instant'})"},ct,2500);
        }
    }
    async Task<ChromeResult> ScrollTool(JsonElement a,CancellationToken ct)
    {
        if(ComputerWire.ArgString(a,"window",80) is {Length:>0}&&ComputerWire.ArgString(a,"tab",200) is null)
            throw new ChromeToolFail("bad-args","scroll mit window geht über den Desktop: wheel (dy in Rasten) an der Stelle im Bild nehmen. Mit tab scrollt es die Seite über Chrome.");
        string tab=TabArg(a);int dy=Math.Clamp(ComputerWire.ArgInt(a,"dy")??600,-20000,20000);
        await Show(tab,ct);
        var (w,h)=await Viewport(tab,ct);
        await Wheel(tab,w/2,h/2,0,dy,0,ct);
        string url=(await Info(tab,ct)).Url;
        return new(true,"Gescrollt um "+dy+" px in "+Name(tab)+".",Target:"tab",Domain:DomainOf(url),ObserveTab:tab);
    }
    // The desktop mouse tools on a `tab` frame: x,y are CSS pixels of the tab's viewport ("<tab>#full": of the whole page, the point is scrolled into view first). dx/dy of the wheel are
    // pixels (the desktop toolset sends 120 per notch for `wheel` and the wire pixels for `scroll`); a wheel at 0,0 means "no place given" and turns at the middle of the page.
    public async Task<string> Mouse(string tabName,string kind,double x,double y,double x2,double y2,string button,int count,double dx,double dy,int ms,string mods,CancellationToken ct=default)
    {
        bool page=tabName.EndsWith("#full",StringComparison.Ordinal);
        string tab=state.Resolve(page?tabName[..^5]:tabName)??(page?tabName[..^5]:tabName);
        int m=CdpKeys.Mods(mods);button=Button(button);
        await Show(tab,ct);
        if(page)
        {
            var p=await InWorld(tab,ChromePageJs.PagePoint,[x,y],ct);double sx=D(p,"x")-x,sy=D(p,"y")-y;
            x+=sx;y+=sy;x2+=sx;y2+=sy;
        }
        switch(kind)
        {
            case "move":
                await On(tab,"Input.dispatchMouseEvent",new{type="mouseMoved",x,y,modifiers=m},ct,InputMs);return "Maus bewegt";
            case "click":
                await MouseClick(tab,x,y,button,count,m,ct);return count>=2?"Doppelklick":"geklickt";
            case "wheel":
                if(x==0&&y==0){var (vw,vh)=await Viewport(tab,ct);x=vw/2;y=vh/2;}
                await Wheel(tab,x,y,dx,dy,m,ct);return "gescrollt";
            case "drag":
            {
                int steps=Math.Max(12,Math.Clamp(ms,100,5000)/25);
                await On(tab,"Input.dispatchMouseEvent",new{type="mouseMoved",x,y,modifiers=m},ct,InputMs);
                try
                {
                    await On(tab,"Input.dispatchMouseEvent",new{type="mousePressed",x,y,button,buttons=ButtonMask(button),clickCount=1,modifiers=m},ct,InputMs);
                    await Sleep(80,ct);
                    for(int i=1;i<=steps;i++)
                    {
                        double f=i/(double)steps;
                        await On(tab,"Input.dispatchMouseEvent",new{type="mouseMoved",x=Math.Round(x+(x2-x)*f,1),y=Math.Round(y+(y2-y)*f,1),button,buttons=ButtonMask(button),modifiers=m},ct,InputMs);
                        await Sleep(Math.Clamp(ms,100,5000)/steps,ct);
                    }
                }
                finally
                {   // the button always comes up again, also when the run is stopped mid-drag
                    try
                    {
                        if(!sessions.TryGetValue(tab,out var s))socket.Abort();
                        else await socket.Call("Input.dispatchMouseEvent",new{type="mouseReleased",x=x2,y=y2,button,buttons=0,clickCount=1,modifiers=m},s,default,100,true);
                    }
                    catch(Exception e) when(e is CdpError or TimeoutException or OperationCanceledException){socket.Abort();}   // the socket is gone or the run stopped: Chrome releases the button with the session
                }
                return "gezogen";
            }
            default:throw new ChromeToolFail("bad-args","Unbekannte Mausaktion.");
        }
    }
    // ---- read / eval
    async Task<ChromeResult> ReadTool(JsonElement a,CancellationToken ct)
    {
        string tab=TabArg(a);int max=Math.Clamp(ComputerWire.ArgInt(a,"max")??20000,200,200000);
        var r=await InWorld(tab,ChromePageJs.Read,[max],ct,10000);
        string text=S(r,"x"),title=S(r,"t"),url=S(r,"u");int len=(int)D(r,"len");
        return new(true,"tab "+state.Alias(tab)+" · "+Cap(title,80)+" · "+Cap(url,200)+"\n"+text+(len>text.Length?"\n… gekürzt: "+text.Length+" von "+len+" Zeichen (max erhöhen oder chrome_snapshot mit filter).":""),Target:title,Domain:DomainOf(url));
    }
    static bool ParseError(JsonElement ex)
    {   // a script that does not parse never ran: it may be tried again as a function body. A SyntaxError thrown while running (JSON.parse) has a stack.
        if(!ex.TryGetProperty("exception",out var e))return false;
        return S(e,"className")=="SyntaxError"&&!S(e,"description").Contains("\n    at ",StringComparison.Ordinal);
    }
    async Task<ChromeResult> EvalTool(JsonElement a,CancellationToken ct)
    {
        string tab=TabArg(a);string js=ComputerWire.ArgString(a,"js",200000)??"";
        if(js.Trim().Length==0)throw new ChromeToolFail("bad-args","js fehlt.");
        int seconds=Math.Clamp(ComputerWire.ArgInt(a,"timeout_s")??30,1,300);
        long t0=Environment.TickCount64;
        async Task<JsonElement> Go(string code,bool repl)
        {
            int left=(int)Math.Max(500,seconds*1000-(Environment.TickCount64-t0));
            return await On(tab,"Runtime.evaluate",new Dictionary<string,object?>{["expression"]=code,["awaitPromise"]=true,["returnByValue"]=true,["userGesture"]=true,["replMode"]=repl,["timeout"]=left},ct,left+1500);
        }
        JsonElement r;
        try
        {
            // an expression (or statements, top-level await allowed) first; a function body ("return ...") only parses wrapped
            r=await Go(js,true);
            if(r.TryGetProperty("exceptionDetails",out var first)&&ParseError(first)&&ChromeJs.NeedsWrap(js))r=await Go(ChromeJs.Wrap(js),false);
        }
        catch(TimeoutException)   // the script did not finish in its time: said so below, never run twice
        {
            if(await Blind(tab,ct))return new(false,"Das Skript wurde von einem Dialog der Seite aufgehalten.","timeout");
            return new(false,"Das Skript lief länger als "+seconds+" s und wurde nicht abgewartet.","timeout");
        }
        string url=(await Info(tab,ct)).Url;
        if(r.TryGetProperty("exceptionDetails",out var ex))return new(false,"JavaScript-Fehler: "+ChromeJs.Cut(Describe(ex),1500,out _),"js-error",Domain:DomainOf(url));
        var res=r.GetProperty("result");string type=S(res,"type");string text;
        if(res.TryGetProperty("value",out var v))text=v.ValueKind==JsonValueKind.String?v.GetString()??"":v.GetRawText();
        else if(res.TryGetProperty("unserializableValue",out var u))text=u.GetString()??"";
        else text=type=="undefined"?"undefined":S(res,"description") is {Length:>0} d?d:type;
        text=ChromeJs.Cut(text,20000,out bool cut);
        return new(true,text+(cut?"\n… gekürzt auf 20000 Zeichen.":""),Target:"js",Domain:DomainOf(url));
    }
    // ---- pictures
    public static string FrameLine(ChromeShot s,string alias)
        =>"frame: tab "+s.Width+"x"+s.Height+" · tab "+alias+(s.Full?" · ganze Seite":" · sichtbarer Bereich")+" · "+Math.Round(s.CssWidth)+"x"+Math.Round(s.CssHeight)+" CSS-px";
    async Task<ChromeResult> ShotTool(JsonElement a,CancellationToken ct)
    {
        string tab=TabArg(a);bool full=ComputerWire.ArgBool(a,"full");
        var s=await Shot(tab,full,ct);
        var (url,title)=await Info(tab,ct);
        return new(true,FrameLine(s,state.Alias(tab))+"\n\""+Cap(title,80)+"\" "+Cap(url,200),Target:title,Domain:DomainOf(url),Shot:s);
    }
    async Task<string> Capture(string tab,object clip,bool beyond,int quality,CancellationToken ct)
    {
        var p=new Dictionary<string,object?>{["format"]="jpeg",["quality"]=quality,["clip"]=clip,["captureBeyondViewport"]=beyond,["fromSurface"]=true};
        JsonElement r;
        try{r=await On(tab,"Page.captureScreenshot",p,ct,beyond?10000:3500);}
        catch(TimeoutException)
        {   // a tab that is not painted (hidden in its window) gives no picture: bring it to the front of its window and try once more
            await On(tab,"Page.bringToFront",null,ct,3000);
            r=await On(tab,"Page.captureScreenshot",p,ct,beyond?10000:5000);
        }
        return S(r,"data");
    }
    // Viewport: JPEG ~70, long edge <= 1280. full: the page from its top, width <= 1280, height <= 4000 picture pixels. Scale = picture pixels per CSS pixel, measured from the picture.
    public async Task<ChromeShot> Shot(string tabName,bool full,CancellationToken ct=default)
    {
        string tab=state.Resolve(tabName)??tabName;
        var m=await On(tab,"Page.getLayoutMetrics",null,ct,4000);
        var vv=m.GetProperty("cssVisualViewport");double w=Math.Max(1,D(vv,"clientWidth")),h=Math.Max(1,D(vv,"clientHeight")),px=D(vv,"pageX"),py=D(vv,"pageY");
        double cw=w,ch=h;
        if(full&&m.TryGetProperty("cssContentSize",out var cs)){cw=Math.Max(w,D(cs,"width"));ch=Math.Max(h,D(cs,"height"));}
        double factor=shotFactor.TryGetValue(tab,out double f)?f:1;
        string data="";int iw=0,ih=0;double scale=1,capH=ch;
        for(int attempt=0;attempt<2;attempt++)
        {
            if(full){scale=Math.Min(1.0,1280/Math.Max(1.0,cw*factor));capH=Math.Min(ch,Math.Floor(3990/(scale*factor)));   /* a few pixels of room: the picture height is rounded up */}
            else scale=ChromeJs.ShotScale(w,h,factor);
            // the clip is in CSS pixels of the page: the visible part starts at the scroll offset
            object clip=full?new{x=0.0,y=0.0,width=cw,height=capH,scale}:new{x=px,y=py,width=w,height=h,scale};
            int quality=70;
            for(;;)
            {
                data=await Capture(tab,clip,full,quality,ct);
                if(data.Length<=MaxJpegChars||quality<=20)break;
                quality=quality>50?50:quality>35?35:20;   // a long page as one picture: lower the quality until it fits the result
            }
            if(data.Length==0)throw new CdpError("cdp-error","no picture");
            var size=ChromeJs.JpegSize(Convert.FromBase64String(data.Length>4096?data[..4096]:data))??ChromeJs.JpegSize(Convert.FromBase64String(data));
            if(size is null)throw new CdpError("cdp-error","picture unreadable");
            (iw,ih)=size.Value;
            double measured=iw/Math.Max(1.0,(full?cw:w)*scale);
            bool tooBig=Math.Max(iw,full?0:ih)>1300;
            if(Math.Abs(measured-factor)>0.02)shotFactor[tab]=factor=measured;
            if(!tooBig)break;
        }
        return new(data,iw,ih,iw/Math.Max(1.0,full?cw:w),tab,full,full?cw:w,full?capH:h);
    }
    public string AliasOf(string tab)=>state.Alias(tab);
    public void Dispose(){socket.Event-=OnEvent;socket.Dispose();}
}
