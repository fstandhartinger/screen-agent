using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Windows.Automation;
namespace ScreenAgent;
internal sealed record WindowInfo(IntPtr Handle,string Process,string Title,string? Url,Rectangle Rect,bool Blocked);
// What the frame is about: the foreground window, and with a config list also every visible window the list was checked against.
internal sealed record CaptureInfo(WindowInfo Foreground,List<WindowInfo> Windows);
// Takes the picture. With the default (empty) lists it looks at one window only, the foreground one, for process, title and address.
// Only a lock screen / secure desktop, a foreground window matching a config list, or a real failure yields no frame; LastSkip says which.
internal sealed class PrivacyCapture
{
    readonly PrivacyPolicy policy;
    readonly Func<bool> stillAllowed;
    (IntPtr Window,string Title,long At,string? Url)? addressCache;
    public PrivacyCapture(PrivacyPolicy policy,Func<bool>? stillAllowed=null){this.policy=policy;this.stillAllowed=stillAllowed??(()=>true);}
    // Why the last Capture returned null: not-active | locked | config-list | windows-changed | capture-failed. A reason word only, never a title or URL.
    public string LastSkip {get;private set;}="";
    public static WindowInfo Describe(IntPtr h,PrivacyPolicy policy,bool readUrl,Func<IntPtr,string,string?>? address=null)
    {
        Rectangle rect=Native.GetWindowRect(h,out var r)?Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom):default;
        Native.GetWindowThreadProcessId(h,out uint pid);
        string process="";
        try{using var p=Process.GetProcessById((int)pid);process=p.ProcessName;}catch{ /* a process that cannot be read has no name; it cannot match a list */ }
        var title=new StringBuilder(1024);Native.GetWindowText(h,title,title.Capacity);
        string? url=readUrl&&PrivacyPolicy.Browser(process)?(address??((w,_)=>Address(w)))(h,title.ToString()):null;
        return new(h,process,title.ToString(),url,rect,policy.Blocked(process,title.ToString(),url));
    }
    string? CachedAddress(IntPtr h,string title)
    {
        long now=Environment.TickCount64;
        if(addressCache is {} c&&c.Window==h&&c.Title==title&&now-c.At<10000)return c.Url;
        string? url=Address(h);addressCache=(h,title,now,url);return url;
    }
    // Only used with a config list: every other visible window, so listed ones can be painted black. Unreadable windows are skipped.
    public List<WindowInfo> Snapshot(Rectangle monitor)
    {
        var list=new List<WindowInfo>();
        if(!Native.EnumWindows((h,_)=>
        {
            if(!Native.IsWindowVisible(h))return true;
            if(!Native.GetWindowRect(h,out var r))return true;
            if(Native.DwmGetWindowAttribute(h,14,out int cloaked,4)!=0||cloaked!=0)return true;
            var rect=Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom);if(!rect.IntersectsWith(monitor))return true;
            Native.GetWindowThreadProcessId(h,out uint pid);
            if(pid==Environment.ProcessId)return true; // own overlays are not part of the picture
            string process="";
            try{using var p=Process.GetProcessById((int)pid);process=p.ProcessName;}catch{return true;}
            var info=Describe(h,policy,policy.NeedsAddress(process));
            list.Add(info);
            return true;
        },IntPtr.Zero))throw new InvalidOperationException("EnumWindows failed");
        return list;
    }
    static string? Address(IntPtr h)
    {
        try
        {
            var root=AutomationElement.FromHandle(h);
            var edits=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit));
            foreach(AutomationElement e in edits)
            {
                string id=e.Current.AutomationId,name=e.Current.Name;
                // Only the real omnibox: a page field named "E-Mail-Adresse" must not decide the URL. Anything else, or text being typed, stays unreadable (null, which never blocks).
                if(id is "addressEditBox" or "urlbar-input" || name.StartsWith("Address and search",StringComparison.OrdinalIgnoreCase)||name.StartsWith("Adress- und Such",StringComparison.OrdinalIgnoreCase))
                {
                    if(e.Current.HasKeyboardFocus)return null;
                    if(e.TryGetCurrentPattern(ValuePattern.Pattern,out var v))
                    {
                        string value=((ValuePattern)v).Current.Value;
                        return value.Contains("://")?value:"https://"+value;
                    }
                }
            }
        }
        catch { }
        return null;
    }
    static bool SecureDesktop()
    {
        IntPtr desktop=Native.OpenInputDesktop(0,false,0x0100); // DESKTOP_SWITCHDESKTOP access fails on the secure desktop
        if(desktop==IntPtr.Zero)return true;
        try
        {
            var name=new StringBuilder(256);
            return !Native.GetUserObjectInformation(desktop,2,name,name.Capacity*2,out _)||!name.ToString().Equals("Default",StringComparison.OrdinalIgnoreCase);
        }
        finally{Native.CloseDesktop(desktop);}
    }
    public Bitmap? Capture(Rectangle monitor,bool sessionActive,bool paused,IntPtr underlying=default)=>Capture(monitor,sessionActive,paused,out _,underlying);
    public Bitmap? Capture(Rectangle monitor,bool sessionActive,bool paused,out CaptureInfo? info,IntPtr underlying=default)
    {
        info=null;LastSkip="not-active";
        if(!sessionActive||paused||!stillAllowed())return null;
        Bitmap? image=null;
        try
        {
            if(SecureDesktop()){LastSkip="locked";return null;}
            IntPtr handle=underlying==IntPtr.Zero?Native.GetForegroundWindow():underlying;
            var fg=handle==IntPtr.Zero?new WindowInfo(IntPtr.Zero,"","",null,default,false):Describe(handle,policy,true,CachedAddress);
            if(PrivacyPolicy.LockedHost(fg.Process)){LastSkip="locked";return null;}
            List<WindowInfo> before=[];
            if(policy.HasLists)
            {
                if(fg.Blocked){LastSkip="config-list";return null;}
                before=Snapshot(System.Windows.Forms.SystemInformation.VirtualScreen);
            }
            image=new Bitmap(monitor.Width,monitor.Height,PixelFormat.Format32bppArgb);
            using(var g=Graphics.FromImage(image))
            {
                g.CopyFromScreen(monitor.Location,Point.Empty,monitor.Size,CopyPixelOperation.SourceCopy);
                foreach(var w in before.Where(w=>w.Blocked))
                { var r=Rectangle.Intersect(monitor,w.Rect);r.Offset(-monitor.X,-monitor.Y);g.FillRectangle(Brushes.Black,r); }
            }
            if(!stillAllowed()){image.Dispose();LastSkip="not-active";return null;}
            // With a list: if any window moved, appeared or changed while the pixels were copied, the black boxes may be in the wrong place. Try again next tick.
            if(policy.HasLists&&(!before.SequenceEqual(Snapshot(System.Windows.Forms.SystemInformation.VirtualScreen))||underlying==IntPtr.Zero&&Native.GetForegroundWindow()!=handle))
            {image.Dispose();LastSkip="windows-changed";return null;}
            info=new(fg,before);LastSkip="";
            return image;
        }
        catch{image?.Dispose();info=null;LastSkip="capture-failed";return null;}
    }
    public static byte[] Png(Bitmap image){using var s=new MemoryStream();image.Save(s,ImageFormat.Png);return s.ToArray();}
}
