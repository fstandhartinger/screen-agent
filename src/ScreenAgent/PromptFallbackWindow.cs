using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using ScreenAgent.Core;
namespace ScreenAgent;
// Shown when a Main Agent prompt could not be typed. Small, topmost, never takes the focus; left out of screen captures. The text only reaches the
// clipboard when the user clicks "Kopieren".
internal sealed class PromptFallbackWindow : Window
{
    readonly string body;
    readonly TextBlock heading=new(){FontWeight=FontWeights.SemiBold,FontSize=13,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,6)};
    readonly TextBox box=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontSize=12};
    readonly Button copy=new(){Content="Kopieren",Padding=new Thickness(12,3,12,3),Margin=new Thickness(0,0,8,0)};
    readonly Button close=new(){Content="Schließen",Padding=new Thickness(12,3,12,3)};
    public string Heading=>heading.Text;
    public bool ReadOnlyText=>box.IsReadOnly;
    public PromptFallbackWindow(string code,string text)
    {
        body=text;heading.Text="Nicht eingetippt: "+PromptCodes.Reason(code);box.Text=text;
        Title="Screen Agent · Prompt";Topmost=true;ShowActivated=false;ShowInTaskbar=false;ResizeMode=ResizeMode.CanResize;WindowStyle=WindowStyle.ToolWindow;
        Width=420;Height=260;MinWidth=300;MinHeight=180;WindowStartupLocation=WindowStartupLocation.Manual;
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,8,0,0)};buttons.Children.Add(copy);buttons.Children.Add(close);
        var grid=new DockPanel{Margin=new Thickness(10)};
        DockPanel.SetDock(heading,Dock.Top);DockPanel.SetDock(buttons,Dock.Bottom);grid.Children.Add(heading);grid.Children.Add(buttons);grid.Children.Add(box);
        Content=grid;
        copy.Click+=(_,_)=>{try{Clipboard.SetText(body);copy.Content="Kopiert";}catch{copy.Content="Kopieren nicht möglich";}};
        close.Click+=(_,_)=>Close();
        SourceInitialized+=(_,_)=>Native.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle,0x11);
    }
    public void Present()
    {
        var h=new WindowInteropHelper(this).EnsureHandle();
        var screen=System.Windows.Forms.Screen.PrimaryScreen??System.Windows.Forms.Screen.AllScreens[0];
        double s=Native.ScaleAt(screen.WorkingArea.X+screen.WorkingArea.Width/2,screen.WorkingArea.Y+screen.WorkingArea.Height/2);
        var box2=WindowPlacement.BottomRight(StatusToast.Px(screen.WorkingArea),(int)(Width*s),(int)(Height*s),(int)Math.Round(14*s));
        Native.SetWindowPos(h,IntPtr.Zero,box2.X,box2.Y-(int)Math.Round(WindowPlacement.PanelNoteReserveDip*s),box2.W,box2.H,0x4|0x10);
        Show();
    }
    // The one way the app shows it: the text as the user would have seen it typed, no focus change.
    internal static PromptFallbackWindow Show(string code,string text){var w=new PromptFallbackWindow(code,PromptText.Normalize(text));w.Present();return w;}
    internal void ClickCopyForTests()=>copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
