using System.Text.Json;
using System.Windows;
using ScreenAgent.Core;
namespace ScreenAgent;
// --selftest-canvas-panel: the drawing panel on a real desktop with a synthetic page (no network): it opens without taking the focus, Windows confirms the
// capture exclusion, a real screen capture does not contain the panel (and does contain it once the exclusion is switched off, so the test can see), close and toggle work.
// Exit 0 passed, 1 failed.
internal static class PanelSelftests
{
    const string Page="<html><head><title>Screen Agent canvas panel fixture</title></head><body style='margin:0;background:#12ab34'><div style='position:fixed;inset:0;background:#12ab34'></div></body></html>";
    static readonly System.Drawing.Color Paint=System.Drawing.Color.FromArgb(0x12,0xab,0x34);
    static bool Near(System.Drawing.Color c)=>Math.Abs(c.R-Paint.R)<=3&&Math.Abs(c.G-Paint.G)<=3&&Math.Abs(c.B-Paint.B)<=3;
    // Share of the pixels of this rectangle (inner 70 %: no frame, no shadow) that have the page colour in a real screen capture.
    static double PaintShare(System.Drawing.Rectangle r,string? save=null)
    {
        var inner=new System.Drawing.Rectangle(r.X+r.Width*15/100,r.Y+r.Height*25/100,r.Width*70/100,r.Height*60/100);
        using var bmp=new System.Drawing.Bitmap(inner.Width,inner.Height);
        using(var g=System.Drawing.Graphics.FromImage(bmp))g.CopyFromScreen(inner.Location,System.Drawing.Point.Empty,inner.Size);
        if(save is not null)bmp.Save(save,System.Drawing.Imaging.ImageFormat.Png);
        long hits=0,total=0;
        for(int y=0;y<bmp.Height;y+=3)for(int x=0;x<bmp.Width;x+=3){total++;if(Near(bmp.GetPixel(x,y)))hits++;}
        return total==0?0:(double)hits/total;
    }
    static void FullShot(string path)
    {
        try
        {
            var b=System.Windows.Forms.Screen.PrimaryScreen!.Bounds;using var image=new System.Drawing.Bitmap(b.Width,b.Height);
            using(var g=System.Drawing.Graphics.FromImage(image))g.CopyFromScreen(b.Location,System.Drawing.Point.Empty,b.Size);
            image.Save(path,System.Drawing.Imaging.ImageFormat.Png);
        }
        catch{ /* evidence only */ }
    }
    public static int Run()
    {
        var results=new List<object>();bool failed=false;
        void Case(string name,bool pass,object? detail=null){results.Add(new{name,pass,detail});if(!pass)failed=true;}
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};int exit=1;
        app.Startup+=async (_,_)=>
        {
            CanvasPanel? panel=null;AgentBridge? bridge=null;Window? reference=null;
            try
            {
                Directory.CreateDirectory("artifacts");
                var config=AppConfig.Parse("{\"deskUrl\":\"https://example.com\"}");
                string temp=Path.Combine(Path.GetTempPath(),"ScreenAgent-panel-"+Guid.NewGuid());Directory.CreateDirectory(temp);
                using var client=new DeviceClient(config,null,Path.Combine(temp,"device.bin"));
                try{File.Delete(CanvasPanel.StateFile);}catch{ }
                bridge=new AgentBridge(config,client);bridge.TestOriginResponses(Page);bridge.StartHidden();
                for(int i=0;i<500&&!bridge.BrowserReady;i++)await Task.Delay(100);
                Case("desk-browser-ready",bridge.BrowserReady&&bridge.WebEnvironment is not null,new{stage=bridge.InitStage,error=bridge.InitErrorType});
                if(!bridge.BrowserReady)throw new InvalidOperationException("no WebView2 on this runner");
                // Something else is in front: a plain window of ours. If Windows refuses it, whatever is in front stays the reference.
                reference=new Window{Title="Screen Agent panel selftest reference",Width=360,Height=240,Left=60,Top=60,ShowInTaskbar=true};reference.Show();reference.Activate();await Task.Delay(500);
                var refHandle=new System.Windows.Interop.WindowInteropHelper(reference).Handle;
                IntPtr before=Native.GetForegroundWindow();
                Case("reference-window-in-front",before!=IntPtr.Zero,new{refInFront=before==refHandle});
                var panelLog=new List<string>();
                panel=new CanvasPanel(bridge,false,(type,data)=>panelLog.Add(type+":"+data["action"]+":"+data["why"]));
                bool closedByUser=false;panel.ClosedByUser+=()=>closedByUser=true;
                Case("affinity-confirmed-before-showing",panel.Prepare()&&!panel.IsVisible,new{panel.AffinityOk});
                Case("panel-is-a-small-topmost-window-with-a-taskbar-button",panel.Topmost&&panel.ShowInTaskbar&&!panel.ShowActivated&&panel.ResizeMode==ResizeMode.CanResize&&panel.Title=="Screen Agent · Zeichnung"&&panel.MinWidth==320&&panel.MinHeight==240&&panel.Width==440&&panel.Height==340);
                bool opened=await panel.Open();
                Case("panel-opens",opened&&panel.IsOpen,new{panel.InitStage});
                for(int i=0;i<200&&!panel.PageLoaded;i++)await Task.Delay(100);
                Case("panel-page-loaded",panel.PageLoaded,new{panel.InitStage});
                await Task.Delay(700);
                IntPtr after=Native.GetForegroundWindow();
                Case("opening-did-not-change-the-foreground-window",after==before&&after!=panel.Hwnd,new{before=before.ToInt64(),after=after.ToInt64(),panel=panel.Hwnd.ToInt64(),restores=panel.ForegroundRestores});
                Native.GetWindowDisplayAffinity(panel.Hwnd,out uint affinity);
                Case("affinity-0x11-read-back",affinity==0x11,new{affinity});
                long ex=Native.GetWindowLongPtr(panel.Hwnd,-20).ToInt64();
                Case("panel-topmost-and-has-a-taskbar-button",(ex&0x8)!=0&&(ex&0x80)==0,new{ex});   // no WS_EX_TOOLWINDOW: Windows gives it a taskbar button
                var rect=panel.ScreenRect;
                var screens=System.Windows.Forms.Screen.AllScreens;
                var works=screens.Select(s=>StatusToast.Px(s.WorkingArea)).ToArray();
                var box=new PxRect(rect.X,rect.Y,rect.Width,rect.Height);
                Case("panel-inside-a-work-area-at-least-minimum-size",works.Any(w=>w.Contains(box))&&rect.Width>=(int)(320*Native.ScaleAt(rect.X+rect.Width/2,rect.Y+rect.Height/2))-2,new{rect=new{rect.X,rect.Y,rect.Width,rect.Height},works});
                await panel.CaptureBrowser("artifacts/canvas-panel-browser.png");
                using(var shot=new System.Drawing.Bitmap("artifacts/canvas-panel-browser.png"))
                    Case("browser-really-shows-the-synthetic-page",Near(shot.GetPixel(shot.Width/2,shot.Height/2)),new{shot.Width,shot.Height,centre=shot.GetPixel(shot.Width/2,shot.Height/2).Name});
                await Task.Delay(300);
                double excluded=PaintShare(rect,"artifacts/canvas-panel-capture-excluded.png");FullShot("artifacts/canvas-panel-screen.png");
                Case("screen-capture-does-not-contain-the-panel",excluded<0.01,new{paintShare=excluded});
                // Control: with the exclusion off, the same capture does see the panel. Without this the line above could pass for the wrong reason.
                Native.SetWindowDisplayAffinity(panel.Hwnd,0);await Task.Delay(500);
                double visible=PaintShare(rect,"artifacts/canvas-panel-capture-control.png");FullShot("artifacts/canvas-panel-screen-unexcluded.png");
                Native.SetWindowDisplayAffinity(panel.Hwnd,0x11);await Task.Delay(300);
                Native.GetWindowDisplayAffinity(panel.Hwnd,out uint again);
                Case("control-capture-sees-the-panel-when-not-excluded",visible>0.5,new{paintShare=visible});
                Case("affinity-back-to-0x11",again==0x11,new{again});
                // Close = hide, then toggle brings it back; the window in front never changes.
                panel.Close();await Task.Delay(300);
                Case("close-hides-and-counts-as-closed-by-user",!panel.IsVisible&&closedByUser);
                Case("foreground-unchanged-after-close",Native.GetForegroundWindow()==before);
                bool reopened=await panel.Open();await Task.Delay(500);
                Case("toggle-opens-again",reopened&&panel.IsOpen&&Native.GetForegroundWindow()==before,new{fg=Native.GetForegroundWindow().ToInt64(),before=before.ToInt64()});
                // A box the user chose is remembered and used again.
                await Task.Delay(1300);
                int nx=works[0].X+80,ny=works[0].Y+80;
                Native.SetWindowPos(panel.Hwnd,IntPtr.Zero,nx,ny,500,400,0x14);await Task.Delay(1200);
                int[]? stored=null;try{stored=JsonSerializer.Deserialize<int[]>(File.ReadAllText(CanvasPanel.StateFile));}catch{ }
                Case("a-box-the-user-chose-is-saved",stored is {Length:4}&&Math.Abs(stored[0]-nx)<=2&&Math.Abs(stored[1]-ny)<=2&&Math.Abs(stored[2]-500)<=2&&Math.Abs(stored[3]-400)<=2,new{stored});
                closedByUser=false;panel.HidePanel(false);
                Case("hide-by-the-app-is-not-a-close-by-the-user",!panel.IsVisible&&!closedByUser);
                await panel.Open();await Task.Delay(400);var again2=panel.ScreenRect;
                Case("saved-box-is-used-on-the-next-open",Math.Abs(again2.X-nx)<=2&&Math.Abs(again2.Y-ny)<=2&&Math.Abs(again2.Width-500)<=2&&Math.Abs(again2.Height-400)<=2&&Native.GetForegroundWindow()==before,new{again2.X,again2.Y,again2.Width,again2.Height});
                // Minimised by the user: it stays minimised whatever the agent draws, the foreground never changes, until he restores it.
                IntPtr fgNow=Native.GetForegroundWindow();
                panel.WindowState=WindowState.Minimized;await Task.Delay(500);
                Case("user-minimise-is-tracked",Native.IsIconic(panel.Hwnd)&&panel.PanelState==PanelState.Minimized,new{state=panel.PanelState.ToString()});
                bool p1=await panel.Pending(),p2=await panel.Pending();await Task.Delay(500);
                Case("pending-while-minimised-changes-nothing",!p1&&!p2&&Native.IsIconic(panel.Hwnd)&&panel.PanelState==PanelState.Minimized&&Native.GetForegroundWindow()==fgNow,
                    new{p1,p2,iconic=Native.IsIconic(panel.Hwnd),fg=Native.GetForegroundWindow().ToInt64(),was=fgNow.ToInt64()});
                Case("ignored-pending-is-logged-with-the-reason",panelLog.Contains("canvas.panel:ignored:pending:minimized"),new{panelLog});
                // The taskbar button brings it back (the equivalent here: the window state the click sets).
                panel.WindowState=WindowState.Normal;await Task.Delay(500);
                Case("restored-by-the-user-is-open-again",!Native.IsIconic(panel.Hwnd)&&panel.PanelState==PanelState.Open&&panelLog.Contains("canvas.panel:restored:user"),new{state=panel.PanelState.ToString(),panelLog});
                Case("pending-while-open-does-nothing",!await panel.Pending());
                // Closed by the user: a pending diagram opens nothing; the tray or shortcut still does.
                panel.Close();await Task.Delay(300);
                bool p3=await panel.Pending();
                Case("pending-after-user-close-stays-closed",!p3&&!panel.IsVisible,new{p3,visible=panel.IsVisible});
                bool shown=await panel.UserShow("selftest");await Task.Delay(500);
                Case("user-show-opens-it-after-a-close",shown&&panel.IsOpen,new{shown});
                panel.WindowState=WindowState.Minimized;await Task.Delay(400);
                bool back=await panel.UserShow("selftest");await Task.Delay(500);
                Case("user-show-restores-a-minimised-panel",back&&!Native.IsIconic(panel.Hwnd)&&panel.PanelState==PanelState.Open,new{back,fg=Native.GetForegroundWindow().ToInt64(),was=before.ToInt64()});
                // Front only the first time a diagram shows up by itself.
                panel.HidePanel(false);
                bool first=await panel.Pending();bool frontAfterFirst=panel.FrontDone;
                panel.HidePanel(false);
                bool pendingAgain=await panel.Pending();
                Case("first-pending-opens-to-the-front-later-ones-open-without-raising",first&&frontAfterFirst&&pendingAgain&&panelLog.Contains("canvas.panel:shown:pending:first")&&panelLog.Contains("canvas.panel:shown:pending:again"),new{first,pendingAgain,panelLog});
                Case("foreground-still-unchanged-at-the-end",Native.GetForegroundWindow()==before||Native.GetForegroundWindow()==fgNow,new{fg=Native.GetForegroundWindow().ToInt64(),before=before.ToInt64()});
                try{File.Delete(CanvasPanel.StateFile);}catch{ }
                exit=failed?1:0;
            }
            catch(Exception e){Case("selftest-crashed",false,e.ToString());exit=1;}
            finally
            {
                Console.WriteLine(JsonSerializer.Serialize(new{test="canvas-panel",status=failed?"failed":"passed",results}));
                try{panel?.Terminate();reference?.Close();if(bridge is not null){bridge.AllowClose=true;bridge.Close();}}catch{ }
                app.Shutdown(exit);
            }
        };
        app.Run();return exit;
    }
}
