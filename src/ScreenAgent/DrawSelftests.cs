using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Windows;
using Font=System.Drawing.Font;
using FontStyle=System.Drawing.FontStyle;
using Point=System.Drawing.Point;
using Brushes=System.Drawing.Brushes;
namespace ScreenAgent;
// Windows CI: the drawing path on a real desktop (--selftest-draw) and the change test on a real bitmap (--selftest-frame <png>).
// Exit codes as in Selftests: 0 passed, 1 failed, 2 skipped because the runner cannot deliver mouse input.
internal static class DrawSelftests
{
    static bool Magenta(Color c)=>c.R>200&&c.G<90&&c.B>200;
    static int CountMagenta(Bitmap image)
    {
        int n=0;for(int y=0;y<image.Height;y++)for(int x=0;x<image.Width;x++)if(Magenta(image.GetPixel(x,y)))n++;return n;
    }
    // Contiguous magenta pixels in column x, starting from the stroke found within 8 rows of y.
    static int RunThrough(Bitmap image,int x,int y)
    {
        if(x<0||x>=image.Width)return 0;
        int seed=-1;
        for(int d=0;d<=8&&seed<0;d++)foreach(int yy in new[]{y+d,y-d})if(yy>=0&&yy<image.Height&&Magenta(image.GetPixel(x,yy))){seed=yy;break;}
        if(seed<0)return 0;
        int top=seed,bottom=seed;
        while(top>0&&Magenta(image.GetPixel(x,top-1)))top--;
        while(bottom<image.Height-1&&Magenta(image.GetPixel(x,bottom+1)))bottom++;
        return bottom-top+1;
    }
    static Bitmap Shot(Rectangle area)
    {
        var image=new Bitmap(area.Width,area.Height,PixelFormat.Format32bppArgb);
        using var g=Graphics.FromImage(image);g.CopyFromScreen(area.Location,Point.Empty,area.Size,CopyPixelOperation.SourceCopy);return image;
    }
    public static int Draw()
    {
        var results=new List<object>();bool failed=false,skipped=false;
        void Case(string name,bool pass,object? detail=null){results.Add(new{name,pass,detail});if(!pass)failed=true;}
        KeyboardHook.AcceptInjectedForTests=true;   // the hook ignores synthetic keys in production; selftests send their own
        var app=new Application();int exit=1;
        app.Startup+=async (_,_)=>
        {
            DrawOverlay? overlay=null;Window? under=null;
            try
            {
                var screen=System.Windows.Forms.Screen.PrimaryScreen!;var monitor=screen.Bounds;
                var c=new System.Drawing.Point(monitor.Left+monitor.Width/2,monitor.Top+monitor.Height/2);const int radius=100;
                double scale=Native.ScaleAt(c.X,c.Y);
                // A window beneath: the click after release must reach it.
                int clicks=0;
                under=new Window{WindowStyle=WindowStyle.None,Topmost=true,ShowInTaskbar=false,ShowActivated=false,Background=System.Windows.Media.Brushes.White,Left=(c.X-300)/scale,Top=(c.Y-300)/scale,Width=600/scale,Height=600/scale};
                under.MouseLeftButtonDown+=(_,_)=>clicks++;under.Show();
                await Task.Delay(300);
                overlay=new DrawOverlay(screen,false);overlay.Show();await Task.Delay(300);
                var overlayHandle=new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
                Case("overlay-capture-exclusion-off-for-the-test",!overlay.CaptureExcluded);
                // A circle drawn with real mouse input: press at the top, go round, release.
                Native.SetCursorPos(c.X,c.Y-radius);await Task.Delay(50);Native.Send(Native.Button(true));
                for(int k=1;k<=60;k++)
                {
                    double angle=-Math.PI/2+k*Math.PI/30;
                    Native.SetCursorPos(c.X+(int)Math.Round(radius*Math.Cos(angle)),c.Y+(int)Math.Round(radius*Math.Sin(angle)));await Task.Delay(10);
                }
                Native.Send(Native.Button(false));await Task.Delay(100);
                int points=overlay.Strokes.Sum(s=>s.Count);
                if(points<20)
                {
                    skipped=true;Case("runner-delivers-mouse-input",false,new{points,reason="SendInput mouse events did not reach the overlay (no interactive desktop)"});failed=false;exit=2;return;
                }
                Case("strokes-recorded",overlay.Strokes.Count==1&&points>=30,new{overlay.Strokes.Count,points});
                // 1. the stroke is visible on screen, 5 physical px wide
                using(var shot=Shot(monitor))
                {
                    int magenta=CountMagenta(shot);
                    int top=RunThrough(shot,c.X-monitor.Left,c.Y-radius-monitor.Top),bottom=RunThrough(shot,c.X-monitor.Left,c.Y+radius-monitor.Top);
                    Case("magenta-pixels-visible-on-screen",magenta>300,new{magenta});
                    Case("stroke-is-5-physical-px-wide",top is >=4 and <=6&&bottom is >=4 and <=6,new{top,bottom,dpiScale=scale});
                }
                // 2. clean capture: overlay out of the picture; then the real composite and crop
                overlay.Opacity=0;await Task.Delay(150);
                using var clean=Shot(monitor);
                int cleanMagenta=CountMagenta(clean);
                overlay.Opacity=1;
                Case("clean-capture-has-no-stroke",cleanMagenta==0,new{cleanMagenta});
                var made=HighlightBuilder.Build(overlay,clean);
                Case("highlight-built",made is not null);
                if(made is not null)
                {
                    using var crop=new Bitmap(new MemoryStream(made.Png));using var context=new Bitmap(new MemoryStream(made.Jpeg));
                    int cropMagenta=CountMagenta(crop);
                    Case("crop-contains-magenta",cropMagenta>300,new{cropMagenta,crop.Width,crop.Height,made.Bbox});
                    Case("crop-has-margin-around-the-circle",crop.Width>=2*radius+10&&crop.Height>=2*radius+10&&crop.Width<=2*radius+120,new{crop.Width,crop.Height});
                    int ctxTop=RunThrough(clean,c.X-monitor.Left,c.Y-radius-monitor.Top);
                    Case("composited-stroke-is-5-px",ctxTop is >=4 and <=6,new{ctxTop});
                    Case("context-is-the-whole-monitor-with-stroke",context.Width>=Math.Min(monitor.Width,1000)&&CountMagenta(context)>50,new{context.Width,context.Height});
                }
                // 3. release: the overlay turns click-through at once
                var strokePoint=new Native.Point{X=c.X+radius,Y=c.Y};
                overlay.Opacity=1;await Task.Delay(150); // the clean capture above faded it out because this test turned capture exclusion off
                Case("before-release-overlay-is-under-the-pointer",Native.GetAncestor(Native.WindowFromPoint(strokePoint),2)==overlayHandle);
                overlay.ClickThrough();overlay.Fade();await Task.Delay(100);
                var hit=Native.GetAncestor(Native.WindowFromPoint(strokePoint),2);
                Case("after-release-window-at-stroke-point-is-not-ours",hit!=overlayHandle,new{hit=hit.ToInt64()});
                Native.SetCursorPos(strokePoint.X,strokePoint.Y);await Task.Delay(50);
                Native.Send(Native.Button(true),Native.Button(false));await Task.Delay(300);
                Case("a-real-click-reaches-the-window-beneath",clicks==1,new{clicks});
            }
            catch(Exception ex){Case("selftest-ran",false,ex.ToString());}
            finally
            {
                try{overlay?.Close();under?.Close();}catch{ /* closing is best effort */ }
                if(exit==1)exit=failed?1:0;
                Console.WriteLine(JsonSerializer.Serialize(new{test="draw",status=skipped?"skipped":failed?"failed":"passed",results}));
                app.Shutdown(exit);
            }
        };
        app.Run();return exit;
    }
    // Change test on a real bitmap: a screenshot (any PNG), a 5-digit number redrawn in the corner, a caret.
    public static int Frame(string path)
    {
        var results=new List<object>();bool failed=false;
        void Case(string name,bool pass,object? detail=null){results.Add(new{name,pass,detail});if(!pass)failed=true;}
        using var source=new Bitmap(path);
        using var baseImage=new Bitmap(source.Width,source.Height,PixelFormat.Format32bppArgb);
        using(var g=Graphics.FromImage(baseImage))g.DrawImage(source,0,0,source.Width,source.Height);
        Bitmap Variant(Action<Graphics> paint){var copy=new Bitmap(baseImage);using var g=Graphics.FromImage(copy);paint(g);return copy;}
        Bitmap Number(string text)=>Variant(g=>
        {
            g.FillRectangle(Brushes.White,source.Width-90,6,80,24);g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var font=new Font("Segoe UI",14f,FontStyle.Regular,GraphicsUnit.Pixel);g.DrawString(text,font,Brushes.Black,source.Width-86,9);
        });
        using var a=Number("12345");using var same=Number("12345");using var b=Number("67890");
        using var caret=Variant(g=>{g.FillRectangle(Brushes.White,source.Width/2,source.Height/2,2,18);});
        using var withoutCaret=Variant(g=>{g.FillRectangle(Brushes.Black,source.Width/2,source.Height/2,2,18);});
        var ga=FrameGrid.Of(a);
        Case("same-picture-is-unchanged",!ChangeDetector.Changed(ga,FrameGrid.Of(same)));
        Case("five-digit-number-in-the-corner-counts",ChangeDetector.Changed(ga,FrameGrid.Of(b)),new{tiles=ChangeDetector.ChangedTiles(ga,FrameGrid.Of(b)),source.Width,source.Height});
        using var oneDigit=Number("12346");
        Case("one-changed-digit-counts",ChangeDetector.Changed(ga,FrameGrid.Of(oneDigit)),new{tiles=ChangeDetector.ChangedTiles(ga,FrameGrid.Of(oneDigit))});
        Case("blinking-caret-does-not-count",!ChangeDetector.Changed(FrameGrid.Of(caret),FrameGrid.Of(withoutCaret)),new{tiles=ChangeDetector.ChangedTiles(FrameGrid.Of(caret),FrameGrid.Of(withoutCaret))});
        var planner=new FramePlanner();
        Case("planner-start",planner.Decide(0,ga,1,false).Reason==FrameReason.Start);
        planner.Captured(0,1);planner.Sent(0,ga,1);
        Case("planner-dedupes-the-same-picture",planner.Decide(4000,FrameGrid.Of(same),1,false).Verdict==FrameVerdict.Deduped);
        Case("planner-sends-the-new-number-as-soon-as-it-is-due",planner.CaptureDue(4000,1,false)&&planner.Decide(4000,FrameGrid.Of(b),1,false).Verdict==FrameVerdict.Send);
        Console.WriteLine(JsonSerializer.Serialize(new{test="frame",status=failed?"failed":"passed",results}));
        return failed?1:0;
    }
}
