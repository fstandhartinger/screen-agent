using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
namespace ScreenAgent;
// The drawing panel: a small window beside the user's work that shows the board the agent draws on (Desk page /?panel=canvas). It is left out of every screen
// capture (otherwise the picture the agent gets would contain its own drawing), so it is never opened when Windows does not confirm that. Its browser is a second
// controller of the Desk window's environment: same profile, same cookies. Opening never takes the focus away from what the user is working in.
internal sealed class CanvasPanel : Window
{
    readonly AgentBridge bridge;
    CoreWebView2Controller? controller;
    CoreWebView2? browser;
    readonly DispatcherTimer saveTimer=new(){Interval=TimeSpan.FromMilliseconds(600)};
    long ignoreMovesUntil;
    bool initStarted,hiding,allowClose,navigating;
    public static string StateFile=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScreenAgent","canvas-panel.json");
    // True once Windows has confirmed WDA_EXCLUDEFROMCAPTURE (read back, not just accepted).
    public bool AffinityOk {get;private set;}
    public bool PageLoaded {get;private set;}
    public string InitStage {get;private set;}="not-started";
    public int ForegroundRestores {get;private set;}
    // The user closed the panel (window close button); not raised when the app hides it itself.
    public event System.Action? ClosedByUser;
    public CanvasPanel(AgentBridge bridge)
    {
        this.bridge=bridge;
        Title="Screen Agent · Zeichnung";Topmost=true;ShowActivated=false;ShowInTaskbar=false;ResizeMode=ResizeMode.CanResize;WindowStyle=WindowStyle.SingleBorderWindow;
        WindowStartupLocation=WindowStartupLocation.Manual;MinWidth=WindowPlacement.PanelMinWidthDip;MinHeight=WindowPlacement.PanelMinHeightDip;
        Width=WindowPlacement.PanelDefaultWidthDip;Height=WindowPlacement.PanelDefaultHeightDip;Background=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(15,19,23));
        SourceInitialized+=(_,_)=>
        {
            var h=new WindowInteropHelper(this).Handle;
            bool set=Native.SetWindowDisplayAffinity(h,0x11);
            AffinityOk=set&&Native.GetWindowDisplayAffinity(h,out uint now)&&now==0x11;
            HwndSource.FromHwnd(h)?.AddHook(WindowMessage);
        };
        IsVisibleChanged+=(_,_)=>ResizeBrowser();
        Closing+=(_,e)=>{if(allowClose)return;e.Cancel=true;SaveBounds();bool wasOpen=IsVisible&&!hiding;Hide();if(wasOpen)ClosedByUser?.Invoke();};
        Closed+=(_,_)=>controller?.Close();
        saveTimer.Tick+=(_,_)=>{saveTimer.Stop();SaveBounds();};
        SizeChanged+=(_,_)=>{ResizeBrowser();Moved();};
        LocationChanged+=(_,_)=>{controller?.NotifyParentWindowPositionChanged();Moved();};
    }
    void Moved(){if(IsVisible&&Environment.TickCount64>ignoreMovesUntil){saveTimer.Stop();saveTimer.Start();}}
    IntPtr WindowMessage(IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(msg is 0x0005 or 0x02e0)Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new System.Action(ResizeBrowser));
        return IntPtr.Zero;
    }
    IntPtr Handle=>new WindowInteropHelper(this).EnsureHandle();
    // Creates the window handle (no show) and reports whether the capture exclusion holds.
    public bool Prepare(){_=Handle;return AffinityOk;}
    public bool IsOpen=>IsVisible&&WindowState!=WindowState.Minimized;
    // Opens (or shows again) without activating. False: not opened (no confirmed capture exclusion, or the Desk's browser is not up yet).
    public async Task<bool> Open()
    {
        if(!Prepare())return false;
        if(bridge.WebEnvironment is null)return false;
        IntPtr before=Native.GetForegroundWindow();
        hiding=false;
        if(!IsVisible)
        {
            Place(Handle);ShowActivated=false;Show();Place(Handle);
        }
        if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;
        ResizeBrowser();
        if(!initStarted){initStarted=true;await InitBrowser();}
        else if(browser is not null&&!PageLoaded&&!navigating)Navigate();   // the page did not load (Desk down, offline): try again. A signed-out Desk page is no failure; it says so itself.
        // A browser that took the focus on creation gives it back.
        var now=Native.GetForegroundWindow();
        if(before!=IntPtr.Zero&&now!=before&&now==Handle){Native.SetForegroundWindow(before);ForegroundRestores++;}
        return browser is not null;
    }
    public void Terminate(){allowClose=true;Close();}
    // Hides because the session ended or the user toggled it; `byUser` decides whether it counts as closed by him.
    public void HidePanel(bool byUser)
    {
        if(!IsVisible)return;
        SaveBounds();hiding=true;Hide();hiding=false;
        if(byUser)ClosedByUser?.Invoke();
    }
    async Task InitBrowser()
    {
        try
        {
            InitStage="controller";
            var env=bridge.WebEnvironment!;
            controller=await env.CreateCoreWebView2ControllerAsync(Handle);
            controller.BoundsMode=CoreWebView2BoundsMode.UseRawPixels;
            controller.ShouldDetectMonitorScaleChanges=true;
            controller.DefaultBackgroundColor=System.Drawing.Color.FromArgb(255,15,19,23);
            controller.RasterizationScaleChanged+=(_,_)=>ResizeBrowser();
            browser=controller.CoreWebView2;
            bridge.InstallTestResponses(browser);
            browser.NavigationStarting+=(_,e)=>{if(!bridge.IsTrusted(e.Uri))e.Cancel=true;};
            browser.NavigationCompleted+=(_,e)=>{PageLoaded=e.IsSuccess;navigating=false;ResizeBrowser();};
            browser.NewWindowRequested+=(_,e)=>{e.Handled=true;};
            browser.PermissionRequested+=(_,e)=>{e.State=CoreWebView2PermissionState.Deny;};
            ResizeBrowser();
            InitStage="navigating";
            Navigate();
            InitStage="ready";
        }
        catch(Exception ex){InitStage="failed:"+ex.GetType().Name;initStarted=false;controller?.Close();controller=null;browser=null;}
    }
    void Navigate(){navigating=true;browser!.Navigate(new Uri(bridge.DeskOrigin,"/?panel=canvas").AbsoluteUri);}
    void ResizeBrowser()
    {
        if(controller is null)return;
        var h=new WindowInteropHelper(this).Handle;
        if(h!=IntPtr.Zero&&Native.GetClientRect(h,out var r))
            controller.Bounds=new System.Drawing.Rectangle(0,0,Math.Max(0,r.Right-r.Left),Math.Max(0,r.Bottom-r.Top));
        controller.IsVisible=IsVisible&&WindowState!=WindowState.Minimized;
    }
    // Saved box if the user moved it and it is still on a monitor; otherwise a corner of a monitor that does not hold the window he works in.
    void Place(IntPtr h)
    {
        if(h==IntPtr.Zero)return;
        var screens=System.Windows.Forms.Screen.AllScreens;
        var works=screens.Select(s=>StatusToast.Px(s.WorkingArea)).ToArray();
        var scales=works.Select(w=>Native.ScaleAt(w.X+w.W/2,w.Y+w.H/2)).ToArray();
        IntPtr fg=Native.GetForegroundWindow();
        int fgIndex=-1;
        if(fg!=IntPtr.Zero&&fg!=h&&Native.GetWindowRect(fg,out var fr))
        {
            var fs=System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(fr.Left,fr.Top,Math.Max(1,fr.Right-fr.Left),Math.Max(1,fr.Bottom-fr.Top)));
            fgIndex=Array.FindIndex(screens,s=>s.DeviceName==fs.DeviceName);
        }
        var cur=System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        int curIndex=Math.Max(0,Array.FindIndex(screens,s=>s.DeviceName==cur.DeviceName));
        PxRect? saved=null;
        try{if(File.Exists(StateFile)){var r=System.Text.Json.JsonSerializer.Deserialize<int[]>(File.ReadAllText(StateFile));if(r is {Length:4})saved=new(r[0],r[1],r[2],r[3]);}}catch{ /* a bad state file means defaults */ }
        var box=WindowPlacement.CanvasPanel(works,scales,fgIndex,curIndex,saved);
        ignoreMovesUntil=Environment.TickCount64+1000;
        Native.SetWindowPos(h,IntPtr.Zero,box.X,box.Y,box.W,box.H,0x4|0x10);   // NOZORDER | NOACTIVATE
    }
    void SaveBounds()
    {
        try
        {
            if(!IsVisible||WindowState!=WindowState.Normal||Environment.TickCount64<=ignoreMovesUntil)return;   // only a box the user chose
            Native.GetWindowRect(Handle,out var r);int w=r.Right-r.Left,hh=r.Bottom-r.Top;if(w<=0||hh<=0||r.Left<-10000)return;
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);File.WriteAllText(StateFile,System.Text.Json.JsonSerializer.Serialize(new[]{r.Left,r.Top,w,hh}));
        }
        catch{ /* remembering the box is a convenience */ }
    }
    internal System.Drawing.Rectangle ScreenRect=>Native.GetWindowRect(Handle,out var r)?new(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top):default;
    internal IntPtr Hwnd=>Handle;
    internal async Task CaptureBrowser(string path)
    {
        if(browser is null)throw new InvalidOperationException("Panel browser not ready");
        using var output=File.Create(path);await browser.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,output);
    }
}
