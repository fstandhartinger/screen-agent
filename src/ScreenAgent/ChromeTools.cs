using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using ScreenAgent.Core;
namespace ScreenAgent;
// Chrome half of the general computer control: his running signed-in Chrome over CDP (auto-accepted allow prompt), own tabs by default. Contract: CONTRACT-V2.
// The tools themselves are ScreenAgent.Core.ChromeFree (tested against a real Chrome on Linux and Windows); this class owns the connection: attach through DevToolsActivePort with the
// allow clicker running, switch remote debugging on when it is off (once per run), remember a failed attach for 20 s, reconnect a dropped socket, and attach a picture of the tab after
// every acting tool. When CDP is away every tool answers with the reason and "nimm die Desktop-Werkzeuge": the run goes on with screenshot and click.
internal sealed class ChromeTools:IFreeToolset
{
    // The clock of the 20 s failure memory; the selftest moves it instead of waiting.
    internal static Func<long> Now=()=>Environment.TickCount64;
    // Selftest only: the toolset of the agent that was built last, and a picture hook for the steps of switching debugging on.
    internal static ChromeTools? Current;
    internal static Action<string>? EnableSnap;
    readonly FreeHost host;
    readonly object sync=new();
    ChromeFree? conn;
    ChromeAllowClicker? clicker;
    ChromeRunState state=new();
    ConnectMemory memory=new();
    bool enableTried;int enablePid;long generation;
    CancellationTokenSource? connecting;
    string? runId;
    internal ChromeAllowClicker? Clicker=>clicker;
    internal FreeHost Host=>host;
    internal long LastConnectMs {get;private set;}
    internal int LastConnectClicks {get;private set;}
    internal string LastEnable {get;private set;}="";
    internal void DropSocketForTest(){lock(sync)conn?.DropSocketForTest();}
    public ChromeTools(FreeHost host)
    {
        this.host=host;Current=this;
        // The desktop mouse tools on a `tab` frame go through CDP to that tab, also when Chrome is not in front.
        host.TabMouseHandler=m=>
        {
            var c=Ensure(out string? fail,out _)??throw new InvalidOperationException(ChromeFallback.Text(fail??"cdp-closed"));
            return c.Mouse(m.Tab,m.Kind,m.X,m.Y,m.X2,m.Y2,m.Button,m.Count,m.Dx,m.Dy,m.Ms,m.Mods).GetAwaiter().GetResult();
        };
        host.TabShot=tab=>
        {
            // Desktop wait/auto-observe may run without the input gate: observation must never enable Chrome.
            ChromeFree? c;lock(sync)c=conn;if(c is not {Open:true})return null;
            try{var s=c.Shot(tab.EndsWith("#full",StringComparison.Ordinal)?tab[..^5]:tab,false).GetAwaiter().GetResult();return (s.Jpeg,FrameOf(s));}
            catch(Exception e) when(e is TimeoutException or CdpError or ChromeToolFail){host.Log("computer.chrome.shot_failed",new(){["type"]=e.GetType().Name});return null;}
        };
    }
    // Every call may enable Chrome and type: take the shared input gate even for reads. Auto-observe still follows ChromeFree.Acts only.
    public IReadOnlyDictionary<string,bool> Tools {get;}=ChromeFree.Tools.ToDictionary(t=>t,_=>true);
    static Frame FrameOf(ChromeShot s)=>new(FrameKind.Tab,s.Width,s.Height,0,0,s.Scale,s.Full?s.Tab+"#full":s.Tab);
    public void RunStarted(ComputerRun run)
    {
        Drop();
        lock(sync){state=new();memory=new();enableTried=false;enablePid=0;runId=run.Id;}

    }
    // His tabs are never closed, and neither are the run's own: he may want to see the result. How many own tabs stay open is logged.
    public void RunEnded(ComputerRun run)
    {
        int opened=state.Own.Count;
        Drop(); // Revoke attempts before any reporting or provider/network work.
        if(opened>0)host.Log("computer.chrome.own_tabs",new(){["run"]=run.Id,["open"]=opened});
    }

