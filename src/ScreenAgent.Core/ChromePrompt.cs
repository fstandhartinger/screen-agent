namespace ScreenAgent.Core;
// Chrome asks "Remote-Fehlerbehebung zulassen?" / "Allow remote debugging?" for every new debugging connection to the default profile. General computer control (unrestricted mode) clicks it
// by itself. This is the matcher: a pure function over the UI tree of all Chrome windows (UIA on Windows, fake or recorded trees in the tests).
// What went wrong on 9 Nov 2026: the old matcher (AllowDialog.Find) wanted exactly ONE node with the heading text; the real prompt carries it twice (the dialog window/pane AND a text inside),
// so it answered "several" and never clicked. Here one OR MORE heading nodes mark a window as holding the prompt.
// A button is pressed only when ALL of this holds:
//  - it is a Button named exactly Zulassen / Allow (or Abbrechen / Cancel when clearing stale prompts),
//  - it is native browser UI: not inside a Document (page content lives in chrome.exe too; a web page that paints the same words is never clicked),
//  - its top-level window belongs to the browser (chrome.exe; when the owner of the debugging port is known: exactly that image),
//  - the same top-level window has the heading text in its native UI.
// Several prompts (stacked, or one per window): every one is returned. When a window holds other native "Allow" buttons too (a permission bubble), only the buttons that share a container
// with a heading are taken.
public static class AllowPrompt
{
    public static readonly string[] Headings=AllowDialog.Headings;
    public static readonly string[] Allow=AllowDialog.Allow,Cancel=AllowDialog.Cancel;
    public static readonly string[] Browsers=["chrome.exe"];
    static string Leaf(string path)=>path[(path.LastIndexOfAny(['\\','/'])+1)..];
    static int Root(IReadOnlyList<DialogNode> t,int i){for(int g=0;g<128&&i>=0&&i<t.Count&&t[i].Parent>=0&&t[i].Parent<t.Count&&t[i].Parent!=i;g++)i=t[i].Parent;return i;}
    static bool Browser(DialogNode n,string owner,string[] browsers)
        =>n.ImagePath.Length>0&&browsers.Any(b=>string.Equals(Leaf(n.ImagePath),b,StringComparison.OrdinalIgnoreCase))&&(owner.Length==0||string.Equals(n.ImagePath,owner,StringComparison.OrdinalIgnoreCase));
    static bool Heading(DialogNode n)=>!n.InDocument&&n.Role is not ("Button" or "Edit" or "Document")&&Array.IndexOf(Headings,n.Name)>=0;
    // Indices of the top-level windows that hold a prompt.
    public static List<int> Windows(IReadOnlyList<DialogNode> tree,string ownerImage,string[]? browsers=null)
    {
        var roots=new List<int>();
        for(int i=0;i<tree.Count;i++)
        {
            if(!Heading(tree[i]))continue;
            int r=Root(tree,i);
            if(r>=0&&Browser(tree[r],ownerImage,browsers??Browsers)&&Browser(tree[i],ownerImage,browsers??Browsers)&&!roots.Contains(r))roots.Add(r);
        }
        return roots;
    }
    // Indices of the buttons to press, in tree order. `names` = Allow (default) or Cancel. ownerImage "" = the owner of the port is not known: any chrome.exe counts.
    public static List<int> Find(IReadOnlyList<DialogNode> tree,string ownerImage,string[]? names=null,string[]? browsers=null)
    {
        names??=Allow;browsers??=Browsers;
        var hits=new List<int>();
        foreach(int root in Windows(tree,ownerImage,browsers))
        {
            var loose=new List<int>();var tight=new List<int>();
            for(int i=0;i<tree.Count;i++)
            {
                var n=tree[i];
                if(n.Role!="Button"||n.InDocument||Array.IndexOf(names,n.Name)<0||!Browser(n,ownerImage,browsers)||Root(tree,i)!=root)continue;
                // the nearest ancestor that has a heading below it (or is one): the dialog container. The window itself counts as a container only when it carries the heading.
                int box=Container(tree,i);
                if(box<0)continue;
                if(box!=root||Heading(tree[root]))tight.Add(i);else loose.Add(i);
            }
            hits.AddRange(tight.Count>0?tight:loose);
        }
        hits.Sort();
        return hits;
    }
    static int Container(IReadOnlyList<DialogNode> t,int button)
    {
        int a=t[button].Parent;
        for(int g=0;g<128&&a>=0&&a<t.Count;g++)
        {
            if(Heading(t[a]))return a;
            for(int i=0;i<t.Count;i++)if(i!=button&&Heading(t[i])&&Under(t,i,a))return a;
            if(t[a].Parent==a)break;
            a=t[a].Parent;
        }
        return -1;
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
