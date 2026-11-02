using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Input;
using System.Drawing;
using Brushes = System.Windows.Media.Brushes;
namespace ScreenAgent;
internal static class OverlayStyle
{
    // Capture exclusion for every window we own; click-through/no-activate/tool-window only for overlays.
    public static void Apply(Window w,bool clickThrough)
    {
        var h=new WindowInteropHelper(w).Handle;
        long ex=Native.GetWindowLongPtr(h,-20).ToInt64();
        ex|=0x08000000L|0x80L|(clickThrough?0x20L:0L);
        Native.SetWindowLongPtr(h,-20,(IntPtr)ex);
        Native.SetWindowDisplayAffinity(h,0x11);
    }
}
// Small status note in the bottom-right corner. It is click-through, never takes focus, has no taskbar button and is
// on screen only while a session runs or for a few seconds after something happened. It steps aside (fades) when the
// mouse comes near, so it cannot be in the way of the app underneath. The tray icon carries the state the rest of the time.
internal sealed class StatusToast : Window
{
    readonly TextBlock label=new(){Foreground=Brushes.White,FontFamily=new System.Windows.Media.FontFamily("Segoe UI"),FontSize=12,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
    readonly System.Windows.Shapes.Ellipse dot=new(){Width=8,Height=8,Margin=new Thickness(0,0,7,0),VerticalAlignment=VerticalAlignment.Center};
    readonly System.Windows.Threading.DispatcherTimer near=new(){Interval=TimeSpan.FromMilliseconds(150)};
    public StatusToast()
    {
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;Background=Brushes.Transparent;
        Topmost=true;ShowInTaskbar=false;ShowActivated=false;Focusable=false;SizeToContent=SizeToContent.WidthAndHeight;
        var row=new StackPanel{Orientation=Orientation.Horizontal};row.Children.Add(dot);row.Children.Add(label);
        Content=new Border{Background=new SolidColorBrush(System.Windows.Media.Color.FromArgb(225,34,34,40)),CornerRadius=new CornerRadius(8),Padding=new Thickness(10,5,12,5),MaxWidth=420,Child=row};
        SourceInitialized+=(_,_)=>OverlayStyle.Apply(this,true);
        SizeChanged+=(_,_)=>Place();
        near.Tick+=(_,_)=>
        {
            if(!IsVisible)return;
            var h=new WindowInteropHelper(this).Handle;
            if(!Native.GetWindowRect(h,out var r))return;
            var c=System.Windows.Forms.Cursor.Position;int pad=24;
            Opacity=c.X>=r.Left-pad&&c.X<=r.Right+pad&&c.Y>=r.Top-pad&&c.Y<=r.Bottom+pad?0.12:1;
        };
    }
    public static System.Windows.Media.Color Colour(Level level)=>level switch
    {
        Level.Active=>System.Windows.Media.Color.FromRgb(61,220,132),Level.Busy=>System.Windows.Media.Color.FromRgb(77,163,255),
        Level.Paused=>System.Windows.Media.Color.FromRgb(240,200,60),Level.Warn=>System.Windows.Media.Color.FromRgb(255,150,60),
        _=>System.Windows.Media.Color.FromRgb(150,150,160)
    };
    public string Text=>label.Text;
    public void Present(string text,Level level)
    {
        label.Text=text;dot.Fill=new SolidColorBrush(Colour(level));
        if(!IsVisible){near.Start();Show();}
        Place();
    }
    public void Dismiss(){if(IsVisible){near.Stop();Hide();}}
    void Place()
    {
        var hwnd=new WindowInteropHelper(this).EnsureHandle();
        var screen=System.Windows.Forms.Screen.PrimaryScreen??System.Windows.Forms.Screen.AllScreens[0];
        var dpi=VisualTreeHelper.GetDpi(this);
        int w=(int)Math.Ceiling(ActualWidth*dpi.DpiScaleX),h=(int)Math.Ceiling(ActualHeight*dpi.DpiScaleY);
        if(w<=0||h<=0)return;
        var box=WindowPlacement.BottomRight(Px(screen.WorkingArea),w,h,(int)Math.Round(14*dpi.DpiScaleX));
        Native.SetWindowPos(hwnd,IntPtr.Zero,box.X,box.Y,0,0,0x1|0x4|0x10); // NOSIZE | NOZORDER | NOACTIVATE
    }
    internal static PxRect Px(Rectangle r)=>new(r.X,r.Y,r.Width,r.Height);
}
internal sealed class DrawOverlay : Window
{
    readonly Canvas canvas=new(){Background=new SolidColorBrush(System.Windows.Media.Color.FromArgb(1,0,0,0))};
    System.Windows.Shapes.Polyline? current;
    readonly List<List<System.Drawing.PointF>> strokes=[];
    readonly Rectangle monitor;
    readonly double sx,sy;
    public IReadOnlyList<List<System.Drawing.PointF>> Strokes=>strokes;
    public Rectangle Monitor=>monitor;
    public long LastStrokeAt {get;private set;}
    public bool InStroke=>current!=null;
    public void Fade(){IsHitTestVisible=false;BeginAnimation(OpacityProperty,new System.Windows.Media.Animation.DoubleAnimation(1,0,TimeSpan.FromMilliseconds(1500)));}
    public DrawOverlay(System.Windows.Forms.Screen screen)
    {
        monitor=screen.Bounds;
        // Per-monitor DPI is obtained via a tiny HWND on that monitor, then coordinates mapped to DIPs.
        WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;Topmost=true;ShowInTaskbar=false;ShowActivated=false;
        Left=monitor.Left;Top=monitor.Top;Width=monitor.Width;Height=monitor.Height;Content=canvas;
        new WindowInteropHelper(this).EnsureHandle();
        var hwnd=new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(hwnd,(IntPtr)(-1),monitor.Left,monitor.Top,monitor.Width,monitor.Height,0x10);
        uint dpi=GetDpiForWindow(hwnd);sx=sy=(dpi==0?96:dpi)/96.0;
        Left=monitor.Left/sx;Top=monitor.Top/sy;Width=monitor.Width/sx;Height=monitor.Height/sy;
        SourceInitialized+=(_,_)=>OverlayStyle.Apply(this,false);OverlayStyle.Apply(this,false);
        Loaded+=(_,_)=>Native.SetWindowPos(hwnd,(IntPtr)(-1),monitor.Left,monitor.Top,monitor.Width,monitor.Height,0x10);
        canvas.MouseLeftButtonDown+=(_,e)=>
        {
            current=new(){Stroke=Brushes.Magenta,StrokeThickness=5/sx,StrokeLineJoin=PenLineJoin.Round};canvas.Children.Add(current);strokes.Add([]);canvas.CaptureMouse();Add(e.GetPosition(canvas));
        };
        canvas.MouseMove+=(_,e)=>{if(current!=null&&e.LeftButton==MouseButtonState.Pressed)Add(e.GetPosition(canvas));};
        canvas.MouseLeftButtonUp+=(_,_)=>{current=null;LastStrokeAt=Environment.TickCount64;canvas.ReleaseMouseCapture();};
    }
    void Add(System.Windows.Point p){current!.Points.Add(p);strokes[^1].Add(new((float)(p.X*sx),(float)(p.Y*sy)));}
    public void Composite(Bitmap clean)
    {
        using var g=Graphics.FromImage(clean);g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen=new System.Drawing.Pen(System.Drawing.Color.Magenta,5){StartCap=System.Drawing.Drawing2D.LineCap.Round,EndCap=System.Drawing.Drawing2D.LineCap.Round};
        foreach(var stroke in strokes){if(stroke.Count>=2)g.DrawLines(pen,stroke.ToArray());else if(stroke.Count==1)g.FillEllipse(System.Drawing.Brushes.Magenta,stroke[0].X-2.5f,stroke[0].Y-2.5f,5,5);}
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]static extern uint GetDpiForWindow(IntPtr h);
}
