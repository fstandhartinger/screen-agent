using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using ScreenAgent.Core;
namespace ScreenAgent;
internal sealed record UiaHeld(string Ref,AutomationElement El,string Rid,int Pid,IntPtr Hwnd,string Name,string Type,Rectangle Rect,bool Password,string Process,string Title,string Class);
internal sealed record UiaWindow(string Ref,IntPtr Hwnd,string Process,string Title,bool Foreground,string Class);
internal sealed class UiaStaleException(string what):Exception(what){}
// Windows and UI Automation for computer use. Refs are unique per snapshot and die with the next one; elements are held, never re-resolved by name. Invoke first. Typing re-checks the target
// before EVERY batch of at most 24 characters (the window between a check and the keys of one batch is the remaining gap, the same as the Main Agent prompt's). Password fields are never read or typed into.
internal sealed class UiaDriver
{
    readonly ComputerPolicy policy;
    readonly Func<bool> revoked;
    readonly Dictionary<string,UiaHeld> held=[];
    readonly Dictionary<string,UiaWindow> windows=[];
    int snapshot;
    public static volatile int KeysSent;
    public UiaDriver(ComputerPolicy policy,Func<bool> revoked){this.policy=policy;this.revoked=revoked;}
    static string Cap(string? s,int n){s=new string((s??"").Where(c=>!char.IsControl(c)).ToArray());return s.Length<=n?s:s[..n];}
    public static string ProcessOf(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h,out uint pid);
        try{using var p=Process.GetProcessById((int)pid);return p.ProcessName;}catch{return "";}   // process already gone: an empty name is classified as unknown (R2 or refused), never allowed
    }
    public static string TitleOf(IntPtr h){var sb=new StringBuilder(512);Native.GetWindowText(h,sb,sb.Capacity);return sb.ToString();}
    public static string ClassOf(IntPtr h){var sb=new StringBuilder(128);Native.GetClassName(h,sb,sb.Capacity);return sb.ToString();}
    public static IntPtr Root(IntPtr h){var r=Native.GetAncestor(h,2);return r==IntPtr.Zero?h:r;}
    // ---- windows
    public List<UiaWindow> Windows()
    {
        snapshot++;held.Clear();windows.Clear();
        var list=new List<UiaWindow>();var fg=Root(Native.GetForegroundWindow());int n=0;
        Native.EnumWindows((h,_)=>
        {
            if(list.Count>=60||!Native.IsWindowVisible(h))return true;
            if(Native.DwmGetWindowAttribute(h,14,out int cloaked,4)!=0||cloaked!=0)return true;
            Native.GetWindowThreadProcessId(h,out uint pid);if(pid==Environment.ProcessId)return true;
            string title=TitleOf(h);if(title.Length==0)return true;
            string proc=ProcessOf(h);if(proc.Length==0)return true;
            var w=new UiaWindow("w"+snapshot+"_"+(++n),h,proc,title,h==fg,ClassOf(h));
            list.Add(w);windows[w.Ref]=w;return true;
        },IntPtr.Zero);
        return list;
    }
    public string WindowListing(List<UiaWindow> ws)=>string.Join("\n",ws.Select(w=>w.Ref+" "+w.Process+" \""+(policy.ReadDenied(w.Process,w.Title,null)?"ausgeblendet":Cap(w.Title,80))+"\""+(w.Foreground?" [vorn]":"")));
    public UiaWindow? Window(string reference)=>windows.TryGetValue(reference,out var w)?w:null;
    public static UiaWindow Describe(IntPtr h)=>new("",h,ProcessOf(h),TitleOf(h),Root(Native.GetForegroundWindow())==h,ClassOf(h));
    // ---- tree (400 nodes at most, breadth first)
    public (string Text,int Count) Tree(UiaWindow w)
    {
        if(policy.ReadDenied(w.Process,w.Title,null))return ("ausgeblendet",0);
        snapshot++;held.Clear();
        var sb=new StringBuilder();var queue=new Queue<AutomationElement>();int count=0;
        queue.Enqueue(AutomationElement.FromHandle(w.Hwnd));
        while(queue.Count>0&&count<400)
        {
            if(revoked())throw new OperationCanceledException("run revoked");
            var e=queue.Dequeue();
            AutomationElement.AutomationElementInformation c;
            try{c=e.Current;}catch(ElementNotAvailableException){continue;}   // element vanished while walking: skipped, the tree is a snapshot
            string type=c.ControlType.ProgrammaticName.Replace("ControlType.","");
            bool pw=c.IsPassword;
            if(count>0&&!c.IsOffscreen)
            {
                string reference="u"+snapshot+"e"+(++count);
                var rect=new Rectangle((int)c.BoundingRectangle.X,(int)c.BoundingRectangle.Y,(int)c.BoundingRectangle.Width,(int)c.BoundingRectangle.Height);
                string rid=string.Join(".",e.GetRuntimeId());
                held[reference]=new(reference,e,rid,c.ProcessId,w.Hwnd,c.Name??"",type,rect,pw,w.Process,w.Title,c.ClassName??"");
                sb.Append(reference).Append(' ').Append(type).Append(" \"").Append(pw?"":Cap(c.Name,80)).Append('"').Append(pw?" [geschützt]":"").Append(c.IsEnabled?"":" [aus]");
                if(!pw&&type is "Edit" or "Document"&&c.IsKeyboardFocusable&&Value(e) is {} v)sb.Append(" Inhalt=\"").Append(Cap(v,200)).Append('"');
                sb.Append('\n');
            }
            else if(count==0)count++;
            var kid=TreeWalker.ControlViewWalker.GetFirstChild(e);int n=0;
            while(kid is not null&&n++<80){queue.Enqueue(kid);kid=TreeWalker.ControlViewWalker.GetNextSibling(kid);}
        }
        return (sb.ToString(),held.Count);
    }
    static string? Value(AutomationElement e)
    {
        try
        {
            if(e.TryGetCurrentPattern(ValuePattern.Pattern,out var v))return ((ValuePattern)v).Current.Value;
            if(e.TryGetCurrentPattern(TextPattern.Pattern,out var t))return ((TextPattern)t).DocumentRange.GetText(200);
        }
        catch{ }   // best effort; the caller verifies the result itself
        return null;
    }
    public UiaHeld? Held(string reference)=>held.TryGetValue(reference,out var h)?h:null;
    // ---- the focused element, for a key that asks (Enter): bound to the element, never to "whatever has the focus when the key goes out"
    public sealed record Focused(ActionBinding Binding,string Name,string Type,bool Password,string Process,string Title);
    public Focused? FocusBinding(string chord,IntPtr foreground)
    {
        AutomationElement? f;try{f=AutomationElement.FocusedElement;}catch{f=null;}   // no focus readable: null, the key is then refused
        if(f is null)return null;
        AutomationElement.AutomationElementInformation c;
        try{c=f.Current;}catch(ElementNotAvailableException){return null;}   // the focused element vanished: no focus, so no asking key
        Native.GetWindowThreadProcessId(foreground,out uint fgPid);
        if(c.ProcessId!=fgPid)return null;   // the focus sits in another process than the window in front
        var rect=new Rectangle((int)c.BoundingRectangle.X,(int)c.BoundingRectangle.Y,(int)c.BoundingRectangle.Width,(int)c.BoundingRectangle.Height);
        var b=new ActionBinding().Set("rid",string.Join(".",f.GetRuntimeId())).Set("pid",c.ProcessId).Set("hwnd",foreground).Set("name",c.Name??"").Set("type",c.ControlType.ProgrammaticName)
            .Set("rect",rect.X+","+rect.Y+","+rect.Width+","+rect.Height).Set("tool","key").Set("key",KeyChord.Normalize(chord));
        return new(b,c.Name??"",c.ControlType.ProgrammaticName.Replace("ControlType.",""),c.IsPassword,ProcessOf(foreground),TitleOf(foreground));
    }
    // Is another window lying over part of this one (z-order from the top)? A screenshot copies the screen rectangle, so whatever covers the window would be in the picture.
    public static bool Covered(IntPtr hwnd)
    {
        if(!Native.GetWindowRect(hwnd,out var rc))return true;
        var target=new WindowCover.Box(rc.Left,rc.Top,rc.Right,rc.Bottom);var above=new List<WindowCover.Box>();bool found=false;
        Native.EnumWindows((h,_)=>
        {
            if(found)return true;
            if(Root(h)==Root(hwnd)){found=true;return true;}
            if(!Native.IsWindowVisible(h)||Native.IsIconic(h))return true;
            if(Native.DwmGetWindowAttribute(h,14,out int cloaked,4)!=0||cloaked!=0)return true;
            Native.GetWindowThreadProcessId(h,out uint pid);if(pid==Environment.ProcessId)return true;   // our overlay is excluded from capture
            if((Native.GetWindowLongPtr(h,-20).ToInt64()&0x20)!=0)return true;   // WS_EX_TRANSPARENT: click-through helper windows
            if(Native.GetWindowRect(h,out var r))above.Add(new(r.Left,r.Top,r.Right,r.Bottom));
            return true;
        },IntPtr.Zero);
        return !found||WindowCover.Covered(target,above);
    }
    // ---- binding
    public ActionBinding Bind(UiaHeld h,string tool,string? text,bool foreground)
    {
        AutomationElement.AutomationElementInformation c;
        try{c=h.El.Current;}catch(ElementNotAvailableException){throw new UiaStaleException("gone");}
        var rect=new Rectangle((int)c.BoundingRectangle.X,(int)c.BoundingRectangle.Y,(int)c.BoundingRectangle.Width,(int)c.BoundingRectangle.Height);
        var b=new ActionBinding().Set("rid",string.Join(".",h.El.GetRuntimeId())).Set("pid",c.ProcessId).Set("hwnd",h.Hwnd).Set("name",c.Name??"").Set("type",c.ControlType.ProgrammaticName)
            .Set("rect",rect.X+","+rect.Y+","+rect.Width+","+rect.Height).Set("tool",tool).Set("text",text??"");
        if(foreground)b.Set("fg",Root(Native.GetForegroundWindow())==h.Hwnd);
        return b;
    }
    // The binding as it was when the snapshot was taken, for R1 actions: still the same element in the same place.
    public ActionBinding Snapshotted(UiaHeld h,string tool,string? text)
    {
        var b=new ActionBinding().Set("rid",h.Rid).Set("pid",h.Pid).Set("hwnd",h.Hwnd).Set("name",h.Name).Set("type","ControlType."+h.Type)
            .Set("rect",h.Rect.X+","+h.Rect.Y+","+h.Rect.Width+","+h.Rect.Height).Set("tool",tool).Set("text",text??"");
        return b;
    }
    public ActionContext Context(UiaHeld h,string tool,string? text=null,string? key=null)=>new(tool)
    {
        Process=h.Process,Title=h.Title,Label=h.Name,Role=h.Type,Password=h.Password,Text=text,Key=key,Nameless=h.Name.Length==0,
        SearchBox=h.Type=="Edit"&&Regex.IsMatch(h.Name,@"(?i)(such|search)"),
        FileDialog=h.Class=="#32770"&&Regex.IsMatch(h.Title,@"(?i)(öffnen|speichern|open|save|datei|file)")||h.Process.Equals("explorer",StringComparison.OrdinalIgnoreCase)
    };
    // ---- actions
    void Check(){if(revoked())throw new OperationCanceledException("run revoked");}
    public static bool WaitModifiersUp(int ms)
    {
        long end=Environment.TickCount64+ms;
        while(!Native.ModifiersUp){if(Environment.TickCount64>end)return false;Thread.Sleep(15);}
        return true;
    }
    public bool Focus(IntPtr hwnd)
    {
        Check();
        if(Native.IsIconic(hwnd))Native.ShowWindow(hwnd,9);
        for(int i=0;i<3;i++)
        {
            Native.SetForegroundWindow(hwnd);
            for(int k=0;k<10;k++){if(Root(Native.GetForegroundWindow())==hwnd)return true;Thread.Sleep(50);}
            // The foreground lock: a tap of an unassigned key lets the call through.
            Native.Send(Native.Key(0xe8,false,0,false,Native.Marker),Native.Key(0xe8,true,0,false,Native.Marker));
        }
        return Root(Native.GetForegroundWindow())==hwnd;
    }
    // Invoke first. The mouse is the fallback only for R1, for a named element outside browsers, after a hit test; R2 never gets it.
    public string Click(UiaHeld h,ActionBinding? expected,bool allowMouse)
    {
        Check();
        var fresh=Bind(h,"ui_click",null,expected is not null);
        var want=expected??Snapshotted(h,"ui_click",null);
        if(!want.Same(fresh)&&expected is null)
        {   // R1: only the position may have moved with the layout; identity fields must hold
            var d=want.Diff(fresh).Where(f=>f!="rect").ToList();if(d.Count>0)throw new UiaStaleException("changed:"+string.Join(",",d));
        }
        else if(expected is not null&&!want.Same(fresh))throw new UiaStaleException("changed:"+string.Join(",",want.Diff(fresh)));
        if(Root(Native.GetForegroundWindow())!=h.Hwnd&&!Focus(h.Hwnd))throw new UiaStaleException("not-foreground");
        Check();
        var el=h.El;
        object? p;
        string how="";
        Action? act=null;
        if(el.TryGetCurrentPattern(InvokePattern.Pattern,out p)){var ip=(InvokePattern)p;act=()=>ip.Invoke();how="invoke";}
        else if(el.TryGetCurrentPattern(TogglePattern.Pattern,out p)){var tp=(TogglePattern)p;act=()=>tp.Toggle();how="toggle";}
        else if(el.TryGetCurrentPattern(SelectionItemPattern.Pattern,out p)){var sp=(SelectionItemPattern)p;act=()=>sp.Select();how="select";}
        else if(el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern,out p)){var xp=(ExpandCollapsePattern)p;act=()=>xp.Expand();how="expand";}
        if(act is not null&&expected is not null&&how!="invoke")throw new InvalidOperationException("no-invoke");   // R2 is Invoke only: Toggle, Select and Expand are not what he was asked about
        if(act is not null)
        {
            // Invoke can block until the app's handler returns (a modal dialog): a time limit, and then "unclear", never a retry.
            var task=Task.Run(act);
            if(!task.Wait(3000))throw new TimeoutException("invoke");
            task.GetAwaiter().GetResult();
            return how;
        }
        if(!allowMouse||h.Name.Length==0||ComputerPolicy.IsBrowser(h.Process))throw new InvalidOperationException("no-invoke");
        var r=h.El.Current.BoundingRectangle;int x=(int)(r.X+r.Width/2),y=(int)(r.Y+r.Height/2);
        var at=AutomationElement.FromPoint(new System.Windows.Point(x,y));
        if(at is null||!(Automation.Compare(at,el)||Inside(at,el)))throw new UiaStaleException("hit-other");
        Check();
        Native.SetCursorPos(x,y);Native.Send(Native.Button(true),Native.Button(false));
        return "mouse";
    }
    static bool Inside(AutomationElement child,AutomationElement ancestor)
    {
        var w=TreeWalker.ControlViewWalker;var p=w.GetParent(child);
        for(int i=0;p is not null&&i<12;i++,p=w.GetParent(p))if(Automation.Compare(p,ancestor))return true;
        return false;
    }
    static Native.Input V(ushort vk,bool up)=>Native.Key(vk,up,0,false,Native.Marker);
    static ushort Vk(string key)=>key switch{"tab"=>0x09,"enter"=>0x0d,"esc"=>0x1b,"up"=>0x26,"down"=>0x28,"left"=>0x25,"right"=>0x27,"pageup"=>0x21,"pagedown"=>0x22,"home"=>0x24,"end"=>0x23,"f"=>0x46,_=>0};
    // One atomic batch (modifiers down, key, modifiers up). The foreground window is checked right before; a failure releases the modifiers.
    public string Key(string chord,IntPtr expectedWindow)
    {
        Check();
        string n=KeyChord.Normalize(chord);var parts=n.Split('+');ushort key=Vk(parts[^1]);
        if(key==0||parts.Contains("win"))throw new InvalidOperationException("key");
        if(expectedWindow!=IntPtr.Zero&&Root(Native.GetForegroundWindow())!=expectedWindow)throw new UiaStaleException("not-foreground");
        if(!WaitModifiersUp(1000))throw new UiaStaleException("modifiers");
        var mods=parts[..^1].Select(m=>m switch{"ctrl"=>(ushort)0x11,"alt"=>(ushort)0x12,"shift"=>(ushort)0x10,_=>(ushort)0}).Where(v=>v!=0).ToArray();
        var list=new List<Native.Input>();
        foreach(var m in mods)list.Add(V(m,false));
        list.Add(V(key,false));list.Add(V(key,true));
        for(int i=mods.Length-1;i>=0;i--)list.Add(V(mods[i],true));
        Check();
        try{Native.Send(list.ToArray());Interlocked.Add(ref KeysSent,list.Count);}
        catch{foreach(var m in mods)try{Native.Send(V(m,true));}catch{ }throw;}   // releasing modifiers after a failed batch; the original exception is rethrown and logged by the caller
        return "taste:"+n;
    }
    // Typing: the element must be the one that has the keyboard focus, in the foreground window, no modifier held, not a password field; re-checked before every batch.
    public string Type(UiaHeld h,string text,ActionBinding? expected)
    {
        Check();
        if(h.Password)throw new InvalidOperationException("password-field");
        var fresh=Bind(h,"ui_type",text,expected is not null);
        if(expected is not null&&!expected.Same(fresh))throw new UiaStaleException("changed:"+string.Join(",",expected.Diff(fresh)));
        if(h.El.Current.IsPassword||!h.El.Current.IsEnabled)throw new InvalidOperationException("password-field");
        if(ComputerNative.ElevatedAbove(h.Hwnd))throw new InvalidOperationException("elevated");
        if(Root(Native.GetForegroundWindow())!=h.Hwnd&&!Focus(h.Hwnd))throw new UiaStaleException("not-foreground");
        try{h.El.SetFocus();}catch(InvalidOperationException){throw new UiaStaleException("no-focus");}
        Thread.Sleep(120);
        int typed=0;var rnd=new Random();
        foreach(var step in PromptText.Steps(text))
        {
            Check();
            if(Root(Native.GetForegroundWindow())!=h.Hwnd)throw new UiaStaleException("focus-left");
            if(!WaitModifiersUp(1000))throw new UiaStaleException("modifiers");
            AutomationElement? f;try{f=AutomationElement.FocusedElement;}catch{f=null;}   // no focused element: null makes the typing check refuse
            if(f is null||!Automation.Compare(f,h.El))throw new UiaStaleException("focus-left");
            var keys=new Native.Input[step.Chars.Length*2];
            for(int i=0;i<step.Chars.Length;i++){keys[2*i]=Native.Key(0,false,step.Chars[i],true,Native.Marker);keys[2*i+1]=Native.Key(0,true,step.Chars[i],true,Native.Marker);}
            Native.Send(keys);Interlocked.Add(ref KeysSent,keys.Length);typed+=step.Chars.Length;
            Thread.Sleep(rnd.Next(6,11));
        }
        Thread.Sleep(200);   // some editors take the keys on another thread
        string? back=Value(h.El);
        return "getippt:"+typed+(back is null?"":PromptText.Landed(back,text)?":gelandet":":nicht-bestätigt");
    }
    // Wheel at the middle of the window, after proving the window under that point is the target.
    public string Scroll(IntPtr hwnd,int dy)
    {
        Check();
        if(!Native.GetWindowRect(hwnd,out var r))throw new UiaStaleException("gone");
        int x=(r.Left+r.Right)/2,y=(r.Top+r.Bottom)/2;
        if(Root(Native.WindowFromPoint(new Native.Point{X=x,Y=y}))!=hwnd)throw new UiaStaleException("covered");
        Native.SetCursorPos(x,y);
        var wheel=new Native.Input{type=0,u=new(){mouse=new(){flags=0x800,data=(uint)(-Math.Clamp(dy,-1200,1200))}}};
        Native.Send(wheel);
        return "gescrollt";
    }
}
