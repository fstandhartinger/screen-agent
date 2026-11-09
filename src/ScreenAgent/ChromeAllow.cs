using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using ScreenAgent.Core;
namespace ScreenAgent;
// The Windows side of attaching to his running Chrome (free toolset): the native browser UI as a node list for the matcher in Core (AllowPrompt), the clicker that answers
// "Remote-Fehlerbehebung zulassen?" for the whole life of a connection, and switching remote debugging on through chrome://inspect when it is off.
internal sealed record UiScan(List<DialogNode> Nodes,List<AutomationElement> Elements,List<IntPtr> Hwnds,int Windows,long Ms);
internal static class ChromeUi
{
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Native.Point p);
    [DllImport("user32.dll")] static extern uint GetDoubleClickTime();
    // Chrome ignores a press on a dialog button that comes within the double-click time after the dialog appeared, and every press that follows another one within that time
    // (its guard against unintended clicks; seen on the runner: an Invoke every 400 ms was ignored for 8 s). So: wait this long after a prompt is first seen, and between presses.
    public static int PressGapMs=>(int)Math.Clamp(GetDoubleClickTime(),200,5000)+150;
    const int MaxNodes=1500,MaxDepth=30;
    static string Leaf(string image)=>image[(image.LastIndexOfAny(['\\','/'])+1)..];
    public static bool IsBrowser(string image)=>AllowPrompt.Browsers.Any(b=>Leaf(image).Equals(b,StringComparison.OrdinalIgnoreCase));
    public static bool Running()
    {
        var ps=Process.GetProcessesByName("chrome");
        try{return ps.Length>0;}finally{foreach(var p in ps)p.Dispose();}
    }
    // Visible top-level windows of the browser, front to back (bubbles and dialogs are top-level windows of their own).
    public static List<(IntPtr Hwnd,string Image,string Title)> Windows()
    {
        var list=new List<(IntPtr,string,string)>();
        Native.EnumWindows((h,_)=>
        {
            if(!Native.IsWindowVisible(h))return true;
            Native.GetWindowThreadProcessId(h,out uint pid);
            if(pid==Environment.ProcessId)return true;
            string image=ComputerNative.ImagePath(pid);
            if(image.Length>0&&IsBrowser(image))list.Add((h,image,UiaDriver.TitleOf(h)));
            return true;
        },IntPtr.Zero);
        return list;
    }
    // The NATIVE UI of every browser window as nodes. A Document (page content) is listed as one node and never entered: a page is neither read nor clickable here, and Chrome's
    // web accessibility is not woken up by the scan. `titledOnly`: just the windows whose title is the prompt's heading (the cheap pass between full scans).
    public static UiScan Scan(bool titledOnly=false)
    {
        var sw=Stopwatch.StartNew();var s=new UiScan([],[],[],0,0);int windows=0;
        var cache=new CacheRequest{AutomationElementMode=AutomationElementMode.Full,TreeScope=TreeScope.Element};
        cache.Add(AutomationElement.ControlTypeProperty);cache.Add(AutomationElement.NameProperty);cache.Add(AutomationElement.NativeWindowHandleProperty);
        var all=Windows();var tops=all.Select(w=>(int)w.Hwnd).ToHashSet();
        using(cache.Activate())
        {
            foreach(var (h,image,title) in all)
            {
                if(titledOnly&&Array.IndexOf(AllowPrompt.Headings,title)<0)continue;
                windows++;
                try{Walk(AutomationElement.FromHandle(h),image,Path.GetFileNameWithoutExtension(image),h,-1,0,s,tops);}
                catch(Exception e) when(e is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException){ }   // the window closed while it was read: it has no prompt any more
            }
        }
        return s with{Windows=windows,Ms=sw.ElapsedMilliseconds};
    }
    static void Walk(AutomationElement e,string image,string process,IntPtr hwnd,int parent,int depth,UiScan s,HashSet<int> tops)
    {
        if(s.Nodes.Count>=MaxNodes||depth>MaxDepth)return;
        ControlType type=e.Cached.ControlType;string name=e.Cached.Name??"";
        // A dialog is a top-level window of its own AND shows up again inside its browser window's tree: it is listed once, under its own window (else its button would be pressed twice).
        if(depth>0&&e.Cached.NativeWindowHandle is int own&&own!=0&&own!=(int)hwnd&&tops.Contains(own))return;
        s.Nodes.Add(new(type.ProgrammaticName.Replace("ControlType.",""),name.Length>200?name[..200]:name,false,process,image,depth,parent));s.Elements.Add(e);s.Hwnds.Add(hwnd);
        int me=s.Nodes.Count-1;
        if(type==ControlType.Document)return;
        foreach(AutomationElement k in e.FindAll(TreeScope.Children,Automation.ControlViewCondition))Walk(k,image,process,hwnd,me,depth+1,s,tops);
    }
    // InvokePattern first; a button without it gets one real click at its centre (the cursor goes back where it was). Returns how.
    public static string Press(AutomationElement el,Func<bool>? authority=null,Action<string>? dispatched=null)
        =>ChromeUiLane.Run(valid=>PressNow(el,()=>valid()&&(authority?.Invoke()??true),dispatched),()=>authority is not null&&!authority(),500);
    internal static string PressNow(AutomationElement el,Func<bool> allowed,Action<string>? dispatched=null)
    {
        void Check(){if(!allowed())throw new OperationCanceledException("prompt authority revoked");}
        Check();
        if(el.TryGetCurrentPattern(InvokePattern.Pattern,out var p))
        {
            Check();
            // Once Invoke enters a UIA provider Windows cannot recall it. The caller is bounded,
            // no fallback is issued on timeout, and the sole lane remains occupied until it returns.
            dispatched?.Invoke("invoke");Check();((InvokePattern)p).Invoke();return "invoke";
        }
        var r=el.Current.BoundingRectangle;
        if(r.IsEmpty||r.Width<2||r.Height<2)throw new InvalidOperationException("prompt button has no place on the screen");
        Check();GetCursorPos(out var was);
        Check();Native.SetCursorPos((int)(r.X+r.Width/2),(int)(r.Y+r.Height/2));
        Check();dispatched?.Invoke("mouse");Check();Native.Send(Native.Button(true),Native.Button(false));
        if(allowed())Native.SetCursorPos(was.X,was.Y);
        return "mouse";
    }
    // For the selftest's artifacts and the Core fixture: roles, names and depth of the native UI (no page content is in a scan).
    public static object Dump(UiScan s,string owner,int clicked=-1)
        =>new{owner,clicked,nodes=s.Nodes.Select(n=>new{role=n.Role,name=n.Name,inDocument=n.InDocument,process=n.Process,image=n.ImagePath,depth=n.Depth,parent=n.Parent}).ToArray()};
}
// Only an in-flight connection attempt grants authority. Every scan/dispatch carries its generation;
// revocation discards late results. All provider calls share one bounded lane with the enable path.
internal sealed class ChromeAllowClicker:IDisposable
{
    public const int IntervalMs=300;
    readonly string owner;
    readonly Action<string,Dictionary<string,object?>> log;
    readonly Thread thread;
    readonly Dictionary<IntPtr,long> pressedAt=[],firstSeen=[];
    readonly long started=Environment.TickCount64;
    volatile bool stop;readonly Func<bool> stopped;long generation;
    long hotUntil,lastFull;
    int clicks,scans,prompts,errors;
    public int Clicks=>clicks;
    public int Scans=>scans;
    public int Prompts=>prompts;
    public long MaxScanMs {get;private set;}
    public int LastWindows {get;private set;}
    public int LastNodes {get;private set;}
    // Selftest only: keep the tree of the last prompt that was pressed (and the button's index), to write it to the artifacts.
    public static volatile bool Record;
    public static volatile object? LastTree;
    public ChromeAllowClicker(string ownerImage,Action<string,Dictionary<string,object?>> log,Func<bool>? stopped=null)
    {
        owner=ownerImage;this.log=log;this.stopped=stopped??(()=>false);
        thread=new Thread(Loop){IsBackground=true,Name="ScreenAgent chrome allow"};thread.Start();
    }
    public void Hot(int ms){Interlocked.Increment(ref generation);Interlocked.Exchange(ref hotUntil,ms>0?Environment.TickCount64+ms:0);}
    void Loop()
    {
        while(!stop)
        {
            try{Tick();}
            catch(Exception e){if(Interlocked.Increment(ref errors)<=3)log("computer.chrome.allow_error",new(){["type"]=e.GetType().Name});}
            // while a connect is in flight the prompt is looked for every 100 ms (title pass): the sooner it is seen, the sooner Chrome accepts the press
            int wait=Environment.TickCount64<Interlocked.Read(ref hotUntil)?100:IntervalMs;
            for(int i=0;i<wait/50&&!stop;i++)Thread.Sleep(50);
        }
    }
    void Tick()
    {
        long epoch=Interlocked.Read(ref generation);
        bool Allowed()=>!stop&&!stopped()&&epoch==Interlocked.Read(ref generation)&&Environment.TickCount64<Interlocked.Read(ref hotUntil);
        long now=Environment.TickCount64;
        bool hot=Allowed()&&now<Interlocked.Read(ref hotUntil);
        // Lead decision (9 Nov 2026): the prompt is pressed only while OUR OWN connect is in flight (ChromeTools makes the clicker hot for that time, also on a reconnect).
        // A prompt that appears at any other moment was caused by another program; that one is his to answer, and Chrome's window is not walked for nothing.
        if(!hot)return;
        bool full=now-lastFull>=IntervalMs;
        var scan=ChromeUiLane.Run(_=>ChromeUi.Scan(titledOnly:!full),()=>!Allowed(),1200);
        if(!Allowed())return;
        Interlocked.Increment(ref scans);
        if(full){lastFull=now;LastWindows=scan.Windows;LastNodes=scan.Nodes.Count;if(scan.Ms>MaxScanMs)MaxScanMs=scan.Ms;}
        int gap=ChromeUi.PressGapMs;var done=new HashSet<string>();
        foreach(int b in AllowPrompt.Find(scan.Nodes,owner))
        {
            IntPtr hwnd=scan.Hwnds[b];
            string id;try{id=string.Join(".",scan.Elements[b].GetRuntimeId());}catch(Exception e) when(e is ElementNotAvailableException or InvalidOperationException or COMException){continue;}   // the prompt closed while it was matched
            if(!done.Add(id))continue;   // the same button reached by two paths
            lock(pressedAt)
            {
                if(!firstSeen.TryGetValue(hwnd,out long seen)){firstSeen[hwnd]=now;Interlocked.Increment(ref prompts);continue;}   // seen now: pressed on a later pass, once Chrome accepts a press
                if(now-seen<gap)continue;
                if(pressedAt.TryGetValue(hwnd,out long at)&&now-at<gap+100)continue;
                pressedAt[hwnd]=now;
            }
            try
            {
                if(!Allowed())return;
                ChromeUi.Press(scan.Elements[b],Allowed,how=>
                {
                    Interlocked.Increment(ref clicks);if(Record)LastTree=ChromeUi.Dump(scan,owner,b);
                    // Receipt belongs to dispatch: Chrome may finish the handshake before Invoke returns.
                    log("computer.chrome.allow",new(){["phase"]="dispatch",["ms"]=Environment.TickCount64-started,["how"]=how,["scan_ms"]=scan.Ms,["waited_ms"]=now-(firstSeen.TryGetValue(hwnd,out long fs)?fs:now)});
                });
            }
            catch(Exception e) when(e is ElementNotAvailableException or InvalidOperationException or COMException){log("computer.chrome.allow_error",new(){["type"]=e.GetType().Name,["step"]="press"});}

        }
    }
    public void Dispose(){Hot(0);stop=true;}
}
// Remote debugging is off (no DevToolsActivePort) while Chrome runs: switch it on the way he would, once per run. A chrome:// page cannot be opened from outside (ShellExecute, command line
// of a running profile), so: bring a Chrome window to the front, Ctrl+T, type chrome://inspect/#remote-debugging, Enter, tick "Allow remote debugging for this browser instance" through
// UI Automation, wait for the port file, close that tab again (Ctrl+W, only while it is still the tab in front), and give the foreground back.
internal static class ChromeEnable
{
    internal static Action<string>? BeforeInput; // Deterministic selftest race injection; never set by normal startup.
    public const string Url="chrome://inspect/#remote-debugging";
    const string PageTitle="Inspect with Chrome Developer Tools";
    static Native.Input V(ushort vk,bool up)=>Native.Key(vk,up,0,false,Native.Marker);
    static void Chord(ushort mod,ushort key)=>Native.Send(V(mod,false),V(key,false),V(key,true),V(mod,true));
    static void TypeText(string text)
    {
        var keys=new Native.Input[text.Length*2];
        for(int i=0;i<text.Length;i++){keys[2*i]=Native.Key(0,false,text[i],true,Native.Marker);keys[2*i+1]=Native.Key(0,true,text[i],true,Native.Marker);}
        Native.Send(keys);
    }
    static string Id(AutomationElement e)=>string.Join(".",e.GetRuntimeId());
    static string Value(AutomationElement e)=>e.TryGetCurrentPattern(ValuePattern.Pattern,out var p)?((ValuePattern)p).Current.Value:"";
    static bool InDocument(AutomationElement e)
    {
        for(int i=0;i<40&&e is not null;i++,e=TreeWalker.ControlViewWalker.GetParent(e))if(e.Current.ControlType==ControlType.Document)return true;
        return false;
    }
    // Web pages (including inspect itself) also expose selected TabItems. Only the native strip identifies our tab.
    static List<AutomationElement> Tabs(AutomationElement root)=>root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.TabItem)).Cast<AutomationElement>().Where(t=>!InDocument(t)).ToList();
    static AutomationElement? Selected(AutomationElement root)=>Tabs(root).FirstOrDefault(t=>t.TryGetCurrentPattern(SelectionItemPattern.Pattern,out var p)&&((SelectionItemPattern)p).Current.IsSelected);
    static bool Omnibox(AutomationElement e)=>e.Current.ControlType==ControlType.Edit&&!InDocument(e)&&
        (e.Current.AutomationId=="omnibox"||e.Current.Name is "Address and search bar" or "Adress- und Suchleiste");
    static AutomationElement? Address(AutomationElement root)=>root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit)).Cast<AutomationElement>().FirstOrDefault(Omnibox);
    // Only a named checkbox within the verified internal page can enable debugging.
    static AutomationElement? Box(AutomationElement root,List<string> seen)
    {
        var docs=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Document));
        foreach(AutomationElement doc in docs)
        {
            if(doc.Current.IsOffscreen||!doc.Current.Name.StartsWith(PageTitle,StringComparison.Ordinal))continue;
            foreach(AutomationElement b in doc.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.CheckBox)))
            {
                string name=b.Current.Name??"";
                if(seen.Count<12&&!seen.Contains(name))seen.Add(name);
                if(name.Contains("remote debugging",StringComparison.OrdinalIgnoreCase)||name.Contains("Remote-Debugging",StringComparison.OrdinalIgnoreCase)||name.Contains("Fehlerbehebung",StringComparison.OrdinalIgnoreCase))return b;
            }
        }
        return null;
    }
    public static (bool Ok,string Step,string Seen) Run(FreeHost host,Func<bool> ready,Action<string>? snap=null,Func<bool>? cancelled=null)
        =>ChromeUiLane.Run(valid=>RunCore(host,ready,snap,valid),()=>host.Stopped()||(cancelled?.Invoke()??false),20000);
    static (bool Ok,string Step,string Seen) RunCore(FreeHost host,Func<bool> ready,Action<string>? snap,Func<bool> valid)
    {
        var browser=ChromeUi.Windows().Where(w=>UiaDriver.ClassOf(w.Hwnd)=="Chrome_WidgetWin_1"&&w.Title.Length>0&&Array.IndexOf(AllowPrompt.Headings,w.Title)<0).ToList();
        var pick=browser.FirstOrDefault(w=>w.Title.Contains("Google Chrome",StringComparison.Ordinal));
        if(pick.Hwnd==IntPtr.Zero)pick=browser.FirstOrDefault();
        if(pick.Hwnd==IntPtr.Zero)return (false,"no-window","");
        IntPtr hwnd=pick.Hwnd,before=UiaDriver.Root(Native.GetForegroundWindow());
        var seen=new List<string>();
        var root=AutomationElement.FromHandle(hwnd);string? own=null,omnibox=null;string typed="";bool lost=false,navigated=false;
        void Check()
        {
            host.ThrowIfStopped();if(!valid())throw new OperationCanceledException("enable deadline");
            if(UiaDriver.Root(Native.GetForegroundWindow())!=hwnd){lost=true;throw new InvalidOperationException("focus-left");}
        }
        void Tab()
        {
            Check();var selected=Selected(root);
            if(selected is null)throw new InvalidOperationException("tab-unavailable");
            if(own is null||Id(selected)!=own){lost=true;throw new InvalidOperationException("tab-left");}
            Check();
        }
        bool Page()
        {
            Tab();var address=Address(root);bool ok=address is not null&&Value(address)==Url;
            if(ok)ok=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Document)).Cast<AutomationElement>()
                .Any(d=>!d.Current.IsOffscreen&&d.Current.Name.StartsWith(PageTitle,StringComparison.Ordinal));
            Check();return ok;
        }
        bool FocusedAddress(string expected)
        {
            Tab();var f=AutomationElement.FocusedElement;
            bool ok=f is not null&&Omnibox(f)&&(omnibox is null||Id(f)==omnibox)&&Value(f)==expected;
            if(ok&&omnibox is null)omnibox=Id(f!);
            Check();return ok;
        }
        try
        {
            if(!UiaDriver.WaitModifiersUp(1500))return (false,"modifiers-held","");
            if(!valid())throw new OperationCanceledException("enable revoked");
            if(Native.IsIconic(hwnd))Native.ShowWindow(hwnd,9);
            Native.SetForegroundWindow(hwnd);
            for(int i=0;i<8&&UiaDriver.Root(Native.GetForegroundWindow())!=hwnd;i++){if(!valid())throw new OperationCanceledException("enable revoked");Wait(host,25);}
            if(UiaDriver.Root(Native.GetForegroundWindow())!=hwnd)return (false,"no-foreground","");
            var oldTabs=Tabs(root).Select(Id).ToHashSet();
            BeforeInput?.Invoke("new-tab");Check();Chord(0x11,0x54);
            Wait(host,450);Check();
            var selected=Selected(root);
            if(selected is null||oldTabs.Contains(Id(selected)))return (false,"new-tab-unconfirmed","");
            own=Id(selected);
            if(!FocusedAddress(""))return (false,"no-omnibox","");
            // One bounded input chunk, after proof that our new tab's native omnibox is focused and empty.
            BeforeInput?.Invoke("type");if(!FocusedAddress(""))return (false,"omnibox-changed","");Check();TypeText(Url);typed=Url;
            Wait(host,150);
            BeforeInput?.Invoke("enter");
            if(!FocusedAddress(Url))return (false,"url-unconfirmed","");
            Check();Native.Send(V(0x0d,false),V(0x0d,true));navigated=true;
            snap?.Invoke("enable-1-typed");
            AutomationElement? box=null;long end=Environment.TickCount64+6000;
            while(box is null)
            {
                try{Tab();if(Page())box=Box(root,seen);}
                catch(Exception e) when(e is ElementNotAvailableException or COMException||(e is InvalidOperationException&&e.Message is not ("tab-left" or "focus-left")))
                {host.Log("computer.chrome.enable_read_retry",new(){["type"]=e.GetType().Name});} // Chrome rebuilds its accessibility tree during navigation; no input is sent until a fresh proof succeeds.
                if(box is null){if(Environment.TickCount64>end)return (false,"no-verified-checkbox",string.Join(" | ",seen));Wait(host,100);}
            }
            snap?.Invoke("enable-2-page");
            if(!ready())
            {
                if(!Page())return (false,"page-left","");
                bool on=box.TryGetCurrentPattern(TogglePattern.Pattern,out var tp)&&((TogglePattern)tp).Current.ToggleState==ToggleState.On;
                if(!on)
                {
                    BeforeInput?.Invoke("toggle");if(!Page())return (false,"page-left","");Check();
                    if(tp is TogglePattern toggle)toggle.Toggle();
                    else ChromeUi.PressNow(box,()=>{Check();return valid();});
                }
            }
            end=Environment.TickCount64+6000;
            while(!ready()){Tab();if(Environment.TickCount64>end){snap?.Invoke("enable-3-no-port");return (false,"no-port",string.Join(" | ",seen));}Wait(host,50);}
            snap?.Invoke("enable-3-on");return (true,"done",string.Join(" | ",seen));
        }
        finally
        {
            try
            {
                if(valid()&&!host.Stopped()&&!lost&&UiaDriver.Root(Native.GetForegroundWindow())==hwnd)
                {
                    if(own is not null&&navigated&&Page()){BeforeInput?.Invoke("close");if(Page()){Check();Chord(0x11,0x57);}}
                    else if(own is not null&&!navigated&&typed.Length>0&&FocusedAddress(typed)){Check();Native.Send(V(0x1b,false),V(0x1b,true));}
                    if(valid()&&!host.Stopped()&&!lost&&UiaDriver.Root(Native.GetForegroundWindow())==hwnd&&before!=IntPtr.Zero&&before!=hwnd)Native.SetForegroundWindow(before);
                }
            }
            catch(Exception e){host.Log("computer.chrome.enable_cleanup",new(){["type"]=e.GetType().Name});}
        }
    }
    static void Wait(FreeHost host,int ms)
    {
        long end=Environment.TickCount64+ms;
        for(;;){host.ThrowIfStopped();long left=end-Environment.TickCount64;if(left<=0)return;Thread.Sleep((int)Math.Min(left,50));}
    }
}
