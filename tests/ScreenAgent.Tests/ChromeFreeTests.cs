using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
// The allow-prompt matcher of the free Chrome toolset. The 9 Nov failure is the first test: the real prompt carries its heading twice (the dialog window and a text inside).
public class AllowPromptTests
{
    const string Chrome=@"C:\Program Files\Google\Chrome\Application\chrome.exe";
    static DialogNode N(string role,string name,int depth,int parent,bool doc=false,string image=Chrome)=>new(role,name,doc,Path.GetFileNameWithoutExtension(image.Replace('\\','/')),image,depth,parent);
    // A browser window, and the prompt as its own top-level window whose name and inner text are both the heading.
    static List<DialogNode> Real(string heading="Remote-Fehlerbehebung zulassen?",string allow="Zulassen",string cancel="Abbrechen",string image=Chrome)=>
    [
        N("Window","Neuer Tab - Google Chrome",0,-1,image:image),
        N("Pane","",1,0,image:image),
        N("Document","Neuer Tab",2,1,image:image),
        N("Window",heading,0,-1,image:image),
        N("Pane",heading,1,3,image:image),
        N("Text",heading,2,4,image:image),
        N("Text","Eine Anwendung möchte diesen Browser steuern.",2,4,image:image),
        N("Button",cancel,2,4,image:image),
        N("Button",allow,2,4,image:image),
    ];
    [Fact] public void TheRealShapeWithTheHeadingTwiceIsClicked()
    {
        Assert.Equal("several",AllowDialog.Find(Real(),Chrome).Reason);   // what the old matcher said on 9 Nov (kept for the guarded mode)
        Assert.Equal([8],AllowPrompt.Find(Real(),Chrome));
        Assert.Equal([7],AllowPrompt.Find(Real(),Chrome,AllowPrompt.Cancel));
    }
    [Fact] public void EnglishAndGerman()
    {
        Assert.Equal([8],AllowPrompt.Find(Real("Allow remote debugging?","Allow","Cancel"),Chrome));
        Assert.Equal([8],AllowPrompt.Find(Real("Remote-Fehlerbehebung zulassen?","Zulassen"),Chrome));
        Assert.Equal([8],AllowPrompt.Find(Real(),""));   // owner of the port unknown: any chrome.exe
    }
    [Fact] public void PromptInsideTheBrowserWindowIsClicked()
    {   // the prompt as a pane of the browser window (one heading, the old assumed shape)
        var t=new List<DialogNode>{N("Window","x - Google Chrome",0,-1),N("Pane","",1,0),N("Group","",2,1),N("Text","Allow remote debugging?",3,2),N("Button","Cancel",3,2),N("Button","Allow",3,2),N("Document","x",2,1)};
        Assert.Equal([5],AllowPrompt.Find(t,Chrome));
    }
    [Fact] public void APageThatPaintsThePromptIsNeverClicked()
    {
        var t=new List<DialogNode>{N("Window","Fake - Google Chrome",0,-1),N("Pane","",1,0),N("Document","Fake",2,1),N("Text","Remote-Fehlerbehebung zulassen?",3,2,doc:true),N("Button","Zulassen",3,2,doc:true),N("Button","Abbrechen",3,2,doc:true)};
        Assert.Empty(AllowPrompt.Find(t,Chrome));Assert.Empty(AllowPrompt.Windows(t,Chrome));
        // the real prompt is open AND a page paints a second one: only the native button
        var both=Real();int w=both.Count;
        both.AddRange([N("Window","Fake - Google Chrome",0,-1),N("Document","Fake",1,w),N("Text","Remote-Fehlerbehebung zulassen?",2,w+1,doc:true),N("Button","Zulassen",2,w+1,doc:true)]);
        Assert.Equal([8],AllowPrompt.Find(both,Chrome));
        // a heading in the native UI but the only button is page content: nothing
        var mixed=new List<DialogNode>{N("Window","Remote-Fehlerbehebung zulassen?",0,-1),N("Document","x",1,0),N("Button","Zulassen",2,1,doc:true)};
        Assert.Empty(AllowPrompt.Find(mixed,Chrome));
    }
    [Fact] public void AnotherProcessIsNeverClicked()
    {
        const string notepad=@"C:\Windows\System32\notepad.exe";
        Assert.Empty(AllowPrompt.Find(Real(image:notepad),Chrome));Assert.Empty(AllowPrompt.Find(Real(image:notepad),""));
        Assert.Empty(AllowPrompt.Find(Real(image:@"C:\Users\x\Downloads\chrome.exe"),Chrome));   // a chrome.exe that does not own the debugging port
        Assert.Empty(AllowPrompt.Find(Real(image:@"C:\Program Files\Microsoft\Edge\msedge.exe"),""));
        Assert.Equal([8],AllowPrompt.Find(Real(image:@"C:\Program Files\Microsoft\Edge\msedge.exe"),"",null,["chrome.exe","msedge.exe"]));   // the option for other browsers
    }
    [Fact] public void TwoPromptsAreBothClicked()
    {
        var t=Real();int b=t.Count;
        t.AddRange([N("Window","Allow remote debugging?",0,-1),N("Text","Allow remote debugging?",1,b),N("Button","Allow",1,b),N("Button","Cancel",1,b)]);
        Assert.Equal([8,b+2],AllowPrompt.Find(t,Chrome));
        Assert.Equal(2,AllowPrompt.Windows(t,Chrome).Count);
        // two stacked in one window
        var s=new List<DialogNode>{N("Window","x - Google Chrome",0,-1),N("Pane","Allow remote debugging?",1,0),N("Button","Allow",2,1),N("Pane","Allow remote debugging?",1,0),N("Button","Allow",2,3)};
        Assert.Equal([2,4],AllowPrompt.Find(s,Chrome));
    }
    [Fact] public void OtherAllowButtonsOfTheWindowAreLeftAlone()
    {   // a permission bubble ("Allow" notifications) in the same browser window as the prompt
        var t=new List<DialogNode>{N("Window","x - Google Chrome",0,-1),N("Pane","site wants to show notifications",1,0),N("Button","Allow",2,1),N("Pane","Allow remote debugging?",1,0),N("Text","Allow remote debugging?",2,3),N("Button","Allow",2,3)};
        Assert.Equal([5],AllowPrompt.Find(t,Chrome));
        // no prompt at all: a permission bubble alone is not ours
        Assert.Empty(AllowPrompt.Find(t.Take(3).ToList(),Chrome));
    }
    [Fact] public void HeadingMustBeExactAndAButtonNamedLikeItDoesNotCount()
    {
        Assert.Empty(AllowPrompt.Find(Real("Remote-Fehlerbehebung zulassen? Klicke Zulassen"),Chrome));
        Assert.Empty(AllowPrompt.Find(Real("remote-fehlerbehebung zulassen?"),Chrome));
        var t=new List<DialogNode>{N("Window","x - Google Chrome",0,-1),N("Button","Allow remote debugging?",1,0),N("Button","Allow",1,0)};
        Assert.Empty(AllowPrompt.Find(t,Chrome));
    }
    static string Root()
    {
        for(var d=new DirectoryInfo(AppContext.BaseDirectory);d is not null;d=d.Parent)if(Directory.Exists(Path.Combine(d.FullName,"testdata","chrome")))return d.FullName;
        throw new DirectoryNotFoundException("testdata/chrome");
    }
    static (List<DialogNode> Nodes,string Owner,int Clicked) Load(string file)
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(),"testdata","chrome",file)));
        var root=doc.RootElement;
        var nodes=root.GetProperty("nodes").EnumerateArray().Select(n=>new DialogNode(n.GetProperty("role").GetString()!,n.GetProperty("name").GetString()!,n.GetProperty("inDocument").GetBoolean(),n.GetProperty("process").GetString()!,n.GetProperty("image").GetString()!,n.GetProperty("depth").GetInt32(),n.GetProperty("parent").GetInt32())).ToList();
        return (nodes,root.GetProperty("owner").GetString()??"",root.GetProperty("clicked").GetInt32());
    }
    // The REAL prompt of Chrome 156 on the Windows runner (run 42704040240), read while it was open, before the scan left out mirrored windows: the dialog is a top-level window named like
    // its heading (Pane > Window > ... Text heading, Buttons "Turn off in settings" / "Allow" / "Cancel") and the same dialog shows up again inside the browser window's tree.
    // Three or six heading nodes, not one: the shape the old matcher called "several".
    [Theory,InlineData("prompt-mirrored-en.json","Allow","Cancel"),InlineData("prompt-mirrored-de.json","Zulassen","Abbrechen")]
    public void TheRealPromptOfChrome156IsMatched(string file,string allow,string cancel)
    {
        var (nodes,_,_)=Load(file);
        Assert.Equal("several",AllowDialog.Find(nodes,Chrome).Reason);
        var hits=AllowPrompt.Find(nodes,Chrome);
        Assert.Equal(2,hits.Count);   // the one button, reached through the dialog's own window and through the browser window (the clicker presses a button once, by its runtime id)
        Assert.All(hits,i=>{Assert.Equal("Button",nodes[i].Role);Assert.Equal(allow,nodes[i].Name);});
        Assert.All(AllowPrompt.Find(nodes,Chrome,AllowPrompt.Cancel),i=>Assert.Equal(cancel,nodes[i].Name));
        Assert.DoesNotContain(hits,i=>nodes[i].Name.Contains("settings")||nodes[i].Name.Contains("Einstellungen"));
        Assert.Equal(2,AllowPrompt.Windows(nodes,Chrome).Count);
        Assert.Empty(AllowPrompt.Find(nodes,@"C:\Other\chrome.exe"));
        // the dialog's own window alone (what the scan lists since the mirror is left out): exactly one button
        int end=nodes.FindIndex(1,n=>n.Parent==-1);
        Assert.Single(AllowPrompt.Find(nodes.Take(end).ToList(),Chrome));
    }
}
public class ChromeFreeLogicTests
{
    static JsonElement J(string json){using var d=JsonDocument.Parse(json);return d.RootElement.Clone();}
    [Fact] public void FreeMethodsAreASecondListTheGuardedClientNeverSees()
    {
        foreach(var m in CdpMethods.Free)Assert.False(CdpMethods.Ok(m),m);
        Assert.True(CdpMethods.OkFree("Runtime.evaluate"));Assert.True(CdpMethods.OkFree("Input.dispatchMouseEvent"));Assert.True(CdpMethods.OkFree("Page.reload"));
        foreach(var m in CdpMethods.Never.Where(m=>m!="Runtime.evaluate"))Assert.False(CdpMethods.OkFree(m),m);
        foreach(var m in CdpMethods.Free)Assert.DoesNotMatch(@"^(Network|Storage|Debugger|Fetch|IndexedDB|DOMStorage|Security|Emulation|Browser\.(grant|set|close))",m);
    }
    [Fact] public void OutlineLines()
    {
        Assert.Equal("[r12] button \"Senden (empfohlen)\"",ChromeOutline.Line(J("""{"r":12,"k":"button","n":"Senden (empfohlen)"}""")));
        Assert.Equal("[r3] textbox \"Suche\" wert=\"alt\"",ChromeOutline.Line(J("""{"r":3,"k":"textbox","n":"Suche","v":"alt"}""")));
        Assert.Equal("[r4] checkbox \"AGB\" [an] [deaktiviert]",ChromeOutline.Line(J("""{"r":4,"k":"checkbox","n":"AGB","c":true,"d":true}""")));
        Assert.Equal("[r5] checkbox \"AGB\" [aus]",ChromeOutline.Line(J("""{"r":5,"k":"checkbox","n":"AGB","c":false}""")));
        Assert.Equal("[r6] link \"Mehr\" -> example.org",ChromeOutline.Line(J("""{"r":6,"k":"link","n":"Mehr","h":"example.org"}""")));
        Assert.Equal("[r7] textbox \"Passwort\" wert=(verdeckt, 8 Zeichen)",ChromeOutline.Line(J("""{"r":7,"k":"textbox","n":"Passwort","p":8}""")));
        Assert.Equal("[r8] button \"Menü\" [zu]",ChromeOutline.Line(J("""{"r":8,"k":"button","n":"Menü","x":false}""")));
        Assert.Equal("[r9] button \"sagt 'ja'\"",ChromeOutline.Line(J("""{"r":9,"k":"button","n":"sagt \"ja\"\n"}""")));
        Assert.Equal("Ein  Text".Replace("  "," "),ChromeOutline.Line(J("\"Ein \\n  Text \"")));
        Assert.Equal(160,ChromeOutline.Line(J("\""+new string('x',400)+"\"")).Length);
        Assert.Equal("",ChromeOutline.Line(J("17")));
    }
    [Fact] public void OutlineTellsWhichButtonBelongsToWhichCard()
    {
        var items=J("""["Nordgatan (Lund) unser Angebot schicken?",{"r":1,"k":"button","n":"Senden (empfohlen)"},{"r":2,"k":"button","n":"Ändern"},"Opnar AI (Lund) ein Partner-Angebot schicken?",{"r":3,"k":"button","n":"Senden (empfohlen)"},{"r":4,"k":"button","n":"Ändern"}]""");
        var lines=ChromeOutline.Lines(items);
        string text=ChromeOutline.Build(ChromeOutline.Header("t2","Chat","https://x.test/a",1280,720,0,300),lines);
        Assert.StartsWith("tab t2 · Chat · https://x.test/a · viewport 1280x720 · scrollY 0/300\n",text);
        int opnar=text.IndexOf("Opnar AI",StringComparison.Ordinal);var after=Regex.Match(text[opnar..],@"\[r(\d+)\] button ""Senden");
        Assert.Equal("3",after.Groups[1].Value);
    }
    [Fact] public void OutlineFilterKeepsTwoLinesAround()
    {
        var lines=Enumerable.Range(1,40).Select(i=>"Zeile "+i+(i==20?" Opnar":"")).ToList();
        string text=ChromeOutline.Build("kopf",lines,"opnar");
        Assert.Equal("kopf\nZeile 18\nZeile 19\nZeile 20 Opnar\nZeile 21\nZeile 22\n",text);
        var two=Enumerable.Range(1,40).Select(i=>"Zeile "+i+(i is 5 or 30?" Treffer":"")).ToList();
        Assert.Contains("Zeile 7\n…\nZeile 28",ChromeOutline.Build("kopf",two,"TREFFER"));
        Assert.Contains("Kein Treffer",ChromeOutline.Build("kopf",lines,"gibt es nicht"));
    }
    [Fact] public void OutlineIsCutAt500LinesAnd24000CharactersAndSaysSo()
    {
        var many=Enumerable.Range(1,900).Select(i=>"Zeile "+i).ToList();
        string text=ChromeOutline.Build("kopf",many);
        Assert.Equal(502,text.TrimEnd('\n').Split('\n').Length);
        Assert.Contains("gekürzt: 500 von 900 Zeilen",text);Assert.Contains("filter",text);Assert.Contains("scroll",text);
        var wide=Enumerable.Range(1,400).Select(i=>new string('a',150)).ToList();
        string cut=ChromeOutline.Build("kopf",wide);
        Assert.True(cut.Length<=ChromeOutline.MaxChars+400,cut.Length.ToString());Assert.Contains("gekürzt",cut);
        Assert.DoesNotContain("gekürzt",ChromeOutline.Build("kopf",many.Take(100).ToList()));
        Assert.Contains("all:true",ChromeOutline.Build("kopf",many.Take(10).ToList(),more:true));
        Assert.Contains("über 4000",ChromeOutline.Build("kopf",Enumerable.Range(1,4000).Select(i=>"z").ToList(),pageCut:true));
    }
    [Fact] public void KeysForCdp()
    {
        var enter=CdpKeys.Parse("Enter",out _)!;Assert.Equal(new CdpKey(0,"Enter","Enter",13,"\r"),enter[0]);
        var sel=CdpKeys.Parse("Strg+A",out _)!;Assert.Equal(new CdpKey(CdpKeys.Ctrl,"a","KeyA",65,""),sel[0]);
        var big=CdpKeys.Parse("shift+a",out _)!;Assert.Equal(new CdpKey(CdpKeys.Shift,"A","KeyA",65,"A"),big[0]);
        var two=CdpKeys.Parse("ctrl+a delete",out _)!;Assert.Equal(2,two.Count);Assert.Equal("Delete",two[1].Key);Assert.Equal(46,two[1].Vk);
        var st=CdpKeys.Parse("ctrl+shift+tab",out _)!;Assert.Equal(CdpKeys.Ctrl|CdpKeys.Shift,st[0].Modifiers);Assert.Equal(9,st[0].Vk);
        Assert.Equal("F5",CdpKeys.Parse("f5",out _)![0].Key);Assert.Equal(116,CdpKeys.Parse("f5",out _)![0].Vk);
        Assert.Equal("ArrowDown",CdpKeys.Parse("down",out _)![0].Key);Assert.Equal("Escape",CdpKeys.Parse("escape",out _)![0].Key);
        Assert.Equal(new CdpKey(0,"7","Digit7",55,"7"),CdpKeys.Parse("7",out _)![0]);
        Assert.Equal(new CdpKey(CdpKeys.Ctrl,"+","Equal",187,""),CdpKeys.Parse("ctrl++",out _)![0]);
        Assert.Equal("ä",CdpKeys.Parse("ä",out _)![0].Text);
        Assert.Equal(CdpKeys.Meta|CdpKeys.Alt,CdpKeys.Parse("win+alt+k",out _)![0].Modifiers);
        Assert.Null(CdpKeys.Parse("ctrl",out string bad));Assert.Equal("ctrl",bad);
        Assert.Null(CdpKeys.Parse("blubb",out bad));Assert.Equal("blubb",bad);
        Assert.Null(CdpKeys.Parse("",out _));
        Assert.Equal(CdpKeys.Ctrl|CdpKeys.Shift,CdpKeys.Mods("ctrl+shift"));Assert.Equal(0,CdpKeys.Mods(""));
    }
    [Fact] public void EvalWrap()
    {
        Assert.False(ChromeJs.NeedsWrap("document.title"));Assert.False(ChromeJs.NeedsWrap("returned+awaiting"));
        Assert.True(ChromeJs.NeedsWrap("return document.title"));Assert.True(ChromeJs.NeedsWrap("const r=await fetch('/x');r.status"));
        Assert.Equal("(async()=>{\nreturn 1\n})()",ChromeJs.Wrap("return 1"));
    }
    [Fact] public void JpegSizeFromTheHeader()
    {
        byte[] jpg=[0xff,0xd8,0xff,0xe0,0x00,0x10,0x4a,0x46,0x49,0x46,0x00,0x01,0x01,0x00,0x00,0x01,0x00,0x01,0x00,0x00,0xff,0xdb,0x00,0x04,0x00,0x00,0xff,0xc0,0x00,0x11,0x08,0x02,0xd0,0x05,0x00,0x03,0x01,0x22,0x00,0x02,0x11,0x01,0x03,0x11,0x01];
        Assert.Equal((1280,720),ChromeJs.JpegSize(jpg));
        Assert.Null(ChromeJs.JpegSize([1,2,3,4]));Assert.Null(ChromeJs.JpegSize([0xff,0xd8,0xff,0xe0]));
        Assert.Equal(1.0,ChromeJs.ShotScale(1000,700,1));Assert.Equal(0.5,ChromeJs.ShotScale(2560,1300,1));Assert.Equal(0.4,ChromeJs.ShotScale(1600,900,2));
    }
    [Fact] public void AFailedConnectIsRememberedFor20Seconds()
    {
        var m=new ConnectMemory();long now=1000;
        Assert.Null(m.Blocked(now));
        m.Fail("debugging-off",now);
        Assert.Equal("debugging-off",m.Blocked(now+1));Assert.Equal("debugging-off",m.Blocked(now+19999));
        Assert.Null(m.Blocked(now+20000));
        m.Fail("no-dialog",now+20000);Assert.Equal("no-dialog",m.Blocked(now+20500));m.Clear();Assert.Null(m.Blocked(now+20500));
    }
    [Fact] public void FallbackTextsNameTheWayOn()
    {
        foreach(string code in new[]{"chrome-not-running","debugging-off","no-dialog","no-answer","cdp-closed","anything"})
        {
            string t=ChromeFallback.Text(code);
            Assert.Contains("Desktop-Werkzeuge (screenshot, click, type, key, ui_tree)",t);
        }
        Assert.Contains("chrome://inspect/#remote-debugging öffnen und den Haken setzen",ChromeFallback.Text("debugging-off"));
    }
    [Fact] public void TabNamesAreShortAndStable()
    {
        var s=new ChromeRunState();
        Assert.Equal("t1",s.Alias("AAAABBBBCCCC1111"));Assert.Equal("t2",s.Alias("AAAABBBBCCCC2222"));Assert.Equal("t1",s.Alias("AAAABBBBCCCC1111"));
        Assert.Equal("AAAABBBBCCCC2222",s.Resolve("t2"));Assert.Equal("AAAABBBBCCCC2222",s.Resolve("[t2]"));Assert.Equal("AAAABBBBCCCC1111",s.Resolve("AAAABBBBCCCC1111"));
        Assert.Null(s.Resolve("t9"));Assert.Null(s.Resolve("AAAABBBB"));   // ambiguous start
        Assert.Equal("AAAABBBBCCCC2222",s.Resolve("AAAABBBBCCCC2"));
        s.Own.Add("AAAABBBBCCCC2222");s.Forget("AAAABBBBCCCC2222");Assert.Null(s.Resolve("t2"));Assert.Empty(s.Own);
    }
    [Fact] public void TestPagesServeTheThreeCards()
    {
        string cards=ChromeTestPages.Page("/cards");
        foreach(string c in new[]{"Nordgatan","Opnar AI","Outwing"})Assert.Contains(c,cards);
        Assert.Equal(3,Regex.Matches(cards,"Senden \\(empfohlen\\)").Count);
        Assert.Equal("",ChromeTestPages.Page("/nope"));
    }
}
