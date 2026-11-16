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
        bool set=Native.SetWindowDisplayAffinity(h,excludeFromCapture?0x11u:0u);
        return excludeFromCapture&&set&&Native.GetWindowDisplayAffinity(h,out uint now)&&now==0x11u;   // read back, not just accepted
    }
    static readonly HashSet<string> refusedLogged=[];
    // capture.exclusion-failed {window}: a window that would show up in the next screenshot is not shown. Once per kind of window.
    public static void RefusedOnce(string window){lock(refusedLogged){if(!refusedLogged.Add(window))return;}Diag.Event("capture.exclusion-failed",new(){["window"]=window});}
    // WS_EX_TRANSPARENT on or off: the status note takes clicks only while it carries the microphone controls.
    public static void SetClickThrough(Window w,bool on)
    {
        var h=new WindowInteropHelper(w).Handle;long ex=Native.GetWindowLongPtr(h,-20).ToInt64();
        Native.SetWindowLongPtr(h,-20,(IntPtr)(on?ex|0x20L:ex&~0x20L));
    }
    // WS_EX_TRANSPARENT: mouse input goes to whatever is below.
    public static void MakeClickThrough(Window w)
    {
        var h=new WindowInteropHelper(w).Handle;
        Native.SetWindowLongPtr(h,-20,(IntPtr)(Native.GetWindowLongPtr(h,-20).ToInt64()|0x20L));
    }
}
// Small status note in the bottom-right corner. It never takes focus and has no taskbar button; it is on screen only while a session runs
// or for a few seconds after something happened. Without a voice session it is click-through and steps aside (fades) when the mouse comes
// near, so it cannot be in the way of the app underneath. While a voice session runs it carries two controls on its left: the microphone
// (click = mute/unmute, same switch as the hotkey) and a small arrow that opens the list of microphones. Then it takes clicks and stays solid.
internal sealed class StatusToast : Window
{
    readonly TextBlock label=new(){Foreground=Brushes.White,FontFamily=new System.Windows.Media.FontFamily("Segoe UI"),FontSize=12,TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
    readonly System.Windows.Shapes.Ellipse dot=new(){Width=8,Height=8,Margin=new Thickness(0,0,7,0),VerticalAlignment=VerticalAlignment.Center};
    readonly System.Windows.Threading.DispatcherTimer near=new(){Interval=TimeSpan.FromMilliseconds(150)};
    static readonly System.Windows.Media.FontFamily Glyphs=new("Segoe Fluent Icons, Segoe MDL2 Assets");
    const string MicGlyph="\uE720",MicOffGlyph="\uEC54",ChevronGlyph="\uE70E";   // Microphone, MicOff, ChevronUp (the list opens upwards)
    static readonly SolidColorBrush Idle=Frozen(System.Windows.Media.Color.FromArgb(0,0,0,0)),Hover=Frozen(System.Windows.Media.Color.FromArgb(70,255,255,255)),MutedBack=Frozen(System.Windows.Media.Color.FromRgb(200,50,50));
    readonly TextBlock micGlyph=new(){FontFamily=Glyphs,FontSize=14,Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center,Text=MicGlyph};
    readonly Border micBox,pickBox;
    readonly StackPanel controls=new(){Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,8,0),Visibility=Visibility.Collapsed};
    bool micShown,micMuted;
    // Raised on the UI thread. MicItems is asked each time the list opens: (name, menu text, selected); name null = Windows default.
    public event System.Action? MuteClicked;
    public event System.Action<string?>? MicPicked;
    public Func<IReadOnlyList<(string? Name,string Text,bool Selected)>>? MicItems;
    public bool MicShown=>micShown;
    static SolidColorBrush Frozen(System.Windows.Media.Color c){var b=new SolidColorBrush(c);b.Freeze();return b;}
    static Border Control(UIElement child,string tip)
    {
        var b=new Border{Background=Idle,CornerRadius=new CornerRadius(5),Padding=new Thickness(5,3,5,3),Margin=new Thickness(0,0,2,0),Child=child,ToolTip=tip,Cursor=System.Windows.Input.Cursors.Hand,VerticalAlignment=VerticalAlignment.Center};
        return b;
    }
    public StatusToast()
    {
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;Background=Brushes.Transparent;
        Topmost=true;ShowInTaskbar=false;ShowActivated=false;Focusable=false;SizeToContent=SizeToContent.WidthAndHeight;
        micBox=Control(micGlyph,"Mikrofon stumm / an");
        pickBox=Control(new TextBlock{FontFamily=Glyphs,FontSize=9,Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center,Text=ChevronGlyph},"Mikrofon wählen");
        foreach(var b in new[]{micBox,pickBox})
        {
            var box=b;
            box.MouseEnter+=(_,_)=>box.Background=box==micBox&&micMuted?MutedBack:Hover;
            box.MouseLeave+=(_,_)=>box.Background=box==micBox&&micMuted?MutedBack:Idle;
        }
        micBox.MouseLeftButtonUp+=(_,e)=>{e.Handled=true;MuteClicked?.Invoke();};
        pickBox.MouseLeftButtonUp+=(_,e)=>{e.Handled=true;OpenPicker();};
        controls.Children.Add(micBox);controls.Children.Add(pickBox);
        var row=new StackPanel{Orientation=Orientation.Horizontal};row.Children.Add(controls);row.Children.Add(dot);row.Children.Add(label);
        Content=new Border{Background=new SolidColorBrush(System.Windows.Media.Color.FromArgb(225,34,34,40)),CornerRadius=new CornerRadius(8),Padding=new Thickness(10,5,12,5),MaxWidth=480,Child=row};
        SourceInitialized+=(_,_)=>{OverlayStyle.Apply(this,!micShown);};
        SizeChanged+=(_,_)=>Place();
        near.Tick+=(_,_)=>
        {
            if(!IsVisible)return;
            if(micShown){Opacity=1;return;}   // the controls must stay where his mouse goes
            var h=new WindowInteropHelper(this).Handle;
            if(!Native.GetWindowRect(h,out var r))return;
            var c=System.Windows.Forms.Cursor.Position;int pad=24;
            Opacity=c.X>=r.Left-pad&&c.X<=r.Right+pad&&c.Y>=r.Top-pad&&c.Y<=r.Bottom+pad?0.12:1;
        };
    }
    // Called from the status update: the controls are there while a voice session runs; muted turns the microphone red.
    public void ShowMic(bool show,bool muted)
    {
        if(show==micShown&&muted==micMuted)return;
        bool clickChange=show!=micShown;
        micShown=show;micMuted=muted;
        controls.Visibility=show?Visibility.Visible:Visibility.Collapsed;
        micGlyph.Text=muted?MicOffGlyph:MicGlyph;
        micBox.Background=muted?MutedBack:micBox.IsMouseOver?Hover:Idle;
        micBox.ToolTip=muted?"Mikrofon ist stumm · klicken zum Einschalten":"Mikrofon an · klicken zum Stummschalten";
        if(clickChange)
        {
            var h=new WindowInteropHelper(this).Handle;
            if(h!=IntPtr.Zero)OverlayStyle.SetClickThrough(this,!show);
            if(show)Opacity=1;
            if(!show&&pickBox.ContextMenu is {IsOpen:true} open)open.IsOpen=false;
        }
    }
    void OpenPicker()
    {
        IReadOnlyList<(string? Name,string Text,bool Selected)> items;
        try{items=MicItems?.Invoke()??[];}
        catch(Exception ex){Diag.Fail("mic-list",ex);items=[];}   // the menu then says that no microphone was found
        var menu=new ContextMenu{PlacementTarget=pickBox,Placement=System.Windows.Controls.Primitives.PlacementMode.Top};
        if(items.Count==0)menu.Items.Add(new MenuItem{Header=new TextBlock{Text="Kein Mikrofon gefunden"},IsEnabled=false});
        foreach(var it in items)
        {
            var name=it.Name;
            var item=new MenuItem{Header=new TextBlock{Text=it.Text},IsChecked=it.Selected};   // a TextBlock header: an underscore in a device name is no access key
            item.Click+=(_,_)=>MicPicked?.Invoke(name);
            menu.Items.Add(item);
        }
        pickBox.ContextMenu=menu;menu.IsOpen=true;
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
