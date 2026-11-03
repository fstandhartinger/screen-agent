using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using ScreenAgent.Core;
namespace ScreenAgent;
// "Diktat nicht verarbeitet: <Grund>", the text of a dictation that could not be typed into its field (or that a retry produced), and a notice that stays until it
// is closed. Like PromptFallbackWindow: small, topmost, never takes the focus (the buttons work without it, so a click does not move the target away), left out of
// screen captures; where Windows does not confirm that, the text is not shown at all (only the buttons put it somewhere). The text only reaches the clipboard when
// the user clicks "Text kopieren", and only goes into a field when he clicks "In aktuelles Feld einfügen".
internal sealed class DictationWindow : Window
{
    static DictationWindow? current;
    readonly TextBlock heading=new(){FontWeight=FontWeights.SemiBold,FontSize=13,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,4)};
    readonly TextBlock path=new(){FontSize=11,TextWrapping=TextWrapping.Wrap,Opacity=0.75,Margin=new Thickness(0,0,0,6)};
    readonly TextBox box=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,FontSize=12,Visibility=Visibility.Collapsed,Height=110,Margin=new Thickness(0,0,0,6)};
    readonly StackPanel buttons=new(){Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,6,0,0)};
    string text="";
    Action? onClosed;   // the user closed the window with the text: the dictation counts as delivered
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
        Closed+=(_,_)=>
        {
            if(ReferenceEquals(current,this))current=null;
            var cb=onClosed;onClosed=null;
            try{cb?.Invoke();}catch(Exception ex){Diag.Fail("dictation-window-closed",ex);}   /* the window is gone either way */
        };
    }
    Button Add(string label,Action click,bool closes=false)
    {
        var b=new Button{Content=label,Padding=new Thickness(10,3,10,3),Margin=new Thickness(6,0,0,0)};
        b.Click+=(_,_)=>{try{click();}catch(Exception ex){Diag.Fail("dictation-window-button",ex);}   /* a button must never take the app down */if(closes)Close();};
        buttons.Children.Add(b);return b;
    }
    void Fill(string head,string file,string? shown,IEnumerable<(string Label,Action Click,bool Closes)> actions,Action? closed=null)
    {
        onClosed=closed;   // a window that shows something else now does not report the earlier one as closed: that dictation stays in the tray
        heading.Text=head;path.Text=file;text=shown??"";box.Text=text;box.Visibility=text.Length>0?Visibility.Visible:Visibility.Collapsed;
        buttons.Children.Clear();
        foreach(var (label,click,closes) in actions)Add(label,click,closes);
    }
    void Present()
    {
        var h=new WindowInteropHelper(this).EnsureHandle();
        if(!AffinityOk&&box.Visibility==Visibility.Visible){box.Visibility=Visibility.Collapsed;path.Text+=" · Text nur über die Knöpfe";OverlayStyle.RefusedOnce("dictation-text");}   // never draw his words where a screenshot could catch them
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
    static void Copy(string text){try{Clipboard.SetText(text);}catch(Exception ex){Diag.Fail("dictation-window-copy",ex);}   /* the window stays; the file is there */ }
    // Failure: reason, where the file is, and what can be done. "Text kopieren" only when a partial or caption text exists.
    internal static DictationWindow ShowFailure(DictationMeta m,string reason,string file,string? partial,Action? retry)
    {
        Diag.Event("ui.window",new(){["window"]="dictation-failure",["id"]=m.Id,["msg"]=reason});
        var w=Window2();var acts=new List<(string,Action,bool)>();
        if(retry is not null)acts.Add(("Erneut versuchen",retry,true));
        acts.Add(("Ordner öffnen",()=>OpenFolder(file),false));
        if(!string.IsNullOrWhiteSpace(partial))acts.Add(("Text kopieren",()=>Copy(partial),false));
        acts.Add(("Schließen",()=>{},true));
        w.Fill($"Diktat nicht verarbeitet: {reason}",$"{m.Label} · {file}",partial,acts);w.Present();return w;
    }
    // The dictation is transcribed but did not go into its field (changed, protected, or a retry): the text stays here until he closes the window, and never reaches the
    // clipboard or a field by itself. insert(text) types into whatever field has the focus now (checked like any dictation target); closed: he closed the window.
    internal static DictationWindow ShowResult(DictationMeta m,string head,string text,Func<string,Task<bool>>? insert=null,Action? closed=null)
    {
        Diag.Event("ui.window",new(){["window"]="dictation-result",["id"]=m.Id,["msg"]=head,["chars"]=text.Length});
        var w=Window2();var acts=new List<(string,Action,bool)>();
        if(insert is not null)acts.Add(("In aktuelles Feld einfügen",()=>_=w.InsertAsync(m,insert),false));
        acts.Add(("Text kopieren",()=>Copy(text),false));acts.Add(("Schließen",()=>{},true));
        w.Fill(head,m.Label,text,acts,closed);w.Present();return w;
    }
    async Task InsertAsync(DictationMeta m,Func<string,Task<bool>> insert)
    {
        bool ok;
        try{ok=await insert(text);}catch(Exception ex){Diag.Fail("dictation-window-insert",ex);ok=false;}
        Diag.Event("dictation.insert",new(){["id"]=m.Id,["ok"]=ok,["chars"]=text.Length});
        if(ok)Close();else heading.Text="Nicht eingefügt · das Feld, in dem der Cursor steht, passt nicht (geschützt oder erhöht)";
    }
    // A warning that stays until it is closed (the microphone went away during a dictation).
    internal static DictationWindow ShowNotice(string head,string detail)
    {
        Diag.Event("ui.window",new(){["window"]="dictation-notice",["msg"]=head});
        var w=Window2();
        w.Fill(head,detail,null,[("Schließen",()=>{},true)]);w.Present();return w;
    }
    internal static void CloseCurrent(){current?.Close();}
}
