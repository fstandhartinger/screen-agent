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
    // Selftest hook: what is typed into the palette instead of the real label (the proof against the highlighted entry still uses the real one).
    public static volatile string? PaletteLabelForTests;
    public static long KeysSent,PaletteEnters;
    public static Action<string>? Debug;
    readonly string[] names;
    readonly Action<string,Dictionary<string,object?>> log;
    readonly bool allowStable;
    public MainAgentPrompt(string[] names,Action<string,Dictionary<string,object?>> log,bool allowStable=false){this.names=names;this.log=log;this.allowStable=allowStable;}
    internal sealed record FocusInfo(AutomationElement Element,int Pid,string Name,List<string> Classes);
    public Task<PromptResult> Run(string raw,CancellationToken ct)=>Run(raw,ct,PromptDeadline.FromNow());
    public Task<PromptResult> Run(string raw,CancellationToken ct,PromptDeadline deadline)=>Task.Run(()=>RunCore(raw,ct,deadline));
    static long Now=>Environment.TickCount64;
    PromptResult RunCore(string raw,CancellationToken ct,PromptDeadline deadline)
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
            ct.ThrowIfCancellationRequested();
            var found=CandidateOrder.Pick(Enumerate(),allowStable);windows=found.Count;
            if(found.Count==0)return Finish(PromptCodes.NoWindow);
            // b. Which window has an open Claude session: the first one with a message box. None: the first candidate and the palette.
            var chosen=found[0];AutomationElement? input=null;
            foreach(var c in found)
            {
                ct.ThrowIfCancellationRequested();
                if(FindInput((IntPtr)c.Handle,SearchBudgetMs,ct) is {} hit){chosen=c;input=hit;break;}
            }
            hwnd=(IntPtr)chosen.Handle;wsl=CandidateOrder.Wsl(chosen);
            log("main.prompt.step",new(){["step"]="window",["windows"]=windows,["ms"]=clock.ElapsedMilliseconds});   // the live activity overlay shows these steps
            Native.GetWindowThreadProcessId(hwnd,out uint pidRaw);int pid=(int)pidRaw;
            Debug?.Invoke($"candidates={found.Count} chosen={chosen.Handle:x} minimized={chosen.Minimized} elementFound={input is not null}");
            // c. In front.
            if(!WaitModifiersUp(2000,ct))return Finish(PromptCodes.Busy);
            if(!Front(hwnd,ct))return Finish(PromptCodes.NoForeground);
            log("main.prompt.step",new(){["step"]="focus",["ms"]=clock.ElapsedMilliseconds});
            // d. Focus: the element when there is one, the palette otherwise or when the element did not take the focus.
            route="element";
            if(input is null&&FindInput(hwnd,1500,ct) is {} late)input=late;   // a minimized window shows nothing before it is restored
            bool ok=false;
            if(input is not null)
            {
                try{input.SetFocus();}catch(Exception ex){Debug?.Invoke("SetFocus failed: "+ex.GetType().Name);Diag.Fail("prompt-setfocus",ex);}
                ok=Verified(pid,1000,ct,out _);
            }
            if(!ok)
            {
                route="palette";
                if(!Palette(hwnd,pid,ct,deadline,out string? stop)){return Finish(stop!);}
                ok=Verified(pid,PaletteWaitMs,ct,out string why);
                if(!ok)
                {
                    Debug?.Invoke("verify after palette: "+why);
                    EscIfPalette(pid,hwnd);
                    return Finish(PromptCodes.WrongFocus);
                }
            }
            log("main.prompt.route",new(){["route"]=route,["windows"]=windows,["wsl"]=wsl});
            // e/f. Verified. Type.
            if(Focus() is not {} verified)return Finish(PromptCodes.NoField);
            var box=verified.Element;
            // The draft is read off to the side with a time budget: Electron can take seconds, and a draft we cannot read costs only a separator space.
            var draft=ReadDraftWithin(box,1000,ct);
            string existing=draft??"";bool readable=draft is not null;
            Debug?.Invoke($"draft: readable={readable} length={existing.Length}");
            ct.ThrowIfCancellationRequested();
            if(!deadline.MayStartTyping(Now)){Debug?.Invoke("less than 8 s left");return Finish(PromptCodes.Interrupted);}
            // Re-verified right before the first key: the focus may have moved while the draft was being read.
            if(!StillTheBox(box,pid,true)){Debug?.Invoke("focus moved before the first key");return Finish(PromptCodes.WrongFocus);}
            log("main.prompt.step",new(){["step"]="typing",["chars"]=text.Length,["ms"]=clock.ElapsedMilliseconds});
            Chord(Ctrl,KeyEnd);   // the caret goes behind whatever is there: a draft is never overwritten or cut
            if(!readable||existing.Trim().Length>0){if(!Type(" ",hwnd,pid,ct,box,false,ref typed,out string? sepFailed))return Finish(sepFailed!);typed=0;}   // an unreadable draft counts as a draft: the separator is harmless
            if(!Type(text,hwnd,pid,ct,box,false,ref typed,out string? failed))return Finish(failed!);
            // g. Read back. The accessibility tree of a window in the background can lag behind the typing by a moment: look again for up to 2 s.
            string? read=null;bool landed=false;
            for(int i=0;i<20&&!landed;i++)
            {
                ct.ThrowIfCancellationRequested();
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
        catch(OperationCanceledException){return Finish(PromptCodes.Interrupted);}   /* cancelled: Finish logs main.prompt.done with the code */
        catch(Exception ex){Debug?.Invoke("unexpected "+ex);Diag.Fail("prompt-unexpected",ex,new(){["ms"]=clock.ElapsedMilliseconds,["route"]=route,["typed"]=typed});return Finish(PromptCodes.Interrupted);}
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
            if(!cache.TryGetValue(pid,out var name)){try{using var p=Process.GetProcessById((int)pid);name=p.ProcessName;}catch(Exception){name="";}   /* the process is gone: no name */cache[pid]=name;}
            bool cloaked=Native.DwmGetWindowAttribute(h,14,out int cl,4)==0&&cl!=0;   // another virtual desktop
            list.Add(new((long)h,name,cls,title,Native.IsIconic(h),cloaked));
            return true;
        },IntPtr.Zero);
        return list;
    }
    internal AutomationElement? FindInput(IntPtr hwnd,int budgetMs,CancellationToken ct=default)
    {
        var until=Environment.TickCount64+budgetMs;
        Condition cond=names.Length==1?new PropertyCondition(AutomationElement.NameProperty,names[0]):new OrCondition(names.Select(n=>(Condition)new PropertyCondition(AutomationElement.NameProperty,n)).ToArray());
        AutomationElement root;
        try{root=AutomationElement.FromHandle(hwnd);}catch(Exception ex){Diag.Fail("prompt-uia-root",ex);return null;}
        while(true)
        {
            ct.ThrowIfCancellationRequested();
            long left=until-Environment.TickCount64;if(left<=0)return null;
            var search=Task.Run(()=>{try{return root.FindFirst(TreeScope.Descendants,cond);}catch(Exception ex){Diag.Fail("prompt-uia-find",ex);return null;}});
            if(!search.Wait((int)left,ct))return null;   // the search itself ran over the budget; abandon it
            if(search.Result is {} hit)return hit;
            ct.WaitHandle.WaitOne(250);
        }
    }
    static bool WaitModifiersUp(int ms,CancellationToken ct=default)
    {
        for(long until=Environment.TickCount64+ms;;Thread.Sleep(10)){ct.ThrowIfCancellationRequested();if(Native.ModifiersUp)return true;if(Environment.TickCount64>=until)return false;}
    }
    static bool Stable(IntPtr h,int overallMs,CancellationToken ct=default)
    {
        long until=Environment.TickCount64+overallMs,since=0;
        while(Environment.TickCount64<until)
        {
            ct.ThrowIfCancellationRequested();
            if(Native.GetForegroundWindow()==h){if(since==0)since=Environment.TickCount64;if(Environment.TickCount64-since>=300)return true;}else since=0;
            Thread.Sleep(20);
        }
        return false;
    }
    // Restore if minimized, then three ways in order. No visible focus-stealing hack beyond these.
    internal static bool Front(IntPtr h,CancellationToken ct=default)
    {
        if(Native.IsIconic(h)){Native.ShowWindow(h,9);Thread.Sleep(200);}
        if(Native.GetForegroundWindow()==h&&Stable(h,600,ct))return true;
        try{AutomationElement.FromHandle(h).SetFocus();}catch(Exception ex){Diag.Fail("prompt-front-focus",ex);}   /* not every window takes it */
        if(Stable(h,900,ct))return true;
        // Alt tap: Windows lets the process that just received input take the foreground. The neutral key between Alt down and up keeps the menu bar closed.
        // Alt is never left down: whatever throws in between, the finally sends its key-up.
        bool altDown=false;
        try
        {
            altDown=true;SendFrontKeys(Key(Alt,false));
            Native.SetForegroundWindow(h);
            SendFrontKeys(Key(Neutral,false),Key(Neutral,true),Key(Alt,true));altDown=false;
        }
        finally{if(altDown)ReleaseModifiers(Alt);}
        if(Stable(h,900,ct))return true;
        uint self=Native.GetCurrentThreadId();
        uint fgThread=Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out _);
        bool attached=fgThread!=0&&fgThread!=self&&Native.AttachThreadInput(self,fgThread,true);
        try{Native.SetForegroundWindow(h);}finally{if(attached)Native.AttachThreadInput(self,fgThread,false);}
        return Stable(h,900,ct);
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
        catch(Exception ex){Diag.Fail("prompt-focus-info",ex);return null;}
    }
    bool Verified(int pid,int waitMs,CancellationToken ct,out string why)
    {
        why="no focused element";
        for(long until=Environment.TickCount64+waitMs;;Thread.Sleep(100))
        {
            ct.ThrowIfCancellationRequested();
            var f=Focus();
            if(f is null)why="no focused element";
            else if(f.Pid!=pid)why="focus in another process";
            else if(!FocusRules.MayType(f.Name,names,f.Classes)){why=$"name '{f.Name}' classes [{string.Join(" | ",f.Classes.Take(12))}]";}
            else{why="ok";return true;}
            if(Environment.TickCount64>=until)break;
        }
        Debug?.Invoke("not verified: "+why);return false;
    }
    // The strong check: the focused element is named like the message box, sits in no editor or terminal, and is the box we hold.
    bool StillTheBox(AutomationElement box,int pid,bool ancestors)
    {
        try
        {
            if(ancestors)
            {
                var f=Focus();
                if(f is null||f.Pid!=pid||!FocusRules.MayType(f.Name,names,f.Classes))return false;
            }
            return box.Current.HasKeyboardFocus;   // one cross-process call: the element we verified still has the keyboard focus
        }
        catch(Exception ex){Diag.Fail("prompt-focus-verify",ex);return false;}
    }
    static bool InQuickInput(int pid)=>Focus() is {} f&&f.Pid==pid&&FocusRules.InQuickInput(f.Classes);
    // Esc goes out only while the palette (quick input) of that window has the focus right now; anywhere else it could cancel something (a terminal's running task).
    static void EscIfPalette(int pid,IntPtr hwnd){if(Native.GetForegroundWindow()==hwnd&&InQuickInput(pid))Tap(Esc);}
    bool Palette(IntPtr hwnd,int pid,CancellationToken ct,PromptDeadline deadline,out string? stop)
    {
        stop=null;
        if(PaletteDisabledForTests){stop=PromptCodes.NoField;return false;}
        if(!deadline.MayStartTyping(Now)){stop=PromptCodes.Interrupted;return false;}
        if(!WaitModifiersUp(1000,ct)){stop=PromptCodes.Busy;return false;}
        Chord(Ctrl,Shift,P);
        bool open=false;
        for(long until=Environment.TickCount64+2000;Environment.TickCount64<until&&!open;Thread.Sleep(100)){ct.ThrowIfCancellationRequested();open=InQuickInput(pid);}
        if(!open){stop=PromptCodes.NoField;return false;}   // nothing sent: Esc here could land in a terminal
        PaletteProbe("opened",pid,hwnd);
        int n=0;
        if(!Type(PaletteLabelForTests??PaletteRules.Label,hwnd,pid,ct,null,true,ref n,out string? failed)){EscIfPalette(pid,hwnd);stop=failed;return false;}
        PaletteProbe("typed",pid,hwnd);
        Thread.Sleep(400);
        // Proof of the highlighted entry: the palette runs whatever is on top, so Enter goes out only when UIA shows "Claude Code: Focus input" selected.
        string seen="";bool match=false;
        for(long until=Environment.TickCount64+1500;;Thread.Sleep(100))
        {
            ct.ThrowIfCancellationRequested();
            var f=Focus();
            if(f is null||f.Pid!=pid||!FocusRules.InQuickInput(f.Classes)||Native.GetForegroundWindow()!=hwnd)break;
            seen=HighlightedEntry(f.Element);
            PaletteProbe("selection",pid,hwnd);
            if(PaletteRules.IsFocusInput(seen)){match=true;break;}
            if(Environment.TickCount64>=until)break;
        }
        Debug?.Invoke($"palette highlighted: match={match} entry='{seen}'");
        if(!match){EscIfPalette(pid,hwnd);stop=PromptCodes.NoField;return false;}
        // The only unshifted Enter of the whole feature: while the palette has the focus and shows the right entry, checked right before.
        if(!InQuickInput(pid)||Native.GetForegroundWindow()!=hwnd){EscIfPalette(pid,hwnd);stop=PromptCodes.NoField;return false;}
        Interlocked.Increment(ref PaletteEnters);
        Tap(Return);
        return true;
    }
    void PaletteProbe(string stage,int pid,IntPtr hwnd)
    {
        var f=Focus();string? query=null;
        try{if(f?.Pid==pid&&FocusRules.InQuickInput(f.Classes)&&f.Element.TryGetCurrentPattern(ValuePattern.Pattern,out var v))query=((ValuePattern)v).Current.Value;}catch(Exception ex){Diag.Fail("prompt-palette-query",ex);}
        bool queryMatch=query==">"+(PaletteLabelForTests??PaletteRules.Label);
        log("main.prompt.palette",new(){["stage"]=stage,["foreground"]=Native.GetForegroundWindow()==hwnd,["quickInput"]=f?.Pid==pid&&FocusRules.InQuickInput(f.Classes),["queryChars"]=query?.Length,["queryMatch"]=queryMatch,["selectedMatch"]=f?.Pid==pid&&PaletteRules.IsFocusInput(HighlightedEntry(f.Element))});
        Debug?.Invoke($"palette {stage}: query='{query}' focused='{f?.Name}' type='{f?.Element.Current.ControlType.ProgrammaticName}' selected='{(f is null?"":HighlightedEntry(f.Element))}'");
    }
    // Name of the selected row of the quick input list that contains `focused` ("" when no row is selected, "?" + names when several are). A row's text may sit in its children.
    internal static string HighlightedEntry(AutomationElement focused)
    {
        try
        {
            var walker=TreeWalker.RawViewWalker;AutomationElement? widget=null;
            var p=focused;
            for(int i=0;p is not null&&i<80;i++,p=walker.GetParent(p))if((p.Current.ClassName??"").Contains("quick-input-widget",StringComparison.OrdinalIgnoreCase)){widget=p;break;}
            if(widget is null)return "";
            var rows=widget.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.IsSelectionItemPatternAvailableProperty,true));
            var picked=new List<string>();
            foreach(AutomationElement row in rows)
            {
                bool selected=row.TryGetCurrentPattern(SelectionItemPattern.Pattern,out var sp)&&((SelectionItemPattern)sp).Current.IsSelected;
                if(!selected&&!row.Current.HasKeyboardFocus)continue;
                string name=row.Current.Name??"";
                if(name.Length==0)
                {
                    var parts=row.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text));
                    name=string.Join(" ",parts.Cast<AutomationElement>().Select(t=>t.Current.Name));
                }
                picked.Add(name);
            }
            return picked.Count==1?picked[0]:picked.Count==0?"":"?"+string.Join(" | ",picked);
        }
        catch(Exception ex){Diag.Fail("prompt-read-picked",ex);return "";}
    }
    // ---- keys
    static Native.Input Key(ushort vk,bool up)=>Native.Key(vk,up,0,false,Native.Marker);
    static void SendKeys(params Native.Input[] keys){Interlocked.Add(ref KeysSent,keys.Length);Native.Send(keys);}
    static void SendFrontKeys(params Native.Input[] keys)=>Native.Send(keys);   // Alt tap for the foreground: not typing, not counted
    static void Tap(ushort vk)=>SendKeys(Key(vk,false),Key(vk,true));
    // Key-up for every modifier among vks (Ctrl, Shift, Alt), best effort: an extra key-up of a key that is up changes nothing.
    static void ReleaseModifiers(params ushort[] vks)
    {
        foreach(var v in vks)if(v is Ctrl or Shift or Alt)try{Native.Send(Key(v,true));}catch(Exception ex){Diag.Fail("prompt-release-modifier",ex);}   /* nothing more we can do */
    }
    // One atomic batch: modifiers down in order, last key down and up, modifiers up in reverse. If SendInput fails part way, the modifiers are released.
    static void Chord(params ushort[] vks)
    {
        var list=new List<Native.Input>();
        foreach(var v in vks)list.Add(Key(v,false));
        for(int i=vks.Length-1;i>=0;i--)list.Add(Key(vks[i],true));
        try{SendKeys(list.ToArray());}
        catch{ReleaseModifiers(vks);throw;}   /* rethrown: the caller logs it */
    }
    // VK_PACKET batches. Before EVERY batch: the target is still in front, no physical modifier is held (it would turn typed characters into shortcuts) and the box we verified still has the
    // keyboard focus (one call). The first batch and every 300 characters repeat the full check with the ancestor walk. `palette`: typing into the quick input, which must have the focus.
    // The window between the check and the keys of one batch (24 characters at most) is the remaining gap.
    bool Type(string text,IntPtr hwnd,int pid,CancellationToken ct,AutomationElement? box,bool palette,ref int typed,out string? failed)
    {
        failed=null;int sinceCheck=0;bool first=true;var rnd=new Random();
        foreach(var step in PromptText.Steps(text))
        {
            if(ct.IsCancellationRequested||Native.GetForegroundWindow()!=hwnd){failed=PromptCodes.Interrupted;return false;}
            if(!WaitModifiersUp(1000)){failed=PromptCodes.Interrupted;return false;}
            bool fine=true;
            if(palette)fine=InQuickInput(pid);
            else if(box is not null)
            {
                fine=StillTheBox(box,pid,first||sinceCheck>=300);
                if(first||sinceCheck>=300)sinceCheck=0;
            }
            if(!fine){Debug?.Invoke("focus left the target before a batch");failed=PromptCodes.Interrupted;return false;}
            first=false;
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
            catch(System.ComponentModel.Win32Exception ex){Diag.Fail("prompt-sendinput",ex,new(){["typed"]=typed,["err"]=ex.NativeErrorCode});failed=PromptCodes.Interrupted;return false;}
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
        catch(Exception ex){Diag.Fail("prompt-readbox",ex);return null;}
    }
    // The user's own text in the box. An empty box reports its placeholder ("Ask Claude to edit…") as value AND as text, so the patterns alone cannot tell
    // "empty" from "a draft". Measured on the real extension: its Send button is disabled exactly while nothing sendable is in the box, and the placeholder is
    // the only child of the box inside a Group that is not a content element (typed text is a direct Text child; this structure was seen to be late right
    // after the panel was created, hence the button first). Anything not recognised as empty counts as a draft.
    internal static string? ReadDraft(AutomationElement el,CancellationToken ct=default)
    {
        string? raw=null;
        if(el.TryGetCurrentPattern(TextPattern.Pattern,out var t))raw=((TextPattern)t).DocumentRange.GetText(-1)??"";
        else if(el.TryGetCurrentPattern(ValuePattern.Pattern,out var v))raw=((ValuePattern)v).Current.Value??"";
        if(raw is null)return null;
        if(raw.Length==0)return raw;
        ct.ThrowIfCancellationRequested();
        try{if(SendButton(el,ct) is {} send&&!send.Current.IsEnabled)return "";}catch(OperationCanceledException){throw;}catch(Exception ex){Diag.Fail("prompt-send-button",ex);}   /* falls through to the structure */
        ct.ThrowIfCancellationRequested();
        return PlaceholderOnly(el)?"":raw;
    }
    // ReadDraft off to the side with a time budget. null: unreadable (no pattern, error, or over budget), which the caller treats as "there may be a draft".
    internal static string? ReadDraftWithin(AutomationElement el,int budgetMs,CancellationToken ct)
    {
        using var cts=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task=Task.Run(()=>ReadDraft(el,cts.Token));
        try
        {
            if(task.Wait(budgetMs,ct))return task.Result;
            Debug?.Invoke("draft read over budget");cts.Cancel();return null;
        }
        catch(OperationCanceledException){cts.Cancel();throw;}
        catch(Exception ex){Debug?.Invoke("draft unreadable: "+ex.GetType().Name);return null;}
    }
    // The Send button above the box, one step up the tree at a time (at most 6), checking the budget between the searches.
    static AutomationElement? SendButton(AutomationElement box,CancellationToken ct)
    {
        var cond=new PropertyCondition(AutomationElement.NameProperty,"Send message");
        var walker=TreeWalker.RawViewWalker;var up=walker.GetParent(box);
        for(int i=0;up is not null&&i<6;i++,up=walker.GetParent(up)){ct.ThrowIfCancellationRequested();if(up.FindFirst(TreeScope.Descendants,cond) is {} hit)return hit;}
        return null;
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
        catch(Exception ex){Diag.Fail("prompt-structure",ex);return false;}
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
        catch(Exception ex){return "describe failed: "+ex.GetType().Name;}   /* into the description text */
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
            catch(Exception ex){sb.Append("  (").Append(ex.GetType().Name).Append(")\n");}   /* into the description text */
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
        catch(Exception ex){sb.Append("caret probe failed: ").Append(ex.GetType().Name).Append('\n');}   /* into the description text */
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
