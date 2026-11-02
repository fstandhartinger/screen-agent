using ScreenAgent.Core;
using System.Text.Json;
using Xunit;
namespace ScreenAgent.Tests;
// Regression tests for Florian's first-run findings of 2 Nov 2026 (v0.1.1 laptop build).
public class FirstRunTests
{
    const int Ctrl=0xa2,Alt=0xa4,Win=0x5b,Shift=0xa0,Esc=0x1b;
    // Runs raw key events through the same two layers as the hook: HotkeyBindings (roles, mask) then the Hotkeys state machine.
    sealed class Rig
    {
        public readonly HotkeyBindings Bindings;public readonly Hotkeys State=new();public readonly List<ActionKind> Actions=[];public int Masks;long t;
        public Rig(Dictionary<string,string>? keys=null)=>Bindings=new HotkeyBindings(keys??AppConfig.DefaultHotkeys());
        public void Key(int vk,bool down)
        {
            bool transition=Bindings.Feed(vk,down,out var key);
            if(Bindings.MaskNow)Masks++;
            if(transition)Actions.AddRange(State.Feed(new(key,down,t+=10)));
        }
        public void Down(params int[] vks){foreach(var vk in vks)Key(vk,true);}
        public void Up(params int[] vks){foreach(var vk in vks)Key(vk,false);}
    }

    // 4 + 5: Ctrl+Win+Shift did nothing; the real clash was that Ctrl+Win belongs to Wispr.
    [Fact]public void DefaultsDoNotUseCtrlWin()
    {
        var keys=AppConfig.DefaultHotkeys();
        Assert.All(keys.Where(k=>k.Key!="draw"),k=>Assert.False(k.Value.Contains("Ctrl"),k.Key+"="+k.Value));
        Assert.False(new HotkeyBindings(keys).CollidesWithWispr);
        Assert.True(AppConfig.Parse("{}").Hotkeys["session"]=="Alt+Win+Shift");
    }
    [Fact]public void CtrlWinIsAlwaysUntouched()
    {
        var rig=new Rig();
        rig.Down(Ctrl,Win);rig.Actions.AddRange(rig.State.Tick(5000));rig.Up(Win,Ctrl);
        Assert.Empty(rig.Actions);Assert.Equal(0,rig.Masks);Assert.False(rig.State.Session);Assert.False(rig.State.Dictating);
    }
    [Fact]public void OldSessionChordDoesNothingAndIsNotMasked()
    {
        var rig=new Rig();
        rig.Down(Ctrl,Win,Shift);rig.Up(Shift,Win,Ctrl);
        Assert.DoesNotContain(ActionKind.SessionToggle,rig.Actions);Assert.Equal(0,rig.Masks);Assert.False(rig.State.Session);
    }
    [Theory][InlineData(Ctrl,Win,Alt)][InlineData(Win,Ctrl,Alt)][InlineData(Alt,Ctrl,Win)]
    public void WisprCommandChordIsForeign(int a,int b,int c)
    {
        var rig=new Rig();
        rig.Down(a,b,c);rig.Actions.AddRange(rig.State.Tick(5000));rig.Up(c,b,a);
        Assert.Equal(0,rig.Masks);Assert.False(rig.State.Session);Assert.DoesNotContain(ActionKind.DictationStart,rig.Actions);
    }
    [Fact]public void ConfiguredChordTogglesSessionAndMasksOnce()
    {
        var rig=new Rig();
        rig.Down(Alt,Win,Shift);rig.Up(Shift,Win,Alt);
        Assert.Single(rig.Actions,a=>a==ActionKind.SessionToggle);Assert.True(rig.State.Session);Assert.Equal(1,rig.Masks);
        rig.Down(Win,Shift,Alt);rig.Up(Alt,Shift,Win);
        Assert.False(rig.State.Session);Assert.Equal(2,rig.Masks);
    }
    [Fact]public void ChordWithAnotherKeyDoesNotToggle()
    {
        var rig=new Rig();
        rig.Down(Alt,Win,Shift);rig.Key('R',true);rig.Key('R',false);rig.Up(Shift,Win,Alt);
        Assert.False(rig.State.Session);
    }
    [Fact]public void OnlyAChordBelongingToUsIsMasked()
    {
        var rig=new Rig();
        rig.Down(Alt);Assert.False(rig.Bindings.MaskNow);
        rig.Down(Win);Assert.True(rig.Bindings.MaskNow);
        rig.Down(Shift);Assert.False(rig.Bindings.MaskNow);
        rig.Key(Win,true);Assert.False(rig.Bindings.MaskNow);   // auto repeat
    }
    [Fact]public void ForeignModifierIsNotOurRole()
    {
        var b=new HotkeyBindings(AppConfig.DefaultHotkeys());
        Assert.Equal(Key.Foreign,b.Role(Ctrl));Assert.Equal(Key.Ctrl,b.Role(Alt));Assert.Equal(Key.Win,b.Role(Win));
        b.Feed(Ctrl,true,out _);Assert.True(b.ForeignModifierHeld);
    }
    [Fact]public void SettingsCanStillChooseCtrlWinButAreWarned()
    {
        var keys=AppConfig.LegacyHotkeys();var b=new HotkeyBindings(keys);
        Assert.True(b.CollidesWithWispr);
        keys["dictation"]="Alt+Win";keys["session"]="Alt+Win+Shift";keys["handsFree"]="Alt+Win+F8";keys["pause"]="Alt+Win+Shift+Q";
        Assert.False(new HotkeyBindings(keys).CollidesWithWispr);
    }
    [Fact]public void TrayCanStartAndStopSessionWithoutKeys()
    {
        var h=new Hotkeys();
        Assert.Contains(ActionKind.SessionToggle,h.SetSession(true));Assert.True(h.Session);
        Assert.Empty(h.SetSession(true));
        Assert.Contains(ActionKind.SessionToggle,h.SetSession(false));Assert.False(h.Session);
    }

