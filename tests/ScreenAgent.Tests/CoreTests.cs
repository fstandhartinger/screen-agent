using ScreenAgent.Core;
using System.Text.Json;
using Xunit;
namespace ScreenAgent.Tests;
public class CoreTests
{
    public static IEnumerable<object[]> Vectors() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"ocr-ids.json"))).RootElement.EnumerateArray().Select(v=>new object[]{v.GetProperty("text").GetString()!,v.GetProperty("ids").EnumerateArray().Select(x=>x.GetString()!).ToArray()});
    [Theory,MemberData(nameof(Vectors))] public void SharedVectors(string text,string[] ids)=>Assert.Equal(ids,ArtifactMatcher.Find(text));
    static IReadOnlyList<ActionKind> Event(Hotkeys h,Key k,bool d,long t)=>h.Feed(new(k,d,t));
    static Hotkeys Session()
    {
        var h=new Hotkeys(); Event(h,Key.Ctrl,true,0); Event(h,Key.Win,true,10); Event(h,Key.Shift,true,20);
        Assert.Contains(ActionKind.SessionToggle,Event(h,Key.Ctrl,false,30)); Event(h,Key.Win,false,40); Event(h,Key.Shift,false,50); return h;
    }
    [Theory][InlineData(199,false)][InlineData(200,true)][InlineData(250,true)]
    public void CapsThreshold(long duration,bool draw)
    {
        var h=Session(); Event(h,Key.Caps,true,1000); var a=Event(h,Key.Caps,false,1000+duration);
        Assert.Equal(draw,a.Contains(ActionKind.DrawStart)); Assert.Equal(!draw,a.Contains(ActionKind.CapsTap)); Assert.Equal(draw,a.Contains(ActionKind.DrawStop));
    }
    [Fact] public void OutsideSessionCapsPasses(){var h=new Hotkeys(); Event(h,Key.Caps,true,0);Assert.False(h.SwallowCaps);Assert.Empty(Event(h,Key.Caps,false,300));}
    [Fact] public void DictationDelayDiscard(){var h=new Hotkeys();Event(h,Key.Alt,true,0);Assert.Contains(ActionKind.DictationBind,Event(h,Key.Win,true,10));Assert.Empty(h.Tick(259));Assert.Contains(ActionKind.DictationStart,h.Tick(260));Assert.Contains(ActionKind.DictationDiscard,Event(h,Key.Ctrl,true,270));}
    [Fact] public void DictationRelease(){var h=new Hotkeys();Event(h,Key.Win,true,0);Event(h,Key.Alt,true,10);Assert.Contains(ActionKind.DictationStart,Event(h,Key.Alt,false,260));Assert.False(h.Dictating);}
    [Fact] public void DictationHoldStopsOnRelease(){var h=new Hotkeys();Event(h,Key.Alt,true,0);Event(h,Key.Win,true,10);h.Tick(300);Assert.True(h.Dictating);Assert.Contains(ActionKind.DictationStop,Event(h,Key.Win,false,900));Assert.False(h.Dictating);}
    [Fact] public void DirtyChordCannotToggle(){var h=new Hotkeys();var all=new List<ActionKind>();foreach(var (k,d,t) in new[]{(Key.Ctrl,true,0),(Key.Win,true,10),(Key.Shift,true,20),(Key.Other,true,25),(Key.Ctrl,false,30),(Key.Win,false,40),(Key.Shift,false,50)})all.AddRange(Event(h,k,d,t));Assert.DoesNotContain(ActionKind.SessionToggle,all);}
    [Fact] public void PauseDoesNotToggleSession(){var h=Session();Event(h,Key.Ctrl,true,100);Event(h,Key.Win,true,110);Event(h,Key.Shift,true,120);Assert.Contains(ActionKind.PauseToggle,Event(h,Key.P,true,130));Event(h,Key.P,false,140);var rest=new List<ActionKind>();rest.AddRange(Event(h,Key.Ctrl,false,150));rest.AddRange(Event(h,Key.Win,false,160));rest.AddRange(Event(h,Key.Shift,false,170));Assert.DoesNotContain(ActionKind.SessionToggle,rest);Assert.True(h.Session);Assert.True(h.Paused);}
    [Theory][InlineData("keepassxc","safe",null)][InlineData("notepad","my password",null)][InlineData("chrome","safe","https://accounts.google.com/a")][InlineData("chrome","safe",null)][InlineData("chrome","Kraken","https://sub.kraken.com")]
    [InlineData("chrome","Earnings","https://bittensor-earnings.lovable.app/reax")][InlineData("","","")][InlineData("explorer","Start","")]
    public void NothingIsBlockedByDefault(string process,string title,string? url)
    {
        var p=new PrivacyPolicy();Assert.False(p.HasLists);Assert.False(p.NeedsAddress("chrome"));Assert.False(p.Blocked(process,title,url));
    }
    [Theory][InlineData("LockApp",true)][InlineData("LogonUI",true)][InlineData("consent",true)][InlineData("ShellExperienceHost",false)][InlineData("StartMenuExperienceHost",false)][InlineData("chrome",false)]
    public void OnlyTheSecureDesktopHostsAreLocked(string process,bool locked)=>Assert.Equal(locked,PrivacyPolicy.LockedHost(process));
    [Fact] public void StartMenuAndAltTabAreOrdinaryWindows()
    {
        var p=new PrivacyPolicy{ExtraProcesses=["bank"]};
        Assert.False(p.Blocked("ShellExperienceHost","Start"));Assert.False(p.Blocked("explorer","Task Switching"));Assert.False(p.Blocked("explorer","Task View"));
    }
    [Fact] public void AnUnreadableAddressNeverBlocks()
    {
        var p=new PrivacyPolicy{ExtraDomains=["bank.test"]};Assert.True(p.NeedsAddress("chrome"));Assert.False(p.NeedsAddress("notepad"));
        Assert.False(p.Blocked("chrome","tab",null));Assert.False(p.Blocked("chrome","tab","not a url"));Assert.True(p.Blocked("chrome","tab","https://www.bank.test/x"));
        Assert.False(p.Blocked("chrome","tab","https://bank.test.evil.example"));
    }
    [Fact] public void ShiftBeforeDelayPreventsDictation()
    {
        var h=new Hotkeys();Event(h,Key.Alt,true,0);Event(h,Key.Win,true,10);Event(h,Key.Shift,true,259);
        Assert.DoesNotContain(ActionKind.DictationStart,h.Tick(1000));
    }
    [Fact] public void QuickDictationChordDoesNothing()
    {
        var h=new Hotkeys();Event(h,Key.Alt,true,0);Event(h,Key.Win,true,10);
        Assert.DoesNotContain(ActionKind.DictationStart,Event(h,Key.Win,false,259));
    }
    [Fact] public void CapsAutoRepeatIsOneTap()
    {
        var h=Session();Event(h,Key.Caps,true,1000);Event(h,Key.Caps,true,1100);
        Assert.Contains(ActionKind.CapsTap,Event(h,Key.Caps,false,1199));
    }
    [Fact] public void CapsMakesSessionChordDirty()
    {
        var h=new Hotkeys();var all=new List<ActionKind>();foreach(var (k,d,t) in new[]{(Key.Ctrl,true,0),(Key.Win,true,10),(Key.Shift,true,20),(Key.Caps,true,25),(Key.Ctrl,false,30),(Key.Win,false,40),(Key.Shift,false,50)})all.AddRange(Event(h,k,d,t));
        Assert.DoesNotContain(ActionKind.SessionToggle,all);
    }
    [Fact] public void ModifierOrderPermutationsToggleOnlyOnce()
    {
        Key[][] orders=[[Key.Ctrl,Key.Win,Key.Shift],[Key.Ctrl,Key.Shift,Key.Win],[Key.Win,Key.Ctrl,Key.Shift],[Key.Win,Key.Shift,Key.Ctrl],[Key.Shift,Key.Ctrl,Key.Win],[Key.Shift,Key.Win,Key.Ctrl]];
        foreach(var press in orders)foreach(var release in orders)
        {
            var h=new Hotkeys();long time=0;var actions=new List<ActionKind>();
            foreach(var k in press)actions.AddRange(Event(h,k,true,time+=10));
            foreach(var k in release)actions.AddRange(Event(h,k,false,time+=10));
            Assert.Single(actions.Where(a=>a==ActionKind.SessionToggle));Assert.True(h.Session);
        }
    }
    [Fact] public void PausedSessionDoesNotDraw()
    {
        var h=Session();Event(h,Key.Ctrl,true,100);Event(h,Key.Win,true,110);Event(h,Key.Shift,true,120);Event(h,Key.P,true,130);
        Event(h,Key.P,false,140);Event(h,Key.Ctrl,false,150);Event(h,Key.Win,false,160);Event(h,Key.Shift,false,170);
        Event(h,Key.Caps,true,1000);Assert.DoesNotContain(ActionKind.DrawStart,Event(h,Key.Caps,false,1300));
    }
    [Fact] public void ConfiguredListsBlockExactlyTheirMatches()
    {
        var p=new PrivacyPolicy{ExtraProcesses=["private-"],ExtraDomains=["private.test"],ExtraTitlePatterns=["confidential"]};
        Assert.True(p.HasLists);
        Assert.True(p.Blocked("private-app","notes"));Assert.True(p.Blocked("chrome","notes","https://sub.private.test"));
        Assert.True(p.Blocked("notepad","Confidential report"));
        Assert.False(p.Blocked("keepassxc","notes"));Assert.False(p.Blocked("chrome","Kraken","https://kraken.com"));
    }
    [Fact] public void ABrokenTitlePatternFallsBackToPlainText()
    {
        var p=new PrivacyPolicy{ExtraTitlePatterns=["(oops"]};
        Assert.True(p.Blocked("notepad","an (oops here"));Assert.False(p.Blocked("notepad","fine"));
    }
}
