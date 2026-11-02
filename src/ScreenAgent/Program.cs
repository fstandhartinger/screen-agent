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
            if(args.FirstOrDefault() is "--selftest-memory-ocr" or "--selftest-tesseract")
            {
                byte[] png=File.ReadAllBytes(args[1]);
                var output=args[0]=="--selftest-tesseract"?Ocr.ReadTesseractMemory(png):Ocr.ReadMemory(png).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(output));return output.Ids.Length>0?0:1;
            }
            if(args.FirstOrDefault()=="--selftest-find-ids")
            {
                using var image=new System.Drawing.Bitmap(args[1]);
                var clock=System.Diagnostics.Stopwatch.StartNew();
                var output=Ocr.FindIds(image,CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(new{output.Engine,output.Ids,output.WinRtFailure,Ms=clock.ElapsedMilliseconds}));return 0;
            }
            if(args.FirstOrDefault()=="--selftest-inject")return InjectTest();
            if(args.FirstOrDefault()=="--selftest-ui")return UiTest();
            if(args.FirstOrDefault()=="--selftest-hotkeys")return Selftests.Hotkeys();
            if(args.FirstOrDefault()=="--selftest-windows")return Selftests.Windows();
            string sid=System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value??"";
            using var instance=new Mutex(true,"Local\\ScreenAgent-"+sid,out bool first);
            string showName="Local\\ScreenAgent-Show-"+sid;
            if(!first)
            {
                // A second start (double click on the exe) is a request to see the app: ask the running one to open the Desk.
                Native.AllowSetForegroundWindow(-1); // let the running instance bring its window to the front for this click
                try{using var running=EventWaitHandle.OpenExisting(showName);running.Set();}catch{ /* the first instance is still starting */ }
                return 0;
            }
            using var showRequest=new EventWaitHandle(false,EventResetMode.AutoReset,showName);
            var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            var config=ScreenConfig.Load();using var client=new DeviceClient(config);
            var bridge=new AgentBridge(config,client);app.MainWindow=bridge;var toast=new StatusToast();
            hook=new KeyboardHook(new HotkeyBindings(config.Hotkeys));using var controller=new AppController(config,client,bridge,hook,toast);
            app.DispatcherUnhandledException+=(_,e)=>{hook.Dispose();e.Handled=false;};
            app.Exit+=(_,_)=>{hook.Dispose();toast.Close();};
            new Thread(()=>{while(showRequest.WaitOne())bridge.Dispatcher.BeginInvoke(new System.Action(bridge.ShowDesk));}){IsBackground=true,Name="ScreenAgent show request"}.Start();
            bridge.StartHidden();app.Run();return 0;
        }
        catch(Exception ex)
        {
            if(args.FirstOrDefault()?.StartsWith("--selftest")==true){Console.WriteLine(JsonSerializer.Serialize(new{error=ex.ToString()}));return 1;}
            // A GUI-subsystem exe has no console: say what is wrong where the user can see it, and keep a log.
            string log=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScreenAgent","startup-error.log");
            try{Directory.CreateDirectory(Path.GetDirectoryName(log)!);File.WriteAllText(log,DateTime.Now.ToString("s")+"\r\n"+ex);}catch{ /* the box below still tells */ }
            string what=ex is InvalidDataException?"Die Einstellungen in config.json sind ungültig (%APPDATA%\\ScreenAgent\\config.json):\r\n\r\n"+ex.Message:ex.Message;
            Native.MessageBox(IntPtr.Zero,"Screen Agent konnte nicht starten.\r\n\r\n"+what+"\r\n\r\nDetails: "+log,"Screen Agent",0x10|0x10000|0x40000);
            return 1;
        }
        finally{hook?.Dispose();}
    }
    static int UiTest()
    {
        var app=new Application();var toast=new StatusToast();toast.Present("Selftest",Level.Active);var h=new System.Windows.Interop.WindowInteropHelper(toast).Handle;
        bool affinity=Native.SetWindowDisplayAffinity(h,0x11);
        toast.Close();Console.WriteLine(JsonSerializer.Serialize(new{test="overlay",excludeFromCapture=affinity}));return affinity?0:1;
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
                if(TextInjector.ElevationRefusal(hwnd) is string elevationReason)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new{test="inject",status="skipped",reason=elevationReason+"; insertion safety refuses this target. Manual test with a non-elevated interactive editor required."}));exit=2;return;
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
