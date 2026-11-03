using System.Text.Json;
using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
// v0.1.7 f-fix: one test per finding of CRITIC-F that would fail if the fix were taken out.
public class ComputerFixPolicyTests
{
    static readonly ComputerPolicy P=new(deskHost:"desk.localhost");
    static ComputerScope Scope(string goal)=>ComputerScope.From(goal);
    static ActionContext Chrome(string tool,string url,string? label=null)=>new(tool){Process="chrome",Url=url,Label=label};
    // ---- F1
    [Fact] public void F1_LinkOutOfScopeAsksAndSpeaksTheTarget()
    {
        var sc=Scope("öffne evil-blog.org");
        var v=P.Classify(Chrome("chrome_click","https://evil-blog.org/post","Mehr") with{Href="https://mail.google.com/mail/u/0/"},sc);
        Assert.Equal((ActionClass.R2,"link-out-of-scope"),(v.Class,v.Rule));
        Assert.Equal(ActionClass.R1,P.Classify(Chrome("chrome_click","https://evil-blog.org/post","Mehr") with{Href="https://evil-blog.org/x"},sc).Class);
        Assert.Equal(ActionClass.R1,P.Classify(Chrome("chrome_click","https://evil-blog.org/post","Mehr") with{Href="/relative"},sc).Class);
        Assert.Equal("link-scheme",P.Classify(Chrome("chrome_click","https://evil-blog.org/post","Mail") with{Href="mailto:a@b.c"},sc).Rule);
        Assert.Equal("scheme",P.Classify(Chrome("chrome_click","https://evil-blog.org/post","x") with{Href="file:///C:/x"},sc).Rule);
        Assert.Equal("desk-origin",P.Classify(Chrome("chrome_click","https://evil-blog.org/post","x") with{Href="https://desk.localhost/chat"},sc).Rule);
    }
    [Fact] public void F1_FormPostToAnotherOriginAsks()   // Href carries form.action / formaction as read live in the page
        =>Assert.Equal("link-out-of-scope",P.Classify(Chrome("chrome_click","https://evil-blog.org/form","Weiter") with{Href="https://collector.example/collect"},Scope("öffne evil-blog.org")).Rule);
    [Fact] public void F1_DataInThePathIsRefusedLikeAQuery()
    {
        var sc=Scope("öffne evil-blog.org");
        Assert.Equal("url-path",P.Classify(new("chrome_open"){OpenUrl="https://evil-blog.org/x/SGVsbG8gTWFpbA"},sc).Rule);
        Assert.Equal(ActionClass.R1,P.Classify(new("chrome_open"){OpenUrl="https://evil-blog.org/"},sc).Class);
        Assert.Equal(ActionClass.R1,P.Classify(new("chrome_open"){OpenUrl="https://evil-blog.org/x/y",UrlInWords=true},sc).Class);
        Assert.Equal(ActionClass.R1,P.Classify(new("chrome_open"){OpenUrl="https://evil-blog.org/x/y",UrlInSnapshot=true},sc).Class);
    }
    [Fact] public void F1_TaintMakesEveryOutwardActionAskButNotReads()
    {
        var sc=Scope("öffne example.org");
        foreach(var t in new[]{"chrome_click","chrome_fill","ui_click","ui_type"})
        {
            var c=new ActionContext(t){Process=t.StartsWith("chrome")?"chrome":"notepad",Url=t.StartsWith("chrome")?"https://example.org/x":null,Label="Ok",Text="a",Tainted=true};
            if(t.StartsWith("ui_"))sc=Scope("öffne example.org und den editor");
            Assert.Equal((ActionClass.R2,"tainted"),(P.Classify(c,sc).Class,P.Classify(c,sc).Rule));
            Assert.Equal(ActionClass.R1,P.Classify(c with{Tainted=false},sc).Class);
        }
        Assert.Equal((ActionClass.R2,"tainted"),(P.Classify(new("chrome_open"){OpenUrl="https://example.org/",Tainted=true},sc).Class,P.Classify(new("chrome_open"){OpenUrl="https://example.org/",Tainted=true},sc).Rule));   // N-covert: after a sensitive read even an in-scope open asks
        Assert.Equal("url-path",P.Classify(new("chrome_open"){OpenUrl="https://example.org/Zm9v",Tainted=true},sc).Rule);
        Assert.Equal(ActionClass.R0,P.Classify(Chrome("chrome_read","https://example.org/") with{Tainted=true},sc).Class);
        Assert.Equal(ActionClass.R2,P.Classify(new ActionContext("scroll"){Process="chrome",Url="https://example.org/",Tainted=true},sc).Class);   // a scroll offset is a covert channel
    }
    [Fact] public void F1_ReadingASensitiveTargetTaints()
    {
        Assert.True(P.SensitiveRead("chrome","Posteingang","https://mail.google.com/mail/u/0/"));
        Assert.True(P.SensitiveRead("chrome","x","https://github.com/settings/tokens"));
        Assert.True(P.SensitiveRead("slack","x",null));
        Assert.False(P.SensitiveRead("chrome","Rezept","https://example.org/"));
        Assert.False(P.SensitiveRead("notepad","Einkaufsliste",null));
    }
    [Fact] public void F1_RunTaintOnlyRises()
    {
        var r=new ComputerRun("r0123456789abcdef","g",Scope("example.org"));
        Assert.False(r.Tainted);r.Taint();Assert.True(r.Tainted);r.Resume();r.Extend("mehr",Scope("example.com"));Assert.True(r.Tainted);
    }
    [Fact] public void F1_OpenedHostsAreVisibleButNotInScope()
    {
        var sc=Scope("öffne example.org");sc.Opened.Add("evil.example");
        Assert.True(sc.Reaches("evil.example"));Assert.False(sc.ContainsHost("evil.example"));Assert.False(sc.Reaches("other.example"));
        Assert.Equal("out-of-scope",P.Classify(Chrome("chrome_click","https://evil.example/x","Weiter"),sc).Rule);
    }
    // ---- F3
    [Theory]
    [InlineData("https://banking.sparkasse-koelnbonn.de/")][InlineData("https://www.vr.de/")][InlineData("https://www.vr-bank-muenchen.de/")][InlineData("https://www.volksbank-bonn.de/")]
    [InlineData("https://www.consorsbank.de/")][InlineData("https://www.dkb.de/")][InlineData("https://www.ing.de/")][InlineData("https://www.comdirect.de/")][InlineData("https://app.finanzblick.de/")][InlineData("https://n26.com/")]
    [InlineData("https://www.paypal.com/")][InlineData("https://www.binance.com/")][InlineData("https://www.kraken.com/")][InlineData("https://accounts.coinbase.com/")][InlineData("https://passwords.google.com/")]
    [InlineData("chrome://settings/passwords")][InlineData("https://my.1password.com/")][InlineData("https://vault.bitwarden.com/")][InlineData("https://lastpass.com/vault")]
    public void F3_MoneyAndPasswordTargetsAreReadDenied(string url)=>Assert.True(P.ReadDenied("chrome","Titel",url),url);
    [Fact] public void F3_PasswordsGoogleIsR3AndGoogleWordIsWwwOnly()
    {
        Assert.Equal(ActionClass.R3,P.Classify(Chrome("chrome_click","https://passwords.google.com/","Anzeigen"),Scope("öffne passwords.google.com")).Class);
        var sc=Scope("öffne google");
        Assert.True(sc.ContainsHost("www.google.com"));Assert.False(sc.ContainsHost("mail.google.com"));Assert.False(sc.ContainsHost("docs.google.com"));Assert.False(sc.ContainsHost("sites.google.com"));
        Assert.Equal("sensitive-domain",P.Classify(Chrome("chrome_click","https://docs.google.com/forms/d/x","Weiter"),sc).Rule);   // documents are on the storage list now
        Assert.Equal("sensitive-domain",P.Classify(Chrome("chrome_click","https://mail.google.com/","Compose"),sc).Rule);
    }
    [Theory]
    [InlineData("https://github.com/settings/tokens")][InlineData("https://board.app.mintapis.com/x")][InlineData("https://console.neon.tech/")][InlineData("https://chutes.ai/app")][InlineData("https://login.tailscale.com/admin")]
    [InlineData("https://dash.cloudflare.com/")][InlineData("https://console.hetzner.cloud/")][InlineData("https://coolify.io/")][InlineData("https://dashboard.render.com/")][InlineData("https://openrouter.ai/keys")]
    public void F3_InfraWritesAreR2(string url)
    {
        string host=new Uri(url).Host;
        var v=P.Classify(Chrome("chrome_click",url,"Erstellen"),Scope("öffne "+host));
        Assert.Equal((ActionClass.R2),v.Class);
    }
    [Theory]
    [InlineData("CHUTES_API_KEY.txt - Editor")][InlineData("OPENROUTER.txt - Dev - Visual Studio Code")][InlineData("prod.env - Editor")][InlineData("server.pem - Editor")][InlineData("id_rsa - Editor")][InlineData("my token list - Editor")]
    [InlineData("secrets - Editor")][InlineData("OPENAI_KEY.txt")]
    public void F3_SecretTitlesOfNonBrowserWindowsAreReadDenied(string title)=>Assert.True(P.ReadDenied("notepad",title,null),title);
    [Fact] public void F3_AWebPageAboutTokensStaysReadable()
    {
        Assert.False(P.ReadDenied("chrome","Tokens erklärt - Blog","https://blog.example/tokens"));
        Assert.False(P.ReadDenied("notepad","Einkaufsliste - Editor",null));
        Assert.False(P.ReadDenied("notepad","readme.txt - Editor",null));
    }
    [Fact] public void F3_ExtraSensitiveFromConfig()
    {
        var p=new ComputerPolicy(extraSensitive:["meine-firma.example"]);
        Assert.Equal("sensitive-domain",p.Classify(Chrome("chrome_click","https://app.meine-firma.example/x","Ok"),Scope("öffne meine-firma.example")).Rule);
    }
    [Fact] public void F3_ListsAreExposedForTheReadme()
    {
        Assert.Contains("github.com",ComputerPolicy.Infra);Assert.Contains("mintapis.com",ComputerPolicy.Infra);Assert.Contains("passwords.google.com",ComputerPolicy.PasswordHosts);
        Assert.Contains("vr",ComputerPolicy.MoneyPrefixes);Assert.Contains("sparkasse-",ComputerPolicy.MoneyPrefixes);Assert.Contains("volksbank-",ComputerPolicy.MoneyPrefixes);
    }
    [Theory][InlineData("warp")][InlineData("hyper")][InlineData("tabby")][InlineData("wezterm")][InlineData("cmd")]
    public void NIT_UnlistedTerminalsAreR3(string proc)=>Assert.Equal("shell",P.Classify(new ActionContext("ui_click"){Process=proc,Label="x"},Scope("öffne "+proc)).Rule);
    // ---- F5
    [Fact] public void F5_R2TypingOver200CharsIsRefusedNotCut()
    {
        var sc=Scope("öffne mail.google.com");
        Assert.Equal((ActionClass.R2,"sensitive-domain"),(P.Classify(Chrome("chrome_fill","https://mail.google.com/",null) with{Text=new string('a',200)},sc).Class,P.Classify(Chrome("chrome_fill","https://mail.google.com/",null) with{Text=new string('a',200)},sc).Rule));
        Assert.Equal("r2-text-long",P.Classify(Chrome("chrome_fill","https://mail.google.com/",null) with{Text=new string('a',201)},sc).Rule);
        Assert.Equal(ActionClass.R1,P.Classify(Chrome("chrome_fill","https://example.org/",null) with{Text=new string('a',1500)},Scope("öffne example.org")).Class);   // R1 typing keeps its 2000
    }
    // ---- F4
    [Fact] public void F4_AnR2KeyNeedsAVerifiedFocusedElement()
    {
        var sc=Scope("öffne example.org");
        Assert.Equal("key-no-focus",P.Classify(Chrome("key","https://example.org/") with{Key="Enter"},sc).Rule);
        Assert.Equal("enter",P.Classify(Chrome("key","https://example.org/") with{Key="Enter",FocusVerified=true},sc).Rule);
        Assert.Equal(ActionClass.R1,P.Classify(Chrome("key","https://example.org/") with{Key="Tab"},sc).Class);   // R1 keys need no binding
    }
}
public class ComputerFixSecretTests
{
    [Theory]
    [InlineData("OPENROUTER_API_KEY=sk-or-v1-0123456789abcdef0123456789abcdef")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----")]
    [InlineData("token: eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijk")]
    [InlineData("AKIAABCDEFGHIJKLMNOP")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("passwort: hunter2hunter2")]
    public void SecretPatternsAreHidden(string text)
    {
        string r=SecretText.Redact("vorher "+text+" nachher");
        Assert.Contains(SecretText.Hidden,r);Assert.DoesNotContain("0123456789abcdef",r);Assert.DoesNotContain("hunter2",r);Assert.DoesNotContain("eyJ",r);Assert.DoesNotContain("BEGIN",r);Assert.DoesNotContain("AKIA",r);Assert.DoesNotContain("ghp_",r);
        Assert.StartsWith("vorher ",r);
    }
    [Fact] public void OrdinaryTextIsKept()
    {
        const string t="Hallo Florian, das Meeting ist um 14:30 Uhr, Raum 4.12. Bestellung 4711 ist unterwegs.";
        Assert.Equal(t,SecretText.Redact(t));Assert.Equal("",SecretText.Redact(null));
    }
}
public class ComputerFixInputTests
{
    sealed class Keys
    {
        public readonly HashSet<int> Down=new();
        public ComputerRun? Run;
        public void Press(int vk,bool injected=false){Down.Add(vk);ComputerInput.OnKey(Run,vk,true,injected,Down.Contains);}
        public void Release(int vk){Down.Remove(vk);ComputerInput.OnKey(Run,vk,false,false,Down.Contains);}
    }
    static PendingAsk Ask(string hash="h1")=>new("chrome_click",hash,"Klick","47",Environment.TickCount64,new ActionBinding(),default,"R2","label-send");
    [Fact] public void A_TheRealOrderCtrlWinShiftYIsAConfirmAndNotHandsOn()
    {
        long now=0;var r=new ComputerRun("r0123456789abcdef","g",ComputerScope.From("example.org"),()=>now);r.Propose(new("chrome_click","h1","Klick","47",now,new ActionBinding(),default,"R2","label-send"));now+=2000;
        var k=new Keys{Run=r};
        k.Press(0xa2);k.Press(0x5b);k.Press(0xa0);   // left Ctrl, left Win, left Shift (the specific codes the hook reports) 
        Assert.False(r.IsHandsOn,"a modifier press alone is not a hand on the keyboard");
        k.Press(0x11);k.Press(0x10);k.Press(0x59);   // then the generic codes GetAsyncKeyState answers for, then Y
        Assert.False(r.IsHandsOn);Assert.True(r.PhysicalConfirmed);
        Assert.Equal(ComputerRun.ConfirmResult.Ok,r.Confirm("h1",null,out _));
    }
    [Fact] public void A_ModifiersAloneAreNeverHandsOnButAnOtherKeyIs()
    {
        var r=new ComputerRun("r0123456789abcdef","g",ComputerScope.From("example.org"));var k=new Keys{Run=r};
        foreach(var vk in new[]{0x10,0x11,0x12,0x5b,0x5c,0xa0,0xa1,0xa2,0xa3,0xa4,0xa5})k.Press(vk);
        Assert.False(r.IsHandsOn);
        k.Press(0x41);Assert.True(r.IsHandsOn);
    }
    [Fact] public void A_InjectedKeysAreNeverHandsOnAndNeverConfirm()
    {
        var r=new ComputerRun("r0123456789abcdef","g",ComputerScope.From("example.org"));r.Propose(Ask());var k=new Keys{Run=r};
        k.Press(0x11);k.Press(0x10);k.Press(0x5b);k.Press(0x59,injected:true);
        Assert.False(r.PhysicalConfirmed);Assert.False(r.IsHandsOn);
        k.Press(0x41,injected:true);Assert.False(r.IsHandsOn);
    }
    [Fact] public void A_KillChordStillKills()
    {
        var r=new ComputerRun("r0123456789abcdef","g",ComputerScope.From("example.org"));var k=new Keys{Run=r};
        k.Press(0x11);k.Press(0x10);k.Press(0x5b);k.Press(0x58);
        Assert.True(r.Revoked);
    }
    [Fact] public void A_ConfirmChordWithNothingPendingConfirmsNothing()
    {
        var r=new ComputerRun("r0123456789abcdef","g",ComputerScope.From("example.org"));var k=new Keys{Run=r};
        k.Press(0x11);k.Press(0x10);k.Press(0x5b);k.Press(0x59);
        Assert.False(r.PhysicalConfirmed);
    }
}
public class ComputerFixRunTests
{
    static PendingAsk Ask(string hash)=>new("chrome_click",hash,"Klick","47",Environment.TickCount64,new ActionBinding(),default,"R2","label-send");
    [Fact] public void B_ResumeEndsHandsOnAndTheStepStopWithoutAddingScope()
    {
        long now=1000;
        var r=new ComputerRun("r0123456789abcdef","goal",ComputerScope.From("example.org"),()=>now);
        for(int i=0;i<25;i++)r.Counted();
        Assert.Equal("step-report",r.Gate()!.Value.Rule);
        r.HandsOn();
        Assert.Equal("handson",r.Gate()!.Value.Rule);
        int hosts=r.Scope.Hosts.Count;string goal=r.Goal;
        r.Resume();
        Assert.Null(r.Gate());Assert.Equal(hosts,r.Scope.Hosts.Count);Assert.Equal(goal,r.Goal);
    }
    [Fact] public void B_ResumeVoidsAPendingAsk()
    {
        var r=new ComputerRun("r0123456789abcdef","goal",ComputerScope.From("example.org"));r.Propose(Ask("h1"));r.Resume();
        Assert.Equal(ComputerRun.ConfirmResult.NoPending,r.Confirm("h1","47",out _));
    }
    [Fact] public void E_EightAsksThenStopAndTwoExpiriesStillStop()
    {
        long now=Environment.TickCount64;
        var r=new ComputerRun("r0123456789abcdef","goal",ComputerScope.From("example.org"),()=>now);
        PendingAsk At(string h)=>new("chrome_click",h,"Klick","47",now,new ActionBinding(),default,"R2","label-send");
        r.Propose(At("a"));now+=61_000;Assert.True(r.ExpirePending());Assert.False(r.IsClosed);
        r.Propose(At("b"));now+=61_000;Assert.True(r.ExpirePending());Assert.True(r.IsClosed);Assert.Equal("expired-twice",r.ClosedWhy);
        var q=new ComputerRun("r0123456789abcdef","goal",ComputerScope.From("example.org"));
        for(int i=0;i<8;i++){Assert.Null(q.Propose(Ask("h"+i)));q.Void();}
        Assert.Equal("asks-cap",q.Propose(Ask("x"))!.Value.Rule);
    }
    [Fact] public void TwoWrongCodesEndTheRun()
    {
        var r=new ComputerRun("r0123456789abcdef","goal",ComputerScope.From("example.org"));
        r.Propose(Ask("h1"));Assert.Equal(ComputerRun.ConfirmResult.Wrong,r.Confirm("h1","ja 1 1",out _));Assert.False(r.IsClosed);
        r.Propose(Ask("h2"));Assert.Equal(ComputerRun.ConfirmResult.Wrong,r.Confirm("h2","ja 2 2",out _));Assert.True(r.IsClosed);Assert.Equal("wrong-codes",r.ClosedWhy);
        var q=new ComputerRun("r0123456789abcdef","goal",ComputerScope.From("example.org"));
        q.Propose(Ask("h1"));q.Confirm("h1","ja 1 1",out _);q.Propose(Ask("h2"));Assert.Equal(ComputerRun.ConfirmResult.Ok,q.Confirm("h2","ja 4 7",out _));Assert.False(q.IsClosed);
    }
    [Fact] public void Speakers_AnUnarmedRunRunsNothingButItsOwnStart()
    {
        var r=new ComputerRun("r0123456789abcdef","goal",ComputerScope.From("example.org"),null,null,armed:false);
        Assert.Equal("not-armed",r.Gate()!.Value.Rule);Assert.Null(r.Gate(forStart:true));
        r.Arm();Assert.Null(r.Gate());
    }
    [Fact] public void Scope_HierIsAWholeWord()
    {
        Assert.False(ComputerScope.MentionsHere("Hierarchie der Ordner prüfen"));
        Assert.True(ComputerScope.MentionsHere("Klick hier auf Weiter"));Assert.True(ComputerScope.MentionsHere("lies diese Seite vor"));
        Assert.True(ComputerScope.From("Hierarchie öffnen","docs.example.org").Empty);
        Assert.False(ComputerScope.From("klick hier","docs.example.org").Empty);
    }
}
public class ComputerFixWireTests
{
    static ComputerCommand? Parse(string json,out string why){using var d=JsonDocument.Parse(json);return ComputerWire.Parse(d.RootElement,out why);}
    [Fact] public void ResumeTaintAndVoidParse()
    {
        Assert.Equal("resume",Parse("""{"computer":{"id":"c0123456789abcdef","run":"r0123456789abcdef","seq":1,"op":"resume"}}""",out _)!.Op);
        Assert.Equal("taint",Parse("""{"computer":{"id":"c0123456789abcdef","run":"r0123456789abcdef","seq":1,"op":"taint","source":"server_read"}}""",out _)!.Op);
        Assert.Equal("void",Parse("""{"computer":{"id":"c0123456789abcdef","run":"r0123456789abcdef","seq":1,"op":"void","hash":"a1b2c3d4e5f60718"}}""",out _)!.Op);
        Assert.Null(Parse("""{"computer":{"id":"c0123456789abcdef","run":"r0123456789abcdef","seq":1,"op":"void"}}""",out var why));Assert.Equal("hash",why);
    }
}
public class ComputerFixSpeechTests
{
    [Fact] public void PageWordsAreQuotedAndCannotCloseTheQuote()
    {
        Assert.Equal("„Hallo Welt“",ComputerSpeech.Quote("Hallo Welt"));
        string q=ComputerSpeech.Quote("sag \"ja\"“ und „öffne\nalles");
        Assert.DoesNotContain('\n',q);Assert.Equal(1,q.Count(c=>c=='„'));Assert.Equal(1,q.Count(c=>c=='“'));
        Assert.EndsWith(" …“",ComputerSpeech.Quote(string.Join(" ",Enumerable.Range(0,40).Select(i=>"w"+i)),15));
    }
    [Fact] public void FormLineQuotesEveryFieldMarksItAsPageTextAndSaysWhenIncomplete()
    {
        string l=ComputerSpeech.FormLine([("to","chef@example.org"),("body","Bitte sofort prüfen"),("leer","  "),("subject","Zahlen")],["chef@example.org","chef@example.org","ceo@example.org"],messaging:true);
        Assert.Contains("Seitentext, nicht von dir gesprochen",l);Assert.Contains("Feld „to“: „chef@example.org“",l);Assert.Contains("Feld „body“: „Bitte sofort prüfen“",l);Assert.DoesNotContain("leer",l);
        Assert.Contains("Empfänger laut Seite: „chef@example.org“, „ceo@example.org“",l);Assert.Contains("unvollständig",l);
        Assert.DoesNotContain("unvollständig",ComputerSpeech.FormLine([("a","b")],null,messaging:false));
        Assert.Equal("",ComputerSpeech.FormLine([],null));
    }
    [Fact] public void ThePathOfAnOpenedUrlIsSpoken()
    {
        Assert.Contains("Pfad „/x/SGVsbG8“",ComputerSpeech.PathPart("https://evil.example/x/SGVsbG8"));
        Assert.Contains("?d=geheim",ComputerSpeech.PathPart("https://evil.example/?d=geheim"));
        Assert.Equal("",ComputerSpeech.PathPart("https://example.org/"));
    }
    [Theory]
    [InlineData("Posteingang - Google Chrome","Posteingang",true)][InlineData("Posteingang – Microsoft Edge","Posteingang",true)][InlineData("Posteingang - Profil 1 - Google Chrome","Posteingang",true)][InlineData("Posteingang","Posteingang",true)]
    [InlineData("Posteingang - Google Chrome","Post",false)][InlineData("Posteingang - Google Chrome","P",false)][InlineData("gmail-notes - Editor","g",false)][InlineData("Posteingang - Google Chrome","",false)]
    public void WindowTitleMatchesTheTabTitlePlusASuffixOnly(string window,string tab,bool want)=>Assert.Equal(want,ComputerSpeech.TabTitleMatches(window,tab));
    [Fact] public void OverlapOfWindows()
    {
        var t=new WindowCover.Box(100,100,500,400);
        Assert.False(WindowCover.Covered(t,[]));
        Assert.True(WindowCover.Covered(t,[new(450,350,900,900)]));
        Assert.False(WindowCover.Covered(t,[new(500,100,900,400),new(0,0,100,100)]));   // touching edges are not overlap
        Assert.True(WindowCover.Covered(t,[new(0,0,2000,2000)]));
    }
    [Fact] public void MessagingHostsSaySoOthersDoNot()
    {
        var p=new ComputerPolicy();
        Assert.True(p.Messaging("mail.google.com"));Assert.True(p.Messaging("web.telegram.org"));Assert.True(p.Messaging("x.com"));Assert.False(p.Messaging("example.org"));
    }
    [Fact] public void DeskInABrowserWithoutCdpIsRefused()
        =>Assert.Equal("desk-origin",new ComputerPolicy().Classify(new ActionContext("ui_click"){Process="chrome",Title="Agent Desk - Google Chrome",Label="Ok"},ComputerScope.From("öffne example.org")).Rule);
}
public class ComputerFixReadmeTests
{
    static string Readme()
    {
        for(var d=new DirectoryInfo(AppContext.BaseDirectory);d is not null;d=d.Parent)
            if(File.Exists(Path.Combine(d.FullName,"README.md"))&&File.ReadAllText(Path.Combine(d.FullName,"README.md")).Contains("## Computer control"))return File.ReadAllText(Path.Combine(d.FullName,"README.md"));
        throw new FileNotFoundException("README.md");
    }
    [Fact] public void EveryListedSiteAppAndPatternIsPrintedInTheReadme()
    {
        string r=Readme();
        foreach(var e in ComputerPolicy.MoneyPrefixes)Assert.True(r.Contains("`"+e+"*`"),"README is missing the pattern "+e);
        foreach(var e in ComputerPolicy.MoneyContains)Assert.True(r.Contains("`*"+e+"*`"),"README is missing the pattern *"+e+"*");
        foreach(var list in new[]{ComputerPolicy.Storage,ComputerPolicy.Shops,ComputerPolicy.Browsers,ComputerPolicy.BrowserClasses,ComputerPolicy.Money,ComputerPolicy.MoneyLabels,ComputerPolicy.Mail,ComputerPolicy.Social,ComputerPolicy.Infra,ComputerPolicy.Composers,ComputerPolicy.PasswordHosts,ComputerPolicy.PasswordApps,ComputerPolicy.SensitiveApps,ComputerPolicy.ComposerApps,ComputerPolicy.Shells,ComputerPolicy.SystemTools,ComputerPolicy.SecurityApps})
            foreach(var e in list)Assert.True(r.Contains("`"+e+"`"),"README is missing the list entry "+e);
        foreach(var must in new[]{"`sparkasse-*`","`vr*`","`volksbank-*`","`www.google.com`","\"ComputerSensitive\"","eight questions"})Assert.Contains(must,r);
    }
    [Fact] public void TheLimitsTheReadmeStatesAreTheCodes()
    {
        Assert.Equal(8,new RunCaps().MaxAsks);Assert.Equal(2,new RunCaps().MaxExpiries);Assert.Equal(200,ComputerPolicy.MaxConfirmedText);
    }
}
