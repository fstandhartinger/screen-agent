using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
namespace ScreenAgent;
internal static class TextInjector
{
    public sealed record Target(IntPtr Window,AutomationElement Element);
    public static Target Bind()=>new(Native.GetForegroundWindow(),AutomationElement.FocusedElement);
    public static string? Read(AutomationElement root)
    {
        if(root.Current.IsPassword)return null;
        if(root.TryGetCurrentPattern(ValuePattern.Pattern,out var v))return ((ValuePattern)v).Current.Value;
        if(root.TryGetCurrentPattern(TextPattern.Pattern,out var t))return ((TextPattern)t).DocumentRange.GetText(-1).TrimEnd('\r','\n');
        return null;
    }
    public static string Sanitize(string text)=>new(text.Select(c=>c is '\r' or '\n' or '\t'?' ':c).Where(c=>!char.IsControl(c)).ToArray());
    internal static bool Elevated(IntPtr hwnd)=>ElevationRefusal(hwnd) is not null;
    internal static string? ElevationRefusal(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd,out uint pid);
        // An unreadable token fails closed; do not attempt UIPI workarounds.
        try
        {
            using var p=Process.GetProcessById((int)pid);
            if(!OpenProcessToken(p.Handle,8,out var token))return "Notepad process token could not be opened";
            try
            {
                int elevation=0;
                if(!GetTokenInformation(token,20,ref elevation,4,out _))return "Notepad token elevation could not be read";
                return elevation!=0?"Runner launched an elevated Notepad process":null;
            }
            finally{CloseHandle(token);}
        }
        catch(Exception ex){return "Notepad elevation check failed: "+ex.GetType().Name;}
    }
    public static async Task<bool> Inject(Target target,string text,bool clipboard,PrivacyPolicy policy)
    {
        text=Sanitize(text);
        for(int i=0;i<100&&!Native.ModifiersUp;i++)await Task.Delay(10);
        bool safe=Native.ModifiersUp && Native.GetForegroundWindow()==target.Window && !Elevated(target.Window);
        try
        {
            safe &= Automation.Compare(target.Element,AutomationElement.FocusedElement) && !target.Element.Current.IsPassword;
            var monitor=System.Windows.Forms.Screen.FromHandle(target.Window).Bounds;
            var info=new PrivacyCapture(policy).Snapshot(monitor).FirstOrDefault(w=>w.Handle==target.Window);
            safe &= info is not null && !info.Blocked;
        }
        catch{safe=false;}
        if(!safe){SetTemporary(text);return false;} // explicit local clipboard fallback; caller shows a notice
        if(clipboard)
        {
            var saved=Clipboard.GetDataObject();
            SetTemporary(text);uint ours=Native.GetClipboardSequenceNumber();
            try{Native.Send(Native.Key(0x11),Native.Key(0x56),Native.Key(0x56,true),Native.Key(0x11,true));await Task.Delay(400);}
            finally
            {
                if(Native.GetClipboardSequenceNumber()==ours)
                {if(saved is null)Clipboard.Clear();else Clipboard.SetDataObject(saved,true);}
            }
        }
        else
        {
            foreach(char c in text)Native.Send(Native.Key(0,false,c,true),Native.Key(0,true,c,true));
        }
        return true;
    }
    static void SetTemporary(string text)
    {
        var data=new DataObject();data.SetData(DataFormats.UnicodeText,text);
        data.SetData("ExcludeClipboardContentFromMonitorProcessing",new MemoryStream(new byte[]{0,0,0,0}));
        Clipboard.SetDataObject(data,true);
    }
    [System.Runtime.InteropServices.DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr p,uint access,out IntPtr token);
    [System.Runtime.InteropServices.DllImport("advapi32.dll",SetLastError=true)] static extern bool GetTokenInformation(IntPtr t,int info,ref int value,int size,out int len);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr h);
}
