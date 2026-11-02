using System.Runtime.InteropServices;
namespace ScreenAgent;
internal static class Native
{
    internal delegate IntPtr HookProc(int code,IntPtr w,IntPtr l);
    internal delegate bool EnumProc(IntPtr h,IntPtr p);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Msg { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public Point pt; public uint extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct Kbd { public uint vk,scan,flags,time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct Keyboard { public ushort vk,scan; public uint flags,time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct Mouse { public int dx,dy; public uint data,flags,time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Explicit,Size=32)] internal struct InputUnion { [FieldOffset(0)] public Keyboard keyboard; [FieldOffset(0)] public Mouse mouse; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint type; public InputUnion u; }
    [DllImport("user32.dll",SetLastError=true)] internal static extern uint SendInput(uint n,Input[] inputs,int size);
    [DllImport("user32.dll",SetLastError=true)] internal static extern IntPtr SetWindowsHookEx(int id,HookProc proc,IntPtr module,uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr h,int code,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] internal static extern int GetMessage(out Msg msg,IntPtr h,uint min,uint max);
    [DllImport("user32.dll")] internal static extern bool PostThreadMessage(uint id,uint msg,UIntPtr w,IntPtr l);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] internal static extern short GetKeyState(int key);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int cx,int cy,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern bool GetUserObjectInformation(IntPtr h,int index,System.Text.StringBuilder value,int size,out int needed);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc proc,IntPtr p);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr h,out Rect r);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr h,out Rect r);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern int GetWindowText(IntPtr h,System.Text.StringBuilder s,int max);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr h,out uint id);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr h,int attr,out int value,int size);
    [DllImport("user32.dll")] internal static extern bool SetWindowDisplayAffinity(IntPtr h,uint affinity);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr h,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] internal static extern IntPtr SetWindowLongPtr(IntPtr h,int index,IntPtr val);
    [DllImport("user32.dll",SetLastError=true)] internal static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll")] internal static extern bool CloseDesktop(IntPtr h);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="MessageBoxW")] internal static extern int MessageBox(IntPtr owner,string text,string caption,uint type);
    [DllImport("user32.dll")] internal static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] internal static extern bool GetWindowDisplayAffinity(IntPtr h,out uint affinity);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(Point p,uint flags);
    [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(IntPtr monitor,int type,out uint x,out uint y);
    // Scale factor (1.0 = 96 dpi) of the monitor under a device-pixel point; 1.0 if Windows cannot say.
    public static double ScaleAt(int x,int y)
    {
        try{var m=MonitorFromPoint(new Point{X=x,Y=y},2);return m!=IntPtr.Zero&&GetDpiForMonitor(m,0,out uint dx,out _)==0&&dx>0?dx/96.0:1.0;}
        catch(Exception){return 1.0;}
    }
    public static void Send(params Input[] inputs)
    { if(SendInput((uint)inputs.Length,inputs,Marshal.SizeOf<Input>())!=inputs.Length) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"SendInput refused"); }
    // dwExtraInfo tag on every key we inject for ourselves, so the hook can tell them from other tools' keys.
    public static readonly UIntPtr Marker=(UIntPtr)0x5341;
    public static Input Key(ushort key,bool up=false,ushort scan=0,bool unicode=false,UIntPtr extra=default) => new(){type=1,u=new(){keyboard=new(){vk=key,scan=scan,flags=(up?2u:0u)|(unicode?4u:0u),extra=extra}}};
    // Unassigned virtual key: cancels "Win alone opens Start" and "Alt alone focuses the menu bar" for the chord in progress.
    public static void Mask()=>SendInput(2,new[]{Key(0xe8,false,0,false,Marker),Key(0xe8,true,0,false,Marker)},Marshal.SizeOf<Input>());
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window,uint flags);
    // Left button down (0x2) or up (0x4) at the current cursor position; selftests only.
    public static Input Button(bool down)=>new(){type=0,u=new(){mouse=new(){flags=down?2u:4u}}};
    public static void Tap(ushort vk)=>Send(Key(vk),Key(vk,true));
    public static bool Caps => (GetKeyState(0x14)&1)!=0;
    public static void RestoreCaps(bool baseline) { if(Caps!=baseline) Tap(0x14); }
    public static bool ModifiersUp => new[]{0x10,0x11,0x12,0x5b,0x5c}.All(k=>(GetAsyncKeyState(k)&0x8000)==0);
}
