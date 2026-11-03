using System.Text.Json;
using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
public class ComputerPolicyTests
{
    static readonly ComputerPolicy P=new(extraSensitive:["mail.localhost"],extraComposer:["composer.localhost"],deskHost:"desk.localhost");
    static ComputerScope Scope(string goal,string? here=null)=>ComputerScope.From(goal,here);
    static Verdict V(ActionContext c,string goal="öffne example.org und klick auf Issues")=>P.Classify(c,Scope(goal));
    static ActionContext Chrome(string tool,string url,string? label=null)=>new(tool){Process="chrome",Url=url,Label=label};
    [Theory]
    [InlineData("www.example.com","example.com")][InlineData("a.b.example.co.uk","example.co.uk")][InlineData("mail.localhost","mail.localhost")][InlineData("x.y.localhost","y.localhost")]
    [InlineData("foo.github.io","foo.github.io")][InlineData("127.0.0.1","127.0.0.1")][InlineData("localhost","localhost")][InlineData("xn--pypal-4ve.com","xn--pypal-4ve.com")][InlineData("EXAMPLE.com.","example.com")]
    public void Etld1(string host,string want)=>Assert.Equal(want,Domains.Etld1(host));
    [Fact] public void PunycodeStaysRaw()=>Assert.Equal("xn--pypal-4ve.com",Domains.HostOf("https://pаypal.com/"));   // Cyrillic a
    [Fact] public void ScopeFromSentence()
    {
        var s=Scope("Übernimm den Rechner, öffne gmail und github.com, hier auch","docs.example.org");
        Assert.True(s.ContainsHost("mail.google.com"));Assert.True(s.ContainsHost("github.com"));Assert.True(s.ContainsHost("api.github.com"));Assert.True(s.ContainsHost("docs.example.org"));Assert.True(s.ContainsApp("calculatorapp"));
        Assert.False(s.ContainsHost("evil.com"));Assert.False(s.ContainsHost("notgithub.com"));Assert.False(s.ContainsHost("github.com.evil.io"));
        Assert.True(Scope("klick auf datei.txt").Empty);   // not a TLD
        Assert.True(Scope("mach irgendwas").Empty);
    }
    [Fact] public void EmptyScopeOnlyReadFocusScroll()
    {
        var e=Scope("mach das");
        Assert.Equal(ActionClass.R0,P.Classify(Chrome("chrome_read","https://example.com/"),e).Class);
        Assert.Equal(ActionClass.R1,P.Classify(new("scroll"){Process="chrome",Url="https://example.com/"},e).Class);
        Assert.Equal(ActionClass.R1,P.Classify(new("window_focus"){Process="notepad"},e).Class);
        var c=P.Classify(Chrome("chrome_click","https://example.com/","Weiter"),e);Assert.Equal((ActionClass.R2,"no-scope"),(c.Class,c.Rule));
        Assert.Equal(ActionClass.R2,P.Classify(Chrome("chrome_open","https://example.com/") with{OpenUrl="https://example.com/"},e).Class);
    }
    [Fact] public void InScopeClickIsR1()
    {
        var v=V(Chrome("chrome_click","https://example.org/x/y","Issues"));Assert.Equal((ActionClass.R1,"in-scope"),(v.Class,v.Rule));
        Assert.Equal(ActionClass.R1,V(Chrome("chrome_fill","https://example.org/search") with{Text="hallo"}).Class);
    }
    [Fact] public void WeiterFixtureOnUnlistedSiteOutsideScope()   // a button named "Weiter" that sends: outside the scope it is R2 whatever it is called
    {
        var v=V(Chrome("chrome_click","https://other.example.net/form","Weiter"));Assert.Equal((ActionClass.R2,"out-of-scope"),(v.Class,v.Rule));
    }
    [Fact] public void WeiterFixtureOnListedDomain()
    {
        var v=V(Chrome("chrome_click","http://mail.localhost:8080/c","Weiter"),"öffne mail.localhost");Assert.Equal((ActionClass.R2,"sensitive-domain"),(v.Class,v.Rule));
        var money=V(Chrome("chrome_click","https://www.paypal.com/x","Zurück"),"öffne paypal.com");Assert.Equal(ActionClass.R2,money.Class);
        Assert.Equal(ActionClass.R1,V(new ActionContext("scroll"){Process="chrome",Url="https://www.paypal.com/x"},"öffne paypal.com").Class);   // scrolling is fine
    }
    [Fact] public void LabelsOnlyRaise()
    {
        Assert.Equal((ActionClass.R2,"label-send"),(V(Chrome("chrome_click","https://example.org/","Senden")).Class,V(Chrome("chrome_click","https://example.org/","Senden")).Rule));
        Assert.Equal("label-delete",V(Chrome("chrome_click","https://example.org/","Löschen")).Rule);
        Assert.Equal(ActionClass.R1,V(Chrome("chrome_click","https://example.org/","Issues")).Class);
        // a friendly label cannot lower anything
        Assert.Equal(ActionClass.R2,V(Chrome("chrome_click","https://example.org/","Speichern")) .Class);
        Assert.Equal(ActionClass.R2,V(Chrome("chrome_click","https://other.com/","Ansehen")).Class);
    }
    [Fact] public void BrowserWithoutCdpUrlIsR2()
    {
        foreach(var t in new[]{"ui_click","ui_type","key"})
        {
            var c=new ActionContext(t){Process="msedge",Label="Ok",Text="a",Key="Tab",FocusVerified=true};
            Assert.Equal((ActionClass.R2,"no-cdp"),(P.Classify(c,Scope("öffne example.org")).Class,P.Classify(c,Scope("öffne example.org")).Rule));
        }
    }
    [Fact] public void AppNamedInSentenceIsR1_OthersR2()
    {
        var n=new ActionContext("ui_click"){Process="notepad",Label="Datei"};
        Assert.Equal(ActionClass.R1,P.Classify(n,Scope("schreib was in den Editor")).Class);
        Assert.Equal((ActionClass.R2,"out-of-scope"),(P.Classify(n,Scope("öffne example.org")).Class,P.Classify(n,Scope("öffne example.org")).Rule));
        Assert.Equal(ActionClass.R2,P.Classify(new ActionContext("ui_click"){Process="notepad",Nameless=true},Scope("editor")).Class);
    }
    [Theory]
    [InlineData("cmd")][InlineData("powershell")][InlineData("pwsh")][InlineData("WindowsTerminal")][InlineData("wt")][InlineData("conhost")][InlineData("wsl")][InlineData("mintty")][InlineData("regedit")][InlineData("mmc")][InlineData("taskmgr")]
    [InlineData("KeePassXC")][InlineData("1Password")][InlineData("Bitwarden")][InlineData("MsMpEng")][InlineData("consent")][InlineData("LogonUI")][InlineData("SearchHost")][InlineData("ScreenAgent")]
    public void R3Processes(string proc)
    {
        foreach(var tool in new[]{"ui_click","ui_type","key","ui_tree"})
            Assert.Equal(ActionClass.R3,P.Classify(new(tool){Process=proc+".exe",Text="a",Key="Tab"},Scope("editor "+proc)).Class);
    }
    [Theory]
    [InlineData("chrome://settings/passwords")][InlineData("chrome://password-manager")][InlineData("about:blank")][InlineData("devtools://devtools/bundled/x.html")][InlineData("file:///C:/x")][InlineData("data:text/html,hi")][InlineData("javascript:alert(1)")]
    [InlineData("blob:https://a.com/1")][InlineData("view-source:https://a.com")][InlineData("chrome-extension://abc/page.html")]
    public void R3Schemes(string url)
    {
        Assert.Equal((ActionClass.R3,"scheme"),(P.Classify(Chrome("chrome_click",url,"x"),Scope("example.org")).Class,P.Classify(Chrome("chrome_click",url,"x"),Scope("example.org")).Rule));
        Assert.Equal(ActionClass.R3,P.Classify(new("chrome_open"){OpenUrl=url},Scope("example.org")).Class);
    }
    [Fact] public void DeskOriginAndOwnWindowsAreR3()
    {
        var d=P.Classify(Chrome("chrome_fill","https://desk.localhost:9/chat") with{Text="ja"},Scope("desk.localhost"));Assert.Equal((ActionClass.R3,"desk-origin"),(d.Class,d.Rule));
        Assert.Equal(ActionClass.R3,P.Classify(new("chrome_open"){OpenUrl="https://desk.localhost/"},Scope("desk.localhost")).Class);
        Assert.Equal("own-window",P.Classify(new ActionContext("ui_type"){Process="msedge",Title="Screen Agent · Desk",Text="ja"},Scope("")).Rule);
        Assert.Equal("own-files",P.Classify(new ActionContext("ui_click"){Process="notepad",Title=@"C:\Users\x\AppData\Roaming\ScreenAgent\config.json - Editor"},Scope("editor")).Rule);
    }
    [Fact] public void RunDialogStartSearchAddressBar()
    {
        Assert.Equal("run-dialog",P.Classify(new ActionContext("ui_type"){Process="explorer",Title="Ausführen",Text="powershell"},Scope("explorer")).Rule);
        Assert.Equal("address-bar",P.Classify(new ActionContext("ui_type"){Process="explorer",Label="Adressleiste",Text="powershell"},Scope("explorer")).Rule);
        Assert.Equal("start-search",P.Classify(new ActionContext("ui_type"){Process="SearchHost",Text="cmd"},Scope("")).Rule);
    }
    [Fact] public void WinRPath()   // Win+R, then type, then Enter: the first key is already refused
    {
        foreach(var k in new[]{"Win+R","Windows+R","Meta+R","Ctrl+Win+Shift+X","win"})
            Assert.Equal("win-chord",P.Classify(new ActionContext("key"){Process="explorer",Key=k},Scope("explorer")).Rule);
    }
    [Theory]
    [InlineData("Tab")][InlineData("Down")][InlineData("PageUp")][InlineData("Home")][InlineData("End")][InlineData("Escape")][InlineData("Ctrl+F")][InlineData("control + f")][InlineData("Shift+Tab")]
    public void AllowedKeys(string key)=>Assert.Equal(ActionClass.R1,P.Classify(new("key"){Process="notepad",Key=key},Scope("editor")).Class);
    [Theory]
    [InlineData("F5")][InlineData("Delete")][InlineData("Alt+F4")][InlineData("Ctrl+Shift+Esc")][InlineData("Ctrl+W")][InlineData("a")][InlineData("Ctrl+V")][InlineData("Ctrl+C")][InlineData("Shift+Insert")][InlineData("Ctrl+Alt+Delete")]
    public void OtherKeysRefused(string key)=>Assert.Equal(ActionClass.R3,P.Classify(new("key"){Process="notepad",Key=key},Scope("editor")).Class);
    [Fact] public void EnterIsR2UnlessSearchBoxInScope()
    {
        Assert.Equal("enter",P.Classify(Chrome("key","https://example.org/") with{FocusVerified=true,Key="Enter"},Scope("example.org")).Rule);
        Assert.Equal("enter",P.Classify(Chrome("key","https://example.org/") with{FocusVerified=true,Key="Ctrl+Enter"},Scope("example.org")).Rule);
        Assert.Equal("enter",P.Classify(Chrome("key","https://example.org/") with{FocusVerified=true,Key="Alt+Enter"},Scope("example.org")).Rule);
        Assert.Equal("enter-search",P.Classify(Chrome("key","https://example.org/") with{FocusVerified=true,Key="Enter",SearchBox=true},Scope("example.org")).Rule);
        Assert.Equal("enter",P.Classify(Chrome("key","https://example.org/") with{FocusVerified=true,Key="Ctrl+Enter",SearchBox=true},Scope("example.org")).Rule);
        Assert.Equal("out-of-scope",P.Classify(Chrome("key","https://other.com/") with{FocusVerified=true,Key="Enter",SearchBox=true},Scope("example.org")).Rule);
    }
    [Fact] public void ClaudeCodeBoxEveryKeyIsR2()
    {
        foreach(var proc in new[]{"Code","Code - Insiders","Cursor","claude"})
            foreach(var key in new[]{"Enter","Tab","Esc"})
                Assert.Equal((ActionClass.R2,"composer-key"),(P.Classify(new ActionContext("key"){Process=proc,Key=key,FocusVerified=true},Scope("editor")).Class,P.Classify(new ActionContext("key"){Process=proc,Key=key,FocusVerified=true},Scope("editor")).Rule));
        Assert.Equal("composer",P.Classify(new ActionContext("ui_type"){Process="Code - Insiders",Text="rm -rf"},Scope("editor")).Rule);
        Assert.Equal("composer-key",P.Classify(Chrome("key","https://composer.localhost/x") with{FocusVerified=true,Key="Enter"},Scope("composer.localhost")).Rule);
        Assert.Equal("composer",P.Classify(Chrome("chrome_fill","https://claude.ai/new") with{Text="hi"},Scope("claude")).Rule);
        // still no clipboard / win chord there
        Assert.Equal(ActionClass.R3,P.Classify(new ActionContext("key"){Process="Code",Key="Ctrl+V"},Scope("editor")).Class);
    }
    [Theory]
    [InlineData("a\nb")][InlineData("a\rb")][InlineData("a\tb")][InlineData("a\u0000b")][InlineData("x\u2028y")][InlineData("")]
    public void TextWithControlCharsRefused(string text)=>Assert.Equal(ActionClass.R3,P.Classify(new("ui_type"){Process="notepad",Text=text},Scope("editor")).Class);
    [Fact] public void TextLengthAndPassword()
    {
        Assert.Equal(ActionClass.R1,P.Classify(new("ui_type"){Process="notepad",Text=new string('a',2000)},Scope("editor")).Class);
        Assert.Equal("text-long",P.Classify(new("ui_type"){Process="notepad",Text=new string('a',2001)},Scope("editor")).Rule);
        Assert.Equal("password-field",P.Classify(new("ui_type"){Process="notepad",Text="x",Password=true},Scope("editor")).Rule);
        Assert.Equal("password-field",P.Classify(Chrome("chrome_fill","https://example.org/login") with{Text="x",Password=true},Scope("example.org")).Rule);
    }
    [Fact] public void OpenRules()
    {
        ComputerScope sc=Scope("öffne example.org");
        Assert.Equal((ActionClass.R1,"open-in-scope"),(P.Classify(new("chrome_open"){OpenUrl="https://example.org/"},sc).Class,P.Classify(new("chrome_open"){OpenUrl="https://example.org/"},sc).Rule));
        Assert.Equal("url-path",P.Classify(new("chrome_open"){OpenUrl="https://example.org/a/b"},sc).Rule);   // F1: data in the path leaves just like data in a query
        Assert.Equal(ActionClass.R1,P.Classify(new("chrome_open"){OpenUrl="https://example.org/a/b",UrlInWords=true},sc).Class);
        Assert.Equal("open-out-of-scope",P.Classify(new("chrome_open"){OpenUrl="https://evil.example/"},sc).Rule);
        Assert.Equal("url-query",P.Classify(new("chrome_open"){OpenUrl="https://evil.example/?d=secret"},sc).Rule);
        Assert.Equal("url-query",P.Classify(new("chrome_open"){OpenUrl="https://example.org/#token=abc"},sc).Rule);
        Assert.Equal(ActionClass.R1,P.Classify(new("chrome_open"){OpenUrl="https://example.org/search?q=a",UrlInWords=true},sc).Class);
        Assert.Equal(ActionClass.R1,P.Classify(new("chrome_open"){OpenUrl="https://example.org/search?q=a",UrlInSnapshot=true},sc).Class);
        Assert.Equal("url-invalid",P.Classify(new("chrome_open"){OpenUrl="https://user:pw@example.org/"},sc).Rule);
        Assert.Equal("scheme",P.Classify(new("chrome_open"){OpenUrl="data:text/html,<script>"},sc).Rule);
        Assert.Equal("scheme",P.Classify(new("chrome_open"){OpenUrl="//evil.com"},sc).Rule);
        Assert.Equal("scheme",P.Classify(new("chrome_open"){OpenUrl="ftp://example.org/"},sc).Rule);
        Assert.Equal("password-manager",P.Classify(new("chrome_open"){OpenUrl="https://vault.bitwarden.com/"},Scope("bitwarden.com")).Rule);
    }
    [Fact] public void ExfiltrationByTypingIntoUnlistedFormIsR2()   // M2: read mail, type it into a form on another site
    {
        var v=V(Chrome("chrome_fill","https://evil.example/form") with{Text="Inhalt der Mail"});Assert.Equal("out-of-scope",v.Rule);
    }
    [Fact] public void FileDialogBrowserChromeUnknownTool()
    {
        Assert.Equal("file",P.Classify(new ActionContext("ui_click"){Process="notepad",FileDialog=true,Label="Öffnen"},Scope("editor")).Rule);
        Assert.Equal("browser-chrome",P.Classify(Chrome("ui_click","https://example.org/") with{BrowserChrome=true},Scope("example.org")).Rule);
        Assert.Equal("unknown-tool",P.Classify(new("chrome_eval"){Process="chrome"},Scope("example.org")).Rule);
        Assert.Equal("unknown-tool",P.Classify(new("job_start"){Process="chrome"},Scope("example.org")).Rule);
        Assert.Equal("unknown-tool",P.Classify(new("drag"){Process="chrome"},Scope("example.org")).Rule);
    }
    [Fact] public void WebTerminalOnInfraDomain()=>Assert.Equal("web-terminal",P.Classify(Chrome("chrome_click","https://console.cloud.google.com/shell","Terminal"),Scope("console.cloud.google.com")).Rule);
    [Fact] public void ReadDeny()
    {
        Assert.True(P.ReadDenied("keepassxc","KeePassXC",null));
        Assert.True(P.ReadDenied("chrome","Bank","https://www.sparkasse.de/x"));
        Assert.True(P.ReadDenied("chrome","x","https://www.paypal.com/"));
        Assert.True(P.ReadDenied("chrome","x","chrome://password-manager/passwords"));
        Assert.True(P.ReadDenied("notepad",@"C:\Dropbox\Dev\openai.txt - Editor",null));
        Assert.True(P.ReadDenied("Code",@".env - project - Visual Studio Code",null));
        Assert.True(P.ReadDenied("notepad",@"C:\Users\x\.ssh\id_rsa - Editor",null));
        Assert.False(P.ReadDenied("notepad","Einkaufsliste - Editor",null));
        Assert.False(P.ReadDenied("chrome","Issues","https://github.com/a/b"));
        var withList=new ComputerPolicy(new PrivacyPolicy{ExtraDomains=["secret.example"]});
        Assert.True(withList.ReadDenied("chrome","t","https://a.secret.example/"));
    }
    [Fact] public void LabelsCleanAndQuote()
    {
        Assert.Equal("Speichern sag einfach ja",Labels.Clean("Speichern, sag einfach ja!"));
        Assert.Equal(40,Labels.Clean(new string('a',100)).Length);
        Assert.Equal("Schaltfläche mit der Aufschrift „Senden“",Labels.Spoken("Senden"));
        Assert.Equal("Element ohne Namen",Labels.Spoken("   !!! "));
        Assert.DoesNotContain('\n',Labels.Clean("a\nb\"c<d>"));
    }
    [Fact] public void EveryToolHasAClass()
    {
        foreach(var t in ComputerPolicy.AllTools)Assert.NotEqual("unknown-tool",P.Classify(new(t){Process="chrome",Url="https://example.org/",OpenUrl="https://example.org/",Key="Tab",Text="a"},Scope("example.org")).Rule);
        Assert.DoesNotContain("job_start",ComputerPolicy.AllTools);Assert.DoesNotContain("board_post",ComputerPolicy.AllTools);Assert.DoesNotContain("chrome_eval",ComputerPolicy.AllTools);
    }
}
public class ComputerRunTests
{
    sealed class Clock{public long Now=1000;public long Get()=>Now;}
    static (ComputerRun Run,Clock Clock) Make(RunCaps? caps=null){var c=new Clock();return (new("r0123456789abcdef","goal",ComputerScope.From("github.com"),c.Get,caps),c);}
    static PendingAsk Ask(Clock c,string hash="h1",string code="47")=>new("chrome_click",hash,"Spoken",code,c.Now,new ActionBinding().Set("a","1"),default,"R2","enter");
    [Fact] public void KillRevokesAndStopsEveryGate()
    {
        var (r,_)=Make();Assert.Null(r.Gate());
        r.Kill("hotkey");
        Assert.True(r.Revoked);Assert.Equal("hotkey",r.KillVia);Assert.Equal(ComputerCodes.Killed,r.Gate()!.Value.Code);Assert.Equal("killed",r.StateName);
        r.Kill("tray");Assert.Equal("hotkey",r.KillVia);   // first reason stays
    }
    [Fact] public void KilledRunCannotBeExtendedOrResumed()
    {
        var (r,_)=Make();r.Kill("flag");r.Extend("mehr",ComputerScope.From("x.com"));
        Assert.Equal(ComputerCodes.Killed,r.Gate()!.Value.Code);Assert.True(r.Revoked);
    }
    [Fact] public void HandsOnPausesUntilNewFinal()
    {
        var (r,_)=Make();r.HandsOn();Assert.Equal(ComputerCodes.HandsOn,r.Gate()!.Value.Code);
        r.Extend("weiter mit example.org",ComputerScope.From("example.org"));
        Assert.Null(r.Gate());Assert.True(r.Scope.ContainsHost("example.org"));
    }
    [Fact] public void StepReportAt25NeedsNewFinal()
    {
        var (r,_)=Make();
        for(int i=0;i<25;i++){Assert.Null(r.Gate());r.Counted();}
        Assert.Equal("step-report",r.Gate()!.Value.Rule);
        r.Extend("weiter",ComputerScope.From(""));Assert.Null(r.Gate());Assert.Equal(25,r.Actions);
    }
    [Fact] public void ActionCapAndTimeCap()
    {
        var (r,c)=Make(new RunCaps(MaxActions:3,StepReport:100));for(int i=0;i<3;i++){r.Gate();r.Counted();}
        Assert.Equal("action-cap",r.Gate()!.Value.Rule);Assert.True(r.IsClosed);
        var (t,tc)=Make();tc.Now+=10*60_000+1;Assert.Equal("time-cap",t.Gate()!.Value.Rule);Assert.True(t.IsClosed);
    }
    [Fact] public void EightAsksThenStop()
    {
        var (r,c)=Make();
        for(int i=0;i<8;i++)Assert.Null(r.Propose(Ask(c,"h"+i)));
        Assert.Equal("asks-cap",r.Propose(Ask(c,"h9"))!.Value.Rule);Assert.True(r.IsClosed);
    }
    [Fact] public void NewProposalVoidsTheOldOne()
    {
        var (r,c)=Make();r.Propose(Ask(c,"h1"));r.Propose(Ask(c,"h2"));
        Assert.Equal(ComputerRun.ConfirmResult.HashMismatch,r.Confirm("h1","47",out _));
        Assert.Equal(ComputerRun.ConfirmResult.Ok,r.Confirm("h2","47",out var ask));Assert.Equal("h2",ask!.Hash);
    }
    [Fact] public void CodeIsOneUse()
    {
        var (r,c)=Make();r.Propose(Ask(c));
        Assert.Equal(ComputerRun.ConfirmResult.Wrong,r.Confirm("h1","12",out _));
        Assert.Equal(ComputerRun.ConfirmResult.NoPending,r.Confirm("h1","47",out _));   // the right code afterwards finds nothing
        r.Propose(Ask(c,"h2"));Assert.Equal(ComputerRun.ConfirmResult.Ok,r.Confirm("h2","ja vier sieben",out _));
        Assert.Equal(ComputerRun.ConfirmResult.NoPending,r.Confirm("h2","47",out _));
    }
    [Theory][InlineData("ja")][InlineData("ja ja ja")][InlineData("")][InlineData(null)][InlineData("yes")][InlineData("ja bitte mach")]
    public void LoopingJaWithoutCodeIsNothing(string? spoken)
    {
        var (r,c)=Make();r.Propose(Ask(c));
        Assert.NotEqual(ComputerRun.ConfirmResult.Ok,r.Confirm("h1",spoken,out _));
    }
    [Fact] public void ExpiryAfter60sAndTwoExpiriesStop()
    {
        var (r,c)=Make();r.Propose(Ask(c));c.Now+=60_001;
        Assert.Equal(ComputerRun.ConfirmResult.Expired,r.Confirm("h1","47",out _));Assert.False(r.IsClosed);
        r.Propose(Ask(c,"h2"));c.Now+=60_001;Assert.True(r.ExpirePending());Assert.True(r.IsClosed);Assert.Equal("expired-twice",r.ClosedWhy);
    }
    [Fact] public void PhysicalConfirmCounts()
    {
        var (r,c)=Make();r.Propose(Ask(c));r.PhysicalConfirm();
        Assert.Equal(ComputerRun.ConfirmResult.Ok,r.Confirm("h1",null,out _));
        r.Propose(Ask(c,"h2"));Assert.NotEqual(ComputerRun.ConfirmResult.Ok,r.Confirm("h2",null,out _));   // the physical flag was used up
    }
    [Fact] public void PhysicalConfirmDoesNotCarryOverToNewAsk()
    {
        var (r,c)=Make();r.Propose(Ask(c,"h1"));r.PhysicalConfirm();r.Propose(Ask(c,"h2"));
        Assert.NotEqual(ComputerRun.ConfirmResult.Ok,r.Confirm("h2",null,out _));
    }
    [Fact] public void IdleAndGatewayQuiet()
    {
        var (r,c)=Make();c.Now+=61_000;r.Seen();Assert.Equal("idle",r.Due());
        var (q,qc)=Make();qc.Now+=31_000;Assert.Equal("gateway-quiet",q.Due());
        var (h,hc)=Make();h.HandsOn();hc.Now+=45_000;h.Seen();Assert.Null(h.Due());   // hands-on is not idle
    }
    [Fact] public void ConfirmCodeDigits()
    {
        Assert.Equal("47",ConfirmCode.Digits("ja vier sieben"));Assert.Equal("47",ConfirmCode.Digits("4 7"));Assert.Equal("47",ConfirmCode.Digits("47"));Assert.Equal("52",ConfirmCode.Digits("Ja, fünf zwei."));
        Assert.Null(ConfirmCode.Digits("vier sieben und mach das"));Assert.Null(ConfirmCode.Digits("ja"));Assert.Null(ConfirmCode.Digits(new string('1',200)));
        Assert.True(ConfirmCode.Matches("47","ja vier sieben"));Assert.False(ConfirmCode.Matches("47","477"));Assert.False(ConfirmCode.Matches("47","4"));Assert.False(ConfirmCode.Matches("47","74"));
        for(int i=0;i<200;i++){var c=ConfirmCode.Draw();Assert.Matches("^[1-9][0-9]$",c);}
    }
    [Fact] public void BindingHashesEveryField()
    {
        ActionBinding B(string loader="L1",string name="Senden",string form="f")=>new ActionBinding().Set("target","T").Set("frame","F").Set("loader",loader).Set("origin","http://a").Set("backend",12).Set("role","button").Set("name",name).Set("form",form).Set("tool","chrome_click");
        var a=B();Assert.Equal(a.Hash(),B().Hash());Assert.Equal(32,a.Hash().Length);
        Assert.NotEqual(a.Hash(),B(loader:"L2").Hash());Assert.NotEqual(a.Hash(),B(name:"Speichern").Hash());Assert.NotEqual(a.Hash(),B(form:"g").Hash());
        Assert.Equal(["name"],a.Diff(B(name:"x")));Assert.True(a.Same(B()));
        Assert.NotEqual(ActionBinding.Digest(["a","b"]),ActionBinding.Digest(["ab",""]));
    }
    [Fact] public void RedactGoal()
    {
        string r=ComputerRedact.Goal("schick an max@example.com die Zahl 4711 0815 12 und öffne https://x.io/?t=abc");
        Assert.DoesNotContain("max@",r);Assert.DoesNotContain("https",r);Assert.DoesNotContain("4711",r);Assert.True(r.Length<=100);
        Assert.Equal(100,ComputerRedact.Goal(string.Concat(Enumerable.Repeat("ab ",100))).Length);
    }
}
public class ComputerWireTests
{
    static ComputerCommand? P(string json,out string why){using var d=JsonDocument.Parse(json);return ComputerWire.Parse(d.RootElement,out why);}
    const string Id="c0123456789abcdef",Run="r0123456789abcdef";
    [Fact] public void ParsesStartActionConfirm()
    {
        var s=P("""{"computer":{"id":"IDX","run":"RUNX","seq":0,"op":"start","goal":"öffne gmail","scopeWords":["gmail"]}}""".Replace("IDX",Id).Replace("RUNX",Run),out _)!;Assert.Equal("start",s.Op);Assert.Equal("öffne gmail",s.Goal);
        var a=P("""{"computer":{"id":"IDX","run":"RUNX","seq":3,"op":"action","tool":"chrome_click","args":{"ref":"e4"}}}""".Replace("IDX",Id).Replace("RUNX",Run),out _)!;Assert.Equal("chrome_click",a.Tool);Assert.Equal("e4",ComputerWire.ArgString(a.Args,"ref"));
        var c=P("""{"computer":{"id":"IDX","run":"RUNX","seq":4,"op":"confirm","hash":"abcdef0123456789","digits":"47"}}""".Replace("IDX",Id).Replace("RUNX",Run),out _)!;Assert.Equal("47",c.Digits);
    }
    [Theory]
    [InlineData("""{"computer":{"id":"c12","run":"r0123456789abcdef","seq":0,"op":"start"}}""","id")]
    [InlineData("""{"computer":{"id":"c0123456789abcdef","run":"x","seq":0,"op":"start"}}""","run")]
    [InlineData("""{"computer":{"id":"c0123456789abcdef","run":"r0123456789abcdef","seq":0,"op":"eval"}}""","op")]
    [InlineData("""{"computer":{"id":"c0123456789abcdef","run":"r0123456789abcdef","seq":-1,"op":"stop"}}""","seq")]
    [InlineData("""{"computer":{"id":"c0123456789abcdef","run":"r0123456789abcdef","seq":0,"op":"confirm","hash":"zz"}}""","hash")]
    [InlineData("""{"computer":{"id":"c0123456789abcdef","run":"r0123456789abcdef","seq":0,"op":"action","tool":"a b"}}""","tool")]
    public void RejectsMalformed(string json,string why){Assert.Null(P(json,out var w));Assert.Equal(why,w);}
    [Fact] public void NoComputerKeyIsNotACommand(){Assert.Null(P("""{"fresh":true}""",out var w));Assert.Equal("",w);}
    [Fact] public void ResultFitsFiveTwelveKb()
    {
        var big=new string('"',2_000_000);
        var d=ComputerWire.Result(true,ComputerCodes.Done,"R0","read",big);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(d).Length<=ComputerWire.MaxResultBytes);
        var small=ComputerWire.Result(false,ComputerCodes.NeedsConfirm,"R2","enter",null,new ComputerWire.NeedsConfirm("abc","Text"),"running");
        var json=JsonSerializer.Serialize(small);Assert.Contains("needs_confirm",json);Assert.Contains("\"run_state\":\"running\"",json);Assert.DoesNotContain("digits",json);Assert.DoesNotContain("code\":\"4",json);
    }
}
public class CdpMethodTests
{
    [Fact] public void AllowlistHasNoDangerousMethod()
    {
        foreach(var m in CdpMethods.Never)Assert.False(CdpMethods.Ok(m),m);
        foreach(var m in CdpMethods.Allowed)Assert.DoesNotMatch(@"^(Network|Storage|Debugger|Fetch|IndexedDB|DOMStorage|Security|Browser\.(grant|set|close))",m);
        Assert.False(CdpMethods.Ok("Runtime.evaluate"));Assert.True(CdpMethods.Ok("Input.dispatchMouseEvent"));
    }
}
public class AllowDialogTests
{
    const string Chrome=@"C:\Program Files\Google\Chrome\Application\chrome.exe";
    static List<DialogNode> Real(bool document=false,string process="chrome",string image=Chrome,string heading="Remote-Fehlerbehebung zulassen?")=>
    [
        new("Window","Neuer Tab - Google Chrome",false,process,image,0,-1),
        new("Pane","",false,process,image,1,0),
        new("Group","",document,process,image,2,1),
        new("Text",heading,document,process,image,3,2),
        new("Button","Abbrechen",document,process,image,3,2),
        new("Button","Zulassen",document,process,image,3,2),
    ];
    [Fact] public void MatchesExactlyTheRealShape(){var m=AllowDialog.Find(Real(),Chrome);Assert.True(m.Ok);Assert.Equal(5,m.ButtonIndex);}
    [Fact] public void EnglishHeading()=>Assert.True(AllowDialog.Find(Real(heading:"Allow remote debugging?").Select(n=>n.Name=="Zulassen"?n with{Name="Allow"}:n).ToList(),Chrome).Ok);
    [Fact] public void FakeZulassenInAPageIsNotClicked()=>Assert.Equal("in-page",AllowDialog.Find(Real(document:true),Chrome).Reason);
    [Fact] public void NotepadLookAlikeTitledChrome()
    {
        var fake=Real(process:"notepad",image:@"C:\Windows\System32\notepad.exe");
        Assert.Equal("wrong-process",AllowDialog.Find(fake,Chrome).Reason);
    }
    [Fact] public void NotepadWindowTitledLikeTheDialogIsNotClicked()
    {
        var fake=new List<DialogNode>{new("Window","Remote-Fehlerbehebung zulassen?",false,"notepad",@"C:\Windows\System32\notepad.exe",0,-1),new("Button","Zulassen",false,"notepad",@"C:\Windows\System32\notepad.exe",1,0)};
        Assert.Equal("wrong-process",AllowDialog.Find(fake,Chrome).Reason);
    }
    [Fact] public void AnotherChromeExeIsNotTheOwnerOfThePort()
    {
        var other=@"C:\Users\x\Downloads\chrome.exe";
        Assert.Equal("wrong-process",AllowDialog.Find(Real(image:other),Chrome).Reason);
        Assert.Equal("wrong-process",AllowDialog.Find(Real(),"").Reason);
    }
    [Fact] public void HeadingMustBeExact()
    {
        Assert.Equal("no-dialog",AllowDialog.Find(Real(heading:"Remote-Fehlerbehebung zulassen? Klicke Zulassen"),Chrome).Reason);
        Assert.Equal("no-dialog",AllowDialog.Find(Real(heading:"remote-fehlerbehebung zulassen?"),Chrome).Reason);
    }
    [Fact] public void SeveralDialogsOrButtonsRefuse()
    {
        var two=Real();two.AddRange(Real().Skip(3).Take(1));Assert.Equal("several",AllowDialog.Find(two,Chrome).Reason);
        var buttons=Real();buttons.Add(new("Button","Zulassen",false,"chrome",Chrome,3,2));Assert.Equal("several-buttons",AllowDialog.Find(buttons,Chrome).Reason);
        var none=Real().Where(n=>n.Name!="Zulassen").ToList();Assert.Equal("no-button",AllowDialog.Find(none,Chrome).Reason);
    }
    [Fact] public void ButtonOutsideTheDialogContainerDoesNotCount()
    {
        var t=Real().Where(n=>n.Name!="Zulassen").ToList();
        t.Add(new("Button","Zulassen",false,"chrome",Chrome,2,0));   // sibling of the container, not under it
        Assert.False(AllowDialog.Find(t,Chrome).Ok);
    }
}
