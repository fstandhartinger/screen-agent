using System.Collections.Concurrent;
using System.Runtime.InteropServices;
namespace ScreenAgent;
internal sealed class KeyboardHook : IDisposable
{
    readonly ConcurrentQueue<(KeyEvent Event,IntPtr Window)> queue=new();
    readonly CancellationTokenSource stop=new();
    readonly Task consumer;
    readonly Hotkeys state=new();
    readonly Native.HookProc callback;
    readonly Thread thread;
    IntPtr hook;
    uint threadId;
    volatile bool swallowCaps, ctrl,win,baseline,disposed,healthy;
    bool capsOwned;
    readonly System.Threading.Timer watchdog;
    public event System.Action<ActionKind>? Action;
    public event System.Action<IntPtr>? BindTarget;
    public bool Healthy=>healthy;
    public bool Session=>state.Session;
    public bool Paused=>state.Paused;
    public bool Dictating=>state.Dictating;
    public KeyboardHook()
    {
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
        var key=k.vk switch {0x11 or 0xa2 or 0xa3=>Core.Key.Ctrl,0x5b or 0x5c=>Core.Key.Win,0x10 or 0xa0 or 0xa1=>Core.Key.Shift,0x14=>Core.Key.Caps,0x50=>Core.Key.P,0x20=>Core.Key.Space,_=>Core.Key.Other};
        if(key==Core.Key.Ctrl)ctrl=down;
        if(key==Core.Key.Win)win=down;
        // Bounded native-only mask before Win can be released; no UI/OCR/network in this callback.
        if(down && ctrl && win && (key is Core.Key.Ctrl or Core.Key.Win))
            Native.SendInput(2,new[]{Native.Key(0xe8),Native.Key(0xe8,true)},Marshal.SizeOf<Native.Input>());
        queue.Enqueue((new(key,down,Environment.TickCount64),Native.GetForegroundWindow()));
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
            if(a==ActionKind.CapsTap){baseline=!baseline;Native.Tap(0x14);}
            if(a==ActionKind.SessionToggle){if(state.Session)baseline=Native.Caps;else Native.RestoreCaps(baseline);}
            if(a==ActionKind.DictationBind)BindTarget?.Invoke(window);
            else Action?.Invoke(a);
        }
        swallowCaps=state.SwallowCaps;
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;watchdog.Dispose();
        if(threadId!=0)Native.PostThreadMessage(threadId,0x12,UIntPtr.Zero,IntPtr.Zero);
        thread.Join(1000);stop.Cancel();try{consumer.Wait(1000);}catch(AggregateException){}
        if(state.Session)Native.RestoreCaps(baseline);stop.Dispose();
    }
}
