using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using ScreenAgent.Core;
namespace ScreenAgent;
// Tray "Wörterbuch bearbeiten": the words the dictation should spell correctly and which wrong forms it replaces. A normal window with a keyboard focus; left out of screen captures like every window of the app.
internal sealed class DictionaryWindow : Window
{
    static DictionaryWindow? current;
    sealed class Row(DictionaryEntry e)
    {
        public DictionaryEntry Entry=>e;
        public string Term=>e.Term;
        public string From=>string.Join(", ",e.ReplaceFrom);
        public string Source=>e.Source switch{"learned"=>"gelernt","manual"=>"von Hand","wispr"=>"Wispr","harvest"=>"gesammelt",_=>e.Source};
    }
    readonly DeviceClient client;readonly Action changed;
    readonly TextBox filter=new(){Margin=new Thickness(0,0,0,8),Padding=new Thickness(4)};
    readonly ListView list=new(){MinHeight=260};
    readonly TextBox term=new(){Padding=new Thickness(4),Margin=new Thickness(0,2,0,8)},from=new(){Padding=new Thickness(4),Margin=new Thickness(0,2,0,8)};
    readonly Button save=new(){Content="Speichern",Padding=new Thickness(14,4,14,4),Margin=new Thickness(6,0,0,0),IsDefault=true},delete=new(){Content="Löschen",Padding=new Thickness(14,4,14,4),Margin=new Thickness(6,0,0,0)},fresh=new(){Content="Neues Wort",Padding=new Thickness(14,4,14,4)};
    readonly TextBlock status=new(){Margin=new Thickness(0,8,0,0),Opacity=0.8,TextWrapping=TextWrapping.Wrap};
    List<Row> rows=[];
    string? editing;
    public int Count=>rows.Count;
    public DictionaryWindow(DeviceClient client,Action changed)
    {
        this.client=client;this.changed=changed;
        Title="Screen Agent · Wörterbuch";Width=680;Height=640;MinWidth=480;MinHeight=460;WindowStartupLocation=WindowStartupLocation.CenterScreen;FontFamily=new System.Windows.Media.FontFamily("Segoe UI");FontSize=13;
        var view=new GridView();
        view.Columns.Add(new GridViewColumn{Header="Wort",Width=210,DisplayMemberBinding=new Binding("Term")});
        view.Columns.Add(new GridViewColumn{Header="Ersetzt diese Falschschreibungen",Width=300,DisplayMemberBinding=new Binding("From")});
        view.Columns.Add(new GridViewColumn{Header="Quelle",Width=90,DisplayMemberBinding=new Binding("Source")});
        list.View=view;
        var root=new DockPanel{Margin=new Thickness(12)};
        var top=new StackPanel();top.Children.Add(new TextBlock{Text="Suchen",Margin=new Thickness(0,0,0,2)});top.Children.Add(filter);DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        var bottom=new StackPanel{Margin=new Thickness(0,10,0,0)};DockPanel.SetDock(bottom,Dock.Bottom);
        bottom.Children.Add(new TextBlock{Text="Wort, so wie es geschrieben werden soll"});bottom.Children.Add(term);
        bottom.Children.Add(new TextBlock{Text="Falschschreibungen, die dadurch ersetzt werden (mit Komma getrennt, darf leer bleiben)"});bottom.Children.Add(from);
        var buttons=new DockPanel();var right=new StackPanel{Orientation=System.Windows.Controls.Orientation.Horizontal};right.Children.Add(delete);right.Children.Add(save);DockPanel.SetDock(right,Dock.Right);buttons.Children.Add(right);buttons.Children.Add(new StackPanel{Orientation=System.Windows.Controls.Orientation.Horizontal,Children={fresh}});
        bottom.Children.Add(buttons);bottom.Children.Add(status);root.Children.Add(bottom);
        root.Children.Add(list);Content=root;
        filter.TextChanged+=(_,_)=>Fill();
        list.SelectionChanged+=(_,_)=>{if(list.SelectedItem is Row r)Edit(r.Entry);};
        fresh.Click+=(_,_)=>{list.SelectedItem=null;Edit(null);term.Focus();};
        save.Click+=async (_,_)=>await Save();
        delete.Click+=async (_,_)=>await Delete();
        SourceInitialized+=(_,_)=>Native.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle,0x11);
        Closed+=(_,_)=>{if(ReferenceEquals(current,this))current=null;};
        Loaded+=async (_,_)=>await Load();
        Edit(null);
    }
    internal static void Open(DeviceClient client,Action changed)
    {
        Diag.Event("ui.window",new(){["window"]="dictionary"});
        if(current is {IsVisible:true} w){w.Activate();return;}
        current=new DictionaryWindow(client,changed);current.Show();current.Activate();
    }
    void Edit(DictionaryEntry? e)
    {
        editing=e?.Term;term.Text=e?.Term??"";from.Text=e is null?"":string.Join(", ",e.ReplaceFrom);delete.IsEnabled=e is not null;
    }
    void Fill()
    {
        string f=filter.Text.Trim();
        list.ItemsSource=f.Length==0?rows:rows.Where(r=>r.Term.Contains(f,StringComparison.OrdinalIgnoreCase)||r.From.Contains(f,StringComparison.OrdinalIgnoreCase)).ToList();
    }
    internal void Show(IEnumerable<DictionaryEntry> entries){rows=entries.OrderBy(e=>e.Term,StringComparer.CurrentCultureIgnoreCase).Select(e=>new Row(e)).ToList();Fill();status.Text=$"{rows.Count} Wörter";}
    async Task Load()
    {
        status.Text="Lädt …";
        try{using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(10));Show(await client.DictionaryItems(cts.Token));}
        catch(Exception ex){Diag.Fail("dictionary-load",ex);status.Text="Das Wörterbuch ließ sich nicht laden. Ist der Screen Agent mit dem Desk verbunden?";}
    }
    async Task Save()
    {
        string word=term.Text.Trim();
        if(word.Length==0){status.Text="Bitte ein Wort eingeben.";term.Focus();return;}
        var forms=DictionaryEntry.SplitForms(from.Text);
        save.IsEnabled=false;
        try
        {
            using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var reply=await client.SaveTerm(word,forms,editing is not null&&editing!=word?editing:null,cts.Token);
            Diag.Event("dictionary.save",new(){["status"]=reply.Status,["forms"]=forms.Length,["renamed"]=editing is not null&&editing!=word});
            if(!reply.Ok){status.Text=reply.Status==400?"Nicht gespeichert: höchstens 100 Zeichen je Wort und 40 Falschschreibungen.":$"Nicht gespeichert (Fehler {reply.Status}).";return;}
            changed();await Load();status.Text=$"Gespeichert: {word}";
            list.SelectedItem=rows.FirstOrDefault(r=>r.Term==word);
        }
        catch(Exception ex){Diag.Fail("dictionary-save",ex);status.Text="Nicht gespeichert: keine Verbindung zum Desk.";}
        finally{save.IsEnabled=true;}
    }
    async Task Delete()
    {
        if(editing is not {} word)return;
        delete.IsEnabled=false;
        try
        {
            using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var reply=await client.DeleteTerm(word,cts.Token);
            Diag.Event("dictionary.delete",new(){["status"]=reply.Status});
            if(!reply.Ok){status.Text=$"Nicht gelöscht (Fehler {reply.Status}).";delete.IsEnabled=true;return;}
            string keptFrom=from.Text;
            changed();await Load();
            term.Text=word;from.Text=keptFrom;editing=null;   // what he just deleted stays in the fields: "Speichern" brings it back
            status.Text=$"Gelöscht: {word}. Mit „Speichern“ kommt es zurück.";
        }
        catch(Exception ex){Diag.Fail("dictionary-delete",ex);status.Text="Nicht gelöscht: keine Verbindung zum Desk.";delete.IsEnabled=true;}
    }
}
