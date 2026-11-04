using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenAgent.Core;
using Brushes = System.Windows.Media.Brushes;
namespace ScreenAgent;
internal enum DictationVisual{None,Listening,Processing}
// The live waveform of a dictation and the circle while it is being processed. Drawn by hand on a timer, so no animation can keep a value behind our back.
internal sealed class VoiceMeter : FrameworkElement
{
    const int Bars=22;const double BarWidth=3,Gap=2,Tall=30,CircleSize=24;
    static readonly SolidColorBrush Accent=Frozen(System.Windows.Media.Color.FromRgb(90,169,255)),Faint=Frozen(System.Windows.Media.Color.FromArgb(70,255,255,255));
    static readonly System.Windows.Media.Pen Ring=FrozenPen(Faint),Arc=FrozenPen(Accent);
    static SolidColorBrush Frozen(System.Windows.Media.Color c){var b=new SolidColorBrush(c);b.Freeze();return b;}
    static System.Windows.Media.Pen FrozenPen(System.Windows.Media.Brush b){var p=new System.Windows.Media.Pen(b,3){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};p.Freeze();return p;}
    double[] levels=[];double angle;
    public DictationVisual Mode {get;private set;}
    public void Show(DictationVisual mode){if(mode==Mode)return;Mode=mode;Visibility=mode==DictationVisual.None?Visibility.Collapsed:Visibility.Visible;InvalidateMeasure();InvalidateVisual();}
    public void Frame(LevelRing ring){if(Mode==DictationVisual.Listening)levels=ring.Last(Bars);else angle=(angle+14)%360;InvalidateVisual();}
    protected override System.Windows.Size MeasureOverride(System.Windows.Size available)=>Mode==DictationVisual.Processing?new(CircleSize,CircleSize):new(Bars*(BarWidth+Gap)-Gap,Tall);
    protected override void OnRender(DrawingContext dc)
    {
        if(Mode==DictationVisual.Listening)
        {
            double mid=Tall/2;
            for(int i=0;i<Bars;i++)
            {
                double level=i<levels.Length?levels[levels.Length-1-i]:0;   // the newest on the right
                double h=LevelRing.BarHeight(level,4,Tall);
                dc.DrawRoundedRectangle(Accent,null,new Rect((Bars-1-i)*(BarWidth+Gap),mid-h/2,BarWidth,h),1.5,1.5);
            }
        }
        else if(Mode==DictationVisual.Processing)
        {
            double c=CircleSize/2,r=c-2;
            dc.DrawEllipse(null,Ring,new System.Windows.Point(c,c),r,r);
            var arc=new StreamGeometry();
            using(var g=arc.Open())
            {
                g.BeginFigure(new System.Windows.Point(c,c-r),false,false);
                g.ArcTo(new System.Windows.Point(c+r,c),new System.Windows.Size(r,r),0,false,SweepDirection.Clockwise,true,false);
            }
            arc.Freeze();
            dc.PushTransform(new RotateTransform(angle,c,c));dc.DrawGeometry(null,Arc,arc);dc.Pop();
        }
    }
}
// The live subtitle, and while dictating the waveform, then the processing circle: a dark translucent pill, bottom centre of the monitor with the active window,
// click-through, never takes the focus, left out of screen captures (the same OverlayStyle as the status note). A subtitle alone fades 3 s after the last text.
// Bug fixed in 0.1.9: the fade kept holding Opacity at 0 after the first hide, so from then on the window was invisible and only flashed during the next fade (1 -> 0), at the end of a dictation.
internal sealed class CaptionOverlay : Window
{
    public const int FadeAfterMs=3000;
    readonly TextBlock label=new(){Foreground=Brushes.White,FontFamily=new System.Windows.Media.FontFamily("Segoe UI"),FontSize=20,TextWrapping=TextWrapping.Wrap,TextAlignment=TextAlignment.Left,MaxWidth=780,Visibility=Visibility.Collapsed};
    readonly TextBlock state=new(){Foreground=new SolidColorBrush(System.Windows.Media.Color.FromRgb(190,198,210)),FontFamily=new System.Windows.Media.FontFamily("Segoe UI"),FontSize=14,Visibility=Visibility.Collapsed};
    readonly VoiceMeter meter=new(){Visibility=Visibility.Collapsed,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,14,0)};
    readonly System.Windows.Threading.DispatcherTimer fade=new(){Interval=TimeSpan.FromMilliseconds(250)},frames=new(){Interval=TimeSpan.FromMilliseconds(40)};
    long touched;
    LevelRing? levels;
    public DictationVisual Visual {get;private set;}
    public string Text=>label.Text;
    public string StateText=>state.Text;
    public bool AffinityOk {get;private set;}
    public FrameworkElement Pill=>(FrameworkElement)Content;
    public CaptionOverlay()
    {
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;Background=Brushes.Transparent;
        Topmost=true;ShowInTaskbar=false;ShowActivated=false;Focusable=false;SizeToContent=SizeToContent.WidthAndHeight;
        var words=new StackPanel{VerticalAlignment=VerticalAlignment.Center};words.Children.Add(label);words.Children.Add(state);
        var row=new StackPanel{Orientation=System.Windows.Controls.Orientation.Horizontal};row.Children.Add(meter);row.Children.Add(words);
        Content=new Border{Background=new SolidColorBrush(System.Windows.Media.Color.FromArgb(225,20,20,26)),CornerRadius=new CornerRadius(18),Padding=new Thickness(18,9,20,10),MinHeight=48,Child=row};
        new WindowInteropHelper(this).EnsureHandle();
        AffinityOk=OverlayStyle.Apply(this,true);   // confirmed before the first word: a subtitle that a screenshot could catch is never shown
        SizeChanged+=(_,_)=>Place();
        fade.Tick+=(_,_)=>{if(IsVisible&&Visual==DictationVisual.None&&Environment.TickCount64-touched>FadeAfterMs)Dismiss();};
        frames.Tick+=(_,_)=>{if(levels is not null&&IsVisible)meter.Frame(levels);};
    }
    void Appear()
    {
        BeginAnimation(OpacityProperty,null);Opacity=1;   // a running or finished fade must not keep the window transparent
        if(!IsVisible){fade.Start();Show();}
        Place();
    }
    // Shows the last two lines of the text; every call restarts the 3 s.
    public void Present(string text)
    {
        if(string.IsNullOrWhiteSpace(text))return;
        if(!AffinityOk){OverlayStyle.RefusedOnce("caption");return;}
        touched=Environment.TickCount64;label.Text=text;label.Visibility=Visibility.Visible;
        if(Visual==DictationVisual.Listening)state.Visibility=Visibility.Collapsed;
        Appear();
    }
    // Waveform while he talks, the circle while the text is on its way, nothing afterwards: the text is in his field by then.
    public void SetVisual(DictationVisual visual,string? detail,LevelRing ring)
    {
        if(!AffinityOk){if(visual!=DictationVisual.None)OverlayStyle.RefusedOnce("caption");return;}
        var was=Visual;Visual=visual;levels=ring;
        meter.Show(visual);
        switch(visual)
        {
            case DictationVisual.Listening:
                if(was!=DictationVisual.Listening){label.Text="";label.Visibility=Visibility.Collapsed;}   // the words of a new dictation, not the last one's
                state.Text="Ich höre zu …";state.Visibility=label.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
                break;
            case DictationVisual.Processing:
                state.Text=string.IsNullOrEmpty(detail)?"Wird verarbeitet …":$"Wird verarbeitet … {detail}";state.Visibility=Visibility.Visible;
                break;
            default:
                frames.Stop();state.Visibility=Visibility.Collapsed;
                if(was!=DictationVisual.None)Dismiss();
                return;
        }
        touched=Environment.TickCount64;
        frames.Start();Appear();
    }
    public void Dismiss()
    {
        if(!IsVisible||Visual!=DictationVisual.None)return;
        fade.Stop();
        long at=touched;
        var a=new System.Windows.Media.Animation.DoubleAnimation(1,0,TimeSpan.FromMilliseconds(220));
        a.Completed+=(_,_)=>
        {
            bool stale=touched!=at||Visual!=DictationVisual.None;   // new words arrived during the fade: they stay
            if(!stale){Hide();label.Text="";label.Visibility=Visibility.Collapsed;}
            BeginAnimation(OpacityProperty,null);Opacity=1;
            if(stale&&IsVisible)fade.Start();
        };
        BeginAnimation(OpacityProperty,a);
    }
    void Place()
    {
        var hwnd=new WindowInteropHelper(this).EnsureHandle();
        var screen=System.Windows.Forms.Screen.FromHandle(Native.GetForegroundWindow());
        var dpi=VisualTreeHelper.GetDpi(this);
        int w=(int)Math.Ceiling(ActualWidth*dpi.DpiScaleX),h=(int)Math.Ceiling(ActualHeight*dpi.DpiScaleY);
        if(w<=0||h<=0)return;
        var area=screen.WorkingArea;
        int x=area.X+(area.Width-w)/2,y=area.Bottom-h-(int)Math.Round(40*dpi.DpiScaleY);
        Native.SetWindowPos(hwnd,(IntPtr)(-1),x,y,0,0,0x1|0x10);   // NOSIZE | NOACTIVATE, topmost
    }
}
