using System.Collections.Concurrent;
using System.Runtime.InteropServices;
namespace ScreenAgent;
internal sealed class KeyboardHook : IDisposable
{
    readonly ConcurrentQueue<(KeyEvent Event,IntPtr Window)> queue=new();
    readonly CancellationTokenSource stop=new();
    readonly Task consumer;
    readonly Hotkeys state=new();
    readonly HotkeyBindings bindings;
    readonly Native.HookProc callback;
    readonly Thread thread;
    IntPtr hook,mouseHook;
    readonly Native.HookProc mouseCallback;
    int installs;
    uint threadId;
    volatile bool mouseHealthy;
    public bool ComputerHotkeyOk {get;private set;}
    public int ComputerHotkeyError {get;private set;}
    volatile bool swallowCaps,muteLive,baseline,disposed,healthy;
    bool capsOwned,muteOwned;
    readonly System.Threading.Timer watchdog,runWatchdog;
    readonly ConcurrentQueue<Func<IReadOnlyList<ActionKind>>> commands=new();
    // Selftests drive the hook with SendInput, which Windows flags as injected. Production ignores injected keys
    // (other tools, remote desktop) and only ever accepts physical ones.
    public static volatile bool AcceptInjectedForTests;
    public event System.Action<ActionKind>? Action;
    public event System.Action<IntPtr>? BindTarget;
    public event System.Action<IntPtr>? BindDrawing;
    public bool Healthy=>healthy;
    public bool Session=>state.Session;
    public bool Paused=>state.Paused;
    public bool Dictating=>state.Dictating;
    public KeyboardHook(HotkeyBindings? bindings=null)
    {
        this.bindings=bindings??new HotkeyBindings(new AppConfig().Hotkeys);
        baseline=Native.Caps; callback=Callback; mouseCallback=MouseCallback;
        thread=new Thread(Run){IsBackground=true,Name="ScreenAgent keyboard hook"};thread.Start();
        consumer=Task.Run(async ()=>
        {
            // One failed event must not end the consumer: without it every hotkey is dead and nothing says why.
            while(!stop.IsCancellationRequested)
            {
                try{Drain();await Task.Delay(5,stop.Token);}
                catch(OperationCanceledException){return;}   /* cancelled */
                catch(Exception ex){Diag.Fail("hook-consumer",ex);try{await Task.Delay(100,stop.Token);}catch(OperationCanceledException){return;}   /* cancelled */}
            }
        });
        // Reinstall proactively: Windows silently removes timed-out LL hooks, with no query API.
        watchdog=new System.Threading.Timer(_=>{if(threadId!=0)Native.PostThreadMessage(threadId,0x8001,UIntPtr.Zero,IntPtr.Zero);},null,30000,30000);
        // During a computer-use run the kill key must not be dead for up to 30 s: reinstall every 5 s.
        runWatchdog=new System.Threading.Timer(_=>{if(threadId!=0&&ComputerGuard.Current is not null)Native.PostThreadMessage(threadId,0x8001,UIntPtr.Zero,IntPtr.Zero);},null,5000,5000);
    }
    // Tray menu: start or stop the session without the chord. Runs on the consumer thread, like key events.
    public void SetSession(bool on)=>commands.Enqueue(()=>state.SetSession(on));
    public void SetPaused(bool on)=>commands.Enqueue(()=>state.SetPaused(on));
    void Run()
    {
        threadId=Native.GetCurrentThreadId(); Install();
        while(Native.GetMessage(out var m,IntPtr.Zero,0,0)>0)
        {
            if(m.message==0x8001) Install();
            else if(m.message==0x8002) RunStarted();
            else if(m.message==0x8003) RunEnded();
            else if(m.message==0x0312&&(int)m.wParam==ComputerNative.HotkeyKill) ComputerGuard.Kill("hotkey-registered");
        }
        RunEnded();
        if(hook!=IntPtr.Zero)Native.UnhookWindowsHookEx(hook);
    }
    void Install()
    {
        if(hook!=IntPtr.Zero&&!Native.UnhookWindowsHookEx(hook))Diag.Event("hook.uninstall.fail",new(){["err"]=Marshal.GetLastWin32Error()});
        bool was=healthy;int err=0;
        hook=Native.SetWindowsHookEx(13,callback,Native.GetModuleHandle(null),0);
        if(hook==IntPtr.Zero)err=Marshal.GetLastWin32Error();
        healthy=hook!=IntPtr.Zero;
        // Reinstalls run every 30 s: only a failure or a change of health is worth a line.
        if(!healthy||!was&&installs>0)Diag.Event("hook.install",new(){["ok"]=healthy,["err"]=err,["was"]=was,["installs"]=installs});
        installs++;
        if(mouseHook!=IntPtr.Zero)InstallMouse();
    }
    // Computer use: a mouse hook (hands-on) and RegisterHotKey for Ctrl+Win+Shift+X as the kill key's second, independent path. Both only while a run exists.
    public void ComputerRunStarted(){if(threadId!=0)Native.PostThreadMessage(threadId,0x8002,UIntPtr.Zero,IntPtr.Zero);}
    public void ComputerRunEnded(){if(threadId!=0)Native.PostThreadMessage(threadId,0x8003,UIntPtr.Zero,IntPtr.Zero);}
    public bool MouseHookHealthy=>mouseHealthy;
    void InstallMouse()
    {
        if(mouseHook!=IntPtr.Zero)Native.UnhookWindowsHookEx(mouseHook);
        mouseHook=Native.SetWindowsHookEx(14,mouseCallback,Native.GetModuleHandle(null),0);mouseHealthy=mouseHook!=IntPtr.Zero;
    }
    void RunStarted()
    {
        InstallMouse();
        ComputerHotkeyOk=ComputerNative.RegisterHotKey(IntPtr.Zero,ComputerNative.HotkeyKill,0x2|0x4|0x8|0x4000,0x58);ComputerHotkeyError=ComputerHotkeyOk?0:Marshal.GetLastWin32Error();   // Ctrl+Shift+Win+X, no repeat; false = another program owns the chord
    }
    void RunEnded()
    {
        if(mouseHook!=IntPtr.Zero){Native.UnhookWindowsHookEx(mouseHook);mouseHook=IntPtr.Zero;mouseHealthy=false;}
        if(ComputerHotkeyOk){ComputerNative.UnregisterHotKey(IntPtr.Zero,ComputerNative.HotkeyKill);ComputerHotkeyOk=false;}
    }
    IntPtr MouseCallback(int code,IntPtr w,IntPtr l)
    {
        if(code<0)return Native.CallNextHookEx(mouseHook,code,w,l);
        var m=Marshal.PtrToStructure<ComputerNative.MouseLl>(l);
        ComputerGuard.OnMouse((int)w.ToInt64(),m.pt.X,m.pt.Y,(m.flags&1)!=0);
        return Native.CallNextHookEx(mouseHook,code,w,l);
    }
    IntPtr Callback(int code,IntPtr w,IntPtr l)
    {
        if(code<0)return Native.CallNextHookEx(hook,code,w,l);
        var k=Marshal.PtrToStructure<Native.Kbd>(l);
        if(k.extra==Native.Marker)return Native.CallNextHookEx(hook,code,w,l); // our own mask key
        if((k.flags&0x10)!=0&&!AcceptInjectedForTests)return Native.CallNextHookEx(hook,code,w,l); // other tools' synthetic keys
        bool down=w.ToInt64() is 0x100 or 0x104;
        ComputerGuard.OnKey((int)k.vk,down,(k.flags&0x10)!=0);   // kill, confirm and hands-on: flags only, in the callback
        if(down)
            foreach(var (_,role) in bindings.Resync(vk=>(Native.GetAsyncKeyState(vk)&0x8000)!=0,(int)k.vk))
                queue.Enqueue((new(role,false,Environment.TickCount64),Native.GetForegroundWindow()));
        bool transition=bindings.Feed((int)k.vk,down,out var key);
        // One bounded native-only mask when OUR chord completes; never for foreign chords such as Ctrl+Win (Wispr).
        // No UI/OCR/network in this callback.
        if(bindings.MaskNow)Native.Mask();
        if(transition)queue.Enqueue((new(key,down,Environment.TickCount64),Native.GetForegroundWindow()));
        if(key is Core.Key.Mute or Core.Key.Canvas)
        {
            // Eat the M / D of Ctrl+Win+Shift+M / +D only while it acts (session running): Ctrl+Shift+M still reaches other apps otherwise.
            if(down)muteOwned=muteLive&&bindings.Is(key==Core.Key.Mute?bindings.Layout.Mute:bindings.Layout.Canvas);
            bool eat=muteOwned;
            if(!down)muteOwned=false;
            if(eat)return (IntPtr)1;
        }
        if(key==Core.Key.Caps)
        {
            if(down)capsOwned=swallowCaps;
            bool eat=capsOwned;
            if(!down)capsOwned=false;
            if(eat)return (IntPtr)1;
        }
        return Native.CallNextHookEx(hook,code,w,l);
    }
    // Dedicated lightweight consumer: UIA/OCR never delay hotkey state processing.
    void Drain()
    {
        while(commands.TryDequeue(out var c))Apply(c());
        while(queue.TryDequeue(out var e))Apply(state.Feed(e.Event),e.Window);
        Apply(state.Tick(Environment.TickCount64));swallowCaps=state.SwallowCaps;muteLive=state.MuteLive;
    }
    void Apply(IReadOnlyList<ActionKind> actions,IntPtr window=default)
    {
        foreach(var a in actions)
        {
            if(a==ActionKind.CapsTap){if(bindings.DrawVk==0x14)baseline=!baseline;Native.Tap((ushort)bindings.DrawVk);}
            if(a==ActionKind.SessionToggle&&bindings.DrawVk==0x14){if(state.Session)baseline=Native.Caps;else Native.RestoreCaps(baseline);}
            if(a==ActionKind.DrawBind)BindDrawing?.Invoke(window);
            else if(a==ActionKind.DictationBind)BindTarget?.Invoke(window);
            else Action?.Invoke(a);
        }
        swallowCaps=state.SwallowCaps;muteLive=state.MuteLive;
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;watchdog.Dispose();runWatchdog.Dispose();
        if(threadId!=0)Native.PostThreadMessage(threadId,0x12,UIntPtr.Zero,IntPtr.Zero);
        thread.Join(1000);stop.Cancel();try{consumer.Wait(1000);}catch(AggregateException){ /* the consumer ended by cancel */ }
        if(state.Session&&bindings.DrawVk==0x14)Native.RestoreCaps(baseline);stop.Dispose();
    }
}