    void Drop()
    {
        ChromeFree? c;ChromeAllowClicker? k;
        lock(sync){generation++;connecting?.Cancel();connecting?.Dispose();connecting=null;c=conn;k=clicker;conn=null;clicker=null;}
        k?.Dispose();
        try{c?.Dispose();}catch(Exception e) when(e is WebSocketException or ObjectDisposedException or InvalidOperationException){host.Log("computer.chrome.dispose",new(){["type"]=e.GetType().Name});}
    }
    public void Dispose()=>Drop();
    // ---- connect
    static (int Port,string Path)? ReadPort(string userData)
    {
        try
        {
            string file=Path.Combine(userData,"DevToolsActivePort");
            if(!File.Exists(file))return null;
            var lines=File.ReadAllLines(file);
            if(lines.Length<2||!int.TryParse(lines[0],out int port)||port is <1 or >65535||!lines[1].StartsWith("/devtools/browser/",StringComparison.Ordinal)||lines[1].Any(c=>c<=' '))return null;
            return (port,lines[1]);
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){return null;}   // Chrome is writing the file right now: counts as not there yet
    }
    static bool Refused(Exception e)
    {
        for(Exception? x=e;x is not null;x=x.InnerException)if(x is SocketException{SocketErrorCode:SocketError.ConnectionRefused or SocketError.TimedOut or SocketError.HostUnreachable})return true;
        return false;
    }
    // The open connection, a new one, or null with the reason. `fresh` = this very call tried (a remembered failure answers without trying).
    ChromeFree? Ensure(out string? fail,out bool fresh)
    {
        fail=null;fresh=false;
        lock(sync)if(conn is {Open:true} live)return live;
        bool dropped;lock(sync)dropped=conn is not null;
        if(dropped){host.Log("computer.chrome.dropped",new(){["run"]=runId});Drop();}   // a dropped socket: one reconnect right away, with the clicker (Chrome asks again)
        if(memory.Blocked(Now()) is {} code){fail=code;return null;}
        fresh=true;
        var sw=Stopwatch.StartNew();
        try
        {
            var c=Connect();
            memory.Clear();LastConnectMs=sw.ElapsedMilliseconds;
            return c;
        }
        catch(CdpError e)
        {
            memory.Fail(e.Code,Now());fail=e.Code;
            host.Log("computer.chrome.failed",new(){["run"]=runId,["reason"]=e.Code,["detail"]=e.Message==e.Code?"":e.Message,["ms"]=sw.ElapsedMilliseconds,["reconnect"]=dropped});
            return null;
        }
    }
    ChromeFree Connect()
    {
        long epoch;CancellationTokenSource attemptLife;ChromeRunState attemptState;
        lock(sync){epoch=generation;connecting?.Cancel();connecting?.Dispose();attemptLife=new();connecting=attemptLife;attemptState=state;}
        var token=attemptLife.Token;
        bool Stopped()=>host.Stopped()||token.IsCancellationRequested||Interlocked.Read(ref generation)!=epoch;
        try{return ConnectAttempt(epoch,attemptState,Stopped,token);}
        finally
        {
            lock(sync)if(ReferenceEquals(connecting,attemptLife)&&conn is null){connecting=null;attemptLife.Dispose();}
        }
    }
    ChromeFree ConnectAttempt(long epoch,ChromeRunState attemptState,Func<bool> Stopped,CancellationToken token)
    {
        long t0=Environment.TickCount64;var o=host.Chrome;string dir=o.UserDataDir??ChromeCdp.DefaultUserData;
        for(int attempt=0;;attempt++)
        {
            if(Stopped())throw new OperationCanceledException("connect revoked");
            var port=ReadPort(dir);
            if(port is null)
            {
                if(!ChromeUi.Running())throw new CdpError("chrome-not-running");
                port=Enable(dir,null,Stopped)??throw new CdpError("debugging-off",LastEnable);
            }
            string owner="";var clients=new List<uint>();
            try{var (pid,cl)=ComputerNative.PortUsers(port.Value.Port);owner=pid==0?"":ComputerNative.ImagePath(pid);clients=cl;}
            catch(Exception e) when(e is System.ComponentModel.Win32Exception or EntryPointNotFoundException or OutOfMemoryException){host.Log("computer.chrome.port_owner_unknown",new(){["type"]=e.GetType().Name});}
            ChromeAllowClicker? ck=null;ChromeFree? free=null;CdpSocket? socket=null;bool published=false;
            try
            {
                if(o.AllowDialog)
                {
                    // Prompts left over from dead connections block a new one. They are cancelled only when no other program is connected to the port: another agent's attach may be in flight,
                    // and then its prompt is left alone (ours gets its own Allow click).
                    // (The prompt is a top-level window named like its heading, so the cheap title pass finds it; with no prompt open this costs no UI Automation call.)
                    var scan=ChromeUiLane.Run(_=>ChromeUi.Scan(titledOnly:true),Stopped,1500);var cancel=AllowPrompt.Find(scan.Nodes,owner,AllowPrompt.Cancel);
                    if(cancel.Count>0)
                    {
                        bool others=clients.Any(c=>c!=(uint)Environment.ProcessId);
                        // Chrome ignores a press on a prompt younger than the double-click time (how old this one is, nobody knows): wait that long first, or the Cancel is lost.
                        if(!others){for(long end=Environment.TickCount64+ChromeUi.PressGapMs;Environment.TickCount64<end;Thread.Sleep(25))if(Stopped())throw new OperationCanceledException("connect revoked");}
                        if(!others){foreach(int i in cancel){try{ChromeUi.Press(scan.Elements[i],()=>!Stopped());}catch(Exception e) when(e is InvalidOperationException or System.Windows.Automation.ElementNotAvailableException or System.Runtime.InteropServices.COMException){host.Log("computer.chrome.allow_error",new(){["type"]=e.GetType().Name,["step"]="cancel"});}}for(long end=Environment.TickCount64+350;Environment.TickCount64<end;Thread.Sleep(25))if(Stopped())throw new OperationCanceledException("connect revoked");}
                        host.Log("computer.chrome.stale",new(){["prompts"]=cancel.Count,["cancelled"]=!others});
                    }
                    ck=new ChromeAllowClicker(owner,host.Log,Stopped);ck.Hot(12000);
                }
                using var cts=CancellationTokenSource.CreateLinkedTokenSource(token);cts.CancelAfter(8000);
                var task=CdpSocket.Connect(new Uri("ws://127.0.0.1:"+port.Value.Port+port.Value.Path),Stopped,host.Log,cts.Token);
                try
                {
                    while(!task.Wait(25)){if(Stopped()){cts.Cancel();_=task.ContinueWith(t=>{if(t.Status==TaskStatus.RanToCompletion)t.Result.Dispose();},TaskScheduler.Default);throw new OperationCanceledException("run stopped");}}
                    socket=task.GetAwaiter().GetResult();
                }
                catch(AggregateException a) when(a.GetBaseException() is OperationCanceledException){if(Stopped())throw new OperationCanceledException("connect revoked");throw new CdpError(ck is {Prompts:>0}?"no-answer":"no-dialog");}   // 8 s without an open socket
                catch(AggregateException a) when(a.GetBaseException() is WebSocketException or SocketException or IOException or System.Net.Http.HttpRequestException)
                {
                    var b=a.GetBaseException();
                    if(Refused(b)||Refused(a))
                    {   // the port file is a leftover of an earlier Chrome: nobody listens there
                        ck?.Dispose();ck=null;
                        if(!ChromeUi.Running())throw new CdpError("chrome-not-running");
                        if(attempt==0)
                        {
                            string stale=port.Value.Port+port.Value.Path;
                            if(Enable(dir,stale,Stopped) is not null)continue;
                        }
                        throw new CdpError("debugging-off",LastEnable.Length>0?LastEnable:"port closed");
                    }
                    throw new CdpError(ck is {Prompts:>0}?"no-answer":"debugging-off",b.GetType().Name);   // Chrome refused the handshake (the prompt was declined, or the address in the file is not this Chrome's)
                }
                free=new ChromeFree(socket,attemptState,Stopped,host.Log);
                try{free.Version(default,3000).GetAwaiter().GetResult();}
                catch(Exception e) when(e is TimeoutException or CdpError){ck?.Hot(0);throw new CdpError("no-answer",e.GetType().Name);}
                ck?.Hot(0);
                lock(sync)
                {
                    if(Stopped())throw new OperationCanceledException("connect revoked");
                    conn=free;clicker=ck;published=true;
                    // The published socket still uses the attempt token; Drop cancels it on run end.
                }
                LastConnectClicks=ck?.Clicks??0;
                ck?.Hot(0);   // connected: the clicker goes quiet (it presses nothing and scans nothing until the next connect of ours makes it hot again)
                host.Log("computer.chrome.connected",new(){["run"]=runId,["port"]=port.Value.Port,["version"]=free.BrowserVersion,["ms"]=Environment.TickCount64-t0,["prompt"]=(ck?.Clicks??0)>0,["free"]=true});
                ck=null;
                return free;
            }
            catch(TimeoutException e){throw new CdpError("no-answer",e.Message);}
            finally{ck?.Hot(0);ck?.Dispose();if(!published){if(free is not null)free.Dispose();else socket?.Dispose();}}
        }
    }
    // Remote debugging is off while Chrome runs: switch it on ourselves, once per run. `stale` = the content of a leftover port file that must change. null = it did not work.
    (int Port,string Path)? Enable(string dir,string? stale,Func<bool> stopped)
    {
        int pid=ChromeUi.Windows().Select(w=>{Native.GetWindowThreadProcessId(w.Hwnd,out uint p);return (int)p;}).FirstOrDefault();
        if(enableTried&&pid==enablePid)return null;
        enableTried=true;enablePid=pid;
        var sw=Stopwatch.StartNew();
        bool Ready()=>ReadPort(dir) is {} p&&p.Port+p.Path!=stale;
        (bool Ok,string Step,string Seen) r;
        try{r=ChromeEnable.Run(host,Ready,EnableSnap,stopped);}
        catch(OperationCanceledException){throw;}
        catch(Exception e){r=(false,"error:"+e.GetType().Name,"");}   // UI Automation or SendInput failed: reported as the step that failed, the model goes on with the desktop tools
        LastEnable=r.Step;
        host.Log("computer.chrome.enable",new(){["run"]=runId,["ok"]=r.Ok,["step"]=r.Step,["ms"]=sw.ElapsedMilliseconds});
        return r.Ok?ReadPort(dir):null;
    }
    FreeResult Away(string code,bool fresh)
    {
        string text=ChromeFallback.Text(code);
        // the first answer after a real attempt brings the desktop along, so the model can go on at once
        if(fresh&&host.ScreenShot is {} shot)
        {
            try{if(shot() is {} s){host.Frame=s.Frame;return new FreeResult(false,text+"\n"+s.Text,s.Jpeg,code,Process:"chrome");}}
            catch(Exception e) when(e is not OperationCanceledException){host.Log("computer.chrome.fallback_shot",new(){["type"]=e.GetType().Name});}
        }
        return new FreeResult(false,text,null,code,Process:"chrome");
    }
    // ---- one tool call
    public FreeResult Run(FreeCall call)
    {
        string tool=call.Tool;var args=call.Args;
        var c=Ensure(out string? fail,out bool fresh);
        if(c is null)return Away(fail!,fresh);
        ChromeResult r;
        try{r=c.Run(tool,args).GetAwaiter().GetResult();}
        catch(CdpError e) when(e.Code=="closed")
        {
            // the socket dropped in the middle of the call: connect again (with the clicker). A read is asked again; for an action nobody knows whether it still happened.
            host.ThrowIfStopped();
            c=Ensure(out fail,out fresh);
            if(c is null)return Away(fail!,fresh);
            if(ChromeFree.Acts(tool)||tool=="chrome_close")
                return new FreeResult(false,"Die Verbindung zu Chrome war kurz weg und steht wieder. Ob der Schritt noch ankam, ist offen: mit chrome_snapshot nachsehen und, wenn nötig, wiederholen.",null,"cdp-reconnected",Process:"chrome");
            try{r=c.Run(tool,args).GetAwaiter().GetResult();}
            catch(CdpError e2) when(e2.Code=="closed"){memory.Fail("cdp-closed",Now());return Away("cdp-closed",false);}
        }
        string text=r.Text;string? jpeg=null;
        if(r.Shot is {} own){jpeg=own.Jpeg;host.Frame=FrameOf(own);}
        else if(r.Ok&&r.ObserveTab is {} tab&&FreeArgs.Bool(args,"observe")!=false)
        {
            // auto-observe: let the page settle, then a picture of the tab's visible part; the mouse tools work on it
            int settle=Math.Clamp(FreeArgs.Int(args,"settle_ms")??350,0,5000);
            long end=Environment.TickCount64+settle;
            for(;;){host.ThrowIfStopped();long left=end-Environment.TickCount64;if(left<=0)break;Thread.Sleep((int)Math.Min(left,50));}
            try
            {
                var s=c.Shot(tab,false).GetAwaiter().GetResult();
                jpeg=s.Jpeg;host.Frame=FrameOf(s);text+="\n"+ChromeFree.FrameLine(s,c.AliasOf(tab));
            }
            catch(Exception e) when(e is TimeoutException or CdpError or ChromeToolFail){text+="\n(kein Bild: der Tab antwortet gerade nicht; chrome_screenshot später oder screenshot vom Desktop)";}   // the action itself worked; the model is told that the picture is missing
        }
        if(tool=="chrome_activate"&&r.Ok)Raise(r.Target);
        return new FreeResult(r.Ok,text,jpeg,r.Ok?"free":r.Rule,r.Target,"chrome",r.Domain,r.Stale);
    }
    // chrome_activate also brings the browser window itself to the front (CDP only switches the tab inside it): the window that now carries the tab's title.
    void Raise(string? title)
    {
        if(string.IsNullOrWhiteSpace(title))return;
        try
        {
            var w=ChromeUi.Windows().FirstOrDefault(x=>UiaDriver.ClassOf(x.Hwnd)=="Chrome_WidgetWin_1"&&x.Title.StartsWith(title,StringComparison.Ordinal));
            if(w.Hwnd!=IntPtr.Zero&&UiaDriver.Root(Native.GetForegroundWindow())!=w.Hwnd)new UiaDriver(host.Policy,host.Stopped).Focus(w.Hwnd);
        }
        catch(Exception e) when(e is System.ComponentModel.Win32Exception or InvalidOperationException){host.Log("computer.chrome.raise_failed",new(){["type"]=e.GetType().Name});}
    }
}
