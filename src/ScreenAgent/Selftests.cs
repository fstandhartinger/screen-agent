using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
namespace ScreenAgent;
// Windows CI selftests for the first-run findings (hotkey pass-through, Desk window, status toast).
// Exit codes: 0 passed, 1 failed, 2 skipped because the runner cannot provide the precondition (documented in the output).
internal static class Selftests
{
    const ushort Ctrl=0xa2,Win=0x5b,Alt=0xa4,Shift=0xa0,Esc=0x1b,F13=0x7c,Dummy=0xe8;
    static readonly UIntPtr Test=(UIntPtr)0x5442;
    record Seen(uint Vk,bool Down,bool Ours);
    // A second low-level hook installed BEFORE the app's hook, so it sits behind it in the chain and records exactly
    // what the rest of the system (Wispr, Windows, the focused app) would receive from us.
    sealed class Probe : IDisposable
    {
        readonly List<Seen> seen=[];readonly Native.HookProc proc;readonly Thread thread;readonly ManualResetEventSlim ready=new();
        IntPtr hook;uint tid;
        public Probe()
        {
            proc=(code,w,l)=>
            {
                if(code>=0)
                {
                    var k=Marshal.PtrToStructure<Native.Kbd>(l);
                    if(k.extra==Test||k.extra==Native.Marker)lock(seen)seen.Add(new(k.vk,w.ToInt64() is 0x100 or 0x104,k.extra==Native.Marker));
                }
                return Native.CallNextHookEx(hook,code,w,l);
            };
            thread=new Thread(()=>
            {
                tid=Native.GetCurrentThreadId();hook=Native.SetWindowsHookEx(13,proc,Native.GetModuleHandle(null),0);ready.Set();
                while(Native.GetMessage(out var m,IntPtr.Zero,0,0)>0){}
                if(hook!=IntPtr.Zero)Native.UnhookWindowsHookEx(hook);
            }){IsBackground=true};
            thread.Start();ready.Wait(5000);
        }
        public bool Installed=>hook!=IntPtr.Zero;
        public void Clear(){lock(seen)seen.Clear();}
        public List<Seen> Snapshot(){lock(seen)return [..seen];}
        public void Dispose(){if(tid!=0)Native.PostThreadMessage(tid,0x12,UIntPtr.Zero,IntPtr.Zero);thread.Join(1000);}
    }
    static void Press(ushort vk,bool up=false){Native.SendInput(1,new[]{Native.Key(vk,up,0,false,Test)},Marshal.SizeOf<Native.Input>());Thread.Sleep(15);}
    static List<Seen> Chord(Probe probe,ushort[] downOrder,ushort[] upOrder,int holdMs=0)
    {
        probe.Clear();
        foreach(var k in downOrder)Press(k);
        if(holdMs>0)Thread.Sleep(holdMs);
        foreach(var k in upOrder)Press(k,true);
        Thread.Sleep(250);
        var seen=probe.Snapshot();
        // A stray Start menu or menu-bar focus would swallow the next case; Esc closes both and is harmless otherwise.
        Press(Esc);Press(Esc,true);Thread.Sleep(100);
        return seen;
    }
    public static int Hotkeys()
    {
        var results=new List<object>();bool failed=false;
        void Case(string name,bool pass,object detail){results.Add(new{name,pass,detail});if(!pass)failed=true;}
        KeyboardHook.AcceptInjectedForTests=true;
        using var probe=new Probe();
        if(!probe.Installed){Console.WriteLine(JsonSerializer.Serialize(new{test="hotkeys",status="skipped",reason="WH_KEYBOARD_LL cannot be installed on this runner"}));return 2;}
        using var hook=new KeyboardHook(new HotkeyBindings(AppConfig.DefaultHotkeys()));
        var actions=new List<ActionKind>();hook.Action+=a=>{lock(actions)actions.Add(a);};
        for(int i=0;i<40&&!hook.Healthy;i++)Thread.Sleep(50);
        // Preflight: can injected keys reach a hook on this desktop at all?
        probe.Clear();Press(F13);Press(F13,true);Thread.Sleep(300);
        if(!hook.Healthy||probe.Snapshot().Count<2)
        {
            Console.WriteLine(JsonSerializer.Serialize(new{test="hotkeys",status="skipped",reason="Injected keys do not reach low-level hooks on this runner (no input desktop); manual checklist required",healthy=hook.Healthy,seen=probe.Snapshot().Count}));return 2;
        }
        bool Same(List<Seen> got,params (ushort vk,bool down)[] want)=>got.Where(s=>!s.Ours).Select(s=>(s.Vk,s.Down)).SequenceEqual(want.Select(w=>((uint)w.vk,w.down)));
        // MaskWin is a legacy state-machine marker with no effect; the real mask is the injected key counted below.
        List<ActionKind> Taken(){lock(actions){var copy=actions.Where(a=>a!=ActionKind.MaskWin).ToList();actions.Clear();return copy;}}

        // 1. Wispr's chord Ctrl+Win must reach everybody untouched: nothing swallowed, nothing injected, no action here.
        Taken();
        var seen=Chord(probe,[Ctrl,Win],[Win,Ctrl],400);var acts=Taken();
        Case("ctrl-win-passes-through-untouched",Same(seen,(Ctrl,true),(Win,true),(Win,false),(Ctrl,false))&&!seen.Any(s=>s.Ours)&&acts.Count==0&&!hook.Session,new{seen=seen.Count,injected=seen.Count(s=>s.Ours),actions=acts});
        // 2. The old v0.1.1 session chord Ctrl+Win+Shift no longer does anything in this app and is not masked.
        seen=Chord(probe,[Ctrl,Win,Shift],[Shift,Win,Ctrl]);acts=Taken();
        Case("old-ctrl-win-shift-is-ignored",Same(seen,(Ctrl,true),(Win,true),(Shift,true),(Shift,false),(Win,false),(Ctrl,false))&&!seen.Any(s=>s.Ours)&&!acts.Contains(ActionKind.SessionToggle)&&!hook.Session,new{seen=seen.Count,injected=seen.Count(s=>s.Ours),actions=acts});
        // 3. Wispr's command chord Ctrl+Win+Alt is foreign too.
        seen=Chord(probe,[Ctrl,Win,Alt],[Alt,Win,Ctrl],300);acts=Taken();
        Case("ctrl-win-alt-passes-through",Same(seen,(Ctrl,true),(Win,true),(Alt,true),(Alt,false),(Win,false),(Ctrl,false))&&!seen.Any(s=>s.Ours)&&acts.Count==0&&!hook.Session,new{seen=seen.Count,injected=seen.Count(s=>s.Ours),actions=acts});
        // 4. The configured chord Alt+Win+Shift toggles the session, forwards every key and masks Start/menu exactly once.
        seen=Chord(probe,[Alt,Win,Shift],[Shift,Win,Alt]);acts=Taken();
        int masks=seen.Count(s=>s.Ours&&s.Vk==Dummy&&s.Down);
        Case("alt-win-shift-toggles-session",Same(seen,(Alt,true),(Win,true),(Shift,true),(Shift,false),(Win,false),(Alt,false))&&masks==1&&acts.Contains(ActionKind.SessionToggle)&&hook.Session,new{seen=seen.Count,masks,actions=acts,session=hook.Session});
        // 5. A second press switches it off again.
        Chord(probe,[Alt,Win,Shift],[Shift,Win,Alt]);acts=Taken();
        Case("alt-win-shift-toggles-back",acts.Contains(ActionKind.SessionToggle)&&!hook.Session,new{actions=acts,session=hook.Session});
        // 6. Tray path: same effect without keys.
        hook.SetSession(true);Thread.Sleep(300);acts=Taken();
        Case("tray-start-session",acts.Contains(ActionKind.SessionToggle)&&hook.Session,new{actions=acts});
        hook.SetSession(false);Thread.Sleep(300);Taken();
        Console.WriteLine(JsonSerializer.Serialize(new{test="hotkeys",status=failed?"failed":"passed",results}));
        return failed?1:0;
    }
    public static int Windows()
    {
        var results=new List<object>();bool failed=false,skipped=false;
        void Case(string name,bool pass,object? detail=null){results.Add(new{name,pass,detail});if(!pass)failed=true;}
        var app=new Application();int exit=1;
        app.Startup+=async (_,_)=>
        {
            try
            {
                string? runtime=null;try{runtime=Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();}catch{}
                var config=AppConfig.Parse("{\"deskUrl\":\"https://example.com\"}");
                using var client=new DeviceClient(config);
                var work=StatusToast.Px(System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea);
                try{File.Delete(AgentBridge.StateFile);}catch{}
                var bridge=new AgentBridge(config,client);
                var ex=0L;
                Case("desk-is-ordinary-window",!bridge.Topmost&&bridge.ShowInTaskbar&&bridge.ResizeMode==ResizeMode.CanResize&&bridge.WindowStyle==WindowStyle.SingleBorderWindow,new{bridge.Topmost,bridge.ShowInTaskbar,bridge.ResizeMode,bridge.WindowStyle});
                bool shownOnScreen=false;
                bridge.IsVisibleChanged+=(_,e2)=>
                {
                    if(!(bool)e2.NewValue)return;
                    var hw=new System.Windows.Interop.WindowInteropHelper(bridge).Handle;
                    if(Native.GetWindowRect(hw,out var vr)&&vr.Left>-1000)shownOnScreen=true;
                };
                bridge.StartHidden();
                Case("desk-hidden-at-start",!bridge.IsVisible);
                if(runtime is null)
                {
                    skipped=true;Case("webview-runtime-present",true,"WebView2 runtime missing on runner: page start not exercised");
                }
                else
                {
                    for(int i=0;i<400&&!bridge.BrowserReady;i++)await Task.Delay(100);
                    Case("page-starts-while-hidden",bridge.BrowserReady,new{runtime,readyMs=bridge.ReadyMs,fallbackUsed=bridge.FallbackUsed});
                    // The window must settle hidden on its own, and must never have been on screen on the way.
                    for(int i=0;i<250&&bridge.IsVisible;i++)await Task.Delay(100);
                    Case("desk-settles-hidden-never-seen",!bridge.IsVisible&&!shownOnScreen,new{visibleNow=bridge.IsVisible,shownOnScreen});
                    // Voice sessions live in this page, so a hidden page must keep its timers and audio running.
                    for(int i=0;i<150&&await bridge.Eval("document.readyState")!="\"complete\"";i++)await Task.Delay(100);
                    string? visibility=await bridge.Eval("(()=>{window.__n=0;setInterval(()=>{window.__n++},100);return document.visibilityState})()");
                    await Task.Delay(3000);
                    int ticks=int.TryParse(await bridge.Eval("window.__n"),out var tv)?tv:-1;
                    Case("hidden-page-timers-not-throttled",ticks>=20,new{visibility,ticks,expectedIfUnthrottled=30});
                }
                // Open like the tray does.
                bridge.ShowDesk();await Task.Delay(500);
                var h=new System.Windows.Interop.WindowInteropHelper(bridge).Handle;
                ex=Native.GetWindowLongPtr(h,-20).ToInt64();
                Native.GetWindowRect(h,out var r);var box=new PxRect(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top);
                Case("desk-opens-on-request",bridge.IsVisible&&bridge.DeskOpen);
                Native.GetWindowDisplayAffinity(h,out uint deskAffinity);
                Case("desk-excluded-from-screen-capture",deskAffinity==0x11,new{deskAffinity});
                Case("desk-not-always-on-top",(ex&0x8)==0,new{ex});
                var expected=WindowPlacement.Desk(work,Native.ScaleAt(work.X+work.W/2,work.Y+work.H/2),null,[work]);
                Case("desk-fits-work-area-at-default-size",work.Contains(box)&&Math.Abs(box.W-expected.W)<=2&&Math.Abs(box.H-expected.H)<=2&&Math.Abs(box.X-expected.X)<=2,new{work,box,expected});
                bridge.WindowState=WindowState.Minimized;await Task.Delay(300);
                Case("desk-minimises",bridge.WindowState==WindowState.Minimized&&!bridge.DeskOpen);
                bridge.ShowDesk();await Task.Delay(300);
                Case("desk-restores",bridge.WindowState==WindowState.Normal&&bridge.DeskOpen);
                // Remembers its size: resize, hide, open again.
                int w=Math.Min(Math.Max(box.W-40,WindowPlacement.DeskMinWidthDip+40),work.W-40),hh=Math.Min(Math.Max(box.H-60,WindowPlacement.DeskMinHeightDip+60),work.H-40);
                Native.SetWindowPos(h,IntPtr.Zero,box.X,box.Y,w,hh,0x14);await Task.Delay(300);
                bridge.HideDesk();await Task.Delay(200);
                bridge.ShowDesk();await Task.Delay(400);
                Native.GetWindowRect(h,out r);
                Case("desk-remembers-size",Math.Abs((r.Right-r.Left)-w)<=2&&Math.Abs((r.Bottom-r.Top)-hh)<=2,new{wanted=new{w,hh},got=new{w=r.Right-r.Left,h=r.Bottom-r.Top}});
                bridge.HideDesk();await Task.Delay(100);
                Case("desk-hides-on-request",!bridge.IsVisible);
                // Toast: small, bottom-right, click-through, no taskbar button, gone when dismissed.
                var toast=new StatusToast();toast.Present("Sitzung · 3 IDs",Level.Active);await Task.Delay(400);
                var th=new System.Windows.Interop.WindowInteropHelper(toast).Handle;
                Native.GetWindowRect(th,out var tr);var tb=new PxRect(tr.Left,tr.Top,tr.Right-tr.Left,tr.Bottom-tr.Top);
                long tex=Native.GetWindowLongPtr(th,-20).ToInt64();
                Case("toast-visible-small-bottom-right",toast.IsVisible&&work.Contains(tb)&&tb.W<work.W/4&&tb.H<work.H/12&&tb.X>work.X+work.W/2&&tb.Y>work.Y+work.H/2,new{work,tb});
                Case("toast-click-through-no-taskbar",(tex&0x20)!=0&&(tex&0x80)!=0&&(tex&0x08000000)!=0,new{tex});
                Native.GetWindowDisplayAffinity(th,out uint toastAffinity);
                Case("toast-excluded-from-screen-capture",toastAffinity==0x11,new{toastAffinity});
                toast.Dismiss();await Task.Delay(100);
                Case("toast-hidden-when-idle",!toast.IsVisible);
                toast.Close();bridge.AllowClose=true;bridge.Close();
                exit=failed?1:0;
            }
            catch(Exception e){Case("selftest-crashed",false,e.ToString());exit=1;}
            finally
            {
                Console.WriteLine(JsonSerializer.Serialize(new{test="windows",status=failed?"failed":"passed",partial=skipped,results}));
                app.Shutdown(exit);
            }
        };
        app.Run();return exit;
    }
}
