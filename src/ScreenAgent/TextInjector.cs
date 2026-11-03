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
        catch(Exception ex){Diag.Fail("elevation-check",ex);return "Notepad elevation check failed: "+ex.GetType().Name;}
    }
    public static async Task<bool> Inject(Target target,string text,bool clipboard,PrivacyPolicy policy,InputGate? gate=null)
    {
        text=Sanitize(text);
        // A Main Agent prompt being typed finishes first (30 s at most); if it does not, the text goes to the clipboard like any unsafe target.
        using var slot=gate is null?null:await gate.BeginInject(TimeSpan.FromSeconds(30));
        if(gate is not null&&slot is null){Refused("prompt-busy",text);SetTemporary(text);return false;}
        for(int i=0;i<100&&!Native.ModifiersUp;i++)await Task.Delay(10);
        string? why=!Native.ModifiersUp?"modifiers-down":SafeWhy(target,policy);
        if(why is not null){Refused(why,text);SetTemporary(text);return false;} // explicit local clipboard fallback; caller shows a notice
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
    public static bool Terminal(IntPtr window)
    {
        Native.GetWindowThreadProcessId(window,out uint pid);
        try{using var p=Process.GetProcessById((int)pid);return p.ProcessName is "WindowsTerminal" or "conhost" or "OpenConsole" or "cmd" or "powershell" or "pwsh";}catch(Exception){return true;}   /* unreadable process: treated as a terminal */
    }
    public static string ProcessName(IntPtr window)
    {
        Native.GetWindowThreadProcessId(window,out uint pid);using var p=Process.GetProcessById((int)pid);return p.ProcessName;
    }
    public static bool Safe(Target target,PrivacyPolicy policy)=>SafeWhy(target,policy) is null;
    // Why a target is refused (null = safe): target-changed | elevated | focus-changed | password | blocked-window | locked-host | error:<type>.
    public static string? SafeWhy(Target target,PrivacyPolicy policy)
    {
        try
        {
            if(Native.GetForegroundWindow()!=target.Window)return "target-changed";
            if(Elevated(target.Window))return "elevated";
            if(!Automation.Compare(target.Element,AutomationElement.FocusedElement))return "focus-changed";
            if(target.Element.Current.IsPassword)return "password";
            // Password fields and elevated windows above are the real dictation fences; the config lists (empty by default) can add windows.
            var info=PrivacyCapture.Describe(target.Window,policy,policy.ExtraDomains.Length>0);
            if(info.Blocked)return "blocked-window";
            return PrivacyPolicy.LockedHost(info.Process)?"locked-host":null;
        }
        catch(Exception ex){Diag.Fail("inject-safe",ex);return "error:"+ex.GetType().Name;}   // unreadable target fails closed
    }
    // inject.refused {why, chars}: the text went to the clipboard instead. Never the text itself.
    static void Refused(string why,string text)=>Diag.Event("inject.refused",new(){["why"]=why,["chars"]=text.Length});
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
