using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
// v0.1.4: Ctrl+Win+Shift session chord in any order, Alt+Win+Shift dictation toggle, Ctrl+Win+Shift+M mute, Ctrl+Win untouched.
public class HotkeyChordTests
{
    static readonly Key[] Ctrl3=[Key.Ctrl,Key.Win,Key.Shift];
    static IEnumerable<Key[]> Perms(Key[] keys)=>keys.SelectMany(a=>keys.Where(b=>b!=a).SelectMany(b=>keys.Where(c=>c!=a&&c!=b).Select(c=>new[]{a,b,c})));
    public static IEnumerable<object[]> Orders()=>Perms(Ctrl3).Select(o=>new object[]{o});
    public static IEnumerable<object[]> AltOrders()=>Perms([Key.Alt,Key.Win,Key.Shift]).Select(o=>new object[]{o});
    sealed class Run(Hotkeys h)
    {
        public readonly List<ActionKind> Actions=[];long t;
        public void Down(params Key[] keys){foreach(var k in keys)Actions.AddRange(h.Feed(new(k,true,t+=10)));}
        public void Up(params Key[] keys){foreach(var k in keys)Actions.AddRange(h.Feed(new(k,false,t+=10)));}
        public void Wait(long ms)=>Actions.AddRange(h.Tick(t+=ms));
        public int Count(ActionKind a)=>Actions.Count(x=>x==a);
    }
    [Theory,MemberData(nameof(Orders))] public void EveryPressOrderStartsOnceAndSecondPressEnds(Key[] order)
    {
        var h=new Hotkeys();var r=new Run(h);
        r.Down(order);r.Up(order);Assert.Equal(1,r.Count(ActionKind.SessionToggle));Assert.True(h.Session);
        r.Down(order.Reverse().ToArray());r.Up(order);Assert.Equal(2,r.Count(ActionKind.SessionToggle));Assert.False(h.Session);
    }
    [Theory,MemberData(nameof(Orders))] public void FiresOnTheFirstReleaseNotAfterAllKeysAreUp(Key[] order)
    {
        var h=new Hotkeys();var r=new Run(h);
        r.Down(order);Assert.Equal(0,r.Count(ActionKind.SessionToggle));r.Up(order[0]);Assert.Equal(1,r.Count(ActionKind.SessionToggle));
        r.Up(order[1],order[2]);Assert.Equal(1,r.Count(ActionKind.SessionToggle));
    }
    [Fact] public void AutoRepeatDoesNotRepeatTheToggle()
    {
        var h=new Hotkeys();var r=new Run(h);
        r.Down(Key.Ctrl,Key.Win,Key.Shift);for(int i=0;i<20;i++)r.Down(Key.Shift,Key.Win);r.Up(Key.Shift,Key.Win,Key.Ctrl);
        Assert.Equal(1,r.Count(ActionKind.SessionToggle));
    }
    [Fact] public void CtrlWinAloneDoesNothingEvenWhenHeldLong()
    {
        var h=new Hotkeys();var r=new Run(h);
        r.Down(Key.Ctrl,Key.Win);r.Wait(5000);r.Up(Key.Ctrl,Key.Win);
        Assert.Empty(r.Actions);Assert.False(h.Session);
    }
    [Theory,InlineData(Key.Ctrl),InlineData(Key.Win)] public void PartialReleaseBeforeThirdKeyNeverToggles(Key released)
    {
        var h=new Hotkeys();var r=new Run(h);
        r.Down(Key.Ctrl,Key.Win);r.Up(released);r.Down(Key.Shift);r.Up(Key.Shift,Key.Ctrl,Key.Win);
        Assert.False(h.Session);Assert.Equal(0,r.Count(ActionKind.SessionToggle));
    }
    [Fact] public void ReleasingAndPressingShiftAgainDoesNotMakeASecondToggle()
    {
        var h=new Hotkeys();var r=new Run(h);
        r.Down(Key.Ctrl,Key.Win,Key.Shift);r.Up(Key.Shift);r.Down(Key.Shift);r.Up(Key.Shift,Key.Win,Key.Ctrl);
        Assert.Equal(1,r.Count(ActionKind.SessionToggle));
    }
    [Fact] public void ExtraModifierMakesItNotOurChord()
    {
        var h=new Hotkeys();var r=new Run(h);
        r.Down(Key.Ctrl,Key.Win,Key.Alt,Key.Shift);r.Up(Key.Alt,Key.Shift,Key.Win,Key.Ctrl);
        Assert.False(h.Session);Assert.Empty(r.Actions);
    }
    [Theory,MemberData(nameof(AltOrders))] public void AltWinShiftTogglesDictationOnceInAnyOrder(Key[] order)
    {
        var h=new Hotkeys();var r=new Run(h);
        r.Down(order);r.Up(order);
        Assert.Equal(1,r.Count(ActionKind.DictationStart));Assert.Equal(1,r.Count(ActionKind.DictationBind));Assert.True(h.Dictating);Assert.False(h.Session);
        r.Wait(2000);Assert.True(h.Dictating);   // survives release: it is a toggle, not a hold
        r.Down(order);r.Up(order);Assert.Equal(1,r.Count(ActionKind.DictationStop));Assert.False(h.Dictating);
        Assert.Equal(0,r.Count(ActionKind.SessionToggle));
    }
    [Fact] public void ShiftAddedToHeldDictationLatchesIt()
    {
        var h=new Hotkeys();var r=new Run(h);
        r.Down(Key.Alt,Key.Win);r.Wait(300);Assert.True(h.Dictating);
        r.Down(Key.Shift);r.Up(Key.Shift,Key.Win,Key.Alt);
        Assert.True(h.Dictating);Assert.Equal(0,r.Count(ActionKind.DictationStop)+r.Count(ActionKind.DictationDiscard));
        r.Down(Key.Alt,Key.Win,Key.Shift);r.Up(Key.Shift,Key.Win,Key.Alt);
        Assert.Equal(1,r.Count(ActionKind.DictationStop));Assert.False(h.Dictating);
    }
    [Fact] public void AltWinHoldIsUnchanged()
    {
        var h=new Hotkeys();var r=new Run(h);
        r.Down(Key.Alt,Key.Win);Assert.Equal(1,r.Count(ActionKind.DictationBind));Assert.Equal(0,r.Count(ActionKind.DictationStart));
        r.Wait(250);Assert.Equal(1,r.Count(ActionKind.DictationStart));r.Up(Key.Win);Assert.Equal(1,r.Count(ActionKind.DictationStop));r.Up(Key.Alt);
        Assert.Equal(1,r.Count(ActionKind.DictationStop));Assert.False(h.Session);
    }
    [Fact] public void MuteChordNeedsASessionAndNeverToggles()
    {
        var h=new Hotkeys();var r=new Run(h);
        r.Down(Key.Ctrl,Key.Win,Key.Shift,Key.Mute);Assert.Equal(0,r.Count(ActionKind.MuteToggle));r.Up(Key.Mute,Key.Shift,Key.Win,Key.Ctrl);
        Assert.False(h.Session);Assert.Equal(0,r.Count(ActionKind.SessionToggle));
        h.SetSession(true);
        r.Down(Key.Ctrl,Key.Win,Key.Shift,Key.Mute);Assert.Equal(1,r.Count(ActionKind.MuteToggle));
        r.Up(Key.Mute);r.Down(Key.Mute);Assert.Equal(2,r.Count(ActionKind.MuteToggle));   // pressed again while the chord is held
        r.Up(Key.Mute,Key.Shift,Key.Win,Key.Ctrl);
        Assert.True(h.Session);Assert.Equal(0,r.Count(ActionKind.SessionToggle));   // SetSession's own action was not fed through Run
    }
    [Fact] public void MuteIgnoredWhilePausedAndAutoRepeatCountsOnce()
    {
        var h=new Hotkeys();h.SetSession(true);var r=new Run(h);
        r.Down(Key.Ctrl,Key.Win,Key.Shift);r.Down(Key.Mute);for(int i=0;i<10;i++)r.Down(Key.Mute);Assert.Equal(1,r.Count(ActionKind.MuteToggle));
        r.Down(Key.P);Assert.True(h.Paused);r.Up(Key.P,Key.Mute);r.Down(Key.Mute);Assert.Equal(1,r.Count(ActionKind.MuteToggle));
    }
    [Fact] public void CtrlShiftMAloneIsNotOurs()
    {
        var h=new Hotkeys();h.SetSession(true);var r=new Run(h);
        r.Down(Key.Ctrl,Key.Shift,Key.Mute);r.Up(Key.Mute,Key.Shift,Key.Ctrl);
        Assert.Empty(r.Actions);
    }
    [Fact] public void TypingBeforeTheChordDoesNotBlockIt()
    {
        var h=new Hotkeys();var r=new Run(h);
        r.Down(Key.Other);r.Up(Key.Other);r.Down(Key.Ctrl,Key.Win,Key.Shift);r.Up(Key.Shift,Key.Win,Key.Ctrl);
        Assert.True(h.Session);
    }
    [Fact] public void SecondTypedKeyWhileFirstIsHeldStillTaints()
    {
        var h=new Hotkeys();var r=new Run(h);
        r.Down(Key.Other);r.Down(Key.Ctrl,Key.Win,Key.Shift);r.Down(Key.Other);r.Up(Key.Shift,Key.Win,Key.Ctrl);
        Assert.False(h.Session);
    }
    [Fact] public void SessionEndsDrawingAndStaysOffWhenPausedDictation()
    {
        var h=new Hotkeys();h.SetSession(true);h.SetPaused(true);var r=new Run(h);
        r.Down(Key.Alt,Key.Win,Key.Shift);r.Up(Key.Shift,Key.Win,Key.Alt);
        Assert.False(h.Dictating);Assert.Equal(0,r.Count(ActionKind.DictationStart));
    }
    [Fact] public void LayoutIsConfigurable()
    {
        var keys=AppConfig.DefaultHotkeys();keys["session"]="Alt+Win+Shift";keys["dictationToggle"]="Ctrl+Win+Shift";
        var b=new HotkeyBindings(keys);var h=new Hotkeys(b.Layout);var r=new Run(h);
        r.Down(Key.Alt,Key.Win,Key.Shift);r.Up(Key.Shift,Key.Win,Key.Alt);Assert.True(h.Session);
        r.Down(Key.Ctrl,Key.Win,Key.Shift);r.Up(Key.Shift,Key.Win,Key.Ctrl);Assert.True(h.Dictating);
    }
}
