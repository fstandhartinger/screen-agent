using System.Diagnostics;
using System.Text.Json;
using System.Windows;
namespace ScreenAgent;
// --selftest-dictation-target [vscode exe]: the v0.1.15 key-down target resolution against real programs on an interactive desktop.
// Each program is started twice, fresh: run A asks the 0.1.14 way (one read at the chord; the first accessibility touch of the
// process), run B asks the new way (TextInjector.Resolve, also the first touch). A is reported (does the old bug reproduce here?),
// B must bind. Plus: taskbar in front (refused with the new notice), foreground settling, our own overlay in front, a window
// without a text field (reported: it binds, as before). Only processes this test started are closed.
internal static class DictationTargetSelftest
{
    static readonly List<object> Rows=[];
    static bool failed;
    static void Row(string name,bool? pass,object detail){Rows.Add(new{@case=name,status=pass is null?"info":pass.Value?"passed":"failed",detail});if(pass==false)failed=true;}
    public static int Run(string[] args)
    {
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};int exit=1;
        string temp=Path.Combine(Path.GetTempPath(),"sa-dictation-target-"+Environment.ProcessId);Directory.CreateDirectory(temp);
        string? vscode=args.Skip(1).FirstOrDefault(File.Exists)??(Environment.GetEnvironmentVariable("SA_VSCODE_EXE") is {} env&&File.Exists(env)?env:null);
        app.Startup+=async (_,_)=>
        {
            try
            {
                var policy=new PrivacyPolicy();
                // GitHub's runner runs everything elevated, this test included: the elevation fence would refuse every window there.
                bool selfElevated=SelfElevated();
                TextInjector.ElevatedPeersForTests=selfElevated;Row("environment",null,new{selfElevated,elevationFence=selfElevated?"off for this test (same integrity level)":"on"});
                string note=Path.Combine(temp,"sa-dictation-notepad.txt");File.WriteAllText(note,"");
                string page=Path.Combine(temp,"sa-dictation-field.html");File.WriteAllText(page,"<!doctype html><title>SA Diktat Feld</title><textarea autofocus style='width:90%;height:200px'></textarea>");
                string? browser=new[]{Environment.GetEnvironmentVariable("ProgramFiles")+@"\Google\Chrome\Application\chrome.exe",Environment.GetEnvironmentVariable("ProgramFiles(x86)")+@"\Google\Chrome\Application\chrome.exe",Environment.GetEnvironmentVariable("ProgramFiles(x86)")+@"\Microsoft\Edge\Application\msedge.exe",Environment.GetEnvironmentVariable("ProgramFiles")+@"\Microsoft\Edge\Application\msedge.exe"}.FirstOrDefault(File.Exists);
                string vsFile=Path.Combine(temp,"sa-dictation-vscode.txt");File.WriteAllText(vsFile,"");
                var programs=new List<(string Name,Func<int,Process?> Start,string Proc,string Title)>
                {
                    ("notepad",_=>Process.Start(new ProcessStartInfo("notepad.exe",Quote(note)){UseShellExecute=true}),"notepad","sa-dictation-notepad"),
                };
                if(browser is not null)programs.Add(("browser",run=>Process.Start(new ProcessStartInfo(browser,$"--user-data-dir={Quote(Path.Combine(temp,"browser"+run))} --no-first-run --no-default-browser-check --disable-features=Translate --app={Quote(new Uri(page).AbsoluteUri)}"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true}).Quiet(),Path.GetFileNameWithoutExtension(browser),"SA Diktat Feld"));
                else Row("browser",null,new{skipped="no Chrome or Edge on this machine"});
                if(vscode is not null)programs.Add(("vscode",run=>Process.Start(new ProcessStartInfo(vscode,$"--user-data-dir {Quote(Path.Combine(temp,"code"+run))} --extensions-dir {Quote(Path.Combine(temp,"ext"+run))} --disable-workspace-trust --skip-welcome --skip-release-notes --new-window {Quote(vsFile)}"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true}).Quiet(),Path.GetFileNameWithoutExtension(vscode),"sa-dictation-vscode"));
                else Row("vscode",null,new{skipped="no VS Code exe given (argument or SA_VSCODE_EXE)"});
                IntPtr keep=IntPtr.Zero;Process? keepProc=null;
                foreach(var (name,start,proc,title) in programs)
                {
                    for(int run=0;run<2;run++)
                    {
                        var (h,owner)=await Open(start(run),proc,title);
                        if(h==IntPtr.Zero){Row($"{name}-{(run==0?"legacy":"new")}",false,new{error="window did not appear",proc,title});continue;}
                        bool front=Front(h);await Task.Delay(name=="notepad"?400:2500);   // the program settles; no accessibility query before the measured one
                        long t0=Environment.TickCount64;
                        if(run==0){var (bound,why)=Legacy(h,policy);Row(name+"-legacy",null,new{bound,why,front,ms=Environment.TickCount64-t0,meaning=bound?"the 0.1.14 read worked here":"the 0.1.14 read failed: this is the reported bug"});}
                        else
                        {
                            var o=await TextInjector.Resolve(policy,h,h);
                            Row(name+"-new",o.Bound&&o.Target!.Window==h,new{o.Bound,o.Attempts,o.Ms,o.Last.Process,o.Last.Source,misses=string.Join(",",o.Misses),o.Refused,control=o.Target is {} t?TextInjector.Control(t):"",front});
                        }
                        if(name=="notepad"&&run==1){keep=h;keepProc=owner;}else Close(owner);
                    }
                }
                if(keep!=IntPtr.Zero)
                {
                    // Taskbar in front: nothing typeable; the refusal comes after ~300 ms and says what to do.
                    IntPtr tray=FindWindow("Shell_TrayWnd",null);
                    if(tray!=IntPtr.Zero&&Front(tray))
                    {
                        var o=await TextInjector.Resolve(policy,IntPtr.Zero,IntPtr.Zero);
                        Row("taskbar-refused",!o.Bound&&o.Refused=="no-target"&&o.Ms>=200,new{o.Bound,o.Refused,o.Ms,o.Attempts,misses=string.Join(",",o.Misses),notice=o.Refused is {} r?DictationTarget.Notice(r):""});
                        // Settling: the taskbar holds the front at key-down, Notepad comes back 100 ms later; the same press must bind Notepad.
                        Front(tray);
                        var flip=Task.Run(async ()=>{await Task.Delay(100);Front(keep);});
                        var s=await TextInjector.Resolve(policy,IntPtr.Zero,IntPtr.Zero);await flip;
                        Row("foreground-settles",s.Bound&&s.Target!.Window==keep&&s.Attempts>1,new{s.Bound,s.Attempts,s.Ms,s.Last.Source,misses=string.Join(",",s.Misses)});
                    }
                    else Row("taskbar-refused",null,new{skipped="the taskbar could not be brought to the front on this desktop"});
                    // Our own overlay in front (no-activate tool window): the front goes back to Notepad and the press binds it.
                    Front(keep);await Task.Delay(200);
                    var overlay=new Window{Width=220,Height=80,WindowStyle=WindowStyle.None,Topmost=true,ShowInTaskbar=false,Title="SA overlay",Left=40,Top=40};overlay.Show();
                    IntPtr oh=new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
                    Native.SetWindowLongPtr(oh,-20,(IntPtr)(Native.GetWindowLongPtr(oh,-20).ToInt64()|0x08000000L|0x80L));
                    bool overlayFront=Front(oh);
                    if(overlayFront&&Native.GetForegroundWindow()==oh)
                    {
                        var o=await TextInjector.Resolve(policy,keep,keep);
                        Row("own-overlay-restored",o.Bound&&o.Target!.Window==keep&&o.Last.Source=="restored",new{o.Bound,o.Attempts,o.Ms,o.Last.Source,misses=string.Join(",",o.Misses),o.Refused});
                    }
                    else Row("own-overlay-restored",null,new{skipped="the overlay could not take the front on this desktop"});
                    overlay.Close();
                    Close(keepProc);
                }
                // A window without a text field (a form with one button, another process): reported; it binds like before 0.1.15.
                var form=Process.Start(new ProcessStartInfo("powershell.exe","-NoProfile -Command \"Add-Type -A System.Windows.Forms; $f=New-Object Windows.Forms.Form; $f.Text='SA ohne Feld'; $b=New-Object Windows.Forms.Button; $b.Text='OK'; $f.Controls.Add($b); [void]$f.ShowDialog()\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}).Quiet();
                var (fh,fowner)=await Open(form,"powershell","SA ohne Feld");
                if(fh!=IntPtr.Zero)
                {
                    Front(fh);await Task.Delay(500);
                    var o=await TextInjector.Resolve(policy,fh,fh);
                    Row("no-text-field-window",null,new{o.Bound,o.Attempts,o.Ms,o.Refused,control=o.Target is {} t?TextInjector.Control(t):"",misses=string.Join(",",o.Misses),note="binds as before 0.1.15 (no control-type fence); the dictation history keeps the text"});
                }
                else Row("no-text-field-window",null,new{skipped="form did not appear"});
                Close(fowner??form);
            }
            catch(Exception ex){Row("crash",false,new{error=ex.ToString()});}   // selftest: the console line is the log
            finally
            {
                Console.WriteLine(JsonSerializer.Serialize(new{test="dictation-target",status=failed?"failed":"passed",rows=Rows}));
                exit=failed?1:0;try{Directory.Delete(temp,true);}catch(Exception){ /* a browser still holds its profile: left in %TEMP% */ }
                app.Shutdown();
            }
        };
        app.Run();return exit;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr FindWindow(string? cls,string? title);
    static bool SelfElevated(){using var id=System.Security.Principal.WindowsIdentity.GetCurrent();return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);}
    static Process? Quiet(this Process? p){if(p is not null){p.OutputDataReceived+=(_,_)=>{};p.ErrorDataReceived+=(_,_)=>{};p.BeginOutputReadLine();p.BeginErrorReadLine();}return p;}
    static string Quote(string s)=>"\""+s+"\"";
    // The 0.1.14 bind: one read at the chord; any miss is "no-target".
    static (bool Bound,string Why) Legacy(IntPtr hookWindow,PrivacyPolicy policy)
    {
        try
        {
            if(Native.GetForegroundWindow()!=hookWindow)return (false,"foreground-differs");
            var t=TextInjector.Bind();
            return TextInjector.SafeWhy(t,policy) is {} why?(false,why):(true,"");
        }
        catch(Exception ex){return (false,"error:"+ex.GetType().Name);}
    }
    // Waits up to 30 s for a visible top-level window of the process name whose title contains the marker; returns it and its owner.
    static async Task<(IntPtr Window,Process? Owner)> Open(Process? started,string processName,string title)
    {
        for(int i=0;i<150;i++)
        {
            await Task.Delay(200);
            foreach(var p in Process.GetProcessesByName(processName))
            {
                try{p.Refresh();if(p.MainWindowHandle!=IntPtr.Zero&&p.MainWindowTitle.Contains(title,StringComparison.OrdinalIgnoreCase))return (p.MainWindowHandle,p);}
                catch(Exception){ /* the process ended meanwhile */ }
            }
        }
        return (IntPtr.Zero,started);
    }
    static bool Front(IntPtr h)
    {
        try{DesktopWindows.Focus(h);return true;}
        catch(Exception){return Native.GetForegroundWindow()==h;}   // Focus throws when the window did not come to the front
    }
    static void Close(Process? p){try{if(p is not null&&!p.HasExited)p.Kill(true);}catch(Exception){ /* already gone */ }}
}
