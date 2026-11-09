namespace ScreenAgent.Core;
// Native remote-debugging dialog matcher. Chrome repeats the heading on Window/Pane/Text nodes;
// require an inner Text heading and both Allow/Cancel buttons in the same native container.
// Window titles alone and anything below a Document never grant authority. The guarded matcher is unchanged.
public static class AllowPrompt
{
    public static readonly string[] Headings=AllowDialog.Headings;
    public static readonly string[] Allow=AllowDialog.Allow,Cancel=AllowDialog.Cancel;
    public static readonly string[] Browsers=["chrome.exe"];
    static string Leaf(string path)=>path[(path.LastIndexOfAny(['\\','/'])+1)..];
    static int Root(IReadOnlyList<DialogNode> t,int i){for(int g=0;g<128&&i>=0&&i<t.Count&&t[i].Parent>=0&&t[i].Parent<t.Count&&t[i].Parent!=i;g++)i=t[i].Parent;return i;}
    static bool Browser(DialogNode n,string owner,string[] browsers)
        =>n.ImagePath.Length>0&&browsers.Any(b=>string.Equals(Leaf(n.ImagePath),b,StringComparison.OrdinalIgnoreCase))&&(owner.Length==0||string.Equals(n.ImagePath,owner,StringComparison.OrdinalIgnoreCase));
    static bool Native(IReadOnlyList<DialogNode> t,int i)
    {
        for(int g=0;g<128&&i>=0&&i<t.Count;g++)
        {
            if(t[i].InDocument||t[i].Role is "Document" or "TitleBar")return false;
            int p=t[i].Parent;if(p<0)return true;if(p==i)return false;i=p;
        }
        return false;
    }
    public static List<int> Windows(IReadOnlyList<DialogNode> tree,string ownerImage,string[]? browsers=null)
        =>Find(tree,ownerImage,null,browsers).Select(i=>Root(tree,i)).Distinct().ToList();
    // An independent inner Text plus BOTH companion buttons in its nearest dialog container is required.
    // A page-controlled window title never contributes authority, even in app/popup windows.
    public static List<int> Find(IReadOnlyList<DialogNode> tree,string ownerImage,string[]? names=null,string[]? browsers=null)
    {
        names??=Allow;browsers??=Browsers;var hits=new HashSet<int>();
        for(int h=0;h<tree.Count;h++)
        {
            if(tree[h].Role!="Text"||!Headings.Contains(tree[h].Name)||!Native(tree,h)||!Browser(tree[h],ownerImage,browsers))continue;
            int a=tree[h].Parent;
            for(int g=0;g<128&&a>=0&&a<tree.Count;g++)
            {
                if(!Native(tree,a)||!Browser(tree[a],ownerImage,browsers))break;
                // The dialog's container is native-only; never combine a title/tooltip with a permission bubble across page content.
                if(Enumerable.Range(0,tree.Count).Any(i=>tree[i].Role=="Document"&&Under(tree,i,a)))break;
                var buttons=Enumerable.Range(0,tree.Count).Where(i=>tree[i].Role=="Button"&&Native(tree,i)&&Browser(tree[i],ownerImage,browsers)&&Under(tree,i,a)).ToArray();
                if(buttons.Any(i=>Allow.Contains(tree[i].Name))&&buttons.Any(i=>Cancel.Contains(tree[i].Name)))
                {
                    foreach(int i in buttons.Where(i=>names.Contains(tree[i].Name)))hits.Add(i);
                    break;
                }
                // Never widen a titled dialog to its surrounding browser window.
                if(Headings.Contains(tree[a].Name)||tree[a].Parent==a)break;
                a=tree[a].Parent;
            }
        }
        return hits.Order().ToList();
    }
    static bool Under(IReadOnlyList<DialogNode> t,int i,int ancestor)
    {
        for(int g=0;g<128&&i>=0&&i<t.Count;g++){if(i==ancestor)return true;if(t[i].Parent==i)return false;i=t[i].Parent;}
        return false;
    }
}
// A failed attach is remembered for 20 s only: every chrome_* call in that time answers at once with the same reason (the model moves on with the desktop tools), then the next call tries again.
// The clock is passed in, so the tests and the selftest do not wait.
public sealed class ConnectMemory(int holdMs=20000)
{
    string? code;long at;
    public int HoldMs=>holdMs;
    public void Fail(string why,long now){code=why;at=now;}
    public void Clear(){code=null;}
    // The remembered reason while it still holds, else null (try again).
    public string? Blocked(long now)=>code is not null&&now-at<holdMs&&now>=at?code:null;
    public string? Last=>code;
}
// What the model reads when Chrome cannot be reached over CDP: what failed and the way on. German, one or two sentences.
public static class ChromeFallback
{
    public const string Desktop="Nimm die Desktop-Werkzeuge (screenshot, click, type, key, ui_tree).";
    public const string OneClick="Florian: chrome://inspect/#remote-debugging öffnen und den Haken setzen.";
    public static string Text(string code)=>(code switch
    {
        "chrome-not-running"=>"Chrome läuft nicht. Mit open kann Chrome gestartet werden.",
        "debugging-off"=>"Chromes Fernsteuerung (Remote-Debugging) ist aus und ließ sich nicht selbst einschalten. "+OneClick,
        "no-dialog"=>"Chrome hat die Verbindung nicht angenommen und keine Abfrage „Remote-Fehlerbehebung zulassen?“ gezeigt.",
        "no-answer"=>"Chrome hat nach der Abfrage „Remote-Fehlerbehebung zulassen?“ nicht geantwortet.",
        "cdp-closed"=>"Die Verbindung zu Chrome ist abgebrochen und kam nicht wieder.",
        _=>"Chrome ist über die Fernsteuerung gerade nicht erreichbar ("+code+")."
    })+" "+Desktop;
}
