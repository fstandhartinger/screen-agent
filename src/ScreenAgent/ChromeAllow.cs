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
    public static string Press(AutomationElement el)
    {
        try
        {
            if(el.TryGetCurrentPattern(InvokePattern.Pattern,out var p))
            {
                var t=Task.Run(()=>((InvokePattern)p).Invoke());
                if(t.Wait(2000)){t.GetAwaiter().GetResult();return "invoke";}
            }
        }
        catch(Exception e) when(e is ElementNotAvailableException or InvalidOperationException or COMException){ }   // no usable Invoke: the mouse click below is the fallback
        var r=el.Current.BoundingRectangle;
        if(r.IsEmpty||r.Width<2||r.Height<2)throw new InvalidOperationException("prompt button has no place on the screen");
        GetCursorPos(out var was);
        Native.SetCursorPos((int)(r.X+r.Width/2),(int)(r.Y+r.Height/2));
        Thread.Sleep(30);
        Native.Send(Native.Button(true),Native.Button(false));
        Thread.Sleep(30);
        Native.SetCursorPos(was.X,was.Y);
        return "mouse";
    }
    // For the selftest's artifacts and the Core fixture: roles, names and depth of the native UI (no page content is in a scan).
    public static object Dump(UiScan s,string owner,int clicked=-1)
        =>new{owner,clicked,nodes=s.Nodes.Select(n=>new{role=n.Role,name=n.Name,inDocument=n.InDocument,process=n.Process,image=n.ImagePath,depth=n.Depth,parent=n.Parent}).ToArray()};
}
// Answers Chrome's allow prompt. One background thread per connection attempt, alive until the connection is disposed: every 300 ms the native UI of the browser windows is matched
// (AllowPrompt.Find) and every prompt's own button is pressed. While a connect is in flight every pass is a full scan; later a full scan runs every pass only while it stays cheap
// (<= 40 ms), otherwise every 3 s, with a title-only pass in between (the prompt is a window of its own, named like its heading).
internal sealed class ChromeAllowClicker:IDisposable
{
    public const int IntervalMs=300;
    readonly string owner;
    readonly Action<string,Dictionary<string,object?>> log;
    readonly Thread thread;
    readonly Dictionary<IntPtr,long> pressedAt=[],firstSeen=[];
    readonly long started=Environment.TickCount64;
    volatile bool stop;
    long hotUntil,lastFull,lastFullMs;
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
    public ChromeAllowClicker(string ownerImage,Action<string,Dictionary<string,object?>> log)
    {
        owner=ownerImage;this.log=log;
        thread=new Thread(Loop){IsBackground=true,Name="ScreenAgent chrome allow"};thread.Start();
    }
    public void Hot(int ms)=>Interlocked.Exchange(ref hotUntil,Environment.TickCount64+ms);
    void Loop()
    {
        while(!stop)
        {
            try{Tick();}
            catch(Exception e){if(Interlocked.Increment(ref errors)<=3)log("computer.chrome.allow_error",new(){["type"]=e.GetType().Name});}
            for(int i=0;i<IntervalMs/50&&!stop;i++)Thread.Sleep(50);
        }
    }
    void Tick()
    {
        long now=Environment.TickCount64;
        bool full=now<Interlocked.Read(ref hotUntil)||lastFullMs<=40||now-lastFull>=3000;
        var scan=ChromeUi.Scan(titledOnly:!full);
        Interlocked.Increment(ref scans);
        if(full){lastFull=now;lastFullMs=scan.Ms;LastWindows=scan.Windows;LastNodes=scan.Nodes.Count;if(scan.Ms>MaxScanMs)MaxScanMs=scan.Ms;}
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
            string how;
            try{how=ChromeUi.Press(scan.Elements[b]);}
            catch(Exception e) when(e is ElementNotAvailableException or InvalidOperationException or COMException){log("computer.chrome.allow_error",new(){["type"]=e.GetType().Name,["step"]="press"});continue;}
            Interlocked.Increment(ref clicks);
            if(Record)LastTree=ChromeUi.Dump(scan,owner,b);
            log("computer.chrome.allow",new(){["ms"]=Environment.TickCount64-started,["how"]=how,["scan_ms"]=scan.Ms,["waited_ms"]=now-(firstSeen.TryGetValue(hwnd,out long fs)?fs:now)});
        }
    }
    public void Dispose(){stop=true;}
}
// Remote debugging is off (no DevToolsActivePort) while Chrome runs: switch it on the way he would, once per run. A chrome:// page cannot be opened from outside (ShellExecute, command line
// of a running profile), so: bring a Chrome window to the front, Ctrl+T, type chrome://inspect/#remote-debugging, Enter, tick "Allow remote debugging for this browser instance" through
// UI Automation, wait for the port file, close that tab again (Ctrl+W, only while it is still the tab in front), and give the foreground back.
internal static class ChromeEnable
{
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
    // The checkbox of the page, found by its name ("Allow remote debugging for this browser instance"; the page is not translated) or, failing that, as the page's only checkbox.
    static AutomationElement? Box(IntPtr hwnd,List<string> seen)
    {
        var root=AutomationElement.FromHandle(hwnd);
        var boxes=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.CheckBox));
        AutomationElement? only=null;int n=0;
        foreach(AutomationElement b in boxes)
        {
            string name=b.Current.Name??"";
            if(seen.Count<12&&!seen.Contains(name))seen.Add(name);
            if(name.Contains("remote debugging",StringComparison.OrdinalIgnoreCase)||name.Contains("Remote-Debugging",StringComparison.OrdinalIgnoreCase)||name.Contains("Fehlerbehebung",StringComparison.OrdinalIgnoreCase))return b;
            only=b;n++;
        }
        return n==1?only:null;
    }
    // (ok, the step that failed or "done", what was seen). `ready` = the port file is there. `snap` = selftest hook for a picture of each step.
    public static (bool Ok,string Step,string Seen) Run(FreeHost host,Func<bool> ready,Action<string>? snap=null)
    {
        var browser=ChromeUi.Windows().Where(w=>UiaDriver.ClassOf(w.Hwnd)=="Chrome_WidgetWin_1"&&w.Title.Length>0&&Array.IndexOf(AllowPrompt.Headings,w.Title)<0).ToList();
        var pick=browser.FirstOrDefault(w=>w.Title.Contains("Google Chrome",StringComparison.Ordinal));
        if(pick.Hwnd==IntPtr.Zero)pick=browser.FirstOrDefault();
        if(pick.Hwnd==IntPtr.Zero)return (false,"no-window","");
        IntPtr hwnd=pick.Hwnd,before=UiaDriver.Root(Native.GetForegroundWindow());
        var uia=new UiaDriver(host.Policy,host.Stopped);
        var seen=new List<string>();bool opened=false;
        try
        {
            if(!UiaDriver.WaitModifiersUp(1500))return (false,"modifiers-held","");
            if(!uia.Focus(hwnd))return (false,"no-foreground","");
            host.ThrowIfStopped();
            Chord(0x11,0x54);opened=true;   // Ctrl+T
            Wait(host,450);
            if(UiaDriver.Root(Native.GetForegroundWindow())!=hwnd)return (false,"focus-left","");
            TypeText(Url);Wait(host,150);
            Native.Send(V(0x0d,false),V(0x0d,true));
            snap?.Invoke("enable-1-typed");
            // the page is there when the window is titled like it
            long end=Environment.TickCount64+6000;
            while(!UiaDriver.TitleOf(hwnd).StartsWith(PageTitle,StringComparison.Ordinal)){if(Environment.TickCount64>end)return (false,"page-not-opened",UiaDriver.CapTitle(UiaDriver.TitleOf(hwnd)));Wait(host,100);}
            AutomationElement? box=null;end=Environment.TickCount64+6000;
            while(box is null)
            {
                try{box=Box(hwnd,seen);}catch(Exception e) when(e is ElementNotAvailableException or InvalidOperationException or COMException){ }   // the page's tree is still being built: ask again
                if(box is null){if(Environment.TickCount64>end)return (false,"no-checkbox",string.Join(" | ",seen));Wait(host,200);}
            }
            snap?.Invoke("enable-2-page");
            if(!ready())
            {
                bool on=box.TryGetCurrentPattern(TogglePattern.Pattern,out var tp)&&((TogglePattern)tp).Current.ToggleState==ToggleState.On;
                if(!on)
                {
                    if(tp is TogglePattern toggle)toggle.Toggle();
                    else ChromeUi.Press(box);
                }
            }
            end=Environment.TickCount64+6000;
            while(!ready()){if(Environment.TickCount64>end){snap?.Invoke("enable-3-no-port");return (false,"no-port",string.Join(" | ",seen));}Wait(host,100);}
            snap?.Invoke("enable-3-on");
            return (true,"done",string.Join(" | ",seen));
        }
        finally
        {
            // our tab goes away again, but only while it is still the page in front of that window: never one of his
            try
            {
                if(opened&&UiaDriver.Root(Native.GetForegroundWindow())==hwnd&&UiaDriver.TitleOf(hwnd).StartsWith(PageTitle,StringComparison.Ordinal)){Chord(0x11,0x57);Thread.Sleep(250);}
                if(before!=IntPtr.Zero&&before!=hwnd&&!host.Stopped())uia.Focus(before);
            }
            catch(Exception e) when(e is OperationCanceledException or System.ComponentModel.Win32Exception){host.Log("computer.chrome.enable_cleanup",new(){["type"]=e.GetType().Name});}
        }
    }
    static void Wait(FreeHost host,int ms)
    {
        long end=Environment.TickCount64+ms;
        for(;;){host.ThrowIfStopped();long left=end-Environment.TickCount64;if(left<=0)return;Thread.Sleep((int)Math.Min(left,50));}
    }
}
