using ScreenAgent.Core;
namespace ScreenAgent;
// The bridge between the input hooks and the active run. Everything here runs inside a low-level hook callback: Interlocked writes only, no allocation, no I/O, no UI.
// Kill: Ctrl+Win+Shift+X (hook, and RegisterHotKey as a second path). Confirm: Ctrl+Win+Shift+Y, physical keys only. Hands-on: any physical key or mouse input.
internal static class ComputerGuard
{
    static volatile ComputerRun? run;
    static long moved,movedAt;
    static int lastX,lastY;
    public static ComputerRun? Current=>run;
    public static void Attach(ComputerRun? r){run=r;Interlocked.Exchange(ref moved,0);}
    public static void Kill(string via)=>run?.Kill(via);
    static bool Chord()=>(Native.GetAsyncKeyState(0x11)&0x8000)!=0&&(Native.GetAsyncKeyState(0x10)&0x8000)!=0&&((Native.GetAsyncKeyState(0x5b)&0x8000)!=0||(Native.GetAsyncKeyState(0x5c)&0x8000)!=0);
    // injected: LLKHF_INJECTED. The hook passes injected keys here only in selftest mode; production never sees one.
    public static void OnKey(int vk,bool down,bool injected)
    {
        var r=run;
        if(r is null||!down)return;
        if(vk==0x58&&Chord()){r.Kill("hotkey");return;}
        if(vk==0x59&&Chord()&&!injected){if(r.Pending is not null)r.PhysicalConfirm();return;}   // the confirm chord is the user's physical hand: never injected, never swallowed
        if(!injected)r.HandsOn();
    }
    public static void OnMouse(int msg,int x,int y,bool injected)
    {
        var r=run;
        if(r is null||injected)return;
        if(msg is 0x201 or 0x204 or 0x207 or 0x20a){r.HandsOn();return;}   // button down, wheel
        if(msg!=0x200)return;
        long now=Environment.TickCount64;
        if(now-Interlocked.Read(ref movedAt)>500){Interlocked.Exchange(ref moved,0);lastX=x;lastY=y;}
        Interlocked.Exchange(ref movedAt,now);
        long d=Interlocked.Add(ref moved,Math.Abs(x-lastX)+Math.Abs(y-lastY));lastX=x;lastY=y;
        if(d>=240)r.HandsOn();   // a real hand on the mouse, not a twitch
    }
}
