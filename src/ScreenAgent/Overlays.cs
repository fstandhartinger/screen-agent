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
    public static void Apply(Window w,bool clickThrough)
    {
        var h=new WindowInteropHelper(w).Handle;
        long ex=Native.GetWindowLongPtr(h,-20).ToInt64();
        Native.SetWindowLongPtr(h,-20,(IntPtr)(ex|0x08000000|0x80|(clickThrough?0x20:0)));
        Native.SetWindowDisplayAffinity(h,0x11);
    }
}
internal sealed class Pill : Window
{
    readonly TextBlock label=new(){Foreground=Brushes.White,Margin=new Thickness(12,7,12,7)};
    public Pill()
    {
        WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;Topmost=true;ShowInTaskbar=false;ShowActivated=false;
        Width=300;Height=70;Left=SystemParameters.WorkArea.Right-320;Top=SystemParameters.WorkArea.Bottom-90;
        var stack=new StackPanel();stack.Children.Add(label);
        // Capture indicator is click-through; the adjacent separate pause control remains interactive.
        Content=new Border{Background=new SolidColorBrush(System.Windows.Media.Color.FromArgb(235,30,30,40)),CornerRadius=new CornerRadius(12),Child=stack};
        SourceInitialized+=(_,_)=>OverlayStyle.Apply(this,true);SetMode("Pausiert");
    }
    public void SetMode(string mode)=>label.Text="● Screen Agent · "+mode;
}
internal sealed class PauseControl : Window
{
    public PauseControl(System.Action pause)
    {
        WindowStyle=WindowStyle.None;Width=75;Height=26;Topmost=true;ShowInTaskbar=false;ShowActivated=false;
        Left=SystemParameters.WorkArea.Right-100;Top=SystemParameters.WorkArea.Bottom-48;
        var b=new Button{Content="Pause"};b.Click+=(_,_)=>pause();Content=b;
        SourceInitialized+=(_,_)=>OverlayStyle.Apply(this,false);
    }
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
        canvas.MouseLeftButtonUp+=(_,_)=>{current=null;canvas.ReleaseMouseCapture();};
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