    // Config written by v0.1.1 still holds the Ctrl+Win defaults: migrate them, keep real customisations.
    [Fact]public void UntouchedLegacyHotkeysAreMigrated()
    {
        string old=JsonSerializer.Serialize(new{DeskUrl="https://desk.app.mintapis.com",Hotkeys=AppConfig.LegacyHotkeys()});
        var c=AppConfig.Parse(old);
        Assert.True(c.HotkeysMigrated);Assert.Equal("Alt+Win+Shift",c.Hotkeys["session"]);Assert.Equal("Alt+Win",c.Hotkeys["dictation"]);
        Assert.Equal("https://desk.app.mintapis.com",c.DeskUrl);
        Assert.False(AppConfig.Parse(JsonSerializer.Serialize(c)).HotkeysMigrated);
        Assert.DoesNotContain("Migrated",JsonSerializer.Serialize(c));
    }
    [Fact]public void CustomisedHotkeysAreKept()
    {
        var keys=AppConfig.LegacyHotkeys();keys["draw"]="F9";
        var c=AppConfig.Parse(JsonSerializer.Serialize(new{Hotkeys=keys}));
        Assert.False(c.HotkeysMigrated);Assert.Equal("Ctrl+Win+Shift",c.Hotkeys["session"]);Assert.Equal("F9",c.Hotkeys["draw"]);
    }
    [Fact]public void IndicatorOptionIsValidated()
    {
        Assert.Equal("session",AppConfig.Parse("{}").Indicator);Assert.Equal("notices",AppConfig.Parse("{\"indicator\":\"notices\"}").Indicator);
        Assert.Throws<InvalidDataException>(()=>AppConfig.Parse("{\"indicator\":\"always\"}"));
    }

