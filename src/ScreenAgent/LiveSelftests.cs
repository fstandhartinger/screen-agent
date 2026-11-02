using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
namespace ScreenAgent;
// End to end on a real Windows desktop: the real app, WebView2, keyboard hook, capture, OCR and frame path against a real Desk (ci/e2e-live.md).
// Only the microphone is a recording. The result is judged afterwards from the session trace. The device token comes from the environment, never from a file in the repo.
internal static class LiveSelftests
{
    [DllImport("user32.dll")]static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")]static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);
    static void Key(ushort vk,bool up=false)=>Native.SendInput(1,[Native.Key(vk,up,0,false,(UIntPtr)0x5442)],Marshal.SizeOf<Native.Input>());
    // Only the public test page and the test Desk are on this runner's screen.
    static void Shot(string name)
    {
        try
        {
            var b=System.Windows.Forms.Screen.PrimaryScreen!.Bounds;using var image=new System.Drawing.Bitmap(b.Width,b.Height);
            using(var g=System.Drawing.Graphics.FromImage(image))g.CopyFromScreen(b.Location,System.Drawing.Point.Empty,b.Size);
            Directory.CreateDirectory("e2e-shots");image.Save(Path.Combine("e2e-shots",name+".png"),System.Drawing.Imaging.ImageFormat.Png);
        }
        catch{ /* evidence only */ }
    }
    static void Step(string text){Console.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss")+" "+text);Console.Out.Flush();}
    // --selftest-live-actions <desk> <vscodeDir> <workDir>: voice to desktop on a real Windows desktop: a recorded microphone says "das soll mein Main Agent erledigen", "mit Claude Code" and asks for a drawing.
    // VS Code Insiders with the real extension sits behind a browser. The result is judged from the box, the panel and the session trace. Nothing is ever submitted; the extension's key is a placeholder.
    public static int Actions(string desk,string vscodeDir,string workDir,string page)
    {
        if(Environment.GetEnvironmentVariable("SA_E2E")!="1")throw new InvalidOperationException("selftest only: SA_E2E=1");
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};int exit=1;var report=new Dictionary<string,object?>();
        using var j=JsonDocument.Parse(Environment.GetEnvironmentVariable("SA_E2E_TOKEN")??throw new InvalidOperationException("SA_E2E_TOKEN missing"));
        var config=new AppConfig{DeskUrl=desk,VoiceProvider=Environment.GetEnvironmentVariable("SA_E2E_PROVIDER")??"xai"};
        using var client=new DeviceClient(config,null,Path.Combine(Path.GetTempPath(),"sa-e2e-device.bin"));
        client.Pair(new(j.RootElement.GetProperty("token").GetString()!,j.RootElement.GetProperty("deviceId").GetString()!,DeviceProtocol.Expiry(j.RootElement.GetProperty("expiresAt"))));
        AgentBridge.TestBrowserArgs=" "+Environment.GetEnvironmentVariable("SA_E2E_ARGS");AgentBridge.AllowMicForTests=true;KeyboardHook.AcceptInjectedForTests=true;
        var bridge=new AgentBridge(config,client);app.MainWindow=bridge;var toast=new StatusToast();
        using var hook=new KeyboardHook(new HotkeyBindings(config.Hotkeys));using var controller=new AppController(config,client,bridge,hook,toast);
        app.Startup+=async (_,_)=>
        {
            try
            {
                Directory.CreateDirectory("e2e-shots");
                string folder=Path.Combine(workDir,"ws-live");Directory.CreateDirectory(folder);string file=Path.Combine(folder,"notes.txt");File.WriteAllText(file,"Zeile eins\n");
                var psi=new System.Diagnostics.ProcessStartInfo(Path.Combine(vscodeDir,"Code - Insiders.exe")){UseShellExecute=false,WorkingDirectory=folder};
                foreach(var a in new[]{"--new-window","--disable-workspace-trust","--skip-release-notes","--skip-welcome","--disable-gpu",folder,file})psi.ArgumentList.Add(a);
                psi.Environment["ANTHROPIC_API_KEY"]="not-a-real-key";psi.Environment.Remove("ELECTRON_RUN_AS_NODE");System.Diagnostics.Process.Start(psi);
                var prompt=new MainAgentPrompt(["Message input"],(_,_)=>{});IntPtr code=IntPtr.Zero;System.Windows.Automation.AutomationElement? box=null;
                for(int i=0;i<180&&box is null;i++)
                {
                    await Task.Delay(500);
                    var hit=MainAgentPrompt.Enumerate().FirstOrDefault(c=>c.Process=="Code - Insiders"&&c.Title.Contains("ws-live"));
                    if(hit.Handle!=0){code=(IntPtr)hit.Handle;box=await Task.Run(()=>prompt.FindInput(code,500));}
                }
                report["boxFoundBefore"]=box is not null;Step($"vscode box {box is not null}");
                bridge.StartHidden();bridge.ShowDesk();
                for(int i=0;i<600&&bridge.SessionId is null;i++)await Task.Delay(100);
                Step($"session {bridge.SessionId is not null}");bridge.Hide();
                // A browser in front: what he talks about is not VS Code, which sits behind it.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("msedge",$"--no-first-run {page}"){UseShellExecute=true});
                await Task.Delay(6000);Shot("1-browser-in-front");
                hook.SetSession(true);
                for(int i=0;i<300&&!bridge.VoiceOn;i++)await Task.Delay(100);
                report["voice"]=bridge.VoiceOn;Step($"voice {bridge.VoiceOn}");
                string? seen=null;bool shotPanel=false;
                for(int t=0;t<165;t+=5)
                {
                    await Task.Delay(5000);
                    var read=await Task.Run(()=>{try{return prompt.FindInput(code,1500) is {} b?MainAgentPrompt.ReadDraft(b):null;}catch{return null;}});
                    if(!string.IsNullOrEmpty(read)&&read!=seen){seen=read;Step($"{t}s box has {read.Length} chars");Shot($"2-box-{t}s");report["boxAtS"]=t;}
                    if(!shotPanel&&controller.CanvasForTests is {IsOpen:true} panel){await Task.Delay(9000);shotPanel=true;try{await panel.CaptureBrowser(Path.GetFullPath("e2e-shots/4-panel-webview.png"));}catch(Exception ex){Step("panel shot "+ex.GetType().Name);}Shot("3-screen-while-panel-open");report["panelAtS"]=t;report["panelAffinityOk"]=panel.AffinityOk;report["panelRect"]=panel.ScreenRect.ToString();Step($"{t}s panel open");}
                }
                report["box"]=seen;report["boxStillHoldsText"]=seen is not null;report["panelOpened"]=shotPanel;Shot("5-end");
                hook.SetSession(false);Step("session off");await Task.Delay(7000);
                exit=bridge.SessionId is not null&&report["voice"] is true?0:1;
            }
            catch(Exception ex){report["error"]=ex.GetType().Name+": "+ex.Message;}
            report["version"]=AppInfo.Version;
            Console.WriteLine(JsonSerializer.Serialize(report));
            foreach(var edge in System.Diagnostics.Process.GetProcessesByName("msedge"))try{edge.Kill();}catch{ /* already gone */ }
            foreach(var c in System.Diagnostics.Process.GetProcessesByName("Code - Insiders"))try{c.Kill();}catch{ /* already gone */ }
            bridge.AllowClose=true;app.Shutdown();
        };
        app.Run();
        return exit;
    }
    public static int Run(string desk,string firstPage,string secondPage)
    {
        if(Environment.GetEnvironmentVariable("SA_E2E")!="1"||!firstPage.StartsWith("https://")||!secondPage.StartsWith("https://"))throw new InvalidOperationException("selftest only: SA_E2E=1 and https pages");
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};int exit=1;var report=new Dictionary<string,object?>();
        using var j=JsonDocument.Parse(Environment.GetEnvironmentVariable("SA_E2E_TOKEN")??throw new InvalidOperationException("SA_E2E_TOKEN missing"));
        var config=new AppConfig{DeskUrl=desk,VoiceProvider="xai"};
        using var client=new DeviceClient(config,null,Path.Combine(Path.GetTempPath(),"sa-e2e-device.bin"));
        client.Pair(new(j.RootElement.GetProperty("token").GetString()!,j.RootElement.GetProperty("deviceId").GetString()!,DeviceProtocol.Expiry(j.RootElement.GetProperty("expiresAt"))));
        AgentBridge.TestBrowserArgs=" "+Environment.GetEnvironmentVariable("SA_E2E_ARGS");AgentBridge.AllowMicForTests=true;KeyboardHook.AcceptInjectedForTests=true;
        var bridge=new AgentBridge(config,client);app.MainWindow=bridge;var toast=new StatusToast();
        using var hook=new KeyboardHook(new HotkeyBindings(config.Hotkeys));using var controller=new AppController(config,client,bridge,hook,toast);
        app.Startup+=async (_,_)=>
        {
            try
            {
                // A runner's WebView2 only initialises behind a shown window; on the laptop the hidden start works (a#7f54a5). The page then goes behind the real page, as in normal use.
                bridge.StartHidden();bridge.ShowDesk();Step("desk shown");
                for(int i=0;i<600&&bridge.SessionId is null;i++)await Task.Delay(100);
                Step($"session {bridge.SessionId is not null} stage {bridge.InitStage} browser {bridge.BrowserReady}");Shot("1-desk");
                bridge.Hide();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("msedge",$"--no-first-run --start-fullscreen {firstPage}"){UseShellExecute=true});
                await Task.Delay(6000);
                report["sid"]=bridge.SessionId is not null;report["stage"]=bridge.InitStage;report["browser"]=bridge.BrowserReady;report["paired"]=client.Paired;report["fallback"]=bridge.FallbackUsed;report["initError"]=bridge.InitErrorType;
                hook.SetSession(true);
                for(int i=0;i<300&&!bridge.VoiceOn;i++)await Task.Delay(100);
                report["voice"]=bridge.VoiceOn;Step($"voice {bridge.VoiceOn}");Shot("2-first-page");
                await bridge.Eval("window.__e2e='?';navigator.mediaDevices.getUserMedia({audio:true}).then(s=>{window.__e2e='mic ok '+s.getAudioTracks().map(t=>t.label).join(',');s.getTracks().forEach(t=>t.stop())},e=>{window.__e2e=e.name+': '+e.message})");
                await Task.Delay(1500);Step("mic "+await bridge.Eval("window.__e2e"));
                await Task.Delay(30_000);
                // The screen changes while the session runs: the agent must get the new page within one interval.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("msedge",$"--new-window --start-fullscreen {secondPage}"){UseShellExecute=true});
                report["pageChangedAtS"]=30;Step("second page");
                await Task.Delay(30_000);Shot("3-second-page");
                // Caps held, a circle, Caps released.
                var screen=System.Windows.Forms.Screen.PrimaryScreen!.Bounds;int cx=screen.Width/2,cy=screen.Height/2,r=Math.Min(screen.Height/6,120);
                Key(0x14);await Task.Delay(500);
                SetCursorPos(cx+r,cy);await Task.Delay(80);mouse_event(2,0,0,0,UIntPtr.Zero);
                for(int a=0;a<=360;a+=15){SetCursorPos(cx+(int)(r*Math.Cos(a*Math.PI/180)),cy+(int)(r*Math.Sin(a*Math.PI/180)));await Task.Delay(25);}
                mouse_event(4,0,0,0,UIntPtr.Zero);await Task.Delay(300);Key(0x14,true);
                report["drawn"]=true;Step("drawn");Shot("4-after-draw");
                await Task.Delay(50_000);
                hook.SetSession(false);Step("session off");
                await Task.Delay(7000);
                exit=report["sid"] is true&&report["voice"] is true?0:1;
            }
            catch(Exception ex){report["error"]=ex.GetType().Name+": "+ex.Message;}
            report["version"]=AppInfo.Version;
            Console.WriteLine(JsonSerializer.Serialize(report));
            // PowerShell's Start-Process -Wait waits for the page's browser too.
            foreach(var edge in System.Diagnostics.Process.GetProcessesByName("msedge"))try{edge.Kill();}catch{ /* already gone */ }
            bridge.AllowClose=true;app.Shutdown();
        };
        app.Run();
        return exit;
    }
}
