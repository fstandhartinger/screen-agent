using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
// v0.1.7 f-fix2: one test per finding of CRITIC-F2 that fails if the fix is taken out (mutated in a scratch copy, see RESULT.md).
public class ComputerFix2PolicyTests
{
    static readonly ComputerPolicy P=new(deskHost:"desk.localhost");
    static ComputerScope Scope(string goal)=>ComputerScope.From(goal);
    static ActionContext Chrome(string tool,string url,string? label=null)=>new(tool){Process="chrome",Url=url,Label=label};
    static string R(ActionContext c,ComputerScope s){var v=P.Classify(c,s);return v.Class+"/"+v.Rule;}
    // ---- N1: data in the host name and the port
    [Theory]
    [InlineData("https://u0vdukvux21bsuw.evil-blog.com/")][InlineData("https://u0vd.ukvu.x21b.suw.evil-blog.com")][InlineData("https://evil-blog.com:4711/")][InlineData("https://www.evil-blog.com/")]
    public void N1_ASubdomainOrAPortOfAScopeHostIsRefusedLikeAPath(string url)
    {
        var sc=Scope("öffne evil-blog.com");
        Assert.Equal("R3/url-host",R(new("chrome_open"){Process="chrome",OpenUrl=url,Tainted=true},sc));
        Assert.Equal("R3/url-host",R(new("chrome_open"){Process="chrome",OpenUrl=url},sc));
    }
    [Fact] public void N1_AHostThatIsAScopeEntryOpensFreelyAndAnyOpenAsksUnderTaint()
    {
        var sc=Scope("öffne evil-blog.com");
        Assert.Equal("R1/open-in-scope",R(new("chrome_open"){Process="chrome",OpenUrl="https://evil-blog.com/"},sc));
        Assert.Equal("R2/tainted",R(new("chrome_open"){Process="chrome",OpenUrl="https://evil-blog.com/",Tainted=true},sc));
        // his own words, or a link of the page, still allow a subdomain or a port (the data cannot be in them)
        Assert.Equal("R1/open-in-scope",R(new("chrome_open"){Process="chrome",OpenUrl="https://www.evil-blog.com/",UrlInWords=true},sc));
        Assert.Equal("R1/open-in-scope",R(new("chrome_open"){Process="chrome",OpenUrl="https://evil-blog.com:8080/",UrlInSnapshot=true},sc));
        Assert.Equal("R2/open-out-of-scope",R(new("chrome_open"){Process="chrome",OpenUrl="https://other.example/",UrlInWords=true},sc));
    }
    [Fact] public void N1_AnApprovedHostIsExactNotItsParent()
    {
        var sc=Scope("öffne evil-blog.com");sc.Opened.Add("accounts.example.org");
        Assert.True(sc.Reaches("accounts.example.org"));Assert.False(sc.Reaches("example.org"));Assert.False(sc.Reaches("mail.accounts.example.org"));
        sc.Opened.Add("google.com");Assert.False(sc.Reaches("mail.google.com"));   // NIT: approving google.com does not open mail.google.com
    }
    // ---- N3: "hier" resolves to a host that is spoken in the ask; unresolvable adds nothing
    [Fact] public void N3_HierResolvesTheFrontTabHostAndThatMakesTheStartAsk()
    {
        var host=ComputerScope.ResolveHere("klick hier auf weiter","chrome","Chrome_WidgetWin_1",()=>"https://shop.example.org/cart",P);
        Assert.Equal("shop.example.org",host);
        var sc=ComputerScope.From("klick hier auf weiter");Assert.True(sc.Empty);   // with no host the scope was empty, so the old code started without any ask
        sc.Hosts.Add(host!);Assert.False(sc.Empty);   // the app asks when the scope can act
        var ext=ComputerScope.From("öffne evil.com hier");Assert.Equal(["evil.com"],ext.Hosts);ext.Hosts.Add(host!);Assert.Contains("shop.example.org",ext.Hosts);
    }
    [Fact] public void N3_HierThatCannotBeResolvedAddsNothing()
    {
        Assert.Null(ComputerScope.ResolveHere("klick hier","chrome",null,()=>null,P));                                  // no tab found
        Assert.Null(ComputerScope.ResolveHere("klick hier","chrome",null,()=>throw new InvalidOperationException(),P)); // lookup failed
        Assert.Null(ComputerScope.ResolveHere("klick hier","notepad","Notepad",()=>"https://example.org/",P));          // not a browser
        Assert.Null(ComputerScope.ResolveHere("Hierarchie öffnen","chrome",null,()=>"https://example.org/",P));         // not the word "hier"
        Assert.Null(ComputerScope.ResolveHere("klick hier","chrome",null,()=>"https://www.sparkasse-koelnbonn.de/",P)); // a page that may not be read is not added
        Assert.Null(ComputerScope.ResolveHere("klick hier","chrome",null,()=>"chrome://settings/",P));
    }
    // ---- N4: Enter on a page that may not be read
    [Fact] public void N4_EnterOnAReadDeniedPageIsRefusedNotAsked()
    {
        var sc=Scope("öffne evil-blog.com");
        Assert.Equal("R3/read-denied",R(new("key"){Process="chrome",Url="https://www.sparkasse-koelnbonn.de/",Key="enter",FocusVerified=true},sc));
        Assert.Equal("R3/read-denied",R(new("key"){Process="chrome",Url="https://www.paypal.com/",Key="tab",FocusVerified=true},sc));
        Assert.Equal("R3/read-denied",R(new("key"){Process="notepad",Title="OPENROUTER.txt - Editor",Key="enter",FocusVerified=true},Scope("öffne den editor")));
        Assert.Equal("R2/out-of-scope",R(new("key"){Process="chrome",Url="https://example.org/",Key="enter",FocusVerified=true},sc));   // out of scope: asks, and the app names only the host
    }
    // ---- SHOULD: where Enter goes, and a page-set name cannot make it a search
    [Fact] public void Enter_FormActionOutOfScopeAsksEvenInASearchBox()
    {
        var sc=Scope("öffne evil-blog.com");
        var k=new ActionContext("key"){Process="chrome",Url="https://evil-blog.com/",Key="enter",SearchBox=true,FocusVerified=true};
        Assert.Equal("R1/enter-search",R(k,sc));
        Assert.Equal("R2/link-out-of-scope",R(k with{Href="https://collector.example/c"},sc));
        Assert.Equal("R1/enter-search",R(k with{Href="https://evil-blog.com/search"},sc));
        Assert.Equal("R2/link-out-of-scope",R(k with{SearchBox=false,Href="https://collector.example/c"},sc));
    }
    // ---- N5: taint by origin and the storage hosts
    [Theory]
    [InlineData("https://docs.google.com/document/d/x")][InlineData("https://drive.google.com/")][InlineData("https://calendar.google.com/")][InlineData("https://contacts.google.com/")][InlineData("https://photos.google.com/")]
    [InlineData("https://www.dropbox.com/home")][InlineData("https://www.notion.so/x")][InlineData("https://onedrive.live.com/")][InlineData("https://firma.sharepoint.com/")][InlineData("https://firma.atlassian.net/wiki")]
    public void N5_StorageHostsTaintOnReadAndAskOnAction(string url)
    {
        Assert.True(P.SensitiveRead("chrome","t",url),url);
        Assert.Equal("R2/sensitive-domain",R(Chrome("chrome_fill",url,"x") with{Text="y"},Scope("öffne "+new Uri(url).Host)));
    }
    [Fact] public void N5_ActingOnAnotherOriginThanTheOneReadAsks()
    {
        var sc=Scope("öffne docs.example.org und evil-blog.com");
        string read=ComputerPolicy.ReadOrigin("chrome","https://docs.example.org/doc/1");
        Assert.Equal("https://docs.example.org",read);
        var fill=Chrome("chrome_fill","https://evil-blog.com/c","Kommentar") with{Text="Inhalt"};
        Assert.Equal("R1/in-scope",R(fill,sc));                                       // nothing read yet
        Assert.Equal("R2/cross-origin",R(fill with{ReadOrigins=[read]},sc));            // read on A, typing on B
        Assert.Equal("R2/cross-origin",R(Chrome("chrome_click","https://evil-blog.com/c","Weiter") with{ReadOrigins=[read]},sc));
        Assert.Equal("R1/in-scope",R(Chrome("chrome_fill","https://docs.example.org/doc/1","Titel") with{Text="x",ReadOrigins=[read]},sc));   // the same origin stays free
        Assert.Equal("R2/cross-origin",R(new ActionContext("ui_type"){Process="notepad",Label="Text",Text="x",ReadOrigins=[read]},Scope("öffne docs.example.org und den editor")));
        Assert.Equal("app:notepad",ComputerPolicy.ReadOrigin("notepad.exe",null));
    }
    [Fact] public void N5_TheRunRemembersReadOriginsAndTheyOnlyGrow()
    {
        var r=new ComputerRun("r0123456789abcdef","g",ComputerScope.From("example.org"));
        Assert.Empty(r.ReadOrigins);r.NoteRead("https://a.example");r.NoteRead("https://a.example");r.NoteRead("app:notepad");
        Assert.Equal(2,r.ReadOrigins.Length);
    }
    // ---- SHOULD: covert channels under taint
    [Fact] public void Covert_UnderTaintOnlyReadsRunScrollKeysAndOpensAsk()
    {
        var sc=Scope("öffne example.org");
        var tab=new ActionContext("key"){Process="chrome",Url="https://example.org/",Key="tab",FocusVerified=true};
        Assert.Equal("R1/key",R(tab,sc));
        Assert.Equal("R2/tainted",R(tab with{Tainted=true},sc));
        Assert.Equal("R2/tainted",R(tab with{Key="down",Tainted=true},sc));
        Assert.Equal("R3/key-no-focus",R(tab with{Tainted=true,FocusVerified=false},sc));
        Assert.Equal("R1/scroll",R(new ActionContext("scroll"){Process="chrome",Url="https://example.org/"},sc));
        Assert.Equal("R2/tainted",R(new ActionContext("scroll"){Process="chrome",Url="https://example.org/",Tainted=true},sc));
        Assert.Equal("R0/read",R(Chrome("chrome_read","https://example.org/") with{Tainted=true},sc));
        Assert.Equal("R0/read",R(Chrome("chrome_snapshot","https://example.org/") with{Tainted=true},sc));
    }
    // ---- N7: secrets never go into the spoken question
    [Fact] public void N7_ATokenInAFieldIsNotSpoken()
    {
        string line=ComputerSpeech.FormLine([("token","ghp_abcdefghijklmnopqrstuvwxyz0123456789")]);
        Assert.DoesNotContain("ghp_",line);Assert.Contains("ausgeblendet",line);
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz",ComputerSpeech.Quote("sk-abcdefghijklmnopqrstuvwxyz012345"));
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9",ComputerSpeech.Quote("Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijk"));
        Assert.Equal("„Hallo Welt“",ComputerSpeech.Quote("Hallo Welt"));   // ordinary words stay
        Assert.Contains("ausgeblendet",ComputerSpeech.Quote("password=hunter2hunter2"));
    }
    [Fact] public void Nit_TheSpokenQuestionIsCappedAndSaysWhatWasLeftOut()
    {
        var many=Enumerable.Range(1,9).Select(i=>("f"+i,"wert"+i)).ToList();
        string line=ComputerSpeech.FormLine(many);
        Assert.Contains("Und 3 weitere Felder",line);Assert.Contains("wert6",line);Assert.DoesNotContain("wert7",line);
        Assert.Contains("Und 2 weitere Empfänger",ComputerSpeech.FormLine([],Enumerable.Range(1,8).Select(i=>"a"+i+"@b.de")));
        string huge=string.Join(" ",Enumerable.Repeat(new string('x',300),6));
        var long6=Enumerable.Range(1,6).Select(i=>("f"+i,string.Join(" ",Enumerable.Repeat(new string('w',300),15)))).ToList();
        Assert.True(ComputerSpeech.Fit(ComputerSpeech.FormLine(long6)+huge+huge).Length<=ComputerSpeech.MaxSpoken+8);
        Assert.True(ComputerSpeech.Quote(new string('a',300)).Length<=ComputerSpeech.WordChars+6);
    }
    // ---- SHOULD: lists
    [Theory]
    [InlineData("https://www.berliner-sparkasse.de/")][InlineData("https://www.sskm.de/")][InlineData("https://www.haspa.de/")][InlineData("https://www.ksk-koeln.de/")][InlineData("https://www.apobank.de/")]
    [InlineData("https://www.bunq.com/")][InlineData("https://www.gls.de/")][InlineData("https://www.tomorrow.one/")][InlineData("https://www.meine-volksbank.de/")][InlineData("https://sparkassen-finanzportal.de/")]
    [InlineData("https://www.volksbank-xyz.example/")][InlineData("https://online.sparkasse.org/")]
    public void Lists_RealBankNamesAreReadDenied(string url)=>Assert.True(P.ReadDenied("chrome","Titel",url),url);
    [Theory]
    [InlineData("https://www.amazon.de/gp/buy/spc/handlers/display.html")][InlineData("https://www.booking.com/book")][InlineData("https://www.zalando.de/warenkorb/")][InlineData("https://www.ebay.de/")]
    public void Lists_ShopsAreSensitive(string url)
    {
        Assert.True(P.SensitiveRead("chrome","t",url));
        Assert.Equal("R2/sensitive-domain",R(Chrome("chrome_click",url,"Weiter"),Scope("öffne "+new Uri(url).Host)));
        Assert.False(P.ReadDenied("chrome","t",url));   // a shop page stays readable, only acting asks
    }
    [Theory]
    [InlineData("Bestellung aufgeben")][InlineData("Kostenpflichtig abschließen")][InlineData("Jetzt buchen")][InlineData("Zur Kasse")][InlineData("Abonnieren")][InlineData("Checkout")][InlineData("Place order")][InlineData("Zahlungspflichtig bestellen")]
    public void Lists_MoneyLabelsAskOnAnUnlistedShop(string label)
    {
        Assert.True(Labels.SendLike(label),label);
        Assert.Equal("R2/label-send",R(Chrome("chrome_click","https://kleiner-laden.com/x",label),Scope("öffne kleiner-laden.com")));
    }
    [Fact] public void Lists_PlainLabelsStayFree()
    {
        foreach(var l in new[]{"Weiter","Mehr anzeigen","Suchen","Menü","Startseite","Details"})Assert.False(Labels.SendLike(l),l);
    }
    // ---- SHOULD: other browsers and the engine window classes
    [Theory][InlineData("chromium")][InlineData("librewolf")][InlineData("zen")][InlineData("tor")][InlineData("waterfox")][InlineData("thorium")][InlineData("msedgewebview2")][InlineData("yandex")][InlineData("comet")][InlineData("dia")]
    public void Browsers_UnlistedBrowsersCountAsBrowsers(string p)=>Assert.True(ComputerPolicy.IsBrowser(p),p);
    [Fact] public void Browsers_AnEngineWindowClassIsABrowserWhateverTheProcessIsCalled()
    {
        Assert.False(ComputerPolicy.IsBrowser("noname"));Assert.True(ComputerPolicy.IsBrowser("noname","Chrome_WidgetWin_1"));Assert.True(ComputerPolicy.IsBrowser("noname","MozillaWindowClass"));Assert.False(ComputerPolicy.IsBrowser("noname","Notepad"));
    }
    // ---- SHOULD: a title match is trusted only for the browser that owns the debugging port
    [Fact] public void Title_OnlyTheBrowserThatOwnsTheDebugPortGetsATab()
    {
        Assert.True(ComputerPolicy.OwnsPort("chrome","chrome.exe"));Assert.True(ComputerPolicy.OwnsPort("chrome","Chrome"));
        Assert.False(ComputerPolicy.OwnsPort("chrome","msedge"));Assert.False(ComputerPolicy.OwnsPort("msedge","chrome"));
        Assert.True(ComputerPolicy.OwnsPort("","msedge"));   // owner not known (selftest with --remote-debugging-port)
        Assert.True(ComputerSpeech.TabTitleMatches("Sparkasse - Microsoft Edge","Sparkasse"));   // the title alone cannot tell the browsers apart, which is why the owner is checked
    }
    // ---- NIT: SVG links
    [Fact] public void Nit_AnSvgLinkTargetIsNeverReadAsRelative()
    {
        var sc=Scope("öffne evil-blog.com");
        Assert.Equal("R3/url-invalid",R(Chrome("chrome_click","https://evil-blog.com/","Mehr") with{Href="[object SVGAnimatedString]"},sc));
        Assert.Equal("R3/url-invalid",R(Chrome("chrome_click","https://evil-blog.com/","Mehr") with{Href="invalid:"},sc));
    }
    [Fact] public void Nit_AnExtendedGoalIsCutAtTheEndNotFrozen()
    {
        var r=new ComputerRun("r0123456789abcdef",new string('a',590),ComputerScope.From("example.org"));
        r.Extend("zweiter Auftrag mit viel Text",ComputerScope.From("evil-blog.com"));
        Assert.True(r.Goal.Length<=600);Assert.EndsWith("…",r.Goal);Assert.Contains("evil-blog.com",r.Scope.Hosts);
    }
}
