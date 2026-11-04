using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using ScreenAgent.Core;
namespace ScreenAgent;
// Tray "Diktat-Verlauf": the dictations still on disk, newest first, with their text, to copy or to transcribe again. Local only; follows the normal retention (24 h by default).
// Left out of screen captures; if Windows does not confirm that, no text is drawn, the buttons still work.
internal sealed class DictationHistoryWindow : Window
{
    static DictationHistoryWindow? current;
    sealed class Row(DictationMeta m)
    {
        public DictationMeta Meta=>m;
        public string Line=>DictationHistory.Line(m);
    }
    readonly Func<List<DictationMeta>> load;readonly Func<DictationMeta,Task> again;
    readonly ListBox list=new(){Width=300,Margin=new Thickness(0,0,10,0),DisplayMemberPath="Line"};
    readonly TextBox box=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(6)};
    readonly Button copy=new(){Content="Text kopieren",Padding=new Thickness(12,4,12,4),Margin=new Thickness(6,0,0,0)},redo=new(){Content="Neu transkribieren",Padding=new Thickness(12,4,12,4),Margin=new Thickness(6,0,0,0)},folder=new(){Content="Ordner öffnen",Padding=new Thickness(12,4,12,4)};
    readonly TextBlock status=new(){Opacity=0.8,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(10,0,0,0)};
    public bool AffinityOk {get;private set;}
    public int Count=>list.Items.Count;
    public DictationHistoryWindow(Func<List<DictationMeta>> load,Func<DictationMeta,Task> again)
    {
        this.load=load;this.again=again;
        Title="Screen Agent · Diktat-Verlauf";Width=860;Height=500;MinWidth=620;MinHeight=320;WindowStartupLocation=WindowStartupLocation.CenterScreen;FontFamily=new System.Windows.Media.FontFamily("Segoe UI");FontSize=13;
        var root=new DockPanel{Margin=new Thickness(12)};
        var buttons=new DockPanel{Margin=new Thickness(0,10,0,0)};DockPanel.SetDock(buttons,Dock.Bottom);
        var right=new StackPanel{Orientation=System.Windows.Controls.Orientation.Horizontal};right.Children.Add(redo);right.Children.Add(copy);DockPanel.SetDock(right,Dock.Right);buttons.Children.Add(right);
        var left=new StackPanel{Orientation=System.Windows.Controls.Orientation.Horizontal};left.Children.Add(folder);left.Children.Add(status);buttons.Children.Add(left);
        root.Children.Add(buttons);DockPanel.SetDock(list,Dock.Left);root.Children.Add(list);root.Children.Add(box);Content=root;
        ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);
        list.SelectionChanged+=(_,_)=>Selected();
        copy.Click+=(_,_)=>{if(list.SelectedItem is Row r&&DictationHistory.TextOf(r.Meta) is {} t){status.Text=DictationWindow.Copy(t)?"Kopiert":"Zwischenablage war belegt, bitte noch einmal";Diag.Event("dictation.copy",new(){["id"]=r.Meta.Id,["chars"]=t.Length,["via"]="history"});}};
        redo.Click+=async (_,_)=>{if(list.SelectedItem is Row r){status.Text="Wird neu transkribiert …";try{await again(r.Meta);}catch(Exception ex){Diag.Fail("dictation-history-again",ex);}status.Text="";Refill();}};
        folder.Click+=(_,_)=>{if(list.SelectedItem is Row r)DictationWindow.OpenFolder(r.Meta.Paths.FirstOrDefault()??r.Meta.MetaPath);};
        SourceInitialized+=(_,_)=>{var h=new WindowInteropHelper(this).Handle;AffinityOk=Native.SetWindowDisplayAffinity(h,0x11)&&Native.GetWindowDisplayAffinity(h,out uint now)&&now==0x11u;if(!AffinityOk)OverlayStyle.RefusedOnce("dictation-history");Selected();};
        Closed+=(_,_)=>{if(ReferenceEquals(current,this))current=null;};
        Refill();
    }
    internal static void Open(Func<List<DictationMeta>> load,Func<DictationMeta,Task> again)
    {
        Diag.Event("ui.window",new(){["window"]="dictation-history"});
        if(current is {IsVisible:true} w){w.Refill();w.Activate();return;}
        current=new DictationHistoryWindow(load,again);current.Show();current.Activate();
    }
    internal void Refill()
    {
        string? keep=(list.SelectedItem as Row)?.Meta.Id;
        var rows=load().Select(m=>new Row(m)).ToList();
        list.ItemsSource=rows;
        list.SelectedItem=rows.FirstOrDefault(r=>r.Meta.Id==keep)??rows.FirstOrDefault();
        if(rows.Count==0){status.Text="Noch keine Diktate gespeichert";Selected();}
    }
    void Selected()
    {
        var m=(list.SelectedItem as Row)?.Meta;
        string? text=m is null?null:DictationHistory.TextOf(m);
        box.Text=m is null?"":text is null?"(kein Text gespeichert; mit „Neu transkribieren“ aus der Aufnahme holen)":AffinityOk?text:"(Text verborgen: Windows bestätigt den Schutz vor Bildschirmfotos nicht. „Text kopieren“ funktioniert.)";
        copy.IsEnabled=text is not null;
        redo.IsEnabled=m is not null&&m.Paths.Any(File.Exists);
        folder.IsEnabled=m is not null;
    }
}
