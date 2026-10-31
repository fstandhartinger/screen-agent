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
    IntPtr hook;
    uint threadId;
    volatile bool swallowCaps,baseline,disposed,healthy;
    bool capsOwned;
    readonly System.Threading.Timer watchdog;
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
        baseline=Native.Caps; callback=Callback;
        thread=new Thread(Run){IsBackground=true,Name="ScreenAgent keyboard hook"};thread.Start();
        consumer=Task.Run(async ()=>
        {
            try{while(!stop.IsCancellationRequested){Drain();await Task.Delay(5,stop.Token);}}
            catch(OperationCanceledException){}
        });
        // Reinstall proactively: Windows silently removes timed-out LL hooks, with no query API.
        watchdog=new System.Threading.Timer(_=>{if(threadId!=0)Native.PostThreadMessage(threadId,0x8001,UIntPtr.Zero,IntPtr.Zero);},null,30000,30000);
    }
    void Run()
    {
        threadId=Native.GetCurrentThreadId(); Install();
        while(Native.GetMessage(out var m,IntPtr.Zero,0,0)>0)
        {
            if(m.message==0x8001) Install();
        }
        if(hook!=IntPtr.Zero)Native.UnhookWindowsHookEx(hook);
    }
    void Install()
    {
        if(hook!=IntPtr.Zero)Native.UnhookWindowsHookEx(hook);
        hook=Native.SetWindowsHookEx(13,callback,Native.GetModuleHandle(null),0);healthy=hook!=IntPtr.Zero;
    }
    IntPtr Callback(int code,IntPtr w,IntPtr l)
    {
        if(code<0)return Native.CallNextHookEx(hook,code,w,l);
        var k=Marshal.PtrToStructure<Native.Kbd>(l);
        if((k.flags&0x10)!=0)return Native.CallNextHookEx(hook,code,w,l); // ignore our synthetic events
        bool down=w.ToInt64() is 0x100 or 0x104;
        bool transition=bindings.Feed((int)k.vk,down,out var key);
        // Bounded native-only mask before Win can be released; no UI/OCR/network in this callback.
        if(down && bindings.CtrlWin && bindings.PhysicalWin && (key is Core.Key.Ctrl or Core.Key.Win))
            Native.SendInput(2,new[]{Native.Key(0xe8),Native.Key(0xe8,true)},Marshal.SizeOf<Native.Input>());
        if(transition)queue.Enqueue((new(key,down,Environment.TickCount64),Native.GetForegroundWindow()));
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
        while(queue.TryDequeue(out var e))Apply(state.Feed(e.Event),e.Window);
        Apply(state.Tick(Environment.TickCount64));swallowCaps=state.SwallowCaps;
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
        swallowCaps=state.SwallowCaps;
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;watchdog.Dispose();
        if(threadId!=0)Native.PostThreadMessage(threadId,0x12,UIntPtr.Zero,IntPtr.Zero);
        thread.Join(1000);stop.Cancel();try{consumer.Wait(1000);}catch(AggregateException){}
        if(state.Session&&bindings.DrawVk==0x14)Native.RestoreCaps(baseline);stop.Dispose();
    }
}
