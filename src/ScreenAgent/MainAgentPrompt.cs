using System.Diagnostics;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;
namespace ScreenAgent;
internal sealed record PromptResult(string Code,bool? Landed,int Chars,string Route,int Windows,bool Wsl,long Ms,IntPtr Window);
// Types a prompt into the Claude Code message box of VS Code Insiders. Hard rules: the text goes into that box and nowhere else, is never submitted (the only
// unshifted Enter is the one that runs the palette command, checked immediately before), and never touches the clipboard. Whatever cannot be proven before
// the first character is typed ends in "type nothing, fail". Runs on a worker thread: UIA searches on Electron can be slow.
internal sealed class MainAgentPrompt
{
    const ushort Ctrl=0x11,Shift=0x10,Alt=0x12,Return=0x0d,Esc=0x1b,KeyEnd=0x23,P=0x50,Neutral=0xe8;
    public const int SearchBudgetMs=2500,PaletteWaitMs=3000;
    // Selftest hooks. KeysSent counts every typing or command key event this class sends, not the Alt tap that brings the window forward (a prompt that fails closed must leave it unchanged).
    public static volatile bool PaletteDisabledForTests;
    public static long KeysSent;
    public static Action<string>? Debug;
    readonly string[] names;
    readonly Action<string,Dictionary<string,object?>> log;
    public MainAgentPrompt(string[] names,Action<string,Dictionary<string,object?>> log){this.names=names;this.log=log;}
    internal sealed record FocusInfo(AutomationElement Element,int Pid,string Name,List<string> Classes);
    public Task<PromptResult> Run(string raw,CancellationToken ct)=>Task.Run(()=>RunCore(raw,ct));
    PromptResult RunCore(string raw,CancellationToken ct)
    {
        var clock=Stopwatch.StartNew();string route="none";int windows=0,typed=0;bool wsl=false;IntPtr hwnd=IntPtr.Zero;
        PromptResult Finish(string code,bool? landed=null)
        {
            var r=new PromptResult(code,landed,typed,route,windows,wsl,clock.ElapsedMilliseconds,hwnd);
            log("main.prompt.done",new(){["code"]=code,["landed"]=landed,["chars"]=typed,["route"]=route,["windows"]=windows,["wsl"]=wsl,["ms"]=r.Ms});return r;
        }
        string text=PromptText.Normalize(raw);
        log("main.prompt.start",new(){["chars"]=text.Length});
        if(text.Length==0)return Finish(PromptCodes.Interrupted);
        try
        {
            var found=CandidateOrder.Pick(Enumerate());windows=found.Count;
            if(found.Count==0)return Finish(PromptCodes.NoWindow);
            // b. Which window has an open Claude session: the first one with a message box. None: the first candidate and the palette.
            var chosen=found[0];AutomationElement? input=null;
            foreach(var c in found)
            {
                ct.ThrowIfCancellationRequested();
                if(FindInput((IntPtr)c.Handle,SearchBudgetMs) is {} hit){chosen=c;input=hit;break;}
            }
            hwnd=(IntPtr)chosen.Handle;wsl=CandidateOrder.Wsl(chosen);
            Native.GetWindowThreadProcessId(hwnd,out uint pidRaw);int pid=(int)pidRaw;
            Debug?.Invoke($"candidates={found.Count} chosen={chosen.Handle:x} minimized={chosen.Minimized} elementFound={input is not null}");
            // c. In front.
            if(!WaitModifiersUp(2000))return Finish(PromptCodes.Busy);
            if(!Front(hwnd))return Finish(PromptCodes.NoForeground);
            // d. Focus: the element when there is one, the palette otherwise or when the element did not take the focus.
            route="element";
            if(input is null&&FindInput(hwnd,1500) is {} late)input=late;   // a minimized window shows nothing before it is restored
            bool ok=false;
            if(input is not null)
            {
                try{input.SetFocus();}catch(Exception ex){Debug?.Invoke("SetFocus failed: "+ex.GetType().Name);}
                ok=Verified(pid,1000,out _);
            }
            if(!ok)
            {
                route="palette";
                if(!Palette(hwnd,pid,ct,out string? stop)){return Finish(stop!);}
                ok=Verified(pid,PaletteWaitMs,out string why);
                if(!ok)
                {
                    Debug?.Invoke("verify after palette: "+why);
                    if(InQuickInput(pid))Tap(Esc);
                    return Finish(PromptCodes.WrongFocus);
                }
            }
            log("main.prompt.route",new(){["route"]=route,["windows"]=windows,["wsl"]=wsl});
            // e/f. Verified. Type.
            var box=Focus()!.Element;
            string existing="";bool readable=true;
            try{var now=ReadDraft(box);existing=now??"";readable=now is not null;Debug?.Invoke($"draft: readable={readable} length={existing.Length} parts={Describe(box)}");}catch(Exception ex){readable=false;Debug?.Invoke("draft unreadable: "+ex.GetType().Name);}
            if(existing.Trim().Length>0||!readable)
            {
                Chord(Ctrl,KeyEnd);   // never clear a draft: continue after it
                if(existing.Trim().Length>0)Type(" ",hwnd,pid,ct,false,ref typed,out _);
                typed=0;
            }
            if(!Type(text,hwnd,pid,ct,true,ref typed,out string? failed))return Finish(failed!);
            // g. Read back. The accessibility tree of a window in the background can lag behind the typing by a moment: look again for up to 2 s.
            string? read=null;bool landed=false;
            for(int i=0;i<20&&!landed;i++)
            {
                Thread.Sleep(100);
                read=ReadNow(box,pid);
                if(read is not null)landed=PromptText.Landed(read,text);
            }
            Debug?.Invoke($"read back: readable={read is not null} length={read?.Length} landed={landed}");
            if(read is null)return Finish(PromptCodes.TypedUnverified);
            if(!landed)return Finish(PromptCodes.WrongFocus,false);
            Thread.Sleep(500);   // still there half a second later: nothing took it away (it was not sent)
            read=ReadNow(box,pid);
            if(read is null||!PromptText.Landed(read,text)){Debug?.Invoke("text gone after 500 ms");return Finish(PromptCodes.WrongFocus,false);}
            return Finish(PromptCodes.Typed,true);
        }
        catch(OperationCanceledException){return Finish(PromptCodes.Interrupted);}
        catch(Exception ex){Debug?.Invoke("unexpected "+ex);return Finish(PromptCodes.Interrupted);}
    }
    // ---- windows
    internal static List<CodeWindow> Enumerate()
    {
        var list=new List<CodeWindow>();var cache=new Dictionary<uint,string>();var sb=new StringBuilder(512);
        Native.EnumWindows((h,_)=>
        {
            if(!Native.IsWindowVisible(h)||Native.GetWindow(h,4)!=IntPtr.Zero)return true;   // visible or minimized, top level, not owned
            sb.Clear();Native.GetClassName(h,sb,512);string cls=sb.ToString();
            if(cls!=CandidateOrder.WindowClass)return true;
            sb.Clear();Native.GetWindowText(h,sb,512);string title=sb.ToString();
            if(title.Length==0)return true;
            Native.GetWindowThreadProcessId(h,out uint pid);
            if(!cache.TryGetValue(pid,out var name)){try{using var p=Process.GetProcessById((int)pid);name=p.ProcessName;}catch{name="";}cache[pid]=name;}
            list.Add(new((long)h,name,cls,title,Native.IsIconic(h)));
            return true;
        },IntPtr.Zero);
        return list;
    }
    internal AutomationElement? FindInput(IntPtr hwnd,int budgetMs)
    {
        var until=Environment.TickCount64+budgetMs;
        Condition cond=names.Length==1?new PropertyCondition(AutomationElement.NameProperty,names[0]):new OrCondition(names.Select(n=>(Condition)new PropertyCondition(AutomationElement.NameProperty,n)).ToArray());
        AutomationElement root;
        try{root=AutomationElement.FromHandle(hwnd);}catch{return null;}
        while(true)
        {
            long left=until-Environment.TickCount64;if(left<=0)return null;
            var search=Task.Run(()=>{try{return root.FindFirst(TreeScope.Descendants,cond);}catch{return null;}});
            if(!search.Wait((int)left))return null;   // the search itself ran over the budget; abandon it
            if(search.Result is {} hit)return hit;
            Thread.Sleep(250);
        }
    }
    static bool WaitModifiersUp(int ms)
    {
        for(long until=Environment.TickCount64+ms;;Thread.Sleep(10)){if(Native.ModifiersUp)return true;if(Environment.TickCount64>=until)return false;}
    }
    static bool Stable(IntPtr h,int overallMs)
    {
        long until=Environment.TickCount64+overallMs,since=0;
        while(Environment.TickCount64<until)
        {
            if(Native.GetForegroundWindow()==h){if(since==0)since=Environment.TickCount64;if(Environment.TickCount64-since>=300)return true;}else since=0;
            Thread.Sleep(20);
        }
        return false;
    }
    // Restore if minimized, then three ways in order. No visible focus-stealing hack beyond these.
    internal static bool Front(IntPtr h)
    {
        if(Native.IsIconic(h)){Native.ShowWindow(h,9);Thread.Sleep(200);}
        if(Native.GetForegroundWindow()==h&&Stable(h,600))return true;
        try{AutomationElement.FromHandle(h).SetFocus();}catch{ /* not every window takes it */ }
        if(Stable(h,900))return true;
        // Alt tap: Windows lets the process that just received input take the foreground. The neutral key between Alt down and up keeps the menu bar closed.
        SendFrontKeys(Key(Alt,false));
        Native.SetForegroundWindow(h);
        SendFrontKeys(Key(Neutral,false),Key(Neutral,true),Key(Alt,true));
        if(Stable(h,900))return true;
        uint self=Native.GetCurrentThreadId();
        uint fgThread=Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out _);
        bool attached=fgThread!=0&&fgThread!=self&&Native.AttachThreadInput(self,fgThread,true);
        try{Native.SetForegroundWindow(h);}finally{if(attached)Native.AttachThreadInput(self,fgThread,false);}
        return Stable(h,900);
    }
    // ---- focus
    internal static FocusInfo? Focus()
    {
        try
        {
            var e=AutomationElement.FocusedElement;if(e is null)return null;
            var cur=e.Current;var classes=new List<string>();var walker=TreeWalker.RawViewWalker;
            var p=walker.GetParent(e);
            for(int i=0;p is not null&&i<80;i++){classes.Add(p.Current.ClassName??"");p=walker.GetParent(p);}
            return new(e,cur.ProcessId,cur.Name??"",classes);
        }
        catch{return null;}
    }
    bool Verified(int pid,int waitMs,out string why)
    {
        why="no focused element";
        for(long until=Environment.TickCount64+waitMs;;Thread.Sleep(100))
        {
            var f=Focus();
            if(f is null)why="no focused element";
            else if(f.Pid!=pid)why="focus in another process";
            else if(!FocusRules.MayType(f.Name,names,f.Classes)){why=$"name '{f.Name}' classes [{string.Join(" | ",f.Classes.Take(12))}]";}
            else{why="ok";return true;}
            if(Environment.TickCount64>=until)break;
        }
        Debug?.Invoke("not verified: "+why);return false;
    }
    static bool InQuickInput(int pid)=>Focus() is {} f&&f.Pid==pid&&FocusRules.InQuickInput(f.Classes);
    bool Palette(IntPtr hwnd,int pid,CancellationToken ct,out string? stop)
    {
        stop=null;
        if(PaletteDisabledForTests){stop=PromptCodes.NoField;return false;}
        if(!WaitModifiersUp(1000)){stop=PromptCodes.Busy;return false;}
        Chord(Ctrl,Shift,P);
        bool open=false;
        for(long until=Environment.TickCount64+2000;Environment.TickCount64<until&&!open;Thread.Sleep(100))open=InQuickInput(pid);
        if(!open){Tap(Esc);stop=PromptCodes.NoField;return false;}
        int n=0;
        if(!Type("Claude Code: Focus input",hwnd,pid,ct,false,ref n,out string? failed)){Tap(Esc);stop=failed;return false;}
        Thread.Sleep(400);
        // The only unshifted Enter of the whole feature: while the palette has the focus, checked right before.
        if(!InQuickInput(pid)||Native.GetForegroundWindow()!=hwnd){Tap(Esc);stop=PromptCodes.NoField;return false;}
        Tap(Return);
        return true;
    }
    // ---- keys
    static Native.Input Key(ushort vk,bool up)=>Native.Key(vk,up,0,false,Native.Marker);
    static void SendKeys(params Native.Input[] keys){Interlocked.Add(ref KeysSent,keys.Length);Native.Send(keys);}
    static void SendFrontKeys(params Native.Input[] keys)=>Native.Send(keys);   // Alt tap for the foreground: not typing, not counted
    static void Tap(ushort vk)=>SendKeys(Key(vk,false),Key(vk,true));
    // One atomic batch: modifiers down in order, last key down and up, modifiers up in reverse.
    static void Chord(params ushort[] vks)
    {
        var list=new List<Native.Input>();
        foreach(var v in vks)list.Add(Key(v,false));
        for(int i=vks.Length-1;i>=0;i--)list.Add(Key(vks[i],true));
        SendKeys(list.ToArray());
    }
    // VK_PACKET batches. Before each batch: the target is still in front and no physical modifier is held (it would turn typed characters into shortcuts); every 300 characters the box is looked at again.
    bool Type(string text,IntPtr hwnd,int pid,CancellationToken ct,bool recheck,ref int typed,out string? failed)
    {
        failed=null;int sinceCheck=0;var rnd=new Random();
        foreach(var step in PromptText.Steps(text))
        {
            if(ct.IsCancellationRequested||Native.GetForegroundWindow()!=hwnd){failed=PromptCodes.Interrupted;return false;}
            if(!WaitModifiersUp(1000)){failed=PromptCodes.Interrupted;return false;}
            if(recheck&&sinceCheck>=300)
            {
                sinceCheck=0;var f=Focus();
                if(f is null||f.Pid!=pid||!FocusRules.MayType(f.Name,names,f.Classes)){failed=PromptCodes.Interrupted;return false;}
            }
            try
            {
                if(step.Kind==StepKind.Newline)Chord(Shift,Return);
                else
                {
                    var keys=new Native.Input[step.Chars.Length*2];
                    for(int i=0;i<step.Chars.Length;i++){keys[2*i]=Native.Key(0,false,step.Chars[i],true,Native.Marker);keys[2*i+1]=Native.Key(0,true,step.Chars[i],true,Native.Marker);}
                    SendKeys(keys);
                }
            }
            catch(System.ComponentModel.Win32Exception){failed=PromptCodes.Interrupted;return false;}
            typed+=step.Chars.Length;sinceCheck+=step.Chars.Length;
            Thread.Sleep(rnd.Next(6,11));
        }
        return true;
    }
    // The message box as it is now: the focused element when it is the box, else the one found before.
    string? ReadNow(AutomationElement fallback,int pid)
    {
        try
        {
            var f=Focus();
            if(f is not null&&f.Pid==pid&&FocusRules.MayType(f.Name,names,f.Classes)&&ReadBox(f.Element) is {} fresh)return fresh;
            return ReadBox(fallback);
        }
        catch{return null;}
    }
    // The user's own text in the box. An empty box reports its placeholder ("Ask Claude to edit…") as value AND as text, so the patterns alone cannot tell
    // "empty" from "a draft". What the tree shows (measured on the real extension): the placeholder is the only child of the box, wrapped in a Group that is not
    // a content element, while typed text is a direct Text child. Anything that does not look exactly like that placeholder counts as a draft.
    internal static string? ReadDraft(AutomationElement el)
    {
        string? raw=null;
        if(el.TryGetCurrentPattern(TextPattern.Pattern,out var t))raw=((TextPattern)t).DocumentRange.GetText(-1)??"";
        else if(el.TryGetCurrentPattern(ValuePattern.Pattern,out var v))raw=((ValuePattern)v).Current.Value??"";
        if(raw is null)return null;
        return raw.Length>0&&PlaceholderOnly(el)?"":raw;
    }
    static bool PlaceholderOnly(AutomationElement el)
    {
        try
        {
            var walker=TreeWalker.RawViewWalker;
            var only=walker.GetFirstChild(el);
            if(only is null||walker.GetNextSibling(only) is not null)return false;
            var c=only.Current;
            if(c.ControlType!=ControlType.Group||c.IsContentElement)return false;
            var inner=walker.GetFirstChild(only);
            return inner is not null&&inner.Current.ControlType==ControlType.Text&&walker.GetNextSibling(inner) is null;
        }
        catch{return false;}
    }
    // Selftest diagnostics: what each pattern says (lengths and a short head, never used for decisions).
    internal static string Describe(AutomationElement el)
    {
        string Head(string? x)=>x is null?"null":$"{x.Length}:'{(x.Length>30?x[..30]:x).Replace("\n","\\n")}'";
        try
        {
            string? value=el.TryGetCurrentPattern(ValuePattern.Pattern,out var v)?((ValuePattern)v).Current.Value:null;
            string? text=el.TryGetCurrentPattern(TextPattern.Pattern,out var t)?((TextPattern)t).DocumentRange.GetText(-1):null;
            return $"value={Head(value)} text={Head(text)} help={Head(el.Current.HelpText)}";
        }
        catch(Exception ex){return "describe failed: "+ex.GetType().Name;}
    }
    // Selftest diagnostics: the element, its children, ARIA properties and the text before the caret, to find out how an empty box differs from one with text.
    internal static string DescribeDeep(AutomationElement el)
    {
        var sb=new StringBuilder();
        void One(AutomationElement e,int depth)
        {
            try
            {
                var c=e.Current;string name=c.Name??"";if(name.Length>30)name=name[..30];
                string aria="";
                sb.Append(new string(' ',depth*2)).Append(c.ControlType.ProgrammaticName.Replace("ControlType.","")).Append(" name='").Append(name).Append("' class='").Append(c.ClassName).Append("' content=").Append(c.IsContentElement).Append(" offscreen=").Append(c.IsOffscreen).Append(" rect=").Append(c.BoundingRectangle).Append(" aria='").Append(aria).Append("' status='").Append(c.ItemStatus).Append("'\n");
                if(depth<3)for(var ch=TreeWalker.RawViewWalker.GetFirstChild(e);ch is not null;ch=TreeWalker.RawViewWalker.GetNextSibling(ch))One(ch,depth+1);
            }
            catch(Exception ex){sb.Append("  (").Append(ex.GetType().Name).Append(")\n");}
        }
        One(el,0);
        try
        {
            if(el.TryGetCurrentPattern(TextPattern.Pattern,out var t))
            {
                var tp=(TextPattern)t;var sel=tp.GetSelection();
                sb.Append("selections=").Append(sel.Length);
                if(sel.Length>0)
                {
                    var before=tp.DocumentRange.Clone();before.MoveEndpointByRange(TextPatternRangeEndpoint.End,sel[0],TextPatternRangeEndpoint.Start);
                    var after=tp.DocumentRange.Clone();after.MoveEndpointByRange(TextPatternRangeEndpoint.Start,sel[0],TextPatternRangeEndpoint.End);
                    sb.Append(" beforeCaret='").Append(before.GetText(40)).Append("' afterCaret='").Append(after.GetText(40)).Append('\'');
                }
                sb.Append('\n');
            }
        }
        catch(Exception ex){sb.Append("caret probe failed: ").Append(ex.GetType().Name).Append('\n');}
        return sb.ToString();
    }
    // What the box shows now: the longer of ValuePattern and TextPattern; null when the element offers neither.
    internal static string? ReadBox(AutomationElement el)
    {
        string? best=null;
        if(el.TryGetCurrentPattern(ValuePattern.Pattern,out var v))best=((ValuePattern)v).Current.Value??"";
        if(el.TryGetCurrentPattern(TextPattern.Pattern,out var t))
        {
            string s=((TextPattern)t).DocumentRange.GetText(-1)??"";
            if(best is null||s.Length>best.Length)best=s;
        }
        return best;
    }
}
