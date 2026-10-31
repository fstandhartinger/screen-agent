using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
namespace ScreenAgent;
internal static class Program
{
    [STAThread] public static int Main(string[] args)
    {
        KeyboardHook? hook=null;
        AppDomain.CurrentDomain.ProcessExit+=(_,_)=>{hook?.Dispose();};
        try
        {
            if(args.FirstOrDefault()=="--selftest-ocr")
            {
                var output=Ocr.Read(args[1]).GetAwaiter().GetResult();Console.WriteLine(JsonSerializer.Serialize(output));return output.Ids.Length>0?0:1;
            }
            if(args.FirstOrDefault()=="--selftest-inject")return InjectTest();
            if(args.FirstOrDefault()=="--selftest-ui")return UiTest();
            var app=new Application{ShutdownMode=ShutdownMode.OnMainWindowClose};
            var bridge=new AgentBridge();app.MainWindow=bridge;var pill=new Pill();var pause=new PauseControl(()=>manualPause=!manualPause);
            var privacy=new PrivacyCapture(ScreenConfig.Load().Policy(),()=>hook is not null&&hook.Healthy&&hook.Session&&!hook.Paused&&!manualPause);List<DrawOverlay> draw=[];IntPtr underlying=IntPtr.Zero;
            hook=new KeyboardHook();
            hook.Action+=a=>app.Dispatcher.BeginInvoke(new System.Action(()=>
            {
                if(a==ActionKind.SessionToggle && hook.Session)manualPause=false;
                if(a==ActionKind.DictationStart)bridge.Dictating(true);
                if(a is ActionKind.DictationStop or ActionKind.DictationDiscard)bridge.Dictating(false);
                if(a==ActionKind.DrawStart && !manualPause)
                {
                    underlying=Native.GetForegroundWindow();
                    foreach(var s in System.Windows.Forms.Screen.AllScreens)
                    {
                        using var probe=privacy.Capture(s.Bounds,hook.Session,hook.Paused||manualPause,underlying);
                        if(probe is null)continue;
                        var overlay=new DrawOverlay(s);draw.Add(overlay);overlay.Show();
                    }
                }
                if(a==ActionKind.DrawStop || (a==ActionKind.SessionToggle&&!hook.Session) || a==ActionKind.PauseToggle)
                {
                    foreach(var d in draw)
                    {
                        using var clean=privacy.Capture(d.Monitor,hook.Session,hook.Paused||manualPause,underlying);
                        if(clean!=null && d.Strokes.Any(s=>s.Count>0)){d.Composite(clean);lastHighlightBytes=PrivacyCapture.Png(clean).Length;} // discard pixels after spike proof; no upload
                        d.Close();
                    }
                    draw.Clear();
                }
            }));
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(10)};
            timer.Tick+=(_,_)=>{
                if(manualPause && draw.Count>0){foreach(var d in draw)d.Close();draw.Clear();}
                pill.SetMode(!hook.Healthy||!hook.Session||hook.Paused||manualPause?"Pausiert":draw.Count>0?"Zeichnen":hook.Dictating?"Diktat":"Sitzung");};timer.Start();
            long captureAt=0;bool capturing=false;IntPtr lastForeground=IntPtr.Zero;
            var captureTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(250)};
            captureTimer.Tick+=async (_,_)=>
            {
                IntPtr foreground=Native.GetForegroundWindow();
                if(capturing||!hook.Healthy||!hook.Session||hook.Paused||manualPause||draw.Count>0||(foreground==lastForeground && Environment.TickCount64-captureAt<4000))return;
                lastForeground=foreground;
                capturing=true;captureAt=Environment.TickCount64;
                try
                {
                    var screen=System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
                    using var frame=privacy.Capture(screen.Bounds,hook.Session,hook.Paused||manualPause);
                    if(frame==null){pill.SetMode("Pausiert · privat");return;}
                    byte[] png=PrivacyCapture.Png(frame);var ocr=await Task.Run(()=>Ocr.ReadMemory(png));
                    // Candidates remain local. Device pairing + /exists confirmation are deliberately absent in spike.
                    if(hook.Session&&!hook.Paused&&!manualPause)pill.SetMode($"Sitzung · {ocr.Ids.Length} Kandidaten");
                }
                catch{pill.SetMode("Pausiert · OCR Fehler");manualPause=true;}
                finally{capturing=false;}
            };captureTimer.Start();
            app.DispatcherUnhandledException+=(_,e)=>{hook.Dispose();e.Handled=false;};
            bridge.Closed+=(_,_)=>{timer.Stop();captureTimer.Stop();hook.Dispose();foreach(var d in draw)d.Close();pill.Close();pause.Close();};
            pill.Show();pause.Show();app.Run(bridge);return 0;
        }
        catch(Exception ex){Console.WriteLine(JsonSerializer.Serialize(new{error=ex.ToString()}));return 1;}
        finally{hook?.Dispose();}
    }
    static bool manualPause;
    static int lastHighlightBytes;
    static int UiTest()
    {
        var app=new Application();var pill=new Pill();pill.Show();var h=new System.Windows.Interop.WindowInteropHelper(pill).Handle;
        bool affinity=Native.SetWindowDisplayAffinity(h,0x11);
        pill.Close();Console.WriteLine(JsonSerializer.Serialize(new{test="overlay",excludeFromCapture=affinity}));return affinity?0:1;
    }
    static int InjectTest()
    {
        var app=new Application();int exit=1;
        app.Startup+=async (_,_)=>
        {
            Process? notepad=null;
            try
            {
                notepad=Process.Start(new ProcessStartInfo("notepad.exe"){UseShellExecute=true});
                AutomationElement? edit=null;IntPtr hwnd=IntPtr.Zero;
                for(int i=0;i<60&&edit is null;i++)
                {
                    await Task.Delay(200);notepad?.Refresh();hwnd=notepad?.MainWindowHandle??IntPtr.Zero;
                    if(hwnd==IntPtr.Zero)continue;
                    var root=AutomationElement.FromHandle(hwnd);
                    edit=root.FindFirst(TreeScope.Descendants,new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit),new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Document)));
                }
                if(edit is null)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new{test="inject",status="skipped",reason="No interactive Notepad UIA editor on runner; manual checklist required"}));exit=2;return;
                }
                Native.SetForegroundWindow(hwnd);edit.SetFocus();await Task.Delay(200);
                string? initial=TextInjector.Read(edit);
                if(initial is null)throw new InvalidOperationException("Notepad has no read-back pattern");
                if(initial.Length!=0)throw new InvalidOperationException("Refusing non-empty Notepad target");
                if(TextInjector.Elevated(hwnd))
                {
                    Console.WriteLine(JsonSerializer.Serialize(new{test="inject",status="skipped",reason="Runner Notepad is elevated or its process token is unreadable; insertion safety refuses this target. Manual test with a non-elevated interactive editor required."}));exit=2;return;
                }
                var target=new TextInjector.Target(hwnd,edit);
                bool foreground=Native.GetForegroundWindow()==hwnd;
                bool focused=Automation.Compare(edit,AutomationElement.FocusedElement);
                bool modifiersUp=Native.ModifiersUp;
                bool pasted=await TextInjector.Inject(target,"Jev und JevBench",true,new PrivacyPolicy());
                string? paste=TextInjector.Read(edit);
                bool unicode=await TextInjector.Inject(target," Jev und JevBench",false,new PrivacyPolicy());
                await Task.Delay(400);string? result=TextInjector.Read(edit);
                bool pass=pasted&&unicode&&paste=="Jev und JevBench"&&result=="Jev und JevBench Jev und JevBench";
                Console.WriteLine(JsonSerializer.Serialize(new{test="inject",status=pass?"passed":"failed",clipboard=paste,unicode=result,pasted,unicodeSent=unicode,foreground,focused,modifiersUp}));exit=pass?0:1;
            }
            catch(Exception ex){Console.WriteLine(JsonSerializer.Serialize(new{test="inject",status="failed",error=ex.Message}));exit=1;}
            finally
            {
                // Owned child only; no unrelated Notepad instances are closed.
                if(notepad is not null){try{if(!notepad.HasExited)notepad.Kill();}catch{}notepad.Dispose();}app.Shutdown(exit);
            }
        };
        app.Run();return exit;
    }
}
