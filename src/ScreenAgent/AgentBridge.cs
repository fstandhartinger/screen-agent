using System.Windows;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;
namespace ScreenAgent;
internal sealed class AgentBridge : Window
{
    readonly WebView2 browser=new();
    public AgentBridge()
    {
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
                browser.CoreWebView2.NewWindowRequested+=(_,e)=>{e.Handled=true;};
                browser.CoreWebView2.PermissionRequested+=(_,e)=>
                {
                    // Explicit WebView permission prompt for the Desk's own microphone request.
                    if(e.PermissionKind==CoreWebView2PermissionKind.Microphone)
                        e.State=e.Uri.StartsWith("https://desk.app.mintapis.com/",StringComparison.Ordinal)&&MessageBox.Show(this,"Allow the Desk to use your microphone?","Screen Agent",MessageBoxButton.YesNo)==MessageBoxResult.Yes?CoreWebView2PermissionState.Allow:CoreWebView2PermissionState.Deny;
                };
                browser.Source=new Uri("https://desk.app.mintapis.com/?companion=1");
            }
            catch(Exception ex){Content=new System.Windows.Controls.TextBlock{Text="WebView2 Runtime required: "+ex.Message,TextWrapping=TextWrapping.Wrap};}
        };
    }
    public void Dictating(bool value)
    {
        // Protocol placeholder only: server/page acknowledgement is required before real audio capture is enabled.
        browser.CoreWebView2?.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new{type="companion-dictation",muted=value}));
    }
}
