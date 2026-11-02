using System.Windows;
using System.Net;
using System.Net.Http;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
namespace ScreenAgent;
internal sealed class AgentBridge : Window
{
    CoreWebView2Controller? controller;
    CoreWebView2? browser;
    CancellationTokenSource? pairing;
    readonly CancellationTokenSource lifetime=new();
    readonly DeviceClient client;
    readonly Uri origin;
    TaskCompletionSource<bool>? muteAck;
    bool requestedMute;
    TaskCompletionSource<bool>? voiceAck;
    bool requestedVoice;
    public bool VoiceOn {get;private set;}
    readonly SemaphoreSlim muteGate=new(1,1);
    public bool AllowClose {get;set;}
    public string? SessionId {get;private set;}
    public event System.Action? SessionChanged;
    bool Trusted(string url)=>Uri.TryCreate(url,UriKind.Absolute,out var u)&&u.GetLeftPart(UriPartial.Authority)==origin.GetLeftPart(UriPartial.Authority);
    readonly AppConfig config;
    readonly System.Windows.Threading.DispatcherTimer saveTimer=new(){Interval=TimeSpan.FromMilliseconds(600)};
    bool initStarted,testPage;
    (string state,string text)? pairStatus;
    readonly System.Diagnostics.Stopwatch startup=new();
    public long ReadyMs {get;private set;}
    public bool BrowserReady=>browser is not null;
    public static string StateFile=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScreenAgent","desk-window.json");
    public AgentBridge(AppConfig config,DeviceClient client)
    {
        this.client=client;this.config=config;origin=new Uri(config.DeskUrl);
        // An ordinary window: not topmost, in the taskbar while open, resizable and minimisable. It stays hidden until
        // the user asks for it (tray) and hides instead of closing, because the page hosts the voice session.
        Title="Screen Agent · Desk";Topmost=false;ShowInTaskbar=true;ResizeMode=ResizeMode.CanResize;WindowStyle=WindowStyle.SingleBorderWindow;
        WindowStartupLocation=WindowStartupLocation.Manual;MinWidth=WindowPlacement.DeskMinWidthDip;MinHeight=WindowPlacement.DeskMinHeightDip;
        Width=WindowPlacement.DeskDefaultWidthDip;Height=WindowPlacement.DeskDefaultHeightDip;Background=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(15,19,23));
        SourceInitialized+=(_,_)=>
        {
            var h=new WindowInteropHelper(this).Handle;
            Native.SetWindowDisplayAffinity(h,0x11); // Desk content is not screen context
            HwndSource.FromHwnd(h)?.AddHook(WindowMessage);
        };
        IsVisibleChanged+=(_,_)=>ResizeBrowser();
        Closed+=(_,_)=>{lifetime.Cancel();pairing?.Cancel();controller?.Close();lifetime.Dispose();};
        Closing+=(_,e)=>{if(!AllowClose){e.Cancel=true;SaveBounds();Hide();}};
        saveTimer.Tick+=(_,_)=>{saveTimer.Stop();SaveBounds();};
        SizeChanged+=(_,_)=>{ResizeBrowser();if(IsVisible){saveTimer.Stop();saveTimer.Start();}};
        LocationChanged+=(_,_)=>{controller?.NotifyParentWindowPositionChanged();if(IsVisible){saveTimer.Stop();saveTimer.Start();}};
        Loaded+=(_,_)=>Begin();
    }
    void Begin(){if(initStarted)return;initStarted=true;_=InitBrowser();}
    async Task InitBrowser()
    {
        try
        {
            string profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScreenAgent","WebView2");
            // The page keeps the voice session alive while this window is hidden: no background throttling.
            var options=new CoreWebView2EnvironmentOptions("--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows");
            var env=await CoreWebView2Environment.CreateAsync(null,profile,options);
            // Direct HWND hosting deliberately uses device pixels. WPF's old WebView2 control retained its
            // off-screen 400x300 layout after a native resize, leaving a large white region at scaled DPI.
            controller=await env.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(this).EnsureHandle());
            controller.BoundsMode=CoreWebView2BoundsMode.UseRawPixels;
            controller.ShouldDetectMonitorScaleChanges=true;
            controller.DefaultBackgroundColor=System.Drawing.Color.FromArgb(255,15,19,23);
            controller.RasterizationScaleChanged+=(_,_)=>ResizeBrowser();
            browser=controller.CoreWebView2;
            ResizeBrowser();
            ReadyMs=startup.ElapsedMilliseconds;
            browser.NavigationStarting+=(_,e)=>{if(!Trusted(e.Uri)&&!(testPage&&e.Uri=="about:blank"))e.Cancel=true;else{SessionId=null;SessionChanged?.Invoke();}};
            browser.WebMessageReceived+=(_,e)=>
            {
                if(!Trusted(e.Source))return;
                try
                {
                    using var json=System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);var m=json.RootElement;
                    switch(m.GetProperty("type").GetString())
                    {
                        case "pairing-start": _=StartPairing();break;
                        case "session":
                            string? next=m.TryGetProperty("sid",out var sid)&&sid.ValueKind==System.Text.Json.JsonValueKind.String?sid.GetString():null;
                            if(next!=SessionId){SessionId=next;SessionChanged?.Invoke();}break;
                        case "voice-ack":
                            if(m.TryGetProperty("on",out var voiceOn)&&voiceOn.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                            {
                                VoiceOn=voiceOn.GetBoolean();
                                if(VoiceOn==requestedVoice)voiceAck?.TrySetResult(true);
                                else if(m.TryGetProperty("state",out var state)&&state.GetString()=="error")voiceAck?.TrySetResult(false);
                            }
                            break;
                        case "mute-ack":if(!m.TryGetProperty("on",out var on)||on.GetBoolean()==requestedMute)muteAck?.TrySetResult(true);break;
                    }
                }
                catch{ /* Invalid page message never changes device credentials. */ }
            };
            browser.NewWindowRequested+=(_,e)=>{e.Handled=true;};
            browser.NavigationCompleted+=(_,_)=>{ResizeBrowser();if(pairStatus is {} status)PairingStatus(status.state,status.text);};
            browser.PermissionRequested+=(_,e)=>
            {
                // Explicit prompt for the Desk's own microphone request; WebView2 remembers the answer in its profile.
                if(e.PermissionKind==CoreWebView2PermissionKind.Microphone)
                    // Win32 box: topmost and foreground, so it cannot hide behind other windows while the Desk is hidden.
                    e.State=Trusted(e.Uri)&&Native.MessageBox(IntPtr.Zero,"Darf Desk dein Mikrofon verwenden?","Screen Agent",0x4|0x20|0x10000|0x40000)==6?CoreWebView2PermissionState.Allow:CoreWebView2PermissionState.Deny;
            };
            if(client.Paired)
            {
                try{await OpenSignedIn(lifetime.Token);}
                catch(HttpRequestException e) when(e.StatusCode==HttpStatusCode.Unauthorized){client.Disconnect();OpenPairing();}
                catch{OpenPairing();PairingStatus("error","Desk konnte nicht geladen werden. Bitte erneut verbinden.");}
            }
            else OpenPairing();
        }
        catch(Exception)
        {
            Content=new System.Windows.Controls.TextBlock{Text="Desk konnte nicht geöffnet werden. Bitte prüfe deine Internetverbindung und die WebView2-Laufzeit.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(12)};
            ShowDesk(); // the user must see why nothing works
        }
    }
    // An HWND controller can start against a hidden window. No temporary off-screen resize or WPF layout is needed.
    public void StartHidden()
    {
        startup.Restart();new WindowInteropHelper(this).EnsureHandle();Begin();
    }
    IntPtr WindowMessage(IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(msg is 0x0005 or 0x02e0) // WM_SIZE / WM_DPICHANGED: let WPF finish the native size change first.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new System.Action(ResizeBrowser));
        return IntPtr.Zero;
    }
    void ResizeBrowser()
    {
        if(controller is null)return;
        var h=new WindowInteropHelper(this).Handle;
        if(h!=IntPtr.Zero&&Native.GetClientRect(h,out var r))
            controller.Bounds=new System.Drawing.Rectangle(0,0,Math.Max(0,r.Right-r.Left),Math.Max(0,r.Bottom-r.Top));
        controller.IsVisible=IsVisible&&WindowState!=WindowState.Minimized;
    }
    void OpenPairing()=>browser?.Navigate(new Uri(origin,"/device?companion=1").AbsoluteUri);
    void PairingStatus(string state,string text)
    {
        pairStatus=(state,text);
        browser?.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new{type="pairing-status",state,text}));
    }
    async Task OpenSignedIn(CancellationToken ct)
    {
        var session=await client.WebSession(ct);
        pairStatus=null;
        ct.ThrowIfCancellationRequested();
        if(browser is null)return;
        var cookie=browser.CookieManager.CreateCookie("desk_s",session.cookie,origin.Host,"/");
        cookie.IsHttpOnly=true;cookie.IsSecure=true;cookie.SameSite=CoreWebView2CookieSameSiteKind.Strict;
        cookie.Expires=DateTimeOffset.FromUnixTimeMilliseconds(session.expiresAt).UtcDateTime;
        browser.CookieManager.AddOrUpdateCookie(cookie);
        browser.Navigate(new Uri(origin,"/?companion=1&voice="+Uri.EscapeDataString(config.VoiceProvider)).AbsoluteUri);
        SessionChanged?.Invoke();
    }
    async Task StartPairing()
    {
        if(pairing is not null)return;
        using var run=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        run.CancelAfter(TimeSpan.FromMinutes(5));pairing=run;
        try
        {
            PairingStatus("pending","Der Browser öffnet sich. Bestätige dort „Dieses Gerät verbinden“. Desk wartet hier.");
            // A temporary outage may have interrupted bootstrap while the saved device credential remains valid.
            if(client.Paired)
            {
                try{await OpenSignedIn(run.Token);return;}
                catch(HttpRequestException e) when(e.StatusCode==HttpStatusCode.Unauthorized){client.Disconnect();}
            }
            using var approval=new LoopbackApproval();
            var start=await client.StartPairing(approval.CallbackUrl,run.Token);
            // The server may only send an HTTPS URL on our configured Desk origin, with the one-time public nonce.
            var url=new Uri(start.browserUrl);
            if(!Trusted(url.AbsoluteUri)||url.Scheme!="https"||url.AbsolutePath!="/device"||url.Query!="?pair="+start.pairId)
                throw new InvalidDataException("Invalid pairing location");
            Process.Start(new ProcessStartInfo(url.AbsoluteUri){UseShellExecute=true});
            var proof=await approval.WaitForProof(run.Token);
            var deadline=DateTimeOffset.FromUnixTimeMilliseconds(start.expiresAt);
            while(DateTimeOffset.UtcNow<deadline)
            {
                await Task.Delay(2000,run.Token);
                var token=await client.PollPairing(start.pairId,start.pollToken,proof,run.Token);
                if(token is null)continue;
                run.Token.ThrowIfCancellationRequested();
                client.Pair(token);
                await OpenSignedIn(run.Token);
                return;
            }
            PairingStatus("error","Die Verbindung ist abgelaufen. Bitte noch einmal auf „Im Browser verbinden“ klicken.");
        }
        catch(OperationCanceledException){if(!lifetime.IsCancellationRequested)PairingStatus("error","Verbindung abgebrochen oder abgelaufen. Bitte erneut verbinden.");}
        catch(HttpRequestException e) when(e.StatusCode==HttpStatusCode.Gone){PairingStatus("error","Die Verbindung ist abgelaufen. Bitte erneut verbinden.");}
        catch{PairingStatus("error","Verbindung fehlgeschlagen. Bitte prüfe das Internet und versuche es erneut.");}
        finally{if(ReferenceEquals(pairing,run))pairing=null;}
    }
    public void ShowDesk()
    {
        var h=new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        if(!IsVisible)
        {
            ApplyPlacement(h);ShowActivated=true;
            if(!IsVisible)Show();
            ApplyPlacement(h); // WPF applies its own first-show position; set ours again
        }
        if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;
        ResizeBrowser();Activate();
    }
    public void HideDesk(){SaveBounds();Hide();}
    public async Task<string?> Eval(string script)=>browser is null?null:await browser.ExecuteScriptAsync(script);
    public bool DeskOpen=>IsVisible&&WindowState!=WindowState.Minimized;
    static PxRect Bounds(IntPtr h)=>Native.GetWindowRect(h,out var r)?new(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top):default;
    void ApplyPlacement(IntPtr h)
    {
        if(h==IntPtr.Zero)return;
        var all=System.Windows.Forms.Screen.AllScreens.Select(s=>StatusToast.Px(s.WorkingArea)).ToArray();
        PxRect? saved=null;
        try{if(File.Exists(StateFile)){var r=System.Text.Json.JsonSerializer.Deserialize<int[]>(File.ReadAllText(StateFile));if(r is {Length:4})saved=new(r[0],r[1],r[2],r[3]);}}catch{ /* a bad state file means defaults */ }
        var cursor=System.Windows.Forms.Cursor.Position;
        var screen=saved is {} sv?System.Windows.Forms.Screen.FromRectangle(new System.Drawing.Rectangle(sv.X,sv.Y,sv.W,sv.H)):System.Windows.Forms.Screen.FromPoint(cursor);
        var work=StatusToast.Px(screen.WorkingArea);
        var box=WindowPlacement.Desk(work,Native.ScaleAt(work.X+work.W/2,work.Y+work.H/2),saved,all);
        Native.SetWindowPos(h,IntPtr.Zero,box.X,box.Y,box.W,box.H,0x4|0x10); // NOZORDER | NOACTIVATE
    }
    void SaveBounds()
    {
        try
        {
            if(!IsVisible||WindowState!=WindowState.Normal)return;   // never remember the hidden start-up position
            var b=Bounds(new System.Windows.Interop.WindowInteropHelper(this).Handle);if(b.W<=0||b.H<=0||b.X<-10000)return;
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);File.WriteAllText(StateFile,System.Text.Json.JsonSerializer.Serialize(new[]{b.X,b.Y,b.W,b.H}));
        }
        catch{ /* remembering the size is a convenience */ }
    }
    public async Task<bool> Voice(bool on,string provider)
    {
        if(browser is null)return false;
        var ack=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);voiceAck=ack;requestedVoice=on;
        try
        {
            browser.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new{type="voice",on,provider=provider=="default"?"xai":provider}));
            return await Task.WhenAny(ack.Task,Task.Delay(2000))==ack.Task&&await ack.Task;
        }
        catch{return false;}
        finally{if(voiceAck==ack)voiceAck=null;}
    }
    public async Task<bool> Mute(bool on)
    {
        await muteGate.WaitAsync();
        try{return await MuteCore(on);}finally{muteGate.Release();}
    }
    async Task<bool> MuteCore(bool on)
    {
        if(SessionId is null)return true;
        if(browser is null)return false;
        var ack=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);muteAck=ack;requestedMute=on;
        try
        {
            browser.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new{type="mute",on}));
            return await Task.WhenAny(ack.Task,Task.Delay(500))==ack.Task&&await ack.Task;
        }
        catch{return false;}
        finally{if(muteAck==ack)muteAck=null;}
    }
    public void Screen(string[] ids)=>browser?.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new{type="screen",ids}));
    public void Disconnect()
    {
        pairing?.Cancel();pairStatus=null;SessionId=null;VoiceOn=false;client.Disconnect();
        browser?.CookieManager.DeleteCookies("desk_s",origin.AbsoluteUri);OpenPairing();SessionChanged?.Invoke();
    }
    // CI uses the real native controller at each rasterization scale; never enabled by the product path.
    internal Task TestConnect(CancellationToken ct)=>OpenSignedIn(ct);
    internal Task<List<CoreWebView2Cookie>> TestCookies()=>browser!.CookieManager.GetCookiesAsync(origin.AbsoluteUri);
    internal void SetTestScale(double scale)
    {
        if(controller is null)return;
        controller.ShouldDetectMonitorScaleChanges=false;controller.RasterizationScale=scale;ResizeBrowser();
    }
    internal System.Drawing.Rectangle BrowserBounds=>controller?.Bounds??default;
    internal async Task CaptureBrowser(string path)
    {
        if(browser is null)throw new InvalidOperationException("Browser not ready");
        using var output=File.Create(path);await browser.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,output);
    }
    internal void TestPage(string html){testPage=true;browser?.NavigateToString(html);}
}
