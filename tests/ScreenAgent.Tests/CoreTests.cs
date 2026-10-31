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
        Event(h,Key.Ctrl,false,30); Event(h,Key.Win,false,40); Assert.Contains(ActionKind.SessionToggle,Event(h,Key.Shift,false,50)); return h;
    }
    [Theory][InlineData(199,false)][InlineData(200,true)][InlineData(250,true)]
    public void CapsThreshold(long duration,bool draw)
    {
        var h=Session(); Event(h,Key.Caps,true,1000); var a=Event(h,Key.Caps,false,1000+duration);
        Assert.Equal(draw,a.Contains(ActionKind.DrawStart)); Assert.Equal(!draw,a.Contains(ActionKind.CapsTap)); Assert.Equal(draw,a.Contains(ActionKind.DrawStop));
    }
    [Fact] public void OutsideSessionCapsPasses(){var h=new Hotkeys(); Event(h,Key.Caps,true,0);Assert.False(h.SwallowCaps);Assert.Empty(Event(h,Key.Caps,false,300));}
    [Fact] public void DictationDelayDiscard(){var h=new Hotkeys();Event(h,Key.Ctrl,true,0);Assert.Contains(ActionKind.MaskWin,Event(h,Key.Win,true,10));Assert.Empty(h.Tick(259));Assert.Contains(ActionKind.DictationStart,h.Tick(260));Assert.Contains(ActionKind.DictationDiscard,Event(h,Key.Shift,true,270));}
    [Fact] public void DictationRelease(){var h=new Hotkeys();Event(h,Key.Win,true,0);Event(h,Key.Ctrl,true,10);Assert.Contains(ActionKind.DictationStart,Event(h,Key.Ctrl,false,260));}
    [Fact] public void DirtyChordCannotToggle(){var h=new Hotkeys();Event(h,Key.Ctrl,true,0);Event(h,Key.Win,true,10);Event(h,Key.Shift,true,20);Event(h,Key.Other,true,25);Event(h,Key.Ctrl,false,30);Event(h,Key.Win,false,40);Assert.DoesNotContain(ActionKind.SessionToggle,Event(h,Key.Shift,false,50));}
    [Fact] public void PauseDoesNotToggleSession(){var h=Session();Event(h,Key.Ctrl,true,100);Event(h,Key.Win,true,110);Event(h,Key.Shift,true,120);Assert.Contains(ActionKind.PauseToggle,Event(h,Key.P,true,130));Event(h,Key.P,false,140);Event(h,Key.Ctrl,false,150);Event(h,Key.Win,false,160);Assert.DoesNotContain(ActionKind.SessionToggle,Event(h,Key.Shift,false,170));Assert.True(h.Session);Assert.True(h.Paused);}
    [Theory][InlineData("keepassxc","safe",null,true)][InlineData("notepad","my password",null,true)][InlineData("chrome","safe","https://accounts.google.com/a",true)][InlineData("chrome","safe",null,true)][InlineData("chrome","safe","https://accounts.google.com.evil.test",false)][InlineData("chrome","safe","https://sub.kraken.com",true)][InlineData("notepad","notes",null,false)][InlineData("chrome","Earnings","https://earnings.app.mintapis.com/password",true)]
    public void Blocklist(string process,string title,string? url,bool blocked)=>Assert.Equal(blocked,new PrivacyPolicy().Blocked(process,title,url));

    [Fact] public void ShiftBeforeDelayPreventsDictation()
    {
        var h=new Hotkeys();Event(h,Key.Ctrl,true,0);Event(h,Key.Win,true,10);Event(h,Key.Shift,true,259);
        Assert.DoesNotContain(ActionKind.DictationStart,h.Tick(1000));
    }
    [Fact] public void QuickDictationChordDoesNothing()
    {
        var h=new Hotkeys();Event(h,Key.Ctrl,true,0);Event(h,Key.Win,true,10);
        Assert.DoesNotContain(ActionKind.DictationStart,Event(h,Key.Win,false,259));
    }
    [Fact] public void CapsAutoRepeatIsOneTap()
    {
        var h=Session();Event(h,Key.Caps,true,1000);Event(h,Key.Caps,true,1100);
        Assert.Contains(ActionKind.CapsTap,Event(h,Key.Caps,false,1199));
    }
    [Fact] public void CapsMakesSessionChordDirty()
    {
        var h=new Hotkeys();Event(h,Key.Ctrl,true,0);Event(h,Key.Win,true,10);Event(h,Key.Shift,true,20);
        Event(h,Key.Caps,true,25);Event(h,Key.Ctrl,false,30);Event(h,Key.Win,false,40);
        Assert.DoesNotContain(ActionKind.SessionToggle,Event(h,Key.Shift,false,50));
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
    [Fact] public void PrivacyAdditionsAreAdditive()
    {
        var p=new PrivacyPolicy{ExtraProcesses=["private-"],ExtraDomains=["private.test"],ExtraTitlePatterns=["confidential"]};
        Assert.True(p.Blocked("private-app","notes"));Assert.True(p.Blocked("chrome","notes","https://sub.private.test"));
        Assert.True(p.Blocked("notepad","Confidential report"));Assert.True(p.Blocked("keepassxc","notes"));
    }
}