    // 2: "Stimme nicht bestätigt" appeared although no voice had ever been requested.
    [Fact]public void NoVoiceRequestMeansNoFailure()
    {
        var v=new VoiceSync();long now=0;
        for(int i=0;i<100;i++){Assert.Equal(VoiceStep.None,v.Next(false,false,now+=100,false));Assert.False(v.Failed);}
    }
    [Fact]public void UnansweredStartFailsVisiblyThenRetries()
    {
        var v=new VoiceSync();
        Assert.Equal(VoiceStep.Start,v.Next(true,false,0,false));v.Result(true,false,0);
        Assert.True(v.Failed);Assert.Equal(VoiceStep.None,v.Next(true,false,1000,false));
        Assert.Equal(VoiceStep.Start,v.Next(true,false,2000,false));v.Result(true,true,2000);
        Assert.False(v.Failed);Assert.Equal(VoiceStep.None,v.Next(true,true,3000,false));
    }
    [Fact]public void StopOnlyAfterStartAndClearsWarning()
    {
        var v=new VoiceSync();
        Assert.Equal(VoiceStep.Start,v.Next(true,false,0,false));v.Result(true,false,0);Assert.True(v.Failed);
        Assert.Equal(VoiceStep.Stop,v.Next(false,false,10,false));Assert.False(v.Failed);
        v.Result(false,true,10);Assert.Equal(VoiceStep.None,v.Next(false,false,5000,false));
    }
    [Fact]public void LostAckForStopIsRetriedWithoutWarning()
    {
        var v=new VoiceSync();
        v.Next(true,false,0,false);v.Result(true,true,0);
        Assert.Equal(VoiceStep.Stop,v.Next(false,true,100,false));v.Result(false,false,100);
        Assert.False(v.Failed);Assert.Equal(VoiceStep.None,v.Next(false,true,200,false));
        Assert.Equal(VoiceStep.Stop,v.Next(false,true,2200,false));
    }
    [Fact]public void PageDroppingVoiceIsReportedAndRetried()
    {
        var v=new VoiceSync();
        v.Next(true,false,0,false);v.Result(true,true,0);
        Assert.Equal(VoiceStep.Start,v.Next(true,false,5000,false));Assert.True(v.Failed);
    }
    [Fact]public void NewPageForgetsOldVoice()
    {
        var v=new VoiceSync();
        v.Next(true,false,0,false);v.Result(true,false,0);v.PageChanged();
        Assert.False(v.Failed);Assert.Equal(VoiceStep.None,v.Next(false,false,10,false));
    }

    // 2: the user always learns what is wrong and what to click; the hotkey answer is never hidden by a stale warning.
    static StatusInputs Inputs(bool paired=true,bool loggedIn=true,bool session=false,bool paused=false,bool voiceFailed=false,bool unmute=false,bool reachable=true,bool hook=true,bool privacy=false,bool dictating=false,bool drawing=false,int ids=0)
        =>new(hook,paired,loggedIn,session,paused,voiceFailed,unmute,reachable,privacy,dictating,drawing,ids,"Alt+Win+Shift");
    [Fact]public void ReadyStatusNamesTheHotkeyAndStaysOffScreen()
    {
        var s=StatusLine.Compute(Inputs());
        Assert.Equal(Level.Idle,s.Level);Assert.False(s.ShowToast);Assert.Contains("Alt+Win+Shift",s.Text);
    }
    [Fact]public void NotPairedSaysWhatToDo()
    {
        var idle=StatusLine.Compute(Inputs(paired:false));
        Assert.Equal(Level.Warn,idle.Level);Assert.False(idle.ShowToast);Assert.Contains("Desk öffnen",idle.Text);
        var started=StatusLine.Compute(Inputs(paired:false,session:true));
        Assert.True(started.ShowToast);Assert.Contains("nicht verbunden",started.Text);Assert.Contains("Desk öffnen",started.Text);
    }
    [Fact]public void StaleVoiceFlagNeverHidesTheHotkeyAnswer()
    {
        Assert.DoesNotContain("Stimme",StatusLine.Compute(Inputs(voiceFailed:true)).Text);
        Assert.DoesNotContain("Stimme",StatusLine.Compute(Inputs(paired:false,session:true,voiceFailed:true)).Text);
        var live=StatusLine.Compute(Inputs(session:true,voiceFailed:true));
        Assert.Equal(Level.Warn,live.Level);Assert.Contains("Desk öffnen",live.Text);Assert.Contains("Mikrofon",live.Text);
    }
    [Fact]public void SessionStatesShowSmallToastOnly()
    {
        Assert.Equal("Sitzung · 4 IDs",StatusLine.Compute(Inputs(session:true,ids:4)).Text);
        Assert.Equal(Level.Paused,StatusLine.Compute(Inputs(session:true,paused:true)).Level);
        Assert.Equal("Diktat",StatusLine.Compute(Inputs(session:true,dictating:true)).Text);
        Assert.All(new[]{Inputs(session:true),Inputs(session:true,privacy:true),Inputs(session:true,drawing:true),Inputs(session:true,reachable:false),Inputs(session:true,unmute:true)},i=>Assert.True(StatusLine.Compute(i).ShowToast));
        Assert.Contains("Tastatur",StatusLine.Compute(Inputs(hook:false)).Text);
    }

