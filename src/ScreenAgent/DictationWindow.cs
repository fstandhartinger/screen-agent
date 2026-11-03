using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using ScreenAgent.Core;
namespace ScreenAgent;
// "Diktat nicht verarbeitet: <Grund>" and the result of a retry that has no window to go to. Like PromptFallbackWindow: small, topmost, never takes the
// focus (the buttons work without it), left out of screen captures. The text only reaches the clipboard when the user clicks "Text kopieren".
internal sealed class DictationWindow : Window
{
    static DictationWindow? current;
    readonly TextBlock heading=new(){FontWeight=FontWeights.SemiBold,FontSize=13,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,4)};
    readonly TextBlock path=new(){FontSize=11,TextWrapping=TextWrapping.Wrap,Opacity=0.75,Margin=new Thickness(0,0,0,6)};
    readonly TextBox box=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontSize=12,Visibility=Visibility.Collapsed,Height=110,Margin=new Thickness(0,0,0,6)};
    readonly StackPanel buttons=new(){Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,6,0,0)};
    string text="";
    public string Heading=>heading.Text;
    public string Path=>path.Text;
    public IReadOnlyList<string> ButtonNames=>buttons.Children.OfType<Button>().Select(b=>(string)b.Content).ToList();
    public bool AffinityOk {get;private set;}
    DictationWindow()
    {
        Title="Screen Agent · Diktat";Topmost=true;ShowActivated=false;ShowInTaskbar=false;ResizeMode=ResizeMode.NoResize;WindowStyle=WindowStyle.ToolWindow;
        Width=440;SizeToContent=SizeToContent.Height;MinHeight=130;WindowStartupLocation=WindowStartupLocation.Manual;
        var dock=new StackPanel{Margin=new Thickness(10)};dock.Children.Add(heading);dock.Children.Add(path);dock.Children.Add(box);dock.Children.Add(buttons);Content=dock;
        SourceInitialized+=(_,_)=>AffinityOk=OverlayStyle.Apply(this,false);   // no activation, tool window, not in captures; clicks still work
        Closed+=(_,_)=>{if(ReferenceEquals(current,this))current=null;};
    }
    Button Add(string label,Action click,bool closes=false)
    {
        var b=new Button{Content=label,Padding=new Thickness(10,3,10,3),Margin=new Thickness(6,0,0,0)};
        b.Click+=(_,_)=>{try{click();}catch{ /* a button must never take the app down */ }if(closes)Close();};
        buttons.Children.Add(b);return b;
    }
    void Fill(string head,string file,string? shown,IEnumerable<(string Label,Action Click,bool Closes)> actions)
    {
        heading.Text=head;path.Text=file;text=shown??"";box.Text=text;box.Visibility=text.Length>0?Visibility.Visible:Visibility.Collapsed;
        buttons.Children.Clear();
        foreach(var (label,click,closes) in actions)Add(label,click,closes);
    }
    void Present()
    {
        var h=new WindowInteropHelper(this).EnsureHandle();
        var screen=System.Windows.Forms.Screen.PrimaryScreen??System.Windows.Forms.Screen.AllScreens[0];
        double s=Native.ScaleAt(screen.WorkingArea.X+screen.WorkingArea.Width/2,screen.WorkingArea.Y+screen.WorkingArea.Height/2);
        if(!IsVisible)Show();
        UpdateLayout();
        var box2=WindowPlacement.BottomRight(StatusToast.Px(screen.WorkingArea),(int)(ActualWidth*s),(int)(ActualHeight*s),(int)Math.Round(14*s));
        Native.SetWindowPos(h,(IntPtr)(-1),box2.X,box2.Y-(int)Math.Round(WindowPlacement.PanelNoteReserveDip*s),box2.W,box2.H,0x10);
    }
    static DictationWindow Window2(){if(current is {IsVisible:true} w)return w;return current=new DictationWindow();}
    static string Folder(string path)=>System.IO.Path.GetDirectoryName(path)??path;
    static void OpenFolder(string file){System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",System.IO.File.Exists(file)?$"/select,\"{file}\"":$"\"{Folder(file)}\""){UseShellExecute=true});}
    static void Copy(string text){try{Clipboard.SetText(text);}catch{ /* the window stays; he can select nothing, but the file is there */ }}
    // Failure: reason, where the file is, and what can be done. "Text kopieren" only when a partial or caption text exists.
    internal static DictationWindow ShowFailure(DictationMeta m,string reason,string file,string? partial,Action retry)
    {
        var w=Window2();var acts=new List<(string,Action,bool)>{("Erneut versuchen",retry,true),("Ordner öffnen",()=>OpenFolder(file),false)};
        if(!string.IsNullOrWhiteSpace(partial))acts.Add(("Text kopieren",()=>Copy(partial),false));
        acts.Add(("Schließen",()=>{},true));
        w.Fill($"Diktat nicht verarbeitet: {reason}",$"{m.Label} · {file}",partial,acts);w.Present();return w;
    }
    // A retry that worked but the original window is gone or no longer fits: the text, never on the clipboard by itself.
    internal static DictationWindow ShowResult(DictationMeta m,string head,string text)
    {
        var w=Window2();
        w.Fill(head,m.Label,text,[("Text kopieren",()=>Copy(text),false),("Schließen",()=>{},true)]);w.Present();return w;
    }
    internal static void CloseCurrent(){current?.Close();}
}
