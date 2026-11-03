using System.Drawing;
using System.Text.Json;
using System.Windows;
namespace ScreenAgent;
// Windows CI, v0.1.7: --selftest-activity (live rows), --selftest-feedback (purple flash + thumbnail, and the proof that a capture contains none of it),
// --selftest-startup (how long WebView2 and the first page take on this runner). Exit 0 passed, 1 failed.
internal static class UiSelftests
{
    delegate void CaseFn(string name,bool pass,object? detail=null);
    static Bitmap Shot(Rectangle area)
    {
        var image=new Bitmap(area.Width,area.Height);
        using var g=Graphics.FromImage(image);g.CopyFromScreen(area.Location,System.Drawing.Point.Empty,area.Size);return image;
    }
    static double Share(Rectangle area,Func<Color,bool> test)
    {
        using var bmp=Shot(area);long hits=0,total=0;
        for(int y=0;y<bmp.Height;y+=2)for(int x=0;x<bmp.Width;x+=2){total++;if(test(bmp.GetPixel(x,y)))hits++;}
        return total==0?0:(double)hits/total;
    }
    static Rectangle Inner(Rectangle r)=>new(r.X+r.Width/8,r.Y+r.Height/8,Math.Max(1,r.Width*3/4),Math.Max(1,r.Height*3/4));
    static Rectangle WindowRect(IntPtr h)=>Native.GetWindowRect(h,out var r)?new(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top):default;
    static Window White(Rectangle area,double scale)=>new(){WindowStyle=WindowStyle.None,Topmost=true,ShowInTaskbar=false,ShowActivated=false,Background=System.Windows.Media.Brushes.White,Left=area.Left/scale,Top=area.Top/scale,Width=area.Width/scale,Height=area.Height/scale};
    static int Harness(string name,Func<CaseFn,Task> body)
    {
        var results=new List<object>();bool failed=false;
        CaseFn Case=(n,pass,detail)=>{results.Add(new{name=n,pass,detail});if(!pass)failed=true;};
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};int exit=1;
        app.Startup+=async (_,_)=>
        {
            try{await body(Case);exit=failed?1:0;}
            catch(Exception e){Case("selftest-crashed",false,e.ToString());exit=1;}
            finally{Console.WriteLine(JsonSerializer.Serialize(new{test=name,status=failed?"failed":"passed",results}));app.Shutdown(exit);}
        };
        app.Run();return exit;
    }
    public static int Activity()=>Harness("activity",async Case=>
    {
        var screen=System.Windows.Forms.Screen.PrimaryScreen!;var work=screen.WorkingArea;
        double scale=Native.ScaleAt(work.Left+work.Width/2,work.Top+work.Height/2);
        var under=White(new Rectangle(work.Right-760,work.Bottom-560,760,560),scale);under.Show();
        ActivityOverlay? overlay=null;
        try
        {
            await Task.Delay(400);
            IntPtr fgBefore=Native.GetForegroundWindow();
            overlay=new ActivityOverlay(()=>0);var h=new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
            Case("capture-exclusion-confirmed",overlay.CaptureExcluded);
            var clock=System.Diagnostics.Stopwatch.StartNew();
            overlay.Present(new("","thinking","","",0,""));
            Case("a-message-shows-before-present-returns",overlay.IsVisible&&overlay.Texts.Length==1,new{ms=clock.ElapsedMilliseconds,texts=overlay.Texts});
            overlay.Present(new("t1","start","Suche in der Registry","artifact_search",0,""));
            Case("first-tool-replaces-the-transient-row",overlay.Texts.SequenceEqual(new[]{"Suche in der Registry"})&&overlay.Marks[0]=="●",new{texts=overlay.Texts});
            overlay.Present(new("t2","start","Zeichne Diagramm","draw",0,""));
            overlay.Present(new("t3","start","Lese Karte","artifact_read",0,""));
            overlay.Present(new("t1","done","","",830,""));
            overlay.Present(new("t4","start","Schaue auf den Bildschirm","look_at_screen",0,""));
            overlay.Present(new("t5","fail","Prompt","main_prompt",0,"kein Fenster"));
            Case("at-most-four-rows",overlay.Texts.Length==4,new{texts=overlay.Texts});
            Case("fail-row-is-orange-with-its-reason",overlay.Texts.Any(t=>t.EndsWith("· kein Fenster"))&&overlay.Marks.Contains("!"),new{texts=overlay.Texts});
            overlay.Present(new("t2","done","","",120,""));
            Case("done-row-gets-a-tick",overlay.Marks.Contains("✓")&&overlay.Texts.Contains("Zeichne Diagramm · 120 ms"),new{texts=overlay.Texts,marks=overlay.Marks});
            await Task.Delay(300);
            Case("overlay-did-not-take-the-focus",Native.GetForegroundWindow()==fgBefore,new{fg=Native.GetForegroundWindow().ToInt64(),before=fgBefore.ToInt64()});
            long ex=Native.GetWindowLongPtr(h,-20).ToInt64();
            Case("click-through-no-activate-tool-window",(ex&0x20)!=0&&(ex&0x08000000)!=0&&(ex&0x80)!=0,new{ex});
            var rect=WindowRect(h);var mid=new Native.Point{X=rect.X+rect.Width/2,Y=rect.Y+rect.Height/2};
            Case("a-click-in-the-rows-reaches-the-window-below",Native.GetAncestor(Native.WindowFromPoint(mid),2)!=h,new{rect=new{rect.X,rect.Y,rect.Width,rect.Height}});
            Native.GetWindowDisplayAffinity(h,out uint affinity);Case("affinity-0x11-read-back",affinity==0x11,new{affinity});
            Case("overlay-is-bottom-right",rect.Right<=work.Right&&rect.Bottom<=work.Bottom&&rect.Left>work.Left+work.Width/2&&rect.Top>work.Top+work.Height/2,new{rect=new{rect.X,rect.Y,rect.Width,rect.Height},work=new{work.X,work.Y,work.Width,work.Height}});
            // Capture: the rows are dark on a white window. Excluded = the capture sees only white; the control (exclusion off) sees the dark rows, so the first line cannot pass for the wrong reason.
            static bool DarkPx(Color c)=>c.R<80&&c.G<80&&c.B<90;
            double excluded=Share(Inner(rect),DarkPx);
            Native.SetWindowDisplayAffinity(h,0);await Task.Delay(400);
            double control=Share(Inner(rect),DarkPx);
            Native.SetWindowDisplayAffinity(h,0x11);await Task.Delay(200);
            Case("screen-capture-does-not-contain-the-rows",excluded<0.005,new{darkShare=excluded});
            Case("control-capture-sees-the-rows-when-not-excluded",control>0.15,new{darkShare=control});
            // Time: a finished tool fades after 2.5 s, a failure stays 8 s.
            await Task.Delay(3200);overlay.Tick();
            Case("done-rows-are-gone-after-2-9-s-the-failure-stays",!overlay.Texts.Any(t=>t.Contains("ms"))&&overlay.Texts.Any(t=>t.EndsWith("· kein Fenster")),new{texts=overlay.Texts});
            overlay.Clear();Case("clear-hides-the-window",!overlay.IsVisible);
        }
        finally{try{overlay?.Close();under.Close();}catch{ }}
    });
    public static int Feedback()=>Harness("feedback",async Case=>
    {
        var screen=System.Windows.Forms.Screen.PrimaryScreen!;var monitor=screen.Bounds;
        double scale=Native.ScaleAt(monitor.Left+monitor.Width/2,monitor.Top+monitor.Height/2);
        var paint=Color.FromArgb(0x12,0xab,0x34);
        static bool Near(Color c,Color p)=>Math.Abs(c.R-p.R)<=6&&Math.Abs(c.G-p.G)<=6&&Math.Abs(c.B-p.B)<=6;
        // A white window in the middle of the monitor shows the tint; a flat green "screenshot" shows where the thumbnail is and whether a capture can see it.
        var centre=new Rectangle(monitor.Left+monitor.Width/2-300,monitor.Top+monitor.Height/2-200,600,400);
        var under=White(centre,scale);under.Show();
        FeedbackOverlay? overlay=null;
        try
        {
            await Task.Delay(400);
            IntPtr fgBefore=Native.GetForegroundWindow();
            using var source=new Bitmap(1600,900);using(var g=Graphics.FromImage(source))g.Clear(paint);
            var clock=System.Diagnostics.Stopwatch.StartNew();var thumb=FeedbackThumb.Make(source,FeedbackOverlay.ThumbWidthPx);
            Case("thumbnail-is-made-in-memory-220-px-wide",thumb.PixelWidth==220&&thumb.PixelHeight==124&&thumb.IsFrozen,new{thumb.PixelWidth,thumb.PixelHeight,ms=clock.ElapsedMilliseconds});
            overlay=new FeedbackOverlay(screen,thumb,true,false);overlay.Show();await Task.Delay(500);
            var h=new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
            Case("capture-exclusion-confirmed",overlay.CaptureExcluded);
            var tr=overlay.ThumbRect;
            Case("thumbnail-bottom-right-about-220-px",tr is {} t&&t.Width is >=218 and <=230&&t.Right>monitor.Left+monitor.Width*3/4&&t.Bottom>monitor.Top+monitor.Height/2&&t.Right<=monitor.Right&&t.Bottom<=monitor.Bottom,new{tr});
            var inner=tr is {} r?Inner(r):default;
            double greenExcluded=Share(inner,c=>Near(c,paint));
            using(var shot=Shot(new Rectangle(centre.X+centre.Width/2,centre.Y+centre.Height/2,1,1)))
            {
                var px=shot.GetPixel(0,0);
                Case("screen-capture-has-no-tint-and-no-thumbnail",greenExcluded<0.01&&px.R>=253&&px.G>=253&&px.B>=253,new{greenShare=greenExcluded,px=px.Name});
            }
            Native.SetWindowDisplayAffinity(h,0);await Task.Delay(500);
            double greenControl=Share(inner,c=>Near(c,paint));
            using(var shot=Shot(new Rectangle(centre.X+centre.Width/2,centre.Y+centre.Height/2,1,1)))
            {
                var px=shot.GetPixel(0,0);
                // 12 % purple over white: G 255*0.88+43*0.12 = 229.
                Case("control-capture-sees-thumbnail-and-a-tint-of-at-most-12-percent",greenControl>0.5&&px.G is >=215 and <=240&&px.R>=235,new{greenShare=greenControl,px=px.Name,g=px.G});
            }
            Native.SetWindowDisplayAffinity(h,0x11);await Task.Delay(200);
            Case("overlay-did-not-take-the-focus",Native.GetForegroundWindow()==fgBefore);
            long ex=Native.GetWindowLongPtr(h,-20).ToInt64();
            Case("click-through-no-activate-tool-window",(ex&0x20)!=0&&(ex&0x08000000)!=0&&(ex&0x80)!=0,new{ex});
            var mid=new Native.Point{X=centre.X+centre.Width/2,Y=centre.Y+centre.Height/2};
            Case("a-click-through-the-tint-reaches-the-window-below",Native.GetAncestor(Native.WindowFromPoint(mid),2)!=h);
            overlay.Close();overlay=null;
            // The real thing with its animation: flash 180 ms, thumbnail fades, the window closes itself.
            var done=new TaskCompletionSource<bool>();long started=Environment.TickCount64;
            overlay=new FeedbackOverlay(screen,thumb);overlay.Finished+=()=>done.TrySetResult(true);overlay.Show();
            bool finished=await Task.WhenAny(done.Task,Task.Delay(5000))==done.Task;long took=Environment.TickCount64-started;
            Case("thumbnail-fades-out-in-about-1-4-s-after-a-short-hold",finished&&took is >=1400 and <=3000,new{took});
            overlay.Close();overlay=null;
            // ScreenFlash: shown only with the confirmed exclusion, one at a time.
            var first=ScreenFlash.Show(monitor,thumb);var second=ScreenFlash.Show(monitor,thumb);
            Case("screen-flash-shows-and-replaces-the-previous-one",first.Shown&&second.Shown,new{first,second});
            await Task.Delay(2400);
        }
        finally{try{overlay?.Close();under.Close();}catch{ }}
    });
    // How long the app-owned part of a cold start takes on this runner: WebView2 environment, controller, first page. Not a pass/fail on speed, only on "it starts".
    public static int Startup()=>Harness("startup",async Case=>
    {
        var config=AppConfig.Parse("{\"deskUrl\":\"https://example.com\"}");
        string temp=Path.Combine(Path.GetTempPath(),"ScreenAgent-startup-"+Guid.NewGuid());Directory.CreateDirectory(temp);
        using var client=new DeviceClient(config,null,Path.Combine(temp,"device.bin"));
        var bridge=new AgentBridge(config,client);var marks=new Dictionary<string,long>();
        bridge.Milestone+=(name,ms)=>marks.TryAdd(name,ms);
        bridge.TestOriginResponses("<html><body>startup fixture</body></html>");
        try
        {
            bridge.StartHidden();
            for(int i=0;i<300&&!marks.ContainsKey("page");i++)await Task.Delay(100);
            Case("webview2-environment-controller-and-page-came-up",marks.ContainsKey("environment")&&marks.ContainsKey("controller")&&marks.ContainsKey("page"),new{marks});
            Case("milestones-are-in-order",marks.GetValueOrDefault("environment")<=marks.GetValueOrDefault("controller")&&marks.GetValueOrDefault("controller")<=marks.GetValueOrDefault("page"),new{marks});
            Console.Error.WriteLine("startup ms: "+JsonSerializer.Serialize(marks));
        }
        finally{bridge.AllowClose=true;bridge.Close();}
    });
}
