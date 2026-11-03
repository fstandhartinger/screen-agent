using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
namespace ScreenAgent;
// The picture the agent just took, shown small: made in memory from the very bitmap that goes out, never written anywhere.
internal static class FeedbackThumb
{
    public static BitmapSource Make(Bitmap shot,int widthPx)
    {
        int h=Math.Max(1,(int)Math.Round(shot.Height*(double)widthPx/Math.Max(1,shot.Width)));
        using var small=new Bitmap(widthPx,h,System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using(var g=Graphics.FromImage(small)){g.InterpolationMode=InterpolationMode.HighQualityBilinear;g.DrawImage(shot,0,0,widthPx,h);}
        using var stream=new MemoryStream();small.Save(stream,ImageFormat.Bmp);stream.Position=0;
        var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=stream;image.EndInit();image.Freeze();
        return image;
    }
}
// Purple flash over the captured monitor (12 % at most, gone in 180 ms) and the thumbnail, bottom right, fading over 1.4 s. Same kind of window as the drawing layer:
// transparent, topmost, click-through, no focus, left out of every capture, so no later frame can contain it. It exists only after the capture.
internal sealed class FeedbackOverlay : Window
{
    public const int FlashMs=180,ThumbMs=1400,ThumbWidthPx=220,ReserveDip=132;
    public const double FlashAlpha=0.12;
    public static readonly System.Windows.Media.Color Purple=System.Windows.Media.Color.FromRgb(138,43,226);
    readonly Rectangle monitor;
    readonly Border? card;
    public Rectangle Monitor=>monitor;
    public bool CaptureExcluded {get;private set;}
    // Where the thumbnail is on screen (device pixels), for the selftest.
    public Rectangle? ThumbRect
    {
        get
        {
            if(card is null)return null;
            var p=card.PointToScreen(new System.Windows.Point(0,0));double s=VisualTreeHelper.GetDpi(this).DpiScaleX;
            return new Rectangle((int)Math.Round(p.X),(int)Math.Round(p.Y),(int)Math.Round(card.ActualWidth*s),(int)Math.Round(card.ActualHeight*s));
        }
    }
    // Fades finished (or never started when `animate` is off): the window closes itself.
    public event System.Action? Finished;
    public FeedbackOverlay(System.Windows.Forms.Screen screen,BitmapSource? thumb,bool excludeFromCapture=true,bool animate=true)
    {
        monitor=screen.Bounds;
        WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;Topmost=true;ShowInTaskbar=false;ShowActivated=false;Focusable=false;ResizeMode=ResizeMode.NoResize;
        double s=Native.ScaleAt(monitor.Left+monitor.Width/2,monitor.Top+monitor.Height/2);
        Left=monitor.Left/s;Top=monitor.Top/s;Width=monitor.Width/s;Height=monitor.Height/s;
        var hwnd=new WindowInteropHelper(this).EnsureHandle();
        CaptureExcluded=OverlayStyle.Apply(this,true,excludeFromCapture);
        Native.SetWindowPos(hwnd,(IntPtr)(-1),monitor.Left,monitor.Top,monitor.Width,monitor.Height,0x10);
        var root=new Grid();
        var tint=new System.Windows.Shapes.Rectangle{Fill=new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)Math.Round(255*FlashAlpha),Purple.R,Purple.G,Purple.B)),IsHitTestVisible=false};
        root.Children.Add(tint);
        if(thumb is not null)
        {
            var work=screen.WorkingArea;
            card=new Border{BorderBrush=new SolidColorBrush(Purple),BorderThickness=new Thickness(2),CornerRadius=new CornerRadius(6),Background=Brushes.Black,
                HorizontalAlignment=System.Windows.HorizontalAlignment.Right,VerticalAlignment=System.Windows.VerticalAlignment.Bottom,
                Margin=new Thickness(0,0,(monitor.Right-work.Right)/s+14,(monitor.Bottom-work.Bottom)/s+14+ReserveDip),
                Child=new System.Windows.Controls.Image{Source=thumb,Width=ThumbWidthPx/s,Stretch=Stretch.Uniform}};
            root.Children.Add(card);
        }
        Content=root;
        if(!animate)return;
        var flash=new DoubleAnimation(1,0,TimeSpan.FromMilliseconds(FlashMs));
        if(card is null)flash.Completed+=(_,_)=>Finished?.Invoke();
        tint.BeginAnimation(OpacityProperty,flash);
        if(card is null)return;
        var fade=new DoubleAnimation(1,0,TimeSpan.FromMilliseconds(ThumbMs)){BeginTime=TimeSpan.FromMilliseconds(250)};   // a short hold, then the fade
        fade.Completed+=(_,_)=>Finished?.Invoke();
        card.BeginAnimation(OpacityProperty,fade);
    }
}
// One flash at a time. Shown only when Windows confirmed the capture exclusion: otherwise the next frame could contain the overlay, so nothing is shown.
internal static class ScreenFlash
{
    static FeedbackOverlay? current;
    public static (bool Shown,string Why) Show(Rectangle monitor,BitmapSource thumb)
    {
        var screen=System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s=>s.Bounds==monitor)??System.Windows.Forms.Screen.FromRectangle(monitor);
        var overlay=new FeedbackOverlay(screen,thumb);
        if(!overlay.CaptureExcluded){overlay.Close();return (false,"no-exclusion");}
        try{current?.Close();}catch(Exception){ /* already gone */ }
        current=overlay;
        overlay.Finished+=()=>{try{overlay.Close();}catch(Exception){ /* already closed */ }if(ReferenceEquals(current,overlay))current=null;};
        overlay.Show();
        return (true,"");
    }
}
