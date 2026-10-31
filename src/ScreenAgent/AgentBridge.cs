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
    public string? SessionId {get;private set;}
    public event System.Action? SessionChanged;
    bool Trusted(string url)=>Uri.TryCreate(url,UriKind.Absolute,out var u)&&u.GetLeftPart(UriPartial.Authority)==origin.GetLeftPart(UriPartial.Authority);
    public AgentBridge(AppConfig config,DeviceClient client)
    {
        this.client=client;origin=new Uri(config.DeskUrl);
        Title="Screen Agent · Desk";Width=420;Height=640;Topmost=true;ShowActivated=false;
        Left=SystemParameters.WorkArea.Right-Width;Top=SystemParameters.WorkArea.Top;Content=browser;
        SourceInitialized+=(_,_)=>
        {
            OverlayStyle.Apply(this,false);
            var h=new System.Windows.Interop.WindowInteropHelper(this).Handle;
            System.Windows.Interop.HwndSource.FromHwnd(h)?.AddHook((IntPtr hwnd,int msg,IntPtr w,IntPtr l,ref bool handled)=>
            {
                if(msg==0x21) // WM_MOUSEACTIVATE: explicit user click may activate the login/editor.
                {
                    long ex=Native.GetWindowLongPtr(hwnd,-20).ToInt64();
                    Native.SetWindowLongPtr(hwnd,-20,(IntPtr)(ex&~0x08000000L));
                    handled=true;return (IntPtr)1; // MA_ACTIVATE
                }
                return IntPtr.Zero;
            });
        };
        Deactivated+=(_,_)=>OverlayStyle.Apply(this,false);
        Loaded+=async (_,_)=>
        {
            try
            {
                string profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScreenAgent","WebView2");
                var env=await CoreWebView2Environment.CreateAsync(null,profile);
                await browser.EnsureCoreWebView2Async(env);
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
                                client.Pair(new(m.GetProperty("token").GetString()!,m.GetProperty("deviceId").GetString()!,m.GetProperty("expiresAt").GetDateTimeOffset()));
                                browser.Source=new Uri(origin,"/?companion=1");break;
                            case "session":
                                SessionId=m.TryGetProperty("sid",out var sid)&&sid.ValueKind==System.Text.Json.JsonValueKind.String?sid.GetString():null;
                                SessionChanged?.Invoke();break;
                            case "mute-ack":muteAck?.TrySetResult(true);break;
                        }
                    }
                    catch{ /* Invalid page message never changes device credentials. */ }
                };
                browser.CoreWebView2.NewWindowRequested+=(_,e)=>{e.Handled=true;};
                browser.CoreWebView2.PermissionRequested+=(_,e)=>
                {
                    // Explicit WebView permission prompt for the Desk's own microphone request.
                    if(e.PermissionKind==CoreWebView2PermissionKind.Microphone)
                        e.State=Trusted(e.Uri)&&MessageBox.Show(this,"Allow the Desk to use your microphone?","Screen Agent",MessageBoxButton.YesNo)==MessageBoxResult.Yes?CoreWebView2PermissionState.Allow:CoreWebView2PermissionState.Deny;
                };
                browser.Source=new Uri(origin,client.Paired?"/?companion=1":"/device");
            }
            catch(Exception ex){Content=new System.Windows.Controls.TextBlock{Text="WebView2 Runtime required: "+ex.Message,TextWrapping=TextWrapping.Wrap};}
        };
    }
    public async Task<bool> Mute(bool on)
    {
        if(SessionId is null)return true;
        if(browser.CoreWebView2 is null)return false;
        var ack=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);muteAck=ack;
        browser.CoreWebView2.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new{type="mute",on}));
        try{return await Task.WhenAny(ack.Task,Task.Delay(500))==ack.Task&&await ack.Task;}
        finally{if(muteAck==ack)muteAck=null;}
    }
    public void Screen(string[] ids)=>browser.CoreWebView2?.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new{type="screen",ids}));
    public void Disconnect(){SessionId=null;client.Disconnect();browser.Source=new Uri(origin,"/device");SessionChanged?.Invoke();}
}
