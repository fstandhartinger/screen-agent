using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Windows.Automation;
namespace ScreenAgent;
internal sealed record WindowInfo(IntPtr Handle,string Process,string Title,string? Url,Rectangle Rect,bool Blocked,bool IdsOnly=false);
internal sealed class PrivacyCapture
{
    readonly PrivacyPolicy policy;
    readonly Func<bool> stillAllowed;
    public PrivacyCapture(PrivacyPolicy policy,Func<bool>? stillAllowed=null){this.policy=policy;this.stillAllowed=stillAllowed??(()=>true);}
    public List<WindowInfo> Snapshot(Rectangle monitor)
    {
        var list=new List<WindowInfo>();
        bool incomplete=false;
        bool enumerated=Native.EnumWindows((h,_)=>
        {
            if(!Native.IsWindowVisible(h))return true;
            if(!Native.GetWindowRect(h,out var r)){incomplete=true;return true;}
            if(Native.DwmGetWindowAttribute(h,14,out int cloaked,4)!=0){incomplete=true;return true;} if(cloaked!=0)return true;
            var rect=Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom);if(!rect.IntersectsWith(monitor))return true;
            Native.GetWindowThreadProcessId(h,out uint pid);
            if(pid==Environment.ProcessId)return true; // own overlays are excluded, not privacy targets
            string process;
            try{using var p=Process.GetProcessById((int)pid);process=p.ProcessName;}catch{list.Add(new(h,"","",null,rect,true));return true;}
            var title=new StringBuilder(1024);Native.GetWindowText(h,title,title.Capacity);
            string? url=PrivacyPolicy.Browser(process)?Address(h):null;
            var mode=policy.Classify(process,title.ToString(),url);
            list.Add(new(h,process,title.ToString(),url,rect,mode==PrivacyClass.Blocked,mode==PrivacyClass.IdsOnly));
            return true;
        },IntPtr.Zero);
        if(!enumerated||incomplete)throw new InvalidOperationException("Fensterdaten nicht verfügbar");
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
                if(id is "addressEditBox" or "urlbar-input" || name.Contains("address",StringComparison.OrdinalIgnoreCase)||name.Contains("Adresse",StringComparison.OrdinalIgnoreCase))
                {
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
    public Bitmap? Capture(Rectangle monitor,bool sessionActive,bool paused,IntPtr underlying=default,bool forUpload=false)
    {
        if(!sessionActive||paused||!stillAllowed())return null;
        IntPtr desktop=Native.OpenInputDesktop(0,false,0x0100); // DESKTOP_SWITCHDESKTOP access fails on secure desktop
        if(desktop==IntPtr.Zero)return null;
        try
        {
            var name=new StringBuilder(256);
            if(!Native.GetUserObjectInformation(desktop,2,name,name.Capacity*2,out _) || !name.ToString().Equals("Default",StringComparison.OrdinalIgnoreCase))return null;
        }
        finally{Native.CloseDesktop(desktop);}
        IntPtr foreground=underlying==IntPtr.Zero?Native.GetForegroundWindow():underlying;
        var before=Snapshot(System.Windows.Forms.SystemInformation.VirtualScreen);var fg=before.FirstOrDefault(w=>w.Handle==foreground);
        if(fg is null || fg.Blocked || PrivacyPolicy.TransientHost(fg.Process,fg.Title))return null;
        if(before.Any(w=>PrivacyPolicy.TransientHost(w.Process,w.Title)))return null;
        var image=new Bitmap(monitor.Width,monitor.Height,PixelFormat.Format32bppArgb);
        try
        {
            if(!stillAllowed()){image.Dispose();return null;}
            using(var g=Graphics.FromImage(image))
            {
                g.CopyFromScreen(monitor.Location,Point.Empty,monitor.Size,CopyPixelOperation.SourceCopy);
                foreach(var w in before.Where(w=>w.Blocked||(forUpload&&w.IdsOnly)))
                { var r=Rectangle.Intersect(monitor,w.Rect);r.Offset(-monitor.X,-monitor.Y);g.FillRectangle(Brushes.Black,r); }
            }
            var after=Snapshot(System.Windows.Forms.SystemInformation.VirtualScreen);
            // Stronger than title/URL-only recheck: any visible-window identity/geometry/order change discards.
            if(!stillAllowed()||!before.SequenceEqual(after)||(underlying==IntPtr.Zero && Native.GetForegroundWindow()!=foreground))
            {image.Dispose();return null;}
            return image;
        }
        catch{image.Dispose();throw;}
    }
    public static byte[] Png(Bitmap image){using var s=new MemoryStream();image.Save(s,ImageFormat.Png);return s.ToArray();}
}
