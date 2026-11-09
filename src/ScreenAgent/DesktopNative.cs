using System.Runtime.InteropServices;
namespace ScreenAgent;
internal static class DesktopNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct MonitorInfo {public int Size;public Native.Rect Monitor,Work;public uint Flags;}
    internal delegate bool MonitorProc(IntPtr monitor,IntPtr hdc,ref Native.Rect rect,IntPtr data);
    [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(IntPtr dc,IntPtr clip,MonitorProc proc,IntPtr data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern bool GetMonitorInfo(IntPtr h,ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Native.Point point);
    [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hwnd,uint msg,IntPtr wp,IntPtr lp);
    [DllImport("user32.dll")] internal static extern uint MapVirtualKey(uint code,uint type);
    [DllImport("user32.dll")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
