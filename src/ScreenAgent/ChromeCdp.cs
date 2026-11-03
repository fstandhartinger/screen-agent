using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using ScreenAgent.Core;
namespace ScreenAgent;
internal sealed class CdpException(string code,string message=""):Exception(string.IsNullOrEmpty(message)?code:message){public string Code=>code;}
internal sealed class StaleRefException(string what):Exception(what){}
internal sealed record CdpTab(string TargetId,string Url,string Title,bool Own);
internal sealed record CdpNode(string Ref,string TargetId,string FrameId,string LoaderId,string Url,int BackendId,string Role,string Name,bool Protected,bool Search,string? Href);
// Chrome through its own debugging port, over a native WebSocket. Compiled method allowlist; page values are read in an isolated world; one socket per run, never reconnected silently.
// The "Remote-Fehlerbehebung zulassen?" dialog of the default profile is handled by ChromeDialogs (exact matcher in Core). With --remote-debugging-port (the selftest) there is no dialog.
internal sealed class ChromeCdp:IDisposable
{
    readonly ClientWebSocket ws=new();
    readonly CancellationTokenSource life=new();
    readonly Dictionary<int,TaskCompletionSource<JsonElement>> waiting=[];
    readonly Dictionary<string,string> sessions=[];            // targetId -> sessionId
    readonly Dictionary<string,(string Frame,string Loader,int World)> worlds=[];
    readonly HashSet<string> own=[];
    readonly Dictionary<string,CdpNode> refs=[];
    readonly HashSet<string> hrefs=new(StringComparer.Ordinal);
    readonly Func<bool> revoked;
    readonly Action<string,Dictionary<string,object?>> log;
    int next,snapshot;
    Task? reader;
    public string BrowserVersion {get;private set;}="";
    public int Port {get;private set;}
    public ChromeCdp(Func<bool> revoked,Action<string,Dictionary<string,object?>> log){this.revoked=revoked;this.log=log;}
    public static string DefaultUserData=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Google","Chrome","User Data");
    // ---- connect
    public sealed record ConnectOptions(string? UserDataDir=null,bool AllowDialog=true,int DialogWaitMs=5000,int AnswerWaitMs=2000);
    public static async Task<ChromeCdp> Connect(ConnectOptions o,Func<bool> revoked,Action<string,Dictionary<string,object?>> log,CancellationToken ct)
    {
        string file=Path.Combine(o.UserDataDir??DefaultUserData,"DevToolsActivePort");
        if(!File.Exists(file))throw new CdpException("debugging-off");
        var lines=File.ReadAllLines(file);
        if(lines.Length<2||!int.TryParse(lines[0],out int port)||port is <1 or >65535||!lines[1].StartsWith("/devtools/browser/")||lines[1].Any(c=>c<=' '))throw new CdpException("debugging-off","DevToolsActivePort unreadable");
        string owner="";
        if(o.AllowDialog)
        {
            var (pid,clients)=ComputerNative.PortUsers(port);
            owner=pid==0?"":ComputerNative.ImagePath(pid);
            if(owner.Length==0)throw new CdpException("port-owner-unknown");
            // Stale prompts of an earlier connection: cancelled only when no laptop agent is known to be connected to this Chrome; a leftover prompt we cannot clear stops us.
            var stale=ChromeDialogs.Scan(owner);
            if(AllowDialog.Headers(stale.Nodes).Count>0)
            {
                if(clients.Any(c=>c!=(uint)Environment.ProcessId))throw new CdpException("chrome-busy","another program is connected to Chrome's debugging port; a pending prompt may be its own");
                var cancel=AllowDialog.Find(stale.Nodes,owner,AllowDialog.Cancel);
                if(cancel.Ok)ChromeDialogs.Press(stale,cancel.ButtonIndex);
                await Task.Delay(400,ct);
                if(AllowDialog.Headers(ChromeDialogs.Scan(owner).Nodes).Count>0)throw new CdpException("stale-dialog");
                log("computer.chrome.stale",new(){["cancelled"]=true});
            }
        }
        var c=new ChromeCdp(revoked,log){Port=port};
        try
        {
            var connect=c.ws.ConnectAsync(new Uri("ws://127.0.0.1:"+port+lines[1]),ct);
            bool clicked=false;long t0=Environment.TickCount64;
            if(o.AllowDialog)
            {
                while(!connect.IsCompleted&&Environment.TickCount64-t0<o.DialogWaitMs)
                {
                    var scan=ChromeDialogs.Scan(owner);var m=AllowDialog.Find(scan.Nodes,owner);
                    if(m.Ok){ChromeDialogs.Press(scan,m.ButtonIndex);clicked=true;log("computer.chrome.allow",new(){["ms"]=Environment.TickCount64-t0});break;}
                    if(m.Reason is "several" or "in-page" or "wrong-process" or "several-buttons")throw new CdpException("dialog-not-ours",m.Reason);   // something that looks like it but is not exactly it: do not click
                    await Task.WhenAny(connect,Task.Delay(150,ct));
                }
            }
            long tClick=Environment.TickCount64;
            if(await Task.WhenAny(connect,Task.Delay(clicked?o.AnswerWaitMs:o.DialogWaitMs,ct))!=connect)throw new CdpException(clicked?"no-answer":"no-dialog");
            await connect;
            c.reader=Task.Run(()=>c.ReadLoop());
            using var answer=CancellationTokenSource.CreateLinkedTokenSource(ct);answer.CancelAfter(clicked?Math.Max(500,o.AnswerWaitMs-(int)(Environment.TickCount64-tClick)):5000);
            var v=await c.Call("Browser.getVersion",null,null,answer.Token);
            // An answer within 2 s of our click. None: we may have approved someone else's request. Disconnect, stop, say so.
            c.BrowserVersion=v.TryGetProperty("product",out var pr)?pr.GetString()??"":"";
            return c;
        }
        catch(Exception e)
        {
            c.Dispose();
            if(e is CdpException)throw;
            throw new CdpException(e is OperationCanceledException?"no-answer":"connect-failed",e.GetType().Name);
        }
    }
    // ---- socket
    async Task ReadLoop()
    {
        var buf=new byte[64*1024];
        try
        {
            while(ws.State==WebSocketState.Open&&!life.IsCancellationRequested)
            {
                using var ms=new MemoryStream();WebSocketReceiveResult r;
                do{r=await ws.ReceiveAsync(buf,life.Token);if(r.MessageType==WebSocketMessageType.Close)return;ms.Write(buf,0,r.Count);if(ms.Length>32<<20)return;}while(!r.EndOfMessage);
                JsonDocument doc;try{doc=JsonDocument.Parse(ms.ToArray());}catch(JsonException){continue;}   // a malformed frame from the browser is skipped; a missing reply times out and is logged by Call
                var root=doc.RootElement;
                if(root.TryGetProperty("id",out var id)&&id.TryGetInt32(out int n))
                {
                    TaskCompletionSource<JsonElement>? tcs;lock(waiting){waiting.Remove(n,out tcs);}
                    if(tcs is null){doc.Dispose();continue;}
                    if(root.TryGetProperty("error",out var err))tcs.TrySetException(new CdpException("cdp-error",(err.TryGetProperty("message",out var em)?em.GetString():"")??""));
                    else tcs.TrySetResult(root.TryGetProperty("result",out var res)?res.Clone():default);
                    doc.Dispose();
                }
                else if(root.TryGetProperty("method",out var mm))
                {
                    string method=mm.GetString()??"";
                    if(method=="Page.javascriptDialogOpening")
                    {
                        string? sid=root.TryGetProperty("sessionId",out var s)?s.GetString():null;
                        string type=root.TryGetProperty("params",out var pp)&&pp.TryGetProperty("type",out var tp)?tp.GetString()??"":"";
                        log("computer.chrome.dialog",new(){["type"]=type});
                        // A page dialog is never answered "yes": it is dismissed (a beforeunload keeps the page).
                        _=Task.Run(async()=>{try{await Call("Page.handleJavaScriptDialog",new(){["accept"]=false},sid,life.Token);}catch{ }});   // dismissing a JS dialog is best effort; the next call fails and is logged if the page is stuck
                    }
                    doc.Dispose();
                }
                else doc.Dispose();
            }
        }
        catch(Exception e){log("computer.chrome.closed",new(){["type"]=e.GetType().Name});}
        finally{lock(waiting){foreach(var t in waiting.Values)t.TrySetException(new CdpException("closed"));waiting.Clear();}}
    }
    public async Task<JsonElement> Call(string method,Dictionary<string,object?>? args,string? session,CancellationToken ct=default)
    {
        if(!CdpMethods.Ok(method))throw new CdpException("method-not-allowed",method);
        if(revoked())throw new OperationCanceledException("run revoked");
        if(ws.State!=WebSocketState.Open)throw new CdpException("closed");
        int id=Interlocked.Increment(ref next);var tcs=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(waiting)waiting[id]=tcs;
        var body=new Dictionary<string,object?>{["id"]=id,["method"]=method};
        if(args is not null)body["params"]=args;
        if(session is not null)body["sessionId"]=session;
        await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(body),WebSocketMessageType.Text,true,ct);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct,life.Token);timeout.CancelAfter(10000);
        try{return await tcs.Task.WaitAsync(timeout.Token);}
        catch(OperationCanceledException){lock(waiting)waiting.Remove(id);throw;}
    }
    // ---- tabs
    static string Cap(string? s,int n){s=new string((s??"").Where(c=>!char.IsControl(c)).ToArray());return s.Length<=n?s:s[..n];}
    public async Task<List<CdpTab>> Targets(CancellationToken ct=default)
    {
        var r=await Call("Target.getTargets",null,null,ct);var list=new List<CdpTab>();
        foreach(var t in r.GetProperty("targetInfos").EnumerateArray())
            if(t.GetProperty("type").GetString()=="page")list.Add(new(t.GetProperty("targetId").GetString()!,t.GetProperty("url").GetString()??"",t.GetProperty("title").GetString()??"",own.Contains(t.GetProperty("targetId").GetString()!)));
        return list;
    }
    // Tabs whose origin is in the run's scope, or that he approved to open. Nothing else is ever shown, read or touched: being a tab the run opened is NOT enough (a redirect or a script can take it anywhere).
    public async Task<List<CdpTab>> VisibleTabs(ComputerScope scope,CancellationToken ct=default)
        =>(await Targets(ct)).Where(t=>Domains.HostOf(t.Url) is {} h&&scope.Reaches(h)).ToList();
    // After every action: a tab of the run whose origin left the scope loses `Own` and its refs die. (F1: link, 302, location= by script, form post.)
    public async Task<int> Revalidate(ComputerScope scope,CancellationToken ct=default)
    {
        int lost=0;
        foreach(var t in await Targets(ct))
        {
            if(!t.Own||(Domains.HostOf(t.Url) is {} h&&scope.Reaches(h)))continue;
            own.Remove(t.TargetId);lost++;
            foreach(var k in refs.Where(r=>r.Value.TargetId==t.TargetId).Select(r=>r.Key).ToList())refs.Remove(k);
        }
        return lost;
    }
    public void ClearRefs(){refs.Clear();hrefs.Clear();}
    public string TabListing(List<CdpTab> tabs)=>JsonSerializer.Serialize(tabs.Select((t,i)=>new{tab=t.TargetId,origin=Domains.Origin(t.Url) is {Length:>0} o?o:Domains.Scheme(t.Url)+":",title=SecretText.Redact(Cap(t.Title,80)),own=t.Own}));
    public async Task<CdpTab?> TabOf(string targetId,CancellationToken ct=default)=>(await Targets(ct)).FirstOrDefault(t=>t.TargetId==targetId);
    // Foreground tab of the browser, as far as CDP can say: the visible, focused page target (document.visibilityState) is not exposed cheaply, so the caller passes the window title to match.
    public async Task<CdpTab?> TabByTitle(string windowTitle,CancellationToken ct=default)
    {
        var tabs=await Targets(ct);
        var hit=tabs.Where(t=>t.Title.Length>0&&ComputerSpeech.TabTitleMatches(windowTitle,t.Title)).ToList();
        return hit.Count==1?hit[0]:null;
    }
    public async Task<CdpTab> Open(string url,CancellationToken ct=default)
    {
        var r=await Call("Target.createTarget",new(){["url"]=url},null,ct);string id=r.GetProperty("targetId").GetString()!;own.Add(id);
        await Task.Delay(300,ct);
        return await TabOf(id,ct)??new(id,url,"",true);
    }
    async Task<string> Session(string targetId,CancellationToken ct)
    {
        if(sessions.TryGetValue(targetId,out var s))return s;
        var r=await Call("Target.attachToTarget",new(){["targetId"]=targetId,["flatten"]=true},null,ct);s=r.GetProperty("sessionId").GetString()!;
        sessions[targetId]=s;
        await Call("Page.enable",null,s,ct);await Call("DOM.enable",null,s,ct);await Call("Accessibility.enable",null,s,ct);
        return s;
    }
    public async Task<(string Frame,string Loader,string Url)> Frame(string targetId,CancellationToken ct)
    {
        var r=await Call("Page.getFrameTree",null,await Session(targetId,ct),ct);var f=r.GetProperty("frameTree").GetProperty("frame");
        return (f.GetProperty("id").GetString()!,f.TryGetProperty("loaderId",out var l)?l.GetString()??"":"",f.TryGetProperty("url",out var u)?u.GetString()??"":"");
    }
    // Isolated world of the main frame: page scripts cannot see or alter what the compiled functions read.
    async Task<int> World(string targetId,string frame,string loader,CancellationToken ct)
    {
        if(worlds.TryGetValue(targetId,out var w)&&w.Frame==frame&&w.Loader==loader)return w.World;
        var r=await Call("Page.createIsolatedWorld",new(){["frameId"]=frame,["worldName"]="screen-agent"},await Session(targetId,ct),ct);
        int id=r.GetProperty("executionContextId").GetInt32();worlds[targetId]=(frame,loader,id);return id;
    }
    // ---- compiled page functions (the only script this client ever runs)
    const string FnInfo="function(){const t=(this.type||'').toLowerCase();const ac=(this.autocomplete||'').toLowerCase();const n=((this.name||'')+' '+(this.id||'')+' '+(this.placeholder||'')+' '+((this.getAttribute&&this.getAttribute('aria-label'))||'')).toLowerCase();return {pw:t==='password'||/password|one-time-code|cc-/.test(ac),search:t==='search'||/search|such/.test(n),connected:this.isConnected===true};}";
    // Every field of the form (or the page) INCLUDING contenteditable bodies and role=textbox: name, the first 300 characters for the question, and a hash of the WHOLE value for the binding.
    // Search boxes are left out of the words (they must not take a slot) but stay in the digest. [email] / data-hovercard-id chips are read as recipients (best effort; on Gmail, X, Telegram, Slack, Discord the chips are not guaranteed).
    const string FnForm="function(){const f=this.form||document;const words=[],dig=[],rc=[];const hash=function(s){let h=5381;for(let i=0;i<s.length;i++){h=((h<<5)+h+s.charCodeAt(i))|0;}return s.length+':'+(h>>>0).toString(16);};for(const e of f.querySelectorAll('input,textarea,select,[contenteditable=\"\"],[contenteditable=\"true\"],[role=\"textbox\"]')){const t=(e.type||'').toLowerCase();const ac=(e.autocomplete||'').toLowerCase();if(t==='password'||t==='hidden'||/password|one-time-code|cc-/.test(ac))continue;const nm=(e.name||e.id||(e.getAttribute&&(e.getAttribute('aria-label')||e.getAttribute('placeholder')))||e.tagName.toLowerCase());const ed=e.isContentEditable===true||(('value' in e)===false);const v=ed?String(e.innerText||''):String(e.value||'');const search=t==='search'||(e.getAttribute&&e.getAttribute('role')==='searchbox')||/search|such/i.test(nm);dig.push(nm+'='+hash(v));if(!search&&v.trim().length>0)words.push([nm,v.slice(0,300)]);if(dig.length>=80)break;}for(const e of f.querySelectorAll('[email],[data-hovercard-id]')){const m=(e.getAttribute('email')||e.getAttribute('data-hovercard-id')||'').slice(0,100);if(m){rc.push(m);}if(rc.length>=10)break;}dig.push('rcpt='+hash(rc.join(',')));return {words:words,digest:dig,rcpt:rc};}";
    // Where a click goes, read live: the closest link, the button's formaction or its form's action (through the prototype getter: a field named "action" cannot shadow it).
    const string FnTarget="function(){const a=this.closest&&this.closest('a[href],area[href]');if(a&&a.href)return String(a.href);const fa=this.getAttribute&&this.getAttribute('formaction');if(fa){return URL.canParse(fa,document.baseURI)?new URL(fa,document.baseURI).href:'invalid:';}const f=this.form||(this.closest&&this.closest('form'));if(f){const d=Object.getOwnPropertyDescriptor(HTMLFormElement.prototype,'action');return d&&d.get?String(d.get.call(f)):'invalid:';}return '';}";
    const string FnActive="function(){const e=document.activeElement;return (e&&e!==document.body&&e!==document.documentElement)?e:null;}";
    const string FnFocusInfo="function(){const t=(this.type||'').toLowerCase();const ac=(this.autocomplete||'').toLowerCase();const n=((this.name||'')+' '+(this.id||'')+' '+(this.placeholder||'')+' '+((this.getAttribute&&this.getAttribute('aria-label'))||'')).toLowerCase();return {pw:t==='password'||/password|one-time-code|cc-/.test(ac),search:t==='search'||/search|such/.test(n)||(this.getAttribute&&this.getAttribute('role'))==='searchbox'};}";
    const string FnContains="function(h){return this===h||this.contains(h);}";
    const string FnSelect="function(){this.focus();if(this.select){this.select();}else{const r=document.createRange();r.selectNodeContents(this);const s=getSelection();s.removeAllRanges();s.addRange(r);}return document.activeElement===this;}";
    const string FnFocused="function(){return document.activeElement===this;}";
    const string FnRead="function(){return {text:(document.body?document.body.innerText:'').slice(0,20000)};}";
    const string FnValue="function(){const t=(this.type||'').toLowerCase();if(t==='password')return null;return String(this.value!==undefined?this.value:this.innerText||'').slice(0,200);}";
    async Task<string> Resolve(string targetId,int backend,int world,string session,CancellationToken ct)
    {
        var r=await Call("DOM.resolveNode",new(){["backendNodeId"]=backend,["executionContextId"]=world},session,ct);
        return r.GetProperty("object").GetProperty("objectId").GetString()!;
    }
    async Task<JsonElement> OnObject(string session,string objectId,string fn,CancellationToken ct,object[]? args=null)
    {
        var p=new Dictionary<string,object?>{["objectId"]=objectId,["functionDeclaration"]=fn,["returnByValue"]=true};
        if(args is not null)p["arguments"]=args;
        var r=await Call("Runtime.callFunctionOn",p,session,ct);
        if(r.TryGetProperty("exceptionDetails",out _))throw new CdpException("page-function");
        return r.GetProperty("result").TryGetProperty("value",out var v)?v.Clone():default;
    }
    public async Task<string> ReadPage(string targetId,CancellationToken ct)
    {
        var (frame,loader,_)=await Frame(targetId,ct);int world=await World(targetId,frame,loader,ct);var s=await Session(targetId,ct);
        var r=await Call("Runtime.callFunctionOn",new(){["executionContextId"]=world,["functionDeclaration"]=FnRead,["returnByValue"]=true},s,ct);
        var v=r.GetProperty("result").GetProperty("value");return v.GetProperty("text").GetString()??"";
    }
    // ---- snapshot: interactive elements of the main frame, refs that die with the next snapshot or any navigation
    static readonly HashSet<string> Roles=new(StringComparer.Ordinal){"button","link","textbox","searchbox","combobox","checkbox","radio","menuitem","tab","option","switch","heading","listbox","slider","spinbutton","menuitemcheckbox","menuitemradio","treeitem"};
    public async Task<(string Text,List<CdpNode> Nodes)> Snapshot(string targetId,CancellationToken ct)
    {
        var (frame,loader,url)=await Frame(targetId,ct);int world=await World(targetId,frame,loader,ct);var s=await Session(targetId,ct);
        var tree=await Call("Accessibility.getFullAXTree",null,s,ct);
        snapshot++;refs.Clear();hrefs.Clear();var nodes=new List<CdpNode>();int inputs=0;
        foreach(var n in tree.GetProperty("nodes").EnumerateArray())
        {
            if(nodes.Count>=400)break;
            if(n.TryGetProperty("ignored",out var ig)&&ig.ValueKind==JsonValueKind.True)continue;
            string role=n.TryGetProperty("role",out var rr)&&rr.TryGetProperty("value",out var rv)?rv.GetString()??"":"";
            if(!Roles.Contains(role)||!n.TryGetProperty("backendDOMNodeId",out var bid))continue;
            string name=n.TryGetProperty("name",out var nm)&&nm.TryGetProperty("value",out var nv)?nv.GetString()??"":"";
            string? href=null;
            if(n.TryGetProperty("properties",out var props))foreach(var p in props.EnumerateArray())
                if(p.GetProperty("name").GetString()=="url"&&p.GetProperty("value").TryGetProperty("value",out var uv)){href=uv.GetString();if(href is not null&&hrefs.Count<400)hrefs.Add(href);}
            bool pw=false,search=role=="searchbox";
            if(role is "textbox" or "searchbox" or "combobox")
            {
                if(inputs++>=60)pw=true;   // 60 decoy boxes: past the checked ones a text input counts as protected, not as fine
                else
                {
                    try
                    {
                        var obj=await Resolve(targetId,bid.GetInt32(),world,s,ct);var info=await OnObject(s,obj,FnInfo,ct);
                        pw=info.GetProperty("pw").GetBoolean();search|=info.GetProperty("search").GetBoolean();
                    }
                    catch(CdpException){pw=true;}   // cannot tell: treat as protected
                }
            }
            var node=new CdpNode("s"+snapshot+"e"+(nodes.Count+1),targetId,frame,loader,url,bid.GetInt32(),role,name,pw,search,href);
            nodes.Add(node);refs[node.Ref]=node;
        }
        var sb=new StringBuilder();
        foreach(var n in nodes)sb.Append(n.Ref).Append(' ').Append(n.Role).Append(" \"").Append(Labels.Clean(n.Name)).Append('"').Append(n.Protected?" [geschützt]":"").Append(n.Search?" [suche]":"").Append('\n');
        return (sb.ToString(),nodes);
    }
    public bool HrefInSnapshot(string url)=>hrefs.Contains(url);
    public CdpNode? Node(string reference)=>refs.TryGetValue(reference,out var n)?n:null;
    // ---- bound actions
    public sealed record Bound(ActionBinding Binding,double X,double Y,string ObjectId,int World,string Session,List<(string Name,string Value)> Fields,List<string> Rcpt,bool Pw,string Url);
    // The live target of a click (link, formaction, form action), read before the click is classified. "" = none; throws StaleRefException when the page moved on.
    public async Task<string> TargetOf(CdpNode n,CancellationToken ct)
    {
        var (frame,loader,_)=await Frame(n.TargetId,ct);
        if(frame!=n.FrameId||loader!=n.LoaderId)throw new StaleRefException("navigated");
        int world=await World(n.TargetId,frame,loader,ct);var s=await Session(n.TargetId,ct);
        string obj;try{obj=await Resolve(n.TargetId,n.BackendId,world,s,ct);}catch(CdpException){throw new StaleRefException("node-gone");}
        var v=await OnObject(s,obj,FnTarget,ct);
        return v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
    }
    static (List<(string,string)> Fields,List<string> Rcpt,string Digest) ReadForm(JsonElement form)
    {
        var fields=new List<(string,string)>();var rcpt=new List<string>();var dig=new List<string>();
        if(form.ValueKind==JsonValueKind.Object)
        {
            if(form.TryGetProperty("words",out var w)&&w.ValueKind==JsonValueKind.Array)foreach(var p in w.EnumerateArray())if(p.ValueKind==JsonValueKind.Array&&p.GetArrayLength()==2)fields.Add((p[0].GetString()??"",p[1].GetString()??""));
            if(form.TryGetProperty("rcpt",out var r)&&r.ValueKind==JsonValueKind.Array)foreach(var x in r.EnumerateArray())rcpt.Add(x.GetString()??"");
            if(form.TryGetProperty("digest",out var d)&&d.ValueKind==JsonValueKind.Array)foreach(var x in d.EnumerateArray())dig.Add(x.GetString()??"");
        }
        return (fields,rcpt,ActionBinding.Digest(dig));
    }
    // Everything an action is bound to, read fresh: target, frame, loader, origin+path, backend node, live role and name from the accessibility tree, the form's field digest (every field, contenteditable bodies and
    // recipient chips included, whole values hashed); plus the hit test. The live password check is repeated here (a field can turn into a password box after the snapshot).
    public async Task<Bound> Bind(CdpNode n,string tool,string? text,CancellationToken ct)
    {
        var (frame,loader,url)=await Frame(n.TargetId,ct);
        if(frame!=n.FrameId||loader!=n.LoaderId)throw new StaleRefException("navigated");
        int world=await World(n.TargetId,frame,loader,ct);var s=await Session(n.TargetId,ct);
        var part=await Call("Accessibility.getPartialAXTree",new(){["backendNodeId"]=n.BackendId,["fetchRelatives"]=false},s,ct);
        var first=part.GetProperty("nodes").EnumerateArray().FirstOrDefault();
        string role=first.ValueKind==JsonValueKind.Object&&first.TryGetProperty("role",out var rr)&&rr.TryGetProperty("value",out var rv)?rv.GetString()??"":"";
        string name=first.ValueKind==JsonValueKind.Object&&first.TryGetProperty("name",out var nm)&&nm.TryGetProperty("value",out var nv)?nv.GetString()??"":"";
        string obj;
        try{obj=await Resolve(n.TargetId,n.BackendId,world,s,ct);}catch(CdpException){throw new StaleRefException("node-gone");}
        var info=await OnObject(s,obj,FnInfo,ct);
        if(!info.GetProperty("connected").GetBoolean())throw new StaleRefException("detached");
        var (fields,rcpt,digest)=ReadForm(await OnObject(s,obj,FnForm,ct));
        await Call("DOM.scrollIntoViewIfNeeded",new(){["backendNodeId"]=n.BackendId},s,ct);
        var box=await Call("DOM.getBoxModel",new(){["backendNodeId"]=n.BackendId},s,ct);
        var q=box.GetProperty("model").GetProperty("content").EnumerateArray().Select(e=>e.GetDouble()).ToArray();
        double x=(q[0]+q[2]+q[4]+q[6])/4,y=(q[1]+q[3]+q[5]+q[7])/4;
        var b=new ActionBinding().Set("target",n.TargetId).Set("frame",frame).Set("loader",loader).Set("origin",Domains.OriginPath(url)).Set("backend",n.BackendId).Set("role",role).Set("name",name)
            .Set("form",digest).Set("tool",tool).Set("text",text??"");
        return new(b,x,y,obj,world,s,fields,rcpt,info.GetProperty("pw").GetBoolean(),url);
    }
    // The focused element of a tab, for a key that asks (Enter): bound like a click, so the key goes where the question said. null = nothing focused.
    public sealed record FocusBound(ActionBinding Binding,List<(string Name,string Value)> Fields,List<string> Rcpt,bool Pw,bool Search,string Name,string Role,string Url);
    public async Task<FocusBound?> BindFocus(string targetId,string key,CancellationToken ct)
    {
        var (frame,loader,url)=await Frame(targetId,ct);int world=await World(targetId,frame,loader,ct);var s=await Session(targetId,ct);
        var r=await Call("Runtime.callFunctionOn",new(){["executionContextId"]=world,["functionDeclaration"]=FnActive,["returnByValue"]=false},s,ct);
        if(r.TryGetProperty("exceptionDetails",out _)||!r.GetProperty("result").TryGetProperty("objectId",out var oid))return null;
        string obj=oid.GetString()!;
        var node=await Call("DOM.describeNode",new(){["objectId"]=obj},s,ct);
        int backend=node.GetProperty("node").TryGetProperty("backendNodeId",out var bn)?bn.GetInt32():0;
        if(backend==0)return null;
        var part=await Call("Accessibility.getPartialAXTree",new(){["backendNodeId"]=backend,["fetchRelatives"]=false},s,ct);
        var first=part.GetProperty("nodes").EnumerateArray().FirstOrDefault();
        string role=first.ValueKind==JsonValueKind.Object&&first.TryGetProperty("role",out var rr)&&rr.TryGetProperty("value",out var rv)?rv.GetString()??"":"";
        string name=first.ValueKind==JsonValueKind.Object&&first.TryGetProperty("name",out var nm)&&nm.TryGetProperty("value",out var nv)?nv.GetString()??"":"";
        var info=await OnObject(s,obj,FnFocusInfo,ct);
        var (fields,rcpt,digest)=ReadForm(await OnObject(s,obj,FnForm,ct));
        var b=new ActionBinding().Set("target",targetId).Set("frame",frame).Set("loader",loader).Set("origin",Domains.OriginPath(url)).Set("backend",backend).Set("role",role).Set("name",name).Set("form",digest).Set("tool","key").Set("key",key);
        return new(b,fields,rcpt,info.GetProperty("pw").GetBoolean(),info.GetProperty("search").GetBoolean(),name,role,url);
    }
    // The hit test: the node under the centre of the element must be the element or inside it.
    async Task HitTest(Bound b,CdpNode n,CancellationToken ct)
    {
        var hit=await Call("DOM.getNodeForLocation",new(){["x"]=(int)Math.Round(b.X),["y"]=(int)Math.Round(b.Y),["includeUserAgentShadowDOM"]=false},b.Session,ct);
        int hb=hit.TryGetProperty("backendNodeId",out var h)?h.GetInt32():0;
        if(hb==0)throw new StaleRefException("hit-none");
        var hobj=await Resolve(n.TargetId,hb,b.World,b.Session,ct);
        var ok=await OnObject(b.Session,b.ObjectId,FnContains,ct,[new{objectId=hobj}]);
        if(ok.ValueKind!=JsonValueKind.True)throw new StaleRefException("hit-other");
    }
    // `expected` = the binding shown to him (R2): any field that differs aborts, no re-resolve. null (R1): only the snapshot's own frame and loader must still hold.
    public async Task<string> Click(CdpNode n,ActionBinding? expected,CancellationToken ct)
    {
        var b=await Bind(n,"chrome_click",null,ct);
        if(expected is not null&&!expected.Same(b.Binding))throw new StaleRefException("changed:"+string.Join(",",expected.Diff(b.Binding)));
        await HitTest(b,n,ct);
        if(revoked())throw new OperationCanceledException("run revoked");
        int x=(int)Math.Round(b.X),y=(int)Math.Round(b.Y);
        await Call("Input.dispatchMouseEvent",new(){["type"]="mouseMoved",["x"]=x,["y"]=y},b.Session,ct);
        await Call("Input.dispatchMouseEvent",new(){["type"]="mousePressed",["x"]=x,["y"]=y,["button"]="left",["clickCount"]=1},b.Session,ct);
        await Call("Input.dispatchMouseEvent",new(){["type"]="mouseReleased",["x"]=x,["y"]=y,["button"]="left",["clickCount"]=1},b.Session,ct);
        return "geklickt";
    }
    public async Task<string> Fill(CdpNode n,string text,ActionBinding? expected,CancellationToken ct)
    {
        if(n.Protected)throw new CdpException("password-field");
        var b=await Bind(n,"chrome_fill",text,ct);
        if(b.Pw)throw new CdpException("password-field");   // it turned into a password box after the snapshot
        if(expected is not null&&!expected.Same(b.Binding))throw new StaleRefException("changed:"+string.Join(",",expected.Diff(b.Binding)));
        await HitTest(b,n,ct);
        await Call("DOM.focus",new(){["backendNodeId"]=n.BackendId},b.Session,ct);
        var sel=await OnObject(b.Session,b.ObjectId,FnSelect,ct);
        if(sel.ValueKind!=JsonValueKind.True)throw new StaleRefException("no-focus");
        if(revoked())throw new OperationCanceledException("run revoked");
        await Call("Input.insertText",new(){["text"]=text},b.Session,ct);
        var back=await OnObject(b.Session,b.ObjectId,FnValue,ct);
        return "eingetragen:"+text.Length+(back.ValueKind==JsonValueKind.String&&back.GetString()!.Length>0?"":"?");
    }
    public async Task Scroll(string targetId,int dy,CancellationToken ct)
        =>await Call("Input.dispatchMouseEvent",new(){["type"]="mouseWheel",["x"]=300,["y"]=300,["deltaX"]=0,["deltaY"]=Math.Clamp(dy,-2000,2000)},await Session(targetId,ct),ct);
    public async Task<string> Screenshot(string targetId,CancellationToken ct)
    {
        var s=await Session(targetId,ct);var m=await Call("Page.getLayoutMetrics",null,s,ct);var v=m.GetProperty("cssVisualViewport");
        double w=v.GetProperty("clientWidth").GetDouble(),h=v.GetProperty("clientHeight").GetDouble(),scale=Math.Min(1.0,900/Math.Max(w,1));
        var r=await Call("Page.captureScreenshot",new(){["format"]="jpeg",["quality"]=45,["clip"]=new{x=0,y=0,width=w,height=h,scale}},s,ct);
        return r.GetProperty("data").GetString()??"";
    }
    public async Task<string?> Text(CdpNode n,CancellationToken ct)
    {
        var b=await Bind(n,"read",null,ct);
        var v=await OnObject(b.Session,b.ObjectId,FnValue,ct);return v.ValueKind==JsonValueKind.String?v.GetString():null;
    }
    public async Task<bool> Focused(CdpNode n,CancellationToken ct)
    {
        var (frame,loader,_)=await Frame(n.TargetId,ct);int world=await World(n.TargetId,frame,loader,ct);var s=await Session(n.TargetId,ct);
        return (await OnObject(s,await Resolve(n.TargetId,n.BackendId,world,s,ct),FnFocused,ct)).ValueKind==JsonValueKind.True;
    }
    public bool Open_=>ws.State==WebSocketState.Open;
    public void Dispose()
    {
        life.Cancel();
        try{if(ws.State==WebSocketState.Open)ws.CloseAsync(WebSocketCloseStatus.NormalClosure,"",new CancellationTokenSource(1000).Token).Wait(1200);}catch{ }   // closing the socket at the end of a run, nothing left to report
        ws.Dispose();life.Dispose();
    }
}
// Chrome's own prompt, found through UI Automation: native browser UI only (the matcher in Core rejects anything inside a page).
internal sealed record DialogScan(List<DialogNode> Nodes,List<AutomationElement> Elements);
internal static class ChromeDialogs
{
    public static DialogScan Scan(string ownerImage)
    {
        var nodes=new List<DialogNode>();var els=new List<AutomationElement>();
        var windows=new List<IntPtr>();
        Native.EnumWindows((h,_)=>{if(Native.IsWindowVisible(h)){Native.GetWindowThreadProcessId(h,out uint pid);if(pid!=Environment.ProcessId)windows.Add(h);}return true;},IntPtr.Zero);
        foreach(var h in windows)
        {
            string image=ComputerNative.ImagePathOf(h);
            if(image.Length==0)continue;
            string leaf=image[(image.LastIndexOfAny(['\\','/'])+1)..];
            if(!leaf.Equals("chrome.exe",StringComparison.OrdinalIgnoreCase)&&!leaf.Equals("notepad.exe",StringComparison.OrdinalIgnoreCase)&&!image.Equals(ownerImage,StringComparison.OrdinalIgnoreCase))continue;   // every candidate imaginable, so look-alikes are seen and rejected by the matcher
            try{Walk(AutomationElement.FromHandle(h),image,-1,0,false,nodes,els);}catch(Exception){ }   // window vanished while scanning for the allow dialog: counts as no dialog, the connect step logs the outcome
        }
        return new(nodes,els);
    }
    static string RoleOf(ControlType t)=>t==ControlType.Window?"Window":t==ControlType.Pane?"Pane":t==ControlType.Group?"Group":t==ControlType.Text?"Text":t==ControlType.Button?"Button":t==ControlType.Document?"Document":t==ControlType.Edit?"Edit":"Other";
    static void Walk(AutomationElement e,string image,int parent,int depth,bool inDoc,List<DialogNode> nodes,List<AutomationElement> els)
    {
        if(nodes.Count>=700||depth>14)return;
        var type=e.Current.ControlType;string name=e.Current.Name??"";
        nodes.Add(new(RoleOf(type),name.Length>200?name[..200]:name,inDoc,Path.GetFileNameWithoutExtension(image),image,depth,parent));els.Add(e);
        int me=nodes.Count-1;bool doc=inDoc||type==ControlType.Document;
        var kid=TreeWalker.ControlViewWalker.GetFirstChild(e);int n=0;
        while(kid is not null&&n++<60){Walk(kid,image,me,depth+1,doc,nodes,els);kid=TreeWalker.ControlViewWalker.GetNextSibling(kid);}
    }
    public static void Press(DialogScan scan,int index)
    {
        var el=scan.Elements[index];
        if(el.TryGetCurrentPattern(InvokePattern.Pattern,out var p))((InvokePattern)p).Invoke();
        else throw new CdpException("dialog-no-invoke");
    }
}
