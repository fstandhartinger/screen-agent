using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
namespace ScreenAgent;
// Win32 pieces of computer use that nothing else needs: the second kill path (RegisterHotKey), the mouse hook for hands-on, the owner of the debug port, image paths.
internal static class ComputerNative
{
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr h,int id,uint mods,uint vk);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr h,int id);
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr p,uint flags,StringBuilder name,ref int size);
    [DllImport("iphlpapi.dll",SetLastError=true)] static extern uint GetExtendedTcpTable(IntPtr table,ref int size,bool sort,int family,int cls,uint reserved);
    [StructLayout(LayoutKind.Sequential)] internal struct MouseLl { public Native.Point pt; public uint data,flags,time; public UIntPtr extra; }
    internal const int HotkeyKill=0x5341;
    // Full path of a running image, or "" when it cannot be read (other session, protected process).
    public static string ImagePath(uint pid)
    {
        IntPtr h=OpenProcess(0x1000,false,pid);   // PROCESS_QUERY_LIMITED_INFORMATION
        if(h==IntPtr.Zero)return "";
        try{var sb=new StringBuilder(1024);int n=sb.Capacity;return QueryFullProcessImageName(h,0,sb,ref n)?sb.ToString():"";}
        finally{CloseHandle(h);}
    }
    public static string ImagePathOf(IntPtr hwnd){Native.GetWindowThreadProcessId(hwnd,out uint pid);return ImagePath(pid);}
    // (listening owner pid, set of other pids with an established connection to the port). TCP_TABLE_OWNER_PID_ALL = 5. IPv4 and IPv6 rows.
    public static (uint Owner,List<uint> Clients) PortUsers(int port)
    {
        uint owner=0;var clients=new List<uint>();
        foreach(int family in new[]{2,23})
        {
            int size=0;GetExtendedTcpTable(IntPtr.Zero,ref size,false,family,5,0);
            IntPtr buf=Marshal.AllocHGlobal(size+64);
            try
            {
                size+=64;
                if(GetExtendedTcpTable(buf,ref size,false,family,5,0)!=0)continue;
                int rows=Marshal.ReadInt32(buf);int rowSize=family==2?24:56;
                for(int i=0;i<rows;i++)
                {
                    IntPtr row=buf+4+i*rowSize;
                    uint state,pid;int localPort,remotePort;
                    if(family==2){state=(uint)Marshal.ReadInt32(row);localPort=Port(Marshal.ReadInt32(row,8));remotePort=Port(Marshal.ReadInt32(row,16));pid=(uint)Marshal.ReadInt32(row,20);}
                    else{localPort=Port(Marshal.ReadInt32(row,20));remotePort=Port(Marshal.ReadInt32(row,44));state=(uint)Marshal.ReadInt32(row,48);pid=(uint)Marshal.ReadInt32(row,52);}
                    if(state==2&&localPort==port)owner=pid;                       // LISTEN
                    else if(state==5&&remotePort==port&&pid!=0)clients.Add(pid);   // ESTABLISHED, client side of a connection to the port
                }
            }
            finally{Marshal.FreeHGlobal(buf);}
        }
        return (owner,clients);
    }
    static int Port(int raw)=>((raw&0xff)<<8)|((raw>>8)&0xff);
    public static bool Elevated(IntPtr hwnd)=>TextInjector.Elevated(hwnd);
}
