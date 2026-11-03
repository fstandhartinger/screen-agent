using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using ScreenAgent.Core;
namespace ScreenAgent;
// Shown when a Main Agent prompt could not be typed. Small, topmost, never takes the focus; left out of screen captures. The text only reaches the
// clipboard when the user clicks "Kopieren".
internal sealed class PromptFallbackWindow : Window
{
    string body="";
    static PromptFallbackWindow? current;
    readonly TextBlock heading=new(){FontWeight=FontWeights.SemiBold,FontSize=13,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,6)};
    readonly TextBox box=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontSize=12};
    readonly Button copy=new(){Content="Kopieren",Padding=new Thickness(12,3,12,3),Margin=new Thickness(0,0,8,0)};
    readonly Button close=new(){Content="Schließen",Padding=new Thickness(12,3,12,3)};
    public string Heading=>heading.Text;
    public bool ReadOnlyText=>box.IsReadOnly;
    // True when Windows confirmed WDA_EXCLUDEFROMCAPTURE (read back). If not, the window still shows: he must get his text.
    public bool AffinityOk {get;private set;}
    public PromptFallbackWindow(string code,string text,int charsAlreadyInBox=0)
    {
        Fill(code,text,charsAlreadyInBox);
        Title="Screen Agent · Prompt";Topmost=true;ShowActivated=false;ShowInTaskbar=false;ResizeMode=ResizeMode.CanResize;WindowStyle=WindowStyle.ToolWindow;
        Width=420;Height=260;MinWidth=300;MinHeight=180;WindowStartupLocation=WindowStartupLocation.Manual;
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,8,0,0)};buttons.Children.Add(copy);buttons.Children.Add(close);
        var grid=new DockPanel{Margin=new Thickness(10)};
        DockPanel.SetDock(heading,Dock.Top);DockPanel.SetDock(buttons,Dock.Bottom);grid.Children.Add(heading);grid.Children.Add(buttons);grid.Children.Add(box);
        Content=grid;
        copy.Click+=(_,_)=>{try{Clipboard.SetText(body);copy.Content="Kopiert";}catch{copy.Content="Kopieren nicht möglich";}};
        close.Click+=(_,_)=>Close();
        SourceInitialized+=(_,_)=>
        {
            var h=new WindowInteropHelper(this).Handle;
            AffinityOk=Native.SetWindowDisplayAffinity(h,0x11)&&Native.GetWindowDisplayAffinity(h,out uint now)&&now==0x11;
        };
        Closed+=(_,_)=>{if(ReferenceEquals(current,this))current=null;};
    }
    void Fill(string code,string text,int charsAlreadyInBox){body=text;heading.Text=PromptCodes.Heading(code,charsAlreadyInBox);box.Text=text;copy.Content="Kopieren";}
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
    // A second failure while the window is still up reuses it with the newest text instead of stacking another one on top.
    internal static PromptFallbackWindow Show(string code,string text,int charsAlreadyInBox=0)
    {
        string shown=PromptText.Normalize(text);
        if(current is {IsVisible:true} w){w.Fill(code,shown,charsAlreadyInBox);w.Present();return w;}
        var fresh=new PromptFallbackWindow(code,shown,charsAlreadyInBox);current=fresh;fresh.Present();return fresh;
    }
    internal void ClickCopyForTests()=>copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
