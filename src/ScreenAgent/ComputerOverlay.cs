using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
namespace ScreenAgent;
// While a computer-use run exists: a purple frame around every monitor, a banner with the verbatim goal, the step and "Strg+Win+Umschalt+X stoppt", and (R2) the question with its code.
// All windows are click-through, never take focus and are left out of every capture (so the code never reaches a screenshot, the vision model or the loop). Over a remote desktop session the overlay may not show.
internal sealed class ComputerOverlay:IDisposable
{
    static readonly System.Windows.Media.Color Purple=System.Windows.Media.Color.FromRgb(138,92,246);
    readonly List<Window> frames=[];
    Window? banner;
    readonly TextBlock head=Line(14,true),state=Line(12,false),ask=Line(15,true);
    readonly bool exclude;
    public bool CaptureExcluded {get;private set;}=true;
    public ComputerOverlay(bool excludeFromCapture=true){exclude=excludeFromCapture;}
    static TextBlock Line(double size,bool bold)=>new(){Foreground=Brushes.White,FontFamily=new System.Windows.Media.FontFamily("Segoe UI"),FontSize=size,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(14,2,14,2)};
    static Window Base()
    {
        var w=new Window{WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,Background=Brushes.Transparent,Topmost=true,ShowInTaskbar=false,ShowActivated=false,Focusable=false};
        return w;
    }
    public bool Visible=>banner is {IsVisible:true};
    public int FrameCount=>frames.Count;
    public string[] Texts=>[head.Text,state.Text,ask.Visibility==Visibility.Visible?ask.Text:""];
    public string Question=>ask.Visibility==Visibility.Visible?ask.Text:"";
    public void Show(string goal)
    {
        if(banner is not null)return;
        foreach(var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var f=Base();f.Content=new Border{BorderBrush=new SolidColorBrush(Purple),BorderThickness=new Thickness(5),Background=Brushes.Transparent};
            f.SourceInitialized+=(_,_)=>{CaptureExcluded&=OverlayStyle.Apply(f,true,exclude)||!exclude;};
            f.Left=screen.Bounds.X;f.Top=screen.Bounds.Y;f.Width=100;f.Height=100;
            f.Show();Place(f,screen.Bounds);frames.Add(f);
        }
        banner=Base();
        var stack=new StackPanel();stack.Children.Add(head);stack.Children.Add(state);stack.Children.Add(ask);ask.Visibility=Visibility.Collapsed;
        banner.Content=new Border{Background=new SolidColorBrush(System.Windows.Media.Color.FromArgb(235,38,28,66)),CornerRadius=new CornerRadius(0,0,10,10),BorderBrush=new SolidColorBrush(Purple),BorderThickness=new Thickness(0,0,0,3),Child=stack,Padding=new Thickness(0,4,0,6)};
        banner.SourceInitialized+=(_,_)=>{CaptureExcluded&=OverlayStyle.Apply(banner,true,exclude)||!exclude;};
        var p=System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        banner.Left=p.X+p.Width/4;banner.Top=p.Y;banner.Width=Math.Max(400,p.Width/2);banner.SizeToContent=SizeToContent.Height;
        head.Text="Computer-Steuerung · Ziel: "+Cap(goal,160);state.Text="Start";
        banner.Show();
    }
    static void Place(Window w,System.Drawing.Rectangle px)
    {
        var h=new WindowInteropHelper(w).Handle;
        Native.SetWindowPos(h,new IntPtr(-1),px.X,px.Y,px.Width,px.Height,0x10);   // HWND_TOPMOST, NOACTIVATE
    }
    static string Cap(string s,int n){s=new string(s.Where(c=>!char.IsControl(c)).ToArray());return s.Length<=n?s:s[..n]+"…";}
    public void Update(string status,int step,int max,string? last=null)
    {
        state.Text=$"Schritt {step} von {max} · {status}"+(last is null?"":" · "+Cap(last,60))+" · Strg+Win+Umschalt+X stoppt";
        foreach(var f in frames)if(f.Content is Border b)b.BorderBrush=new SolidColorBrush(status.StartsWith("Du bist dran")?System.Windows.Media.Color.FromRgb(240,170,60):Purple);
    }
    // The question and its code, readable by him only: this window is not in any capture.
    public void Ask(string code,string spoken)
    {
        ask.Text=$"Bestätigen? {Cap(spoken,200)} · Code {code}: sage „ja {string.Join(" ",code.ToCharArray())}“ oder drücke Strg+Win+Umschalt+Y";ask.Visibility=Visibility.Visible;
    }
    public void ClearAsk(){ask.Visibility=Visibility.Collapsed;ask.Text="";}
    public bool AllExcludedAndClickThrough()
    {
        foreach(var w in frames.Append(banner!).Where(w=>w is not null))
        {
            var h=new WindowInteropHelper(w).Handle;
            if(!Native.GetWindowDisplayAffinity(h,out uint a)||a!=0x11)return false;
            if((Native.GetWindowLongPtr(h,-20).ToInt64()&0x20)==0)return false;
        }
        return true;
    }
    public void Hide()
    {
        foreach(var f in frames)f.Close();frames.Clear();
        if(banner?.Content is Border b&&b.Child is StackPanel sp)sp.Children.Clear();   // the text blocks are reused by the next run
        banner?.Close();banner=null;
    }
    public void Dispose()=>Hide();
}
