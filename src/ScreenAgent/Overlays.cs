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
    public static bool Apply(Window w,bool clickThrough,bool excludeFromCapture=true)
    {
        var h=new WindowInteropHelper(w).Handle;
        long ex=Native.GetWindowLongPtr(h,-20).ToInt64();
        ex|=0x08000000L|0x80L|(clickThrough?0x20L:0L);
        Native.SetWindowLongPtr(h,-20,(IntPtr)ex);
        return Native.SetWindowDisplayAffinity(h,excludeFromCapture?0x11u:0u)&&excludeFromCapture;
    }
    // WS_EX_TRANSPARENT: mouse input goes to whatever is below.
    public static void MakeClickThrough(Window w)
    {
        var h=new WindowInteropHelper(w).Handle;
        Native.SetWindowLongPtr(h,-20,(IntPtr)(Native.GetWindowLongPtr(h,-20).ToInt64()|0x20L));
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
        Level.Paused=>System.Windows.Media.Color.FromRgb(240,200,60),Level.Muted=>System.Windows.Media.Color.FromRgb(235,64,64),Level.Warn=>System.Windows.Media.Color.FromRgb(255,150,60),
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
// Full-screen drawing layer for one monitor, up while Caps is held. Strokes are kept in physical pixels of that monitor (what CopyFromScreen sees),
// drawn 5 physical px wide whatever the monitor's scaling is.
internal sealed class DrawOverlay : Window
{
    public const int FadeMs=1500,StrokePx=5;
    readonly Canvas canvas=new(){Background=new SolidColorBrush(System.Windows.Media.Color.FromArgb(1,0,0,0))};
    System.Windows.Shapes.Polyline? current;
    readonly List<List<System.Drawing.PointF>> strokes=[];
    readonly Rectangle monitor;
    public IReadOnlyList<List<System.Drawing.PointF>> Strokes=>strokes;
    public Rectangle Monitor=>monitor;
    public bool InStroke=>current!=null;
    // True when Windows leaves this window out of screen captures (WDA_EXCLUDEFROMCAPTURE); the product relies on it, the selftest switches it off to see the strokes.
    public bool CaptureExcluded {get;private set;}
    bool fading;
    double Scale=>VisualTreeHelper.GetDpi(this).DpiScaleX;
    // The instant Caps is released: clicks go to the window below, the strokes stay visible while they fade.
    public void ClickThrough(){IsHitTestVisible=false;OverlayStyle.MakeClickThrough(this);if(current!=null){current=null;canvas.ReleaseMouseCapture();}}
    public void Fade()
    {
        if(fading)return;fading=true;IsHitTestVisible=false;
        BeginAnimation(OpacityProperty,new System.Windows.Media.Animation.DoubleAnimation(1,0,TimeSpan.FromMilliseconds(FadeMs)));
    }
    public DrawOverlay(System.Windows.Forms.Screen screen,bool excludeFromCapture=true)
    {
        monitor=screen.Bounds;
        WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;Topmost=true;ShowInTaskbar=false;ShowActivated=false;Focusable=false;Content=canvas;
        double s=Native.ScaleAt(monitor.Left+monitor.Width/2,monitor.Top+monitor.Height/2);
        Left=monitor.Left/s;Top=monitor.Top/s;Width=monitor.Width/s;Height=monitor.Height/s;
        var hwnd=new WindowInteropHelper(this).EnsureHandle();
        CaptureExcluded=OverlayStyle.Apply(this,false,excludeFromCapture);
        Native.SetWindowPos(hwnd,(IntPtr)(-1),monitor.Left,monitor.Top,monitor.Width,monitor.Height,0x10);
        Loaded+=(_,_)=>Native.SetWindowPos(hwnd,(IntPtr)(-1),monitor.Left,monitor.Top,monitor.Width,monitor.Height,0x10);
        canvas.MouseLeftButtonDown+=(_,e)=>
        {
            current=new(){Stroke=Brushes.Magenta,StrokeThickness=StrokePx/Scale,StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round};canvas.Children.Add(current);strokes.Add([]);canvas.CaptureMouse();Add(e.GetPosition(canvas));
        };
        canvas.MouseMove+=(_,e)=>{if(current!=null&&e.LeftButton==MouseButtonState.Pressed)Add(e.GetPosition(canvas));};
        canvas.MouseLeftButtonUp+=(_,_)=>{current=null;canvas.ReleaseMouseCapture();};
    }
    void Add(System.Windows.Point p){current!.Points.Add(p);double s=Scale;strokes[^1].Add(new((float)(p.X*s),(float)(p.Y*s)));}
    // Test hook: draws the same stroke the mouse would.
    internal void AddPoints(IEnumerable<System.Drawing.PointF> points)
    {
        double s=Scale;var line=new System.Windows.Shapes.Polyline{Stroke=Brushes.Magenta,StrokeThickness=StrokePx/s,StrokeLineJoin=PenLineJoin.Round};
        var list=new List<System.Drawing.PointF>();foreach(var p in points){line.Points.Add(new(p.X/s,p.Y/s));list.Add(p);}
        canvas.Children.Add(line);strokes.Add(list);
    }
    public void Composite(Bitmap clean)
    {
        using var g=Graphics.FromImage(clean);g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen=new System.Drawing.Pen(System.Drawing.Color.Magenta,StrokePx){StartCap=System.Drawing.Drawing2D.LineCap.Round,EndCap=System.Drawing.Drawing2D.LineCap.Round,LineJoin=System.Drawing.Drawing2D.LineJoin.Round};
        foreach(var stroke in strokes){if(stroke.Count>=2)g.DrawLines(pen,stroke.ToArray());else if(stroke.Count==1)g.FillEllipse(System.Drawing.Brushes.Magenta,stroke[0].X-StrokePx/2f,stroke[0].Y-StrokePx/2f,StrokePx,StrokePx);}
    }
}
