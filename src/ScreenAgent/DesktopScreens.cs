using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
namespace ScreenAgent;
internal sealed class DesktopScreens(FreeHost host)
{
    internal sealed record Monitor(int Index,IntPtr Handle,Rectangle Bounds,int Dpi,bool Primary);
    public static List<Monitor> Monitors()
    {
        var list=new List<Monitor>();
        DesktopNative.EnumDisplayMonitors(IntPtr.Zero,IntPtr.Zero,(IntPtr h,IntPtr dc,ref Native.Rect r,IntPtr data)=>
        {
            var info=new DesktopNative.MonitorInfo{Size=Marshal.SizeOf<DesktopNative.MonitorInfo>()};DesktopNative.GetMonitorInfo(h,ref info);
            Native.GetDpiForMonitor(h,0,out uint dpi,out _);list.Add(new(0,h,new(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top),(int)(dpi==0?96:dpi),(info.Flags&1)!=0));return true;
        },IntPtr.Zero);
        return list.OrderByDescending(m=>m.Primary).ThenBy(m=>m.Bounds.Left).Select((m,i)=>m with{Index=i}).ToList();
    }
    public static Rectangle VirtualBounds()=>Monitors().Select(m=>m.Bounds).Aggregate(Rectangle.Union);
    bool grid;
    string area="";
    static Frame Make(FrameKind kind,PixelFrame f,IntPtr hwnd=default)=>new(kind,f.Width,f.Height,f.X,f.Y,f.Width/f.SourceWidth,Hwnd:hwnd,SourceWidth:f.SourceWidth,SourceHeight:f.SourceHeight);
    public (string Jpeg,Frame Frame,string Text) Capture(System.Text.Json.JsonElement args,bool repeat=false)
    {
        host.ThrowIfStopped();var monitors=Monitors();if(monitors.Count==0)throw new InvalidOperationException("Kein Bildschirm verfügbar.");
        Frame f=host.Frame;int max=Math.Clamp(FreeArgs.Int(args,"max")??1280,640,2000);
        if(!repeat)
        {
            grid=FreeArgs.Bool(args,"grid")??false;
            if(FreeArgs.Nums(args,"region",4) is {} crop)
            {
                if(f.Kind is FrameKind.None or FrameKind.Tab)throw new ArgumentException("Für eine Region zuerst einen Desktop-Screenshot aufnehmen.");
                area=$"of {f.Kind.ToString().ToLowerInvariant()} {f.Width}x{f.Height} ({area})";f=Make(FrameKind.Region,f.Pixels.Crop(crop[0],crop[1],crop[2],crop[3],max),f.Hwnd);
            }
            else if(FreeArgs.Str(args,"window") is {} window)
            {
                var h=DesktopWindows.Resolve(window);if(!Native.GetWindowRect(h,out var r))throw new ArgumentException("Fenster nicht gefunden.");
                f=Make(FrameKind.Window,PixelFrame.Fit(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top,max),h);area=UiaDriver.TitleOf(h);
            }
            else if(FreeArgs.Bool(args,"all")==true)
            {
                var b=monitors.Select(m=>m.Bounds).Aggregate(Rectangle.Union);f=Make(FrameKind.Screen,PixelFrame.Fit(b.X,b.Y,b.Width,b.Height,max));area=$"Desktop, {b.Width}x{b.Height}";
            }
            else
            {
                var foreground=Native.GetForegroundWindow();Native.GetWindowRect(foreground,out var r);var point=new Point((r.Left+r.Right)/2,(r.Top+r.Bottom)/2);
                var m=FreeArgs.Int(args,"monitor") is {} index?monitors.FirstOrDefault(m=>m.Index==index)??throw new ArgumentException("Monitor nicht gefunden."):monitors.FirstOrDefault(m=>m.Bounds.Contains(point))??monitors[0];
                f=Make(FrameKind.Screen,PixelFrame.Fit(m.Bounds.X,m.Bounds.Y,m.Bounds.Width,m.Bounds.Height,max));area=$"Monitor {m.Index}, {m.Bounds.Width}x{m.Bounds.Height} @{Math.Round(m.Dpi/96.0*100)} %";
            }
        }
        else if(f.Kind is FrameKind.None or FrameKind.Tab)return Capture(default);
        // Window images follow the window; region images keep the exact physical crop.
        if(repeat&&f.Kind==FrameKind.Window&&Native.GetWindowRect(f.Hwnd,out var wr))f=Make(FrameKind.Window,PixelFrame.Fit(wr.Left,wr.Top,wr.Right-wr.Left,wr.Bottom-wr.Top,Math.Max(f.Width,f.Height)),f.Hwnd);
        using var source=new Bitmap((int)f.Pixels.SourceWidth,(int)f.Pixels.SourceHeight,PixelFormat.Format24bppRgb);
        using(var g=Graphics.FromImage(source)){g.Clear(Color.Black);g.CopyFromScreen((int)f.OriginX,(int)f.OriginY,0,0,source.Size,CopyPixelOperation.SourceCopy);}
        using var image=new Bitmap(f.Width,f.Height,PixelFormat.Format24bppRgb);
        DesktopNative.GetCursorPos(out var cursor);var cp=f.FromTarget(cursor.X,cursor.Y);int cx=(int)Math.Round(cp.X),cy=(int)Math.Round(cp.Y);
        using(var g=Graphics.FromImage(image))
        {
            g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.DrawImage(source,new Rectangle(0,0,f.Width,f.Height));
            using var font=new Font(FontFamily.GenericSansSerif,9,GraphicsUnit.Pixel);using var pen=new Pen(Color.FromArgb(140,255,255,255));
            void Label(string s,int x,int y){var size=g.MeasureString(s,font);g.FillRectangle(Brushes.Black,x,y,size.Width,size.Height);g.DrawString(s,font,Brushes.White,x,y);}
            if(grid){for(int x=100;x<f.Width;x+=100){g.DrawLine(pen,x,0,x,f.Height);Label(x.ToString(),x+2,1);}for(int y=100;y<f.Height;y+=100){g.DrawLine(pen,0,y,f.Width,y);Label(y.ToString(),1,y+2);}}
            if(f.Contains(cx,cy)){using var cross=new Pen(Color.Magenta,2);g.DrawLine(cross,cx-7,cy,cx+7,cy);g.DrawLine(cross,cx,cy-7,cx,cy+7);Label($"{cx},{cy}",Math.Clamp(cx+9,0,Math.Max(0,f.Width-90)),Math.Clamp(cy+9,0,Math.Max(0,f.Height-16)));}
        }
        var codec=ImageCodecInfo.GetImageEncoders().First(c=>c.MimeType=="image/jpeg");string jpeg="";
        for(long quality=70;quality>=10;quality-=10)
        {
            host.ThrowIfStopped();using var stream=new MemoryStream();using var options=new EncoderParameters(1);options.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,quality);image.Save(stream,codec,options);jpeg=Convert.ToBase64String(stream.ToArray());if(jpeg.Length<=1300000)break;
        }
        if(jpeg.Length>1300000)throw new InvalidOperationException("Bild zu groß; bitte max verkleinern.");
        host.Frame=f;var fg=Native.GetForegroundWindow();string text=$"frame: {f.Kind.ToString().ToLowerInvariant()} {f.Width}x{f.Height} {(f.Kind==FrameKind.Region?area:"("+area+")")}\ncursor: {cx},{cy}\nvorn: {UiaDriver.ProcessOf(fg)} \"{UiaDriver.TitleOf(fg)}\"\n";
        text+=string.Join("\n",monitors.Select(m=>$"Monitor {m.Index}: {m.Bounds.Width}x{m.Bounds.Height} @{Math.Round(m.Dpi/96.0*100)} % ({m.Bounds.X},{m.Bounds.Y}){(m.Primary?" primary":"")}"));
        return (jpeg,f,text);
    }
}
internal static class DesktopWindows
{
    public static string Ref(IntPtr h)=>"w"+h.ToInt64().ToString("x");
    public static IntPtr Resolve(string? reference)
    {
        if(reference is null or "foreground")return UiaDriver.Root(Native.GetForegroundWindow());
        if(reference.StartsWith('w')&&long.TryParse(reference[1..],System.Globalization.NumberStyles.HexNumber,null,out long value)&&DesktopNative.IsWindow((IntPtr)value))return (IntPtr)value;
        throw new ArgumentException("Fenster nicht gefunden; ui_windows erneut aufrufen.");
    }
    public static List<IntPtr> List(){var windows=new List<IntPtr>();Native.EnumWindows((h,_)=>{if(Native.IsWindowVisible(h)&&UiaDriver.TitleOf(h).Length>0&&(!Native.DwmGetWindowAttribute(h,14,out int cloaked,4).Equals(0)||cloaked==0))windows.Add(h);return true;},IntPtr.Zero);return windows;}
    public static string Listing()
    {
        var monitors=DesktopScreens.Monitors();return string.Join("\n",List().Select(h=>{Native.GetWindowRect(h,out var r);var m=monitors.FirstOrDefault(m=>m.Bounds.Contains((r.Left+r.Right)/2,(r.Top+r.Bottom)/2));return $"{Ref(h)} {UiaDriver.ProcessOf(h)} \"{UiaDriver.TitleOf(h)}\" {(Native.IsIconic(h)?"min":DesktopNative.IsZoomed(h)?"max":"normal")} [{r.Left},{r.Top},{r.Right-r.Left},{r.Bottom-r.Top}] Monitor {m?.Index??0}{(h==Native.GetForegroundWindow()?" [vorn]":"")}";}));
    }
    public static void Focus(IntPtr h)
    {
        if(Native.IsIconic(h))Native.ShowWindow(h,9);
        var foreground=Native.GetForegroundWindow();uint ft=Native.GetWindowThreadProcessId(foreground,out _),me=Native.GetCurrentThreadId();bool joined=ft!=me&&Native.AttachThreadInput(me,ft,true);
        try{Native.SetForegroundWindow(h);}finally{if(joined)Native.AttachThreadInput(me,ft,false);}
        if(UiaDriver.Root(Native.GetForegroundWindow())!=h)throw new InvalidOperationException("Fenster konnte nicht in den Vordergrund gebracht werden.");
    }
}
