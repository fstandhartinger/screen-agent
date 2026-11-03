using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
namespace ScreenAgent;
// The live subtitle: one or two lines in a dark translucent pill, bottom centre of the monitor with the active window, click-through, never takes the focus,
// left out of screen captures (the same OverlayStyle as the status note). Fades out 3 s after the last text.
internal sealed class CaptionOverlay : Window
{
    public const int FadeAfterMs=3000;
    readonly TextBlock label=new(){Foreground=Brushes.White,FontFamily=new System.Windows.Media.FontFamily("Segoe UI"),FontSize=20,TextWrapping=TextWrapping.Wrap,TextAlignment=TextAlignment.Center,MaxWidth=860};
    readonly System.Windows.Threading.DispatcherTimer fade=new(){Interval=TimeSpan.FromMilliseconds(250)};
    long touched;
    public string Text=>label.Text;
    public bool AffinityOk {get;private set;}
    public CaptionOverlay()
    {
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;Background=Brushes.Transparent;
        Topmost=true;ShowInTaskbar=false;ShowActivated=false;Focusable=false;SizeToContent=SizeToContent.WidthAndHeight;
        Content=new Border{Background=new SolidColorBrush(System.Windows.Media.Color.FromArgb(215,20,20,26)),CornerRadius=new CornerRadius(16),Padding=new Thickness(18,8,18,10),Child=label};
        new WindowInteropHelper(this).EnsureHandle();
        AffinityOk=OverlayStyle.Apply(this,true);   // confirmed before the first word: a subtitle that a screenshot could catch is never shown
        SizeChanged+=(_,_)=>Place();
        fade.Tick+=(_,_)=>{if(IsVisible&&Environment.TickCount64-touched>FadeAfterMs)Dismiss();};
    }
    // Shows the last two lines of the text; every call restarts the 3 s.
    public void Present(string text)
    {
        if(string.IsNullOrWhiteSpace(text))return;
        if(!AffinityOk){OverlayStyle.RefusedOnce("caption");return;}
        touched=Environment.TickCount64;label.Text=text;Opacity=1;
        if(!IsVisible){fade.Start();Show();}
        Place();
    }
    public void Dismiss()
    {
        if(!IsVisible)return;
        fade.Stop();
        var a=new System.Windows.Media.Animation.DoubleAnimation(1,0,TimeSpan.FromMilliseconds(350));
        a.Completed+=(_,_)=>{if(Environment.TickCount64-touched>FadeAfterMs){Hide();label.Text="";}else{BeginAnimation(OpacityProperty,null);Opacity=1;fade.Start();}};
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