    // 2 + 3: layout on Florian's 2560x1440 laptop (and the scales people actually use).
    static readonly PxRect Work2560=new(0,0,2560,1400);   // taskbar takes 40 px
    [Theory][InlineData(1.0)][InlineData(1.25)][InlineData(1.5)][InlineData(1.75)][InlineData(2.0)]
    public void DeskFitsWorkAreaAtEveryScale(double scale)
    {
        var work=new PxRect(0,0,(int)(2560),(int)(1400));
        var box=WindowPlacement.Desk(work,scale,null,[work]);
        Assert.True(work.Contains(box),box.ToString());
        Assert.True(box.W>=WindowPlacement.DeskMinWidthDip*scale-1&&box.H>=WindowPlacement.DeskMinHeightDip*scale-1);
        Assert.Equal(work.Right-(int)Math.Round(12*scale),box.Right);   // top-right corner, small margin
    }
    [Theory][InlineData(1366,728,1.0)][InlineData(1280,680,1.25)][InlineData(1920,1040,1.5)][InlineData(1536,816,1.25)]
    public void DeskFitsSmallLaptopScreens(int w,int h,double scale)
    {
        var work=new PxRect(0,0,w,h);var box=WindowPlacement.Desk(work,scale,null,[work]);
        Assert.True(work.Contains(box),box.ToString());
    }
    [Fact]public void DeskRemembersSizeAndPositionOnlyWhenReachable()
    {
        var work=Work2560;
        var back=WindowPlacement.Desk(work,1.5,new PxRect(300,200,900,1000),[work]);
        Assert.Equal(new PxRect(300,200,900,1000),back);
        var gone=WindowPlacement.Desk(work,1.5,new PxRect(9000,200,900,1000),[work]);   // monitor unplugged
        Assert.True(work.Contains(gone));Assert.Equal(900,gone.W);
        var huge=WindowPlacement.Desk(work,1.5,new PxRect(0,0,9000,9000),[work]);
        Assert.True(work.Contains(huge));
        var partly=WindowPlacement.Desk(work,1.0,new PxRect(2400,1000,600,600),[work]);
        Assert.True(work.Contains(partly));
    }
    [Fact]public void DeskUsesTheMonitorItWasLeftOn()
    {
        PxRect left=new(0,0,1920,1040),right=new(1920,0,2560,1400);
        var box=WindowPlacement.Desk(right,1.0,new PxRect(100,100,700,800),[left,right]);
        Assert.True(left.Contains(box));
    }
    [Theory][InlineData(1.0)][InlineData(1.5)][InlineData(2.0)]
    public void ToastStaysInsideBottomRight(double scale)
    {
        var box=WindowPlacement.BottomRight(Work2560,(int)(240*scale),(int)(30*scale),(int)(14*scale));
        Assert.True(Work2560.Contains(box));Assert.Equal(Work2560.Right-(int)(14*scale),box.Right);Assert.Equal(Work2560.Bottom-(int)(14*scale),box.Bottom);
        var tiny=WindowPlacement.BottomRight(new PxRect(0,0,200,100),500,500,14);
        Assert.True(new PxRect(0,0,200,100).Contains(tiny));
    }
}
