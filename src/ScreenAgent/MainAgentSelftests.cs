using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using ScreenAgent.Core;
namespace ScreenAgent;
// --selftest-main-agent <vscodeDir> <workDir> <outDir>: the Main Agent prompt against a real VS Code Insiders with the real Claude Code extension on a real desktop
// (driven by ci/main-agent-e2e.ps1, which installs both). Each scenario ends with a proof read through UIA and a screenshot in <outDir>. Nothing is ever submitted and
// no API key is real. A scenario whose precondition could not be set up is reported as "precondition-failed", never as passed.
internal static class MainAgentSelftests
{
    const string Marker="a#abc123";
    const string Prompt="Grüße aus dem Test: Müller prüft "+Marker+" 😀 schön\nZweite Zeile ÄÖÜ ß\nDritte Zeile";
    static readonly UIntPtr Test=(UIntPtr)0x5442;
    static readonly string[] Names=["Message input"];
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    sealed class Scn{public string Name="";public string Status="failed";public string? Why;public Dictionary<string,object?> Facts=[];}
    static StreamWriter? logFile;
    static string outDir="",vscodeDir="",workDir="";
    static void Step(string text){string line=DateTime.UtcNow.ToString("HH:mm:ss.fff")+" "+text;Console.WriteLine(line);Console.Out.Flush();try{logFile?.WriteLine(line);logFile?.Flush();}catch{ }}
    static void Shot(string name)
    {
        try
        {
            var b=System.Windows.Forms.Screen.PrimaryScreen!.Bounds;using var image=new System.Drawing.Bitmap(b.Width,b.Height);
            using(var g=System.Drawing.Graphics.FromImage(image))g.CopyFromScreen(b.Location,System.Drawing.Point.Empty,b.Size);
            image.Save(Path.Combine(outDir,name+".png"),System.Drawing.Imaging.ImageFormat.Png);
        }
        catch(Exception ex){Step("screenshot failed: "+ex.Message);}
    }
    public static int Run(string[] args)
    {
        if(args.Length<3)throw new ArgumentException("--selftest-main-agent <vscodeDir> <workDir> <outDir>");
        vscodeDir=Path.GetFullPath(args[0]);workDir=Path.GetFullPath(args[1]);outDir=Path.GetFullPath(args[2]);Directory.CreateDirectory(outDir);
        logFile=new StreamWriter(Path.Combine(outDir,"main-agent.log"),false,new UTF8Encoding(false));
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};int exit=1;
        app.Startup+=async (_,_)=>
        {
            try{exit=await Task.Run(()=>RunAll(app));}
            catch(Exception ex){Step("crashed "+ex);exit=1;}
            finally{logFile?.Dispose();app.Shutdown(exit);}
        };
        app.Run();return exit;
    }
    static async Task<int> RunAll(Application app)
    {
        var scenarios=new List<Scn>();var events=new List<string>();
        KeyboardHook.AcceptInjectedForTests=true;
        var hookActions=new List<ActionKind>();
        using var hook=new KeyboardHook(new HotkeyBindings(new AppConfig().Hotkeys));hook.Action+=a=>{lock(hookActions)hookActions.Add(a);};
        MainAgentPrompt.Debug=m=>Step("  · "+m);
        var log=new Action<string,Dictionary<string,object?>>((t,d)=>{string e=t+" "+JsonSerializer.Serialize(TraceSanitizer.Clean(d));events.Add(e);Step("  telemetry "+e);});
        var scenariosToRun=new (string Name,Func<Scn,Task> Body)[]
        {
            ("A-background-behind-notepad",A),("B-minimized",B),("C-panel-not-open-palette",C),("D-focus-in-editor",D),("E-focus-in-terminal",E),("F-two-windows",F),("G-fails-closed",G),("I-existing-draft-is-kept",I)
        };
        string only=Environment.GetEnvironmentVariable("SA_E2E_ONLY")??"";
        foreach(var (name,body) in scenariosToRun)
        {
            if(only.Length>0&&!only.Split(',').Any(o=>name.StartsWith(o,StringComparison.OrdinalIgnoreCase)))continue;
            var s=new Scn{Name=name};scenarios.Add(s);
            Step($"==== {name}");
            KillCode();
            using var cts=new CancellationTokenSource(TimeSpan.FromMinutes(4));
            try{await body(s).WaitAsync(cts.Token);}
            catch(OperationCanceledException){s.Status="failed";s.Why="scenario timed out after 4 minutes";}
            catch(Exception ex){s.Status="failed";s.Why="crashed: "+ex.GetType().Name+": "+ex.Message;Step(ex.ToString());}
            Step($"==== {name}: {s.Status} {s.Why}");
            if(s.Status!="passed"){TryDump(s.Name+"-fail");Shot(s.Name+"-fail");}
            KillCode();KillNotepad();
        }
        // Typing never triggers or disturbs chords: no action reached the keyboard hook (it accepted injected keys, so only the marker kept our keys out), and a marker-tagged chord does not start a session.
        var hookCase=new Scn{Name="H-typing-never-disturbs-the-keyboard-hook"};scenarios.Add(hookCase);
        try
        {
            ActionKind[] seen;lock(hookActions)seen=hookActions.ToArray();
            hookCase.Facts["actionsDuringAllScenarios"]=seen.Select(a=>a.ToString()).ToArray();
            hookCase.Facts["hookHealthy"]=hook.Healthy;
            foreach(var vk in new ushort[]{0xa2,0x5b,0xa0})Native.SendInput(1,[Native.Key(vk,false,0,false,Native.Marker)],Marshal.SizeOf<Native.Input>());
            Native.SendInput(1,[Native.Key(0x44,false,0,false,Native.Marker)],Marshal.SizeOf<Native.Input>());Native.SendInput(1,[Native.Key(0x44,true,0,false,Native.Marker)],Marshal.SizeOf<Native.Input>());
            foreach(var vk in new ushort[]{0xa0,0x5b,0xa2})Native.SendInput(1,[Native.Key(vk,true,0,false,Native.Marker)],Marshal.SizeOf<Native.Input>());
            await Task.Delay(400);
            hookCase.Facts["sessionAfterMarkerChord"]=hook.Session;
            lock(hookActions)hookCase.Facts["actionsAfterMarkerChord"]=hookActions.Select(a=>a.ToString()).ToArray();
            // Control: the same chord with the test tag instead of ours does reach the hook and starts the session, so the silence above is the marker's doing.
            bool silent=!hook.Session&&hookActions.Count==0;
            foreach(var vk in new ushort[]{0xa2,0x5b,0xa0})Native.SendInput(1,[Native.Key(vk,false,0,false,Test)],Marshal.SizeOf<Native.Input>());
            foreach(var vk in new ushort[]{0xa0,0x5b,0xa2})Native.SendInput(1,[Native.Key(vk,true,0,false,Test)],Marshal.SizeOf<Native.Input>());
            await Task.Delay(600);
            hookCase.Facts["controlChordStartsSession"]=hook.Session;
            bool pass=hook.Healthy&&silent&&hook.Session;
            hook.SetSession(false);
            hookCase.Status=pass?"passed":(hook.Healthy?"failed":"precondition-failed");hookCase.Why=hook.Healthy?null:"keyboard hook not installed on this runner";
        }
        catch(Exception ex){hookCase.Why=ex.Message;}
        var json=JsonSerializer.Serialize(new{test="main-agent",status=scenarios.All(s=>s.Status=="passed")?"passed":"failed",scenarios=scenarios.Select(s=>new{s.Name,s.Status,s.Why,s.Facts}),telemetry=events},new JsonSerializerOptions{WriteIndented=true});
        File.WriteAllText(Path.Combine(outDir,"main-agent.json"),json);
        Console.WriteLine(json);
        return scenarios.All(s=>s.Status=="passed")?0:1;

        // ---- scenarios
        async Task A(Scn s)
        {
            var w=await Start(s,"ws-A",openClaude:true);if(w==IntPtr.Zero)return;
            TryDump("A-after-open-claude");
            var np=await NotepadInFront(s);if(!np)return;
            await RunAndProve(s,w,"A",expectRoute:null);
        }
        async Task B(Scn s)
        {
            var w=await Start(s,"ws-B",openClaude:true);if(w==IntPtr.Zero)return;
            Native.ShowWindow(w,3);await Task.Delay(800);bool zoomed=IsZoomed(w);
            Native.ShowWindow(w,6);await Task.Delay(1000);
            s.Facts["maximizedBefore"]=zoomed;s.Facts["minimizedBeforeRun"]=Native.IsIconic(w);
            if(!Native.IsIconic(w)){s.Status="precondition-failed";s.Why="window did not minimize";return;}
            Shot("B-before-run");
            await RunAndProve(s,w,"B",expectRoute:null);
            s.Facts["notIconicAfter"]=!Native.IsIconic(w);s.Facts["stillMaximizedAfter"]=IsZoomed(w);
            if(s.Status=="passed"&&(Native.IsIconic(w)||(zoomed&&!IsZoomed(w)))){s.Status="failed";s.Why="window state not kept (iconic or lost maximized)";}
        }
        async Task C(Scn s)
        {
            var w=await Start(s,"ws-C",openClaude:false);if(w==IntPtr.Zero)return;
            s.Facts["extensionOpenedPanelOnItsOwn"]=new MainAgentPrompt(Names,(_,_)=>{}).FindInput(w,2500) is not null;
            if(!await CloseClaude(w)){s.Status="precondition-failed";s.Why="could not close the Claude panel (secondary side bar)";return;}
            s.Facts["claudePanelOpenBefore"]=false;TryDump("C-panel-closed");
            await NotepadInFront(s);
            await RunAndProve(s,w,"C",expectRoute:"palette");
        }
        async Task D(Scn s)
        {
            var w=await Start(s,"ws-D",openClaude:true);if(w==IntPtr.Zero)return;
            if(!await FocusVia(w,0x79)){s.Status="precondition-failed";s.Why="could not put the keyboard focus in the editor";return;}
            var f=MainAgentPrompt.Focus();s.Facts["focusBeforeInEditor"]=f is not null&&FocusRules.InEditorOrTerminal(f.Classes);s.Facts["focusBeforeName"]=f?.Name;
            if(f is null||!FocusRules.InEditorOrTerminal(f.Classes)){s.Status="precondition-failed";s.Why="focus was not in the editor buffer before the run (name '"+f?.Name+"')";return;}
            s.Facts["titleBefore"]=TitleOf(w);
            await RunAndProve(s,w,"D",expectRoute:null);
            string title=TitleOf(w);s.Facts["titleAfter"]=title;s.Facts["titleDirty"]=title.Contains('●');
            if(title.Contains('●')){s.Status="failed";s.Why="the file buffer is dirty: something was typed into the editor";}
            s.Facts["editorRead"]=EditorText(w);
            if(s.Facts["editorRead"] is string et&&et.Contains(Marker)){s.Status="failed";s.Why="the editor contains the text";}
        }
        async Task E(Scn s)
        {
            var w=await Start(s,"ws-E",openClaude:true);if(w==IntPtr.Zero)return;
            if(!await FocusVia(w,0x7a)){s.Status="precondition-failed";s.Why="could not put the keyboard focus in the terminal";return;}
            await Task.Delay(4000);
            var f=MainAgentPrompt.Focus();
            s.Facts["focusBeforeInTerminal"]=f is not null&&FocusRules.InEditorOrTerminal(f.Classes);s.Facts["focusBeforeName"]=f?.Name;s.Facts["focusBeforeClasses"]=f?.Classes.Take(10).ToArray();
            if(f is null||!FocusRules.InEditorOrTerminal(f.Classes)){s.Status="precondition-failed";s.Why="focus was not in the terminal before the run (name '"+f?.Name+"')";return;}
            await RunAndProve(s,w,"E",expectRoute:null);
            s.Facts["markerSeenInAnyNodeName"]=DumpText(w).Split('\n').Count(l=>l.Contains(Marker));
        }
        async Task F(Scn s)
        {
            var old=await Start(s,"ws-F1",openClaude:true);if(old==IntPtr.Zero)return;
            var neu=await Launch(s,"ws-F2");if(neu==IntPtr.Zero)return;
            await Task.Delay(3000);
            s.Facts["newerWindowOpenedPanelOnItsOwn"]=new MainAgentPrompt(Names,(_,_)=>{}).FindInput(neu,2500) is not null;
            await CloseClaude(neu);
            var probe=new MainAgentPrompt(Names,(_,_)=>{});
            bool newHas=probe.FindInput(neu,2500) is not null,oldHas=probe.FindInput(old,2500) is not null;
            s.Facts["newerWindowHasClaude"]=newHas;s.Facts["olderWindowHasClaude"]=oldHas;
            if(newHas||!oldHas){s.Status="precondition-failed";s.Why="windows were not in the wanted state (older has panel, newer has none)";return;}
            Native.SetForegroundWindow(neu);await Task.Delay(500);
            var order=CandidateOrder.Pick(MainAgentPrompt.Enumerate()).Select(c=>c.Handle).ToArray();
            s.Facts["candidateOrderNewestFirst"]=order.Select(h=>h==(long)neu?"newer":h==(long)old?"older":"other").ToArray();
            var r=await RunAndProve(s,old,"F",expectRoute:"element");
            s.Facts["chosenWasOlder"]=r?.Window==old;
            if(s.Status=="passed"&&r?.Window!=old){s.Status="failed";s.Why="the newer window without a Claude session was chosen";}
            s.Facts["newerWindowTitleDirty"]=TitleOf(neu).Contains('●');
        }
        async Task G(Scn s)
        {
            var w=await Start(s,"ws-G",openClaude:true);if(w==IntPtr.Zero)return;
            await NotepadInFront(s);
            MainAgentPrompt.PaletteDisabledForTests=true;
            long keysBefore=Interlocked.Read(ref MainAgentPrompt.KeysSent);uint clip=Native.GetClipboardSequenceNumber();
            var fgBefore=Native.GetForegroundWindow();
            Shot("G-before-run");
            PromptResult r;
            try{r=await new MainAgentPrompt(["Nonexistent input name"],log).Run(Prompt,CancellationToken.None);}
            finally{MainAgentPrompt.PaletteDisabledForTests=false;}
            long keys=Interlocked.Read(ref MainAgentPrompt.KeysSent)-keysBefore;
            s.Facts["code"]=r.Code;s.Facts["route"]=r.Route;s.Facts["keysSent"]=keys;s.Facts["chars"]=r.Chars;
            var fgAfterRun=Native.GetForegroundWindow();
            PromptFallbackWindow? fallback=null;string heading="";bool visible=false,readOnly=false;IntPtr fh=IntPtr.Zero;
            await app.Dispatcher.InvokeAsync(()=>{fallback=PromptFallbackWindow.Show(r.Code,Prompt);heading=fallback.Heading;visible=fallback.IsVisible;readOnly=fallback.ReadOnlyText;fh=new System.Windows.Interop.WindowInteropHelper(fallback).Handle;});
            await Task.Delay(700);Shot("G-fallback");
            bool clipUnchanged=Native.GetClipboardSequenceNumber()==clip;
            s.Facts["fallbackHeading"]=heading;s.Facts["fallbackVisible"]=visible;s.Facts["fallbackReadOnly"]=readOnly;
            Native.GetWindowDisplayAffinity(fh,out uint aff);s.Facts["fallbackAffinity"]=aff;
            s.Facts["clipboardUntouchedUntilCopyClicked"]=clipUnchanged;
            s.Facts["foregroundUnchangedByFallback"]=Native.GetForegroundWindow()==fgAfterRun;s.Facts["foregroundChangedByRunItself"]=fgAfterRun!=fgBefore;
            string? boxNow=null;try{var el=new MainAgentPrompt(Names,(_,_)=>{}).FindInput(w,3000);boxNow=el is null?null:MainAgentPrompt.ReadDraft(el);s.Facts["messageInputParts"]=el is null?null:MainAgentPrompt.Describe(el);}catch{ }
            s.Facts["messageInputAfter"]=boxNow;s.Facts["titleDirty"]=TitleOf(w).Contains('●');
            await app.Dispatcher.InvokeAsync(()=>fallback!.ClickCopyForTests());await Task.Delay(300);
            string? copied=null;await app.Dispatcher.InvokeAsync(()=>{try{copied=System.Windows.Clipboard.GetText();}catch{ }});
            s.Facts["copyButtonPutsTheTextOnTheClipboard"]=copied==PromptText.Normalize(Prompt);
            await app.Dispatcher.InvokeAsync(()=>fallback!.Close());
            bool ok=r.Code is PromptCodes.NoField or PromptCodes.WrongFocus&&keys==0&&r.Chars==0&&clipUnchanged&&visible&&readOnly&&(bool)s.Facts["foregroundUnchangedByFallback"]!&&heading.StartsWith("Nicht eingetippt: ")
                &&aff==0x11&&(boxNow is null||boxNow.Trim().Length==0)&&!(bool)s.Facts["titleDirty"]!&&(bool)s.Facts["copyButtonPutsTheTextOnTheClipboard"]!;
            s.Status=ok?"passed":"failed";if(!ok)s.Why="see facts";
        }

        async Task I(Scn s)
        {
            var w=await Start(s,"ws-I",openClaude:true);if(w==IntPtr.Zero)return;
            const string draft="Alter Entwurf";
            var el=new MainAgentPrompt(Names,(_,_)=>{}).FindInput(w,3000);
            if(el is null||!MainAgentPrompt.Front(w)){s.Status="precondition-failed";s.Why="no message box to put a draft in";return;}
            try{el.SetFocus();}catch{ }
            await Task.Delay(600);
            foreach(char c in draft)Native.Send(Native.Key(0,false,c,true,Test),Native.Key(0,true,c,true,Test));
            await Task.Delay(800);
            string? before=el is null?null:MainAgentPrompt.ReadDraft(el);s.Facts["draftBeforeRun"]=before;
            if(before is null||!before.Contains(draft)){s.Status="precondition-failed";s.Why="the draft did not land in the box";return;}
            await NotepadInFront(s);
            await RunAndProve(s,w,"I",expectRoute:null,draft:draft);
        }
        // ---- helpers
        async Task<IntPtr> Start(Scn s,string ws,bool openClaude)
        {
            var w=await Launch(s,ws);if(w==IntPtr.Zero)return IntPtr.Zero;
            if(openClaude&&!await OpenClaude(s,w)){s.Status="precondition-failed";s.Why="the Claude panel did not open (no 'Message input' in the UIA tree); see uia dump";return IntPtr.Zero;}
            return w;
        }
        async Task<PromptResult?> RunAndProve(Scn s,IntPtr w,string tag,string? expectRoute,string draft="")
        {
            uint clip=Native.GetClipboardSequenceNumber();Shot(tag+"-before-run");
            var r=await new MainAgentPrompt(Names,log).Run(Prompt,CancellationToken.None);
            s.Facts["code"]=r.Code;s.Facts["landed"]=r.Landed;s.Facts["route"]=r.Route;s.Facts["chars"]=r.Chars;s.Facts["ms"]=r.Ms;s.Facts["windowsSeen"]=r.Windows;s.Facts["wsl"]=r.Wsl;
            Shot(tag+"-after-run");
            // Proof by UIA, independent of the code under test: the box holds the text, normalized equal; three seconds later it still does (nothing was sent).
            string exact=draft.Length>0?draft+" "+PromptText.Normalize(Prompt):PromptText.Normalize(Prompt);string want=PromptText.Squash(exact);
            var probe=new MainAgentPrompt(Names,(_,_)=>{});
            string? first=ReadInput(probe,r.Window!=IntPtr.Zero?r.Window:w);
            s.Facts["boxAfterRun"]=first;
            try{var el=probe.FindInput(r.Window!=IntPtr.Zero?r.Window:w,3000);s.Facts["boxParts"]=el is null?null:MainAgentPrompt.Describe(el);}catch{ }
            await Task.Delay(3000);
            string? later=ReadInput(probe,r.Window!=IntPtr.Zero?r.Window:w);
            s.Facts["boxAfter3s"]=later;Shot(tag+"-after-3s");
            // Equal normalized, and no stray character in front (a placeholder mistaken for a draft once put a space there).
            bool equal=first is not null&&PromptText.Squash(first)==want&&first.Replace("\r\n","\n").TrimEnd()==exact,still=later is not null&&PromptText.Squash(later)==want;
            s.Facts["boxExactlyTheText"]=first is not null&&first.Replace("\r\n","\n").TrimEnd()==exact;
            s.Facts["boxEqualsTextNormalized"]=equal;s.Facts["stillThereAfter3s"]=still;
            bool clipOk=Native.GetClipboardSequenceNumber()==clip;s.Facts["clipboardUntouched"]=clipOk;
            bool routeOk=expectRoute is null||r.Route==expectRoute;s.Facts["routeAsExpected"]=routeOk;
            bool pass=r.Code==PromptCodes.Typed&&r.Landed==true&&equal&&still&&clipOk&&routeOk;
            s.Status=pass?"passed":"failed";if(!pass)s.Why=$"code={r.Code} landed={r.Landed} equal={equal} still={still} clipboardUntouched={clipOk} route={r.Route}";
            return r;
        }
        string? ReadInput(MainAgentPrompt probe,IntPtr w){try{var el=probe.FindInput(w,3000);return el is null?null:MainAgentPrompt.ReadBox(el);}catch{return null;}}
        async Task<IntPtr> Launch(Scn s,string ws)
        {
            string folder=Path.Combine(workDir,ws);Directory.CreateDirectory(folder);
            string file=Path.Combine(folder,"notes.txt");File.WriteAllText(file,"Zeile eins\nZeile zwei\n");
            var psi=new ProcessStartInfo(Path.Combine(vscodeDir,"Code - Insiders.exe")){UseShellExecute=false,WorkingDirectory=folder};
            foreach(var a in new[]{"--new-window","--disable-workspace-trust","--skip-release-notes","--skip-welcome","--disable-gpu",folder,file})psi.ArgumentList.Add(a);
            psi.Environment["ANTHROPIC_API_KEY"]="not-a-real-key";psi.Environment.Remove("ELECTRON_RUN_AS_NODE");
            Process.Start(psi);
            for(int i=0;i<180;i++)
            {
                await Task.Delay(500);
                var hit=MainAgentPrompt.Enumerate().FirstOrDefault(c=>c.Process=="Code - Insiders"&&c.Title.Contains(ws));
                if(hit.Handle!=0&&hit.Title.Contains("notes.txt")){await Task.Delay(4000);Step($"window up: {hit.Handle:x}");return (IntPtr)hit.Handle;}
            }
            s.Status="precondition-failed";s.Why="VS Code Insiders window for "+ws+" did not appear within 90 s";return IntPtr.Zero;
        }
        async Task<bool> OpenClaude(Scn s,IntPtr w)
        {
            var probe=new MainAgentPrompt(Names,(_,_)=>{});
            for(int attempt=0;attempt<8;attempt++)
            {
                if(!MainAgentPrompt.Front(w)){Step("could not bring VS Code to the front for setup");continue;}
                Press(0x11,0x12,0x10,0x78);   // keybindings.json: ctrl+alt+shift+f9 = claude-vscode.focus
                for(int i=0;i<6;i++)
                {
                    await Task.Delay(1500);
                    if(probe.FindInput(w,2500) is not null){Step("claude panel open");await Task.Delay(1500);return true;}
                }
            }
            return false;
        }
        // The extension opens its panel in every new window on its own; scenarios that need a window without it close the secondary side bar (setup key ctrl+alt+shift+f12).
        async Task<bool> CloseClaude(IntPtr w)
        {
            var probe=new MainAgentPrompt(Names,(_,_)=>{});
            for(int attempt=0;attempt<4;attempt++)
            {
                if(probe.FindInput(w,1500) is null)return true;
                if(!MainAgentPrompt.Front(w))continue;
                Press(0x11,0x12,0x10,0x7b);await Task.Delay(1500);
            }
            return probe.FindInput(w,2500) is null;
        }
        async Task<bool> FocusVia(IntPtr w,ushort fkey)
        {
            if(!MainAgentPrompt.Front(w))return false;
            Press(0x11,0x12,0x10,fkey);await Task.Delay(2500);
            return true;
        }
        async Task<bool> NotepadInFront(Scn s)
        {
            try
            {
                var p=Process.Start(new ProcessStartInfo("notepad.exe"){UseShellExecute=true});
                for(int i=0;i<40;i++)
                {
                    await Task.Delay(250);
                    var fg=Native.GetForegroundWindow();Native.GetWindowThreadProcessId(fg,out uint pid);
                    try{using var q=Process.GetProcessById((int)pid);if(q.ProcessName.Equals("notepad",StringComparison.OrdinalIgnoreCase)){s.Facts["notepadInFront"]=true;return true;}}catch{ }
                    if(i==8)foreach(var n in Process.GetProcessesByName("notepad")){if(n.MainWindowHandle!=IntPtr.Zero)Native.SetForegroundWindow(n.MainWindowHandle);}
                }
            }
            catch(Exception ex){s.Facts["notepadError"]=ex.Message;}
            s.Status="precondition-failed";s.Why="could not put Notepad in front";return false;
        }
    }
    static void KillNotepad(){foreach(var p in Process.GetProcessesByName("notepad"))try{p.Kill();}catch{ }}
    // Only processes started from the folder this run was given.
    static void KillCode()
    {
        foreach(var p in Process.GetProcessesByName("Code - Insiders"))
            try{if(p.MainModule?.FileName?.StartsWith(vscodeDir,StringComparison.OrdinalIgnoreCase)==true){p.Kill();p.WaitForExit(3000);}}catch{ }
        Thread.Sleep(1500);
    }
    static void Press(params ushort[] vks)
    {
        var list=new List<Native.Input>();
        foreach(var v in vks)list.Add(Native.Key(v,false,0,false,Test));
        for(int i=vks.Length-1;i>=0;i--)list.Add(Native.Key(vks[i],true,0,false,Test));
        Native.Send(list.ToArray());
    }
    static string TitleOf(IntPtr w){var sb=new StringBuilder(512);Native.GetWindowText(w,sb,512);return sb.ToString();}
    static string? EditorText(IntPtr w)
    {
        try
        {
            var el=AutomationElement.FromHandle(w).FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,"Editor content"));
            return el is null?null:MainAgentPrompt.ReadBox(el);
        }
        catch{return null;}
    }
    static void TryDump(string name)
    {
        try
        {
            var hits=MainAgentPrompt.Enumerate().Where(c=>c.Process=="Code - Insiders").ToList();
            if(hits.Count==0)return;
            File.WriteAllText(Path.Combine(outDir,"uia-"+name+".txt"),string.Join("\n\n",hits.Take(2).Select(h=>"== window "+h.Handle.ToString("x")+" minimized="+h.Minimized+"\n"+DumpText((IntPtr)h.Handle))));
        }
        catch(Exception ex){Step("dump failed: "+ex.Message);}
    }
    // Flat text dump of the UIA control view, bounded in nodes and time: what the accessibility tree of VS Code (and its webviews) really exposes.
    static string DumpText(IntPtr hwnd,int maxNodes=2500,int budgetMs=25000)
    {
        var sb=new StringBuilder();var clock=Stopwatch.StartNew();int nodes=0;
        var walker=TreeWalker.ControlViewWalker;
        void Walk(AutomationElement e,int depth)
        {
            if(nodes>=maxNodes||clock.ElapsedMilliseconds>budgetMs||depth>60)return;
            nodes++;
            try
            {
                var c=e.Current;string name=(c.Name??"").Replace("\n"," ").Replace("\r"," ");if(name.Length>80)name=name[..80];
                sb.Append(new string(' ',Math.Min(depth,40))).Append(c.ControlType.ProgrammaticName.Replace("ControlType.","")).Append(" name='").Append(name).Append("' class='").Append(c.ClassName).Append('\'');
                if(!string.IsNullOrEmpty(c.AutomationId))sb.Append(" id='").Append(c.AutomationId.Length>40?c.AutomationId[..40]:c.AutomationId).Append('\'');
                if(c.HasKeyboardFocus)sb.Append(" FOCUS");
                sb.Append('\n');
                for(var child=walker.GetFirstChild(e);child is not null;child=walker.GetNextSibling(child)){Walk(child,depth+1);if(nodes>=maxNodes||clock.ElapsedMilliseconds>budgetMs)break;}
            }
            catch{ /* element went away */ }
        }
        try{Walk(AutomationElement.FromHandle(hwnd),0);}catch(Exception ex){sb.Append("dump error: ").Append(ex.Message);}
        sb.Append("-- nodes=").Append(nodes).Append(" ms=").Append(clock.ElapsedMilliseconds).Append('\n');
        return sb.ToString();
    }
}
