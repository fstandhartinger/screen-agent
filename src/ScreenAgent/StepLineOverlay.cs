using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Brushes=System.Windows.Media.Brushes;
namespace ScreenAgent;
// The bottom edge stays free of the computer-control frame (5 px) and its top banner.
// Native physical-pixel placement makes the inset hold on scaled and negative-origin monitors.
internal sealed class StepLineOverlay:Window
{
    readonly StepLine state=new();
    readonly TextBlock line=new(){Foreground=Brushes.White,FontSize=13,FontFamily=new System.Windows.Media.FontFamily("Segoe UI"),TextTrimming=TextTrimming.CharacterEllipsis,TextWrapping=TextWrapping.NoWrap};
    public bool CaptureExcluded {get;}
    public string Text=>line.Text;
    public StepLineOverlay()
    {
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;Background=Brushes.Transparent;
        Topmost=true;ShowInTaskbar=false;ShowActivated=false;Focusable=false;SizeToContent=SizeToContent.Height;
        Content=new Border{Background=new SolidColorBrush(System.Windows.Media.Color.FromArgb(240,34,28,45)),CornerRadius=new CornerRadius(6),Padding=new Thickness(12,5,12,5),Child=line};
        new WindowInteropHelper(this).EnsureHandle();
        CaptureExcluded=OverlayStyle.Apply(this,true,true);
        SizeChanged+=(_,_)=>Place();
    }
    public void Present(ActivityNote note){state.Activity(note,Environment.TickCount64);Tick(true);}
    public void Computer(string type,IReadOnlyDictionary<string,object?> data){state.Computer(type,data,Environment.TickCount64);Tick(true);}
    public void Clear(){state.Clear();if(IsVisible)Hide();}
    public void Tick(bool voice)
    {
        if(!voice){Clear();return;}
        var view=state.View(Environment.TickCount64,true);
        if(view.Text.Length==0||!CaptureExcluded)
        {
            if(view.Text.Length>0&&!CaptureExcluded)OverlayStyle.RefusedOnce("step-line");
            if(IsVisible)Hide();return;
        }
        line.Text=view.Text;Opacity=view.Opacity;
        Place();if(!IsVisible)Show();Place();
    }
    void Place()
    {
        var screen=System.Windows.Forms.Screen.PrimaryScreen??System.Windows.Forms.Screen.AllScreens[0];
        var dpi=VisualTreeHelper.GetDpi(this);var work=screen.WorkingArea;var bounds=screen.Bounds;
        int inset=Math.Max(12,(int)Math.Ceiling(12*dpi.DpiScaleX));
        int width=Math.Max(1,Math.Min(work.Width-2*inset,(int)Math.Ceiling(1000*dpi.DpiScaleX)));
        Width=width/dpi.DpiScaleX;
        int height=Math.Max(1,(int)Math.Ceiling(ActualHeight*dpi.DpiScaleY));
        int x=work.Left+(work.Width-width)/2,y=Math.Min(work.Bottom,bounds.Bottom-inset)-height-inset;
        Native.SetWindowPos(new WindowInteropHelper(this).Handle,new IntPtr(-1),x,y,width,height,0x10); // TOPMOST, NOACTIVATE
    }
}
