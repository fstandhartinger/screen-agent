using System.Windows;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;
namespace ScreenAgent;
internal sealed class AgentBridge : Window
{
    readonly WebView2 browser=new();
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
    Task initTask=Task.CompletedTask;
    bool initStarted,userShown,offscreen;
    readonly System.Diagnostics.Stopwatch startup=new();
    public bool FallbackUsed {get;private set;}
    public long ReadyMs {get;private set;}
    public static int FallbackSeconds=6;
    public bool BrowserReady=>browser.CoreWebView2 is not null;
    public static string StateFile=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScreenAgent","desk-window.json");
    public AgentBridge(AppConfig config,DeviceClient client)
    {
        this.client=client;this.config=config;origin=new Uri(config.DeskUrl);
        // An ordinary window: not topmost, in the taskbar while open, resizable and minimisable. It stays hidden until
        // the user asks for it (tray) and hides instead of closing, because the page hosts the voice session.
        Title="Screen Agent · Desk";Topmost=false;ShowInTaskbar=true;ResizeMode=ResizeMode.CanResize;WindowStyle=WindowStyle.SingleBorderWindow;
        WindowStartupLocation=WindowStartupLocation.Manual;MinWidth=WindowPlacement.DeskMinWidthDip;MinHeight=WindowPlacement.DeskMinHeightDip;
        Width=WindowPlacement.DeskDefaultWidthDip;Height=WindowPlacement.DeskDefaultHeightDip;Content=browser;
        SourceInitialized+=(_,_)=>Native.SetWindowDisplayAffinity(new System.Windows.Interop.WindowInteropHelper(this).Handle,0x11); // Desk content is not screen context
        Closing+=(_,e)=>{if(!AllowClose){e.Cancel=true;SaveBounds();Hide();}};
        saveTimer.Tick+=(_,_)=>{saveTimer.Stop();SaveBounds();};
        SizeChanged+=(_,_)=>{if(IsVisible){saveTimer.Stop();saveTimer.Start();}};
        LocationChanged+=(_,_)=>{if(IsVisible){saveTimer.Stop();saveTimer.Start();}};
        Loaded+=(_,_)=>Begin();
    }
    void Begin(){if(initStarted)return;initStarted=true;initTask=InitBrowser();}
    async Task InitBrowser()
    {
        try
        {
            string profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScreenAgent","WebView2");
            // The page keeps the voice session alive while this window is hidden: no background throttling.
            var options=new CoreWebView2EnvironmentOptions("--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows");
            var env=await CoreWebView2Environment.CreateAsync(null,profile,options);
            await browser.EnsureCoreWebView2Async(env);
            ReadyMs=startup.ElapsedMilliseconds;
            browser.CoreWebView2.NavigationStarting+=(_,e)=>{if(!Trusted(e.Uri))e.Cancel=true;else{SessionId=null;SessionChanged?.Invoke();}};
            browser.CoreWebView2.WebMessageReceived+=(_,e)=>
            {
                if(!Trusted(e.Source))return;
                try
                {
                    using var json=System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);var m=json.RootElement;
                    switch(m.GetProperty("type").GetString())
                    {
                        case "device-token":
                            client.Pair(new(m.GetProperty("token").GetString()!,m.GetProperty("deviceId").GetString()!,DeviceProtocol.Expiry(m.GetProperty("expiresAt"))));
                            browser.Source=new Uri(origin,"/?companion=1&voice="+Uri.EscapeDataString(config.VoiceProvider));break;
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
            browser.CoreWebView2.NewWindowRequested+=(_,e)=>{e.Handled=true;};
            browser.CoreWebView2.PermissionRequested+=(_,e)=>
            {
                // Explicit prompt for the Desk's own microphone request; WebView2 remembers the answer in its profile.
                if(e.PermissionKind==CoreWebView2PermissionKind.Microphone)
                    e.State=Trusted(e.Uri)&&MessageBox.Show("Darf Desk dein Mikrofon verwenden?","Screen Agent",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes?CoreWebView2PermissionState.Allow:CoreWebView2PermissionState.Deny;
            };
            browser.Source=new Uri(origin,client.Paired?"/?companion=1&voice="+Uri.EscapeDataString(config.VoiceProvider):"/device");
        }
        catch(Exception ex)
        {
            Content=new System.Windows.Controls.TextBlock{Text="WebView2-Laufzeit erforderlich: "+ex.Message,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(12)};
            ShowDesk(); // the user must see why nothing works
        }
    }
    // Start with the window hidden. First try to bring the page up without ever showing the window; if the control
    // has not come alive after a few seconds, show it off-screen without taskbar activation and hide it again when ready.
    public void StartHidden()
    {
        startup.Restart();
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        Begin();
        var fallback=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(FallbackSeconds)};
        fallback.Tick+=async (_,_)=>
        {
            fallback.Stop();
            if(BrowserReady||IsVisible)return;
            FallbackUsed=true;
            var h=new System.Windows.Interop.WindowInteropHelper(this).Handle;
            // Off-screen, no activation, no taskbar button (tool window) while the control comes alive.
            long ex=Native.GetWindowLongPtr(h,-20).ToInt64();Native.SetWindowLongPtr(h,-20,(IntPtr)(ex|0x80L));
            Native.SetWindowPos(h,IntPtr.Zero,-32000,-32000,400,300,0x14);ShowActivated=false;offscreen=true;Show();
            await Task.WhenAny(initTask,Task.Delay(15000));
            if(offscreen)
            {
                Hide();offscreen=false;
                Native.SetWindowLongPtr(h,-20,(IntPtr)(Native.GetWindowLongPtr(h,-20).ToInt64()&~0x80L));
            }
        };
        fallback.Start();
    }
    public void ShowDesk()
    {
        userShown=true;
        var h=new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        if(!IsVisible||offscreen)
        {
            if(offscreen)
            {
                offscreen=false;
                Native.SetWindowLongPtr(h,-20,(IntPtr)(Native.GetWindowLongPtr(h,-20).ToInt64()&~0x80L));
            }
            ApplyPlacement(h);ShowActivated=true;
            if(!IsVisible)Show();
            ApplyPlacement(h); // WPF applies its own first-show position; set ours again
        }
        if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;
        Activate();
    }
    public void HideDesk(){SaveBounds();Hide();userShown=false;}
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
            if(!IsVisible||WindowState!=WindowState.Normal)return;
            var b=Bounds(new System.Windows.Interop.WindowInteropHelper(this).Handle);if(b.W<=0||b.H<=0)return;
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);File.WriteAllText(StateFile,System.Text.Json.JsonSerializer.Serialize(new[]{b.X,b.Y,b.W,b.H}));
        }
        catch{ /* remembering the size is a convenience */ }
    }
    public async Task<bool> Voice(bool on,string provider)
    {
        if(browser.CoreWebView2 is null)return false;
        var ack=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);voiceAck=ack;requestedVoice=on;
        try
        {
            browser.CoreWebView2.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new{type="voice",on,provider=provider=="default"?"xai":provider}));
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
        if(browser.CoreWebView2 is null)return false;
        var ack=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);muteAck=ack;requestedMute=on;
        try
        {
            browser.CoreWebView2.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new{type="mute",on}));
            return await Task.WhenAny(ack.Task,Task.Delay(500))==ack.Task&&await ack.Task;
        }
        catch{return false;}
        finally{if(muteAck==ack)muteAck=null;}
    }
    public void Screen(string[] ids)=>browser.CoreWebView2?.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new{type="screen",ids}));
    public void Disconnect(){SessionId=null;client.Disconnect();browser.Source=new Uri(origin,"/device");SessionChanged?.Invoke();}
}
