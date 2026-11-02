using System.Text.Json;
using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
// v0.1.6: Main Agent prompt logic (text rules, candidate order, focus rules, command parsing, gate), the canvas chord and the drawing panel placement.
public class MainAgentTests
{
    static JsonElement Json(string s)=>JsonDocument.Parse(s).RootElement.Clone();
    [Fact] public void PromptCommandNeedsShapeAndId()
    {
        var ok=PromptCommand.Parse(Json("""{"fresh":false,"prompt":{"id":"p0123456789abcdef","text":"Hallo"}}"""));
        Assert.Equal(new PromptRequest("p0123456789abcdef","Hallo"),ok);
        Assert.Null(PromptCommand.Parse(Json("""{"fresh":true,"id":"x"}""")));
        Assert.Null(PromptCommand.Parse(Json("""{"prompt":{"id":"p123","text":"x"}}""")));
        Assert.Null(PromptCommand.Parse(Json("""{"prompt":{"id":"P0123456789ABCDEF","text":"x"}}""")));
        Assert.Null(PromptCommand.Parse(Json("""{"prompt":{"id":"p0123456789abcdef","text":5}}""")));
        Assert.Null(PromptCommand.Parse(Json("[]")));
    }
    [Fact] public void GateAcceptsOneAtATimeIgnoresRepeats()
    {
        var g=new PromptGate();
        Assert.Equal(PromptStart.Accepted,g.Begin("a"));
        Assert.Equal(PromptStart.Repeat,g.Begin("a"));
        Assert.Equal(PromptStart.Busy,g.Begin("b"));
        Assert.Equal(PromptStart.Repeat,g.Begin("b"));   // answered busy once, never again
        g.End();
        Assert.Equal(PromptStart.Accepted,g.Begin("c"));
    }
    [Fact] public void GateForgetsOldIds()
    {
        var g=new PromptGate(2);g.Begin("a");g.End();g.Begin("b");g.End();g.Begin("c");g.End();
        Assert.Equal(PromptStart.Accepted,g.Begin("a"));
    }
    [Theory]
    [InlineData("a\r\nb","a\nb")][InlineData("a\rb","a\nb")][InlineData("a\tb","a  b")][InlineData("a\u0007b\u001bc","abc")]
    [InlineData("a\n\n\n\nb","a\n\nb")][InlineData("  \n hi \n ","hi")][InlineData("/clear"," /clear")][InlineData("@datei"," @datei")]
    [InlineData("Grüße 😀 ÄÖÜ ß a#abc123","Grüße 😀 ÄÖÜ ß a#abc123")]
    public void NormalizeRules(string input,string expected)=>Assert.Equal(expected,PromptText.Normalize(input));
    [Fact] public void NormalizeCapsWithoutSplittingAPair()
    {
        string s=PromptText.Normalize(new string('x',PromptText.MaxChars-1)+"😀 tail");
        Assert.True(s.Length<=PromptText.MaxChars);Assert.False(char.IsHighSurrogate(s[^1]));
        Assert.Equal(PromptText.MaxChars,PromptText.Normalize(new string('y',20000)).Length);
    }
    [Fact] public void StepsKeepNewlinesAtomicAndPairsTogether()
    {
        string text="Zeile eins 😀 mit Emoji\nzwei\n\ndrei "+new string('z',60);
        var steps=PromptText.Steps(text);
        Assert.All(steps.Where(s=>s.Kind==StepKind.Chars),s=>{Assert.DoesNotContain('\n',s.Chars);Assert.InRange(s.Chars.Length,1,PromptText.BatchChars);Assert.False(char.IsHighSurrogate(s.Chars[^1]));Assert.False(char.IsLowSurrogate(s.Chars[0]));});
        Assert.Equal(3,steps.Count(s=>s.Kind==StepKind.Newline));
        Assert.Equal(text,string.Concat(steps.Select(s=>s.Chars)));
        Assert.Equal(4,steps.Count(s=>s.Kind==StepKind.Chars&&s.Chars.Contains('z')));   // "zwei", then "drei " + 60 z = 65 units in batches of 24, 24, 17
    }
    [Fact] public void ReadBackComparesTheTailWhitespaceNormalized()
    {
        string typed="Erste Zeile mit Umlauten äöü\nzweite Zeile a#abc123 und noch etwas Text am Ende 😀";
        Assert.True(PromptText.Landed("  Erste Zeile mit Umlauten äöü zweite Zeile a#abc123 und noch etwas Text am Ende 😀 ",typed));
        Assert.True(PromptText.Landed("alter Entwurf "+typed.Replace("\n","\r\n"),typed));
        Assert.False(PromptText.Landed("Erste Zeile mit Umlauten äöü zweite Zeile a#abc123 und noch etwas",typed));
        Assert.False(PromptText.Landed("",typed));
        Assert.False(PromptText.Landed("irgendwas",""));
        Assert.True(PromptText.Tail(typed).Length<=PromptText.TailChars);
        Assert.False(char.IsLowSurrogate(PromptText.Tail(new string('a',39)+"😀")[0]));
    }
    static CodeWindow W(long h,string process,string title,string cls="Chrome_WidgetWin_1",bool min=false)=>new(h,process,cls,title,min);
    [Fact] public void CandidatesAreInsidersFirstWslFirstThenRecency()
    {
        var list=CandidateOrder.Pick([W(1,"Code - Insiders","a - x - Visual Studio Code - Insiders"),W(2,"Code","stable"),W(3,"Code - Insiders","b [WSL: Ubuntu] - Visual Studio Code - Insiders"),W(4,"Code - Insiders","c"),W(5,"Code - Insiders","",min:true),W(6,"Code - Insiders","tip","Other"),W(7,"msedge","edge")]);
        Assert.Equal([3L,1L,4L],list.Select(w=>w.Handle));
    }
    [Fact] public void StableCodeOnlyWhenAllowedAndNoInsiders()
    {
        Assert.Empty(CandidateOrder.Pick([W(2,"Code","stable"),W(7,"msedge","edge")]));   // default: Insiders only
        Assert.Equal([2L],CandidateOrder.Pick([W(2,"Code","stable"),W(7,"msedge","edge")],allowStable:true).Select(w=>w.Handle));
        Assert.Equal([1L],CandidateOrder.Pick([W(2,"Code","stable"),W(1,"Code - Insiders","insiders")],allowStable:true).Select(w=>w.Handle));
        Assert.Empty(CandidateOrder.Pick([W(7,"msedge","edge")],allowStable:true));
    }
    [Fact] public void CloakedAndAuxiliaryWindowsNeverQualify()
    {
        var list=CandidateOrder.Pick([new(1,"Code - Insiders","Chrome_WidgetWin_1","other desktop",false,true),W(2,"Code - Insiders","Developer Tools - x"),W(3,"Code - Insiders","Issue Reporter"),W(4,"Code - Insiders","Process Explorer"),W(5,"Code - Insiders","proj - Visual Studio Code - Insiders"),W(6,"Code - Insiders","My Developer Tools notes")]);
        Assert.Equal([5L,6L],list.Select(w=>w.Handle));
    }
    [Fact] public void AMenuTriggerFirstOnAnyLineGetsASpace()
    {
        Assert.Equal("eins\n /clear\nzwei\n @datei x",PromptText.Normalize("eins\n/clear\nzwei\n@datei x"));
        Assert.Equal("Schau mal @datei und /clear",PromptText.Normalize("Schau mal @datei und /clear"));   // mid-line stays
        Assert.Equal(" /a\n\n @b",PromptText.Normalize("/a\n\n\n@b"));
        Assert.Equal(PromptText.Normalize(PromptText.Normalize("/a\n/b")),PromptText.Normalize("/a\n/b"));
    }
    [Fact] public void FocusRules()
    {
        string[] names=["Message input"];
        Assert.True(Core.FocusRules.MayType("Message input",names,["webview","chat"]));
        Assert.False(Core.FocusRules.MayType("Message input",names,["monaco-editor no-user-select","x"]));
        Assert.False(Core.FocusRules.MayType("Message input",names,["terminal-wrapper"]));
        Assert.False(Core.FocusRules.MayType("Message input",names,["xterm-helpers"]));
        Assert.False(Core.FocusRules.MayType("Editor content",names,[]));
        Assert.False(Core.FocusRules.MayType(null,names,[]));
        Assert.False(Core.FocusRules.MayType("message input",names,[]));
        Assert.True(Core.FocusRules.InQuickInput(["x","quick-input-widget show-file-icons"]));
        Assert.False(Core.FocusRules.InQuickInput(["monaco-editor"]));
    }
    [Fact] public void PaletteLabelIsProvenByTheHighlightedEntryAndStaysOneBatch()
    {
        Assert.True(PaletteRules.IsFocusInput("Claude Code: Focus input"));
        Assert.True(PaletteRules.IsFocusInput("claude code: focus INPUT, recently used"));
        Assert.False(PaletteRules.IsFocusInput("Claude Code: Focus Input Elsewhere".Replace("Focus Input","Open")));
        Assert.False(PaletteRules.IsFocusInput("Claude Code: Open in New Tab"));
        Assert.False(PaletteRules.IsFocusInput("")); Assert.False(PaletteRules.IsFocusInput(null));
        // typed as one SendInput batch; a longer label would silently become two batches
        Assert.Single(PromptText.Steps(PaletteRules.Label));
    }
    [Fact] public void DeadlineCountsFromReceiptAndKeepsEightSecondsForTyping()
    {
        var d=new PromptDeadline(1000);
        Assert.Equal(30000,d.Left(1000));Assert.True(d.MayStartTyping(1000+22000));Assert.False(d.MayStartTyping(1000+22001));
        Assert.False(d.Expired(1000+29999));Assert.True(d.Expired(1000+30000));
    }
    [Fact] public async Task InputGatePromptAndInjectionExcludeEachOther()
    {
        var g=new InputGate();int ended=0;g.PromptEnded+=()=>ended++;
        Assert.True(await g.BeginPrompt(TimeSpan.Zero));Assert.True(g.PromptActive);
        Assert.Null(await g.BeginInject(TimeSpan.FromMilliseconds(50)));   // the injection waits, then gives up
        var waiting=g.BeginInject(TimeSpan.FromSeconds(5));Assert.False(waiting.IsCompleted);
        g.EndPrompt();Assert.False(g.PromptActive);Assert.Equal(1,ended);
        using(var slot=await waiting){Assert.NotNull(slot);
            Assert.False(await g.BeginPrompt(TimeSpan.FromMilliseconds(50)));}   // a prompt during an injection: busy
        Assert.True(await g.BeginPrompt(TimeSpan.Zero));g.EndPrompt();
    }
    [Fact] public async Task InputGateListenerThatThrowsDoesNotKeepItShut()
    {
        var g=new InputGate();g.PromptEnded+=()=>throw new InvalidOperationException();
        Assert.True(await g.BeginPrompt(TimeSpan.Zero));g.EndPrompt();
        Assert.True(await g.BeginPrompt(TimeSpan.Zero));
    }
    [Fact] public void HeadingNamesTheCharactersAlreadyInTheBoxAfterAnInterruption()
    {
        Assert.Equal("Nicht eingetippt: die Eingabe wurde unterbrochen",PromptCodes.Heading(PromptCodes.Interrupted));
        Assert.Equal("Nicht eingetippt: die Eingabe wurde unterbrochen · 48 Zeichen stehen schon im Feld",PromptCodes.Heading(PromptCodes.Interrupted,48));
        Assert.Equal("Nicht eingetippt: Claude-Eingabefeld nicht gefunden",PromptCodes.Heading(PromptCodes.NoField,48));
    }
    [Fact] public void CodesAreOkOnlyWhenTyped()
    {
        Assert.True(PromptCodes.Ok("typed"));Assert.True(PromptCodes.Ok("typed-unverified"));
        foreach(var c in new[]{"no-window","no-foreground","no-field","wrong-focus","interrupted","busy","no-session"}){Assert.False(PromptCodes.Ok(c));Assert.NotEqual("unbekannter Grund",PromptCodes.Reason(c));}
    }
    // Canvas chord: Ctrl+Win+Shift+D, session only (like mute), never toggles the session.
    sealed class Run(Hotkeys h)
    {
        public readonly List<ActionKind> Actions=[];long t;
        public void Down(params Key[] keys){foreach(var k in keys)Actions.AddRange(h.Feed(new(k,true,t+=10)));}
        public void Up(params Key[] keys){foreach(var k in keys)Actions.AddRange(h.Feed(new(k,false,t+=10)));}
        public int Count(ActionKind a)=>Actions.Count(x=>x==a);
    }
    [Fact] public void CanvasChordNeedsASessionAndNeverToggleIt()
    {
        var h=new Hotkeys();var r=new Run(h);
        r.Down(Key.Ctrl,Key.Win,Key.Shift,Key.Canvas);Assert.Equal(0,r.Count(ActionKind.CanvasToggle));r.Up(Key.Canvas,Key.Shift,Key.Win,Key.Ctrl);
        Assert.False(h.Session);
        h.SetSession(true);
        r.Down(Key.Ctrl,Key.Win,Key.Shift,Key.Canvas);Assert.Equal(1,r.Count(ActionKind.CanvasToggle));
        r.Up(Key.Canvas);r.Down(Key.Canvas);Assert.Equal(2,r.Count(ActionKind.CanvasToggle));
        r.Up(Key.Canvas,Key.Shift,Key.Win,Key.Ctrl);Assert.Equal(0,r.Count(ActionKind.SessionToggle));
    }
    [Fact] public void CanvasBindingMapsDAndMustDifferFromOtherActionKeys()
    {
        var b=new HotkeyBindings(new AppConfig().Hotkeys);
        Assert.Equal(Key.Canvas,b.Role('D'));Assert.Equal('D',b.CanvasVk);
        var keys=new AppConfig().Hotkeys;keys["canvas"]="Ctrl+Win+Shift+M";
        Assert.Throws<InvalidDataException>(()=>new HotkeyBindings(keys));
        keys["canvas"]="Ctrl+Win";Assert.Throws<InvalidDataException>(()=>new HotkeyBindings(keys));
    }
    [Fact] public void OldConfigWithoutCanvasKeyGetsTheDefault()
    {
        var c=AppConfig.Parse("""{"Hotkeys":{"session":"Ctrl+Win+Shift","dictation":"Alt+Win","dictationToggle":"Alt+Win+Shift","mute":"Ctrl+Win+Shift+M","pause":"Ctrl+Win+Shift+P","handsFree":"Alt+Win+Y","draw":"Caps"}}""");
        Assert.Equal("Ctrl+Win+Shift+D",c.Hotkeys["canvas"]);Assert.False(c.HotkeysMigrated);
        Assert.Equal("auto",c.CanvasPanel);Assert.Equal(["Message input"],c.MainAgentInputNames);
    }
    [Fact] public void ConfigValidatesCanvasPanelAndInputNames()
    {
        Assert.Equal("manual",AppConfig.Parse("""{"CanvasPanel":"manual"}""").CanvasPanel);
        Assert.Throws<InvalidDataException>(()=>AppConfig.Parse("""{"CanvasPanel":"always"}"""));
        Assert.Throws<InvalidDataException>(()=>AppConfig.Parse("""{"MainAgentInputNames":[]}"""));
        Assert.Equal(["A","B"],AppConfig.Parse("""{"MainAgentInputNames":["A","B"]}""").MainAgentInputNames);
    }
    [Fact] public void ACollidingDefaultCanvasChordNeverStopsTheApp()
    {
        string Cfg(string extra)=>"{\"Hotkeys\":{\"session\":\"Ctrl+Win+Shift\",\"dictation\":\"Alt+Win\",\"dictationToggle\":\"Alt+Win+Shift\",\"mute\":\"Ctrl+Win+Shift+D\",\"pause\":\"Ctrl+Win+Shift+P\",\"handsFree\":\"Alt+Win+Y\",\"draw\":\"Caps\""+extra+"}}";
        var c=AppConfig.Parse(Cfg(""));   // D is the mute key here
        Assert.Equal("Ctrl+Win+Shift+Z",c.Hotkeys["canvas"]);Assert.Contains("Z",c.HotkeyNote);
        var b=new HotkeyBindings(c.Hotkeys);Assert.Equal('Z',b.CanvasVk);
        // D, Z, G and J taken by the other four action keys: K is the first free letter. (Four action keys can block four of the five letters at most; the empty chord is the fallback of the fallback.)
        var k=AppConfig.Parse("{\"Hotkeys\":{\"session\":\"Ctrl+Win+Shift\",\"dictation\":\"Alt+Win\",\"dictationToggle\":\"Alt+Win+Shift\",\"mute\":\"Ctrl+Win+Shift+D\",\"pause\":\"Ctrl+Win+Shift+Z\",\"handsFree\":\"Alt+Win+G\",\"draw\":\"J\"}}");
        Assert.Equal("Ctrl+Win+Shift+K",k.Hotkeys["canvas"]);
        Assert.Null(AppConfig.Parse("{}").HotkeyNote);
    }
    [Fact] public void ACanvasChordThatIsExplicitlyInvalidStillFails()
    {
        var keys=new AppConfig().Hotkeys;keys["canvas"]="Ctrl+Win+Shift+M";
        Assert.Throws<InvalidDataException>(()=>new HotkeyBindings(keys));
    }
    [Fact] public void NoCanvasChordMatchesNothingAndTheRestWorks()
    {
        var keys=new AppConfig().Hotkeys;keys["canvas"]=AppConfig.NoChord;
        var b=new HotkeyBindings(keys);Assert.Equal(-1,b.CanvasVk);Assert.Equal(Key.Other,b.Role('D'));Assert.False(b.CanvasChordHeld);
        var h=new Hotkeys(b.Layout);h.SetSession(true);var r=new Run(h);
        r.Down(Key.Ctrl,Key.Win,Key.Shift,Key.Canvas);Assert.Equal(0,r.Count(ActionKind.CanvasToggle));
    }
    [Fact] public void MainAgentConfigNullNamesFallBackAndStableCodeIsOptIn()
    {
        Assert.Equal(["Message input"],AppConfig.Parse("""{"MainAgentInputNames":null}""").MainAgentInputNames);
        Assert.False(AppConfig.Parse("{}").MainAgentAllowStableCode);
        Assert.True(AppConfig.Parse("""{"MainAgentAllowStableCode":true}""").MainAgentAllowStableCode);
    }
    // Drawing panel placement.
    static readonly PxRect M1=new(0,0,1920,1040),M2=new(1920,0,1920,1040);
    [Theory][InlineData(1.0)][InlineData(1.5)][InlineData(2.0)]
    public void PanelBottomRightAboveTheNoteOnAMonitorWithoutTheForegroundWindow(double scale)
    {
        var box=WindowPlacement.CanvasPanel([M1,M2],[scale,scale],0,0,null);
        Assert.True(M2.Contains(box));Assert.Equal(M2.Right-(int)Math.Round(12*scale),box.Right);
        Assert.True(M2.Bottom-box.Bottom>=(int)Math.Round(WindowPlacement.PanelNoteReserveDip*scale));
        Assert.Equal((int)Math.Round(440*scale),box.W);Assert.Equal((int)Math.Round(340*scale),box.H);
    }
    [Fact] public void PanelUsesTheCurrentMonitorWhenThereIsOnlyOne()
    {
        var box=WindowPlacement.CanvasPanel([M1],[1.0],0,0,null);Assert.True(M1.Contains(box));Assert.True(box.X>M1.W/2&&box.Y>M1.H/2);
        Assert.True(M1.Contains(WindowPlacement.CanvasPanel([M1],[1.0],-1,0,null)));
    }
    [Fact] public void PanelWithoutForegroundWindowUsesTheCurrentMonitorEvenWithTwo()
        =>Assert.True(M2.Contains(WindowPlacement.CanvasPanel([M1,M2],[1.0,1.0],-1,1,null)));
    [Fact] public void PanelKeepsASavedBoxThatIsStillReachableOtherwiseForgetsIt()
    {
        var saved=new PxRect(300,200,500,380);
        Assert.Equal(saved,WindowPlacement.CanvasPanel([M1,M2],[1.0,1.0],0,0,saved));
        var gone=new PxRect(9000,200,500,380);   // monitor unplugged
        Assert.True(M2.Contains(WindowPlacement.CanvasPanel([M1,M2],[1.0,1.0],0,0,gone)));
        var hanging=new PxRect(1800,1000,500,380);   // bottom edge off: pulled back
        Assert.True(M1.Contains(WindowPlacement.CanvasPanel([M1],[1.0],0,0,hanging))||new PxRect(0,0,M1.W,M1.H).Intersect(WindowPlacement.CanvasPanel([M1],[1.0],0,0,hanging)).W>0);
    }
    [Fact] public void PanelFitsATinyWorkArea()
    {
        var tiny=new PxRect(0,0,400,300);var box=WindowPlacement.CanvasPanel([tiny],[1.0],-1,0,null);
        Assert.True(tiny.Contains(box));Assert.True(box.W>0&&box.H>0);
    }
}
