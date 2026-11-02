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
    public static int Run(string desk,string firstPage,string secondPage)
    {
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};int exit=1;var report=new Dictionary<string,object?>();
        using var j=JsonDocument.Parse(Environment.GetEnvironmentVariable("SA_E2E_TOKEN")??throw new InvalidOperationException("SA_E2E_TOKEN missing"));
        var config=new AppConfig{DeskUrl=desk,VoiceProvider="xai"};
        using var client=new DeviceClient(config,null,Path.Combine(Path.GetTempPath(),"sa-e2e-device.bin"));
        client.Pair(new(j.RootElement.GetProperty("token").GetString()!,j.RootElement.GetProperty("deviceId").GetString()!,DeviceProtocol.Expiry(j.RootElement.GetProperty("expiresAt"))));
        AgentBridge.AllowMicForTests=true;KeyboardHook.AcceptInjectedForTests=true;
        var bridge=new AgentBridge(config,client);app.MainWindow=bridge;var toast=new StatusToast();
        using var hook=new KeyboardHook(new HotkeyBindings(config.Hotkeys));using var controller=new AppController(config,client,bridge,hook,toast);
        app.Startup+=async (_,_)=>
        {
            try
            {
                // A runner's WebView2 only initialises behind a shown window; on the laptop the hidden start works (a#7f54a5). The page then goes behind the real page, as in normal use.
                bridge.StartHidden();bridge.ShowDesk();
                for(int i=0;i<600&&bridge.SessionId is null;i++)await Task.Delay(100);
                bridge.Hide();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("msedge",$"--no-first-run --start-fullscreen {firstPage}"){UseShellExecute=true});
                await Task.Delay(6000);
                report["sid"]=bridge.SessionId is not null;report["stage"]=bridge.InitStage;report["browser"]=bridge.BrowserReady;report["paired"]=client.Paired;report["fallback"]=bridge.FallbackUsed;report["initError"]=bridge.InitErrorType;
                report["page"]=await bridge.Eval("location.pathname+location.search+' | '+document.readyState+' | '+(document.body?document.body.innerText.slice(0,160):'')");
                hook.SetSession(true);
                for(int i=0;i<300&&!bridge.VoiceOn;i++)await Task.Delay(100);
                report["voice"]=bridge.VoiceOn;
                await Task.Delay(30_000);
                // The screen changes while the session runs: the agent must get the new page within one interval.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("msedge",$"--new-window --start-fullscreen {secondPage}"){UseShellExecute=true});
                report["pageChangedAtS"]=30;
                await Task.Delay(30_000);
                // Caps held, a circle, Caps released.
                var screen=System.Windows.Forms.Screen.PrimaryScreen!.Bounds;int cx=screen.Width/2,cy=screen.Height/2,r=Math.Min(screen.Height/6,120);
                Key(0x14);await Task.Delay(500);
                SetCursorPos(cx+r,cy);await Task.Delay(80);mouse_event(2,0,0,0,UIntPtr.Zero);
                for(int a=0;a<=360;a+=15){SetCursorPos(cx+(int)(r*Math.Cos(a*Math.PI/180)),cy+(int)(r*Math.Sin(a*Math.PI/180)));await Task.Delay(25);}
                mouse_event(4,0,0,0,UIntPtr.Zero);await Task.Delay(300);Key(0x14,true);
                report["drawn"]=true;
                await Task.Delay(50_000);
                hook.SetSession(false);
                await Task.Delay(7000);
                exit=report["sid"] is true&&report["voice"] is true?0:1;
            }
            catch(Exception ex){report["error"]=ex.GetType().Name+": "+ex.Message;}
            report["version"]=AppInfo.Version;
            Console.WriteLine(JsonSerializer.Serialize(report));
            bridge.AllowClose=true;app.Shutdown();
        };
        app.Run();
        return exit;
    }
}
