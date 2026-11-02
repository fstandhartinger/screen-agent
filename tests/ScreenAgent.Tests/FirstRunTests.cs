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
        public readonly HotkeyBindings Bindings;public readonly Hotkeys State;public readonly List<ActionKind> Actions=[];public int Masks;long t;
        public Rig(Dictionary<string,string>? keys=null){Bindings=new HotkeyBindings(keys??AppConfig.DefaultHotkeys());State=new(Bindings.Layout);}
        public void Key(int vk,bool down)
        {
            bool transition=Bindings.Feed(vk,down,out var key);
            if(Bindings.MaskNow)Masks++;
            if(transition)Actions.AddRange(State.Feed(new(key,down,t+=10)));
        }
        public void Down(params int[] vks){foreach(var vk in vks)Key(vk,true);}
        public void Up(params int[] vks){foreach(var vk in vks)Key(vk,false);}
    }

    // Florian 2 Nov: Ctrl+Win+Shift is the session chord again (v0.1.2 had moved it to Alt+Win+Shift); Ctrl+Win alone stays Wispr's.
    [Fact]public void DefaultsAreFloriansSpec()
    {
        var keys=AppConfig.DefaultHotkeys();
        Assert.Equal("Ctrl+Win+Shift",keys["session"]);Assert.Equal("Alt+Win",keys["dictation"]);Assert.Equal("Alt+Win+Shift",keys["dictationToggle"]);
        Assert.Equal("Ctrl+Win+Shift+M",keys["mute"]);Assert.False(new HotkeyBindings(keys).CollidesWithWispr);
        Assert.Equal("Ctrl+Win+Shift",AppConfig.Parse("{}").Hotkeys["session"]);
    }
    [Fact]public void CtrlWinIsAlwaysUntouched()
    {
        var rig=new Rig();
        rig.Down(Ctrl,Win);rig.Actions.AddRange(rig.State.Tick(5000));Assert.Equal(0,rig.Masks);rig.Up(Win,Ctrl);
        Assert.Empty(rig.Actions);Assert.Equal(0,rig.Masks);Assert.False(rig.State.Session);Assert.False(rig.State.Dictating);
    }
    [Fact]public void CtrlWinThenShiftCompletesTheChordAndMasksOnlyThen()
    {
        var rig=new Rig();
        rig.Down(Ctrl);Assert.False(rig.Bindings.MaskNow);rig.Down(Win);Assert.False(rig.Bindings.MaskNow);
        rig.Down(Shift);Assert.True(rig.Bindings.MaskNow);Assert.Equal(1,rig.Masks);
        rig.Up(Shift,Win,Ctrl);
        Assert.Single(rig.Actions,a=>a==ActionKind.SessionToggle);Assert.True(rig.State.Session);Assert.Equal(1,rig.Masks);
    }
    [Theory][InlineData(Ctrl,Win,Alt)][InlineData(Win,Ctrl,Alt)]
    public void WisprCommandChordIsNotOurs(int a,int b,int c)
    {
        var rig=new Rig();
        rig.Down(a,b);Assert.Equal(0,rig.Masks);rig.Down(c);rig.Actions.AddRange(rig.State.Tick(5000));rig.Up(c,b,a);
        Assert.Equal(0,rig.Masks);Assert.False(rig.State.Session);Assert.DoesNotContain(ActionKind.DictationStart,rig.Actions);
    }
    [Fact]public void ChordWithAnotherKeyDoesNotToggle()
    {
        var rig=new Rig();
        rig.Down(Ctrl,Win,Shift);rig.Key('R',true);rig.Key('R',false);rig.Up(Shift,Win,Ctrl);
        Assert.False(rig.State.Session);
    }
    [Fact]public void OnlyAChordBelongingToUsIsMasked()
    {
        var rig=new Rig();
        rig.Down(Alt);Assert.False(rig.Bindings.MaskNow);
        rig.Down(Win);Assert.True(rig.Bindings.MaskNow);          // Alt+Win hold
        rig.Down(Shift);Assert.False(rig.Bindings.MaskNow);       // Alt+Win+Shift: already masked in this press
        rig.Key(Win,true);Assert.False(rig.Bindings.MaskNow);     // auto repeat
    }
    [Fact]public void ReleasedKeyCannotRecompleteAChord()
    {
        var rig=new Rig();
        rig.Down(Ctrl,Win,Shift);rig.Up(Shift);rig.Down(Shift);
        Assert.Equal(1,rig.Masks);rig.Up(Shift,Win,Ctrl);Assert.Single(rig.Actions,a=>a==ActionKind.SessionToggle);
    }
    [Fact]public void RolesAreOnePerPhysicalModifier()
    {
        var b=new HotkeyBindings(AppConfig.DefaultHotkeys());
        Assert.Equal(Key.Ctrl,b.Role(Ctrl));Assert.Equal(Key.Alt,b.Role(Alt));Assert.Equal(Key.Win,b.Role(Win));Assert.Equal(Key.Mute,b.Role('M'));Assert.Equal(Key.P,b.Role('P'));
    }
    [Fact]public void SettingsCanStillChooseCtrlWinButAreWarned()
    {
        var keys=AppConfig.DefaultHotkeys();keys["dictation"]="Ctrl+Win";keys["dictationToggle"]="Ctrl+Win+Alt";
        Assert.True(new HotkeyBindings(keys).CollidesWithWispr);
        Assert.False(new HotkeyBindings(AppConfig.DefaultHotkeys()).CollidesWithWispr);
    }
    [Fact]public void TrayCanStartAndStopSessionWithoutKeys()
    {
        var h=new Hotkeys();
        Assert.Contains(ActionKind.SessionToggle,h.SetSession(true));Assert.True(h.Session);
        Assert.Empty(h.SetSession(true));
        Assert.Contains(ActionKind.SessionToggle,h.SetSession(false));Assert.False(h.Session);
    }

    [Fact]public void TrayCanPauseAndResumeAChordPause()
    {
        var h=new Hotkeys();h.SetSession(true);
        Assert.Contains(ActionKind.PauseToggle,h.SetPaused(true));Assert.True(h.Paused);
        Assert.Empty(h.SetPaused(true));
        Assert.Contains(ActionKind.PauseToggle,h.SetPaused(false));Assert.False(h.Paused);
    }
    // A key-up the hook never saw (Win+L, secure desktop) must not turn the next Alt into a chord.
    [Fact]public void LostWinReleaseDoesNotStartDictation()
    {
        var rig=new Rig();
        rig.Down(Win);                                                        // Win+L: the Win-up never arrives
        var released=rig.Bindings.Resync(vk=>vk!=Win,Alt);                    // Windows says Win is no longer down
        foreach(var (_,role) in released)rig.Actions.AddRange(rig.State.Feed(new(role,false,50)));
        rig.Down(Alt);rig.Actions.AddRange(rig.State.Tick(5000));
        Assert.Single(released);Assert.DoesNotContain(ActionKind.DictationStart,rig.Actions);Assert.False(rig.Bindings.Is(Hotkeys.Default.Dictation));
    }
    [Fact]public void ResyncKeepsKeysWindowsStillSeesDown()
    {
        var b=new HotkeyBindings(AppConfig.DefaultHotkeys());
        b.Feed(Alt,true,out _);b.Feed(Win,true,out _);
        Assert.Empty(b.Resync(_=>true,Shift));Assert.True(b.Is(Hotkeys.Default.Dictation));
    }
    [Fact]public void UnansweredStopIsNotRetriedForever()
    {
        var v=new VoiceSync();long now=0;
        v.Next(true,false,now,false);v.Result(true,false,now);          // start never acknowledged
        int stops=0;
        for(int i=0;i<200;i++)
        {
            now+=1000;
            if(v.Next(false,false,now,false)==VoiceStep.Stop){stops++;v.Result(false,false,now);}
        }
        Assert.Equal(VoiceSync.MaxStopTries,stops);Assert.False(v.Failed);
    }
    // Wispr's chord starts with Ctrl+Win, and its command chord adds Alt: no mask while Ctrl+Win is the pair held.
    [Theory][InlineData(Ctrl,Win,Alt)][InlineData(Win,Ctrl,Alt)][InlineData(Ctrl,Alt,Win)][InlineData(Alt,Ctrl,Win)]
    public void CtrlChordsOtherThanOursNeverGetTheMask(int a,int b,int c)
    {
        var rig=new Rig();
        rig.Down(a,b,c);
        Assert.Equal(0,rig.Masks);
    }
    [Theory][InlineData(Win,Alt,Ctrl)][InlineData(Alt,Win,Ctrl)]
    public void AltWinThenCtrlMaskedOnceAtAltWin(int a,int b,int c)
    {
        var rig=new Rig();
        rig.Down(a,b,c);
        Assert.Equal(1,rig.Masks);   // Alt+Win hold looked like ours when it completed; documented in the README
    }

    // Config written by v0.1.1 still holds the Ctrl+Win defaults: migrate them, keep real customisations.
    [Fact]public void UntouchedLegacyHotkeysAreMigrated()
    {
        string old=JsonSerializer.Serialize(new{DeskUrl="https://desk.app.mintapis.com",Hotkeys=AppConfig.LegacyHotkeys()});
        var c=AppConfig.Parse(old);
        Assert.True(c.HotkeysMigrated);Assert.Equal("Ctrl+Win+Shift",c.Hotkeys["session"]);Assert.Equal("Alt+Win",c.Hotkeys["dictation"]);Assert.Equal("Alt+Win+Shift",c.Hotkeys["dictationToggle"]);Assert.Equal("Ctrl+Win+Shift+M",c.Hotkeys["mute"]);
        Assert.Equal("https://desk.app.mintapis.com",c.DeskUrl);
        Assert.False(AppConfig.Parse(JsonSerializer.Serialize(c)).HotkeysMigrated);
        Assert.DoesNotContain("Migrated",JsonSerializer.Serialize(c));
    }
    [Fact]public void V012DefaultsAreMigratedToTheSpec()
    {
        var c=AppConfig.Parse(JsonSerializer.Serialize(new{Hotkeys=AppConfig.V012Hotkeys()}));
        Assert.True(c.HotkeysMigrated);Assert.Equal("Ctrl+Win+Shift",c.Hotkeys["session"]);Assert.Equal("Ctrl+Win+Shift+P",c.Hotkeys["pause"]);
    }
    [Fact]public void CustomisedHotkeysAreKeptAndMissingKeysFilled()
    {
        var keys=AppConfig.V012Hotkeys();keys["draw"]="F9";keys["session"]="Ctrl+Alt+Shift";
        var c=AppConfig.Parse(JsonSerializer.Serialize(new{Hotkeys=keys}));
        Assert.False(c.HotkeysMigrated);Assert.Equal("Ctrl+Alt+Shift",c.Hotkeys["session"]);Assert.Equal("F9",c.Hotkeys["draw"]);Assert.Equal("Ctrl+Win+Shift+M",c.Hotkeys["mute"]);
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
        =>new(hook,paired,loggedIn,session,paused,voiceFailed,unmute,reachable,privacy,dictating,drawing,ids,"Ctrl+Win+Shift");
    [Fact]public void ReadyStatusNamesTheHotkeyAndStaysOffScreen()
    {
        var s=StatusLine.Compute(Inputs());
        Assert.Equal(Level.Idle,s.Level);Assert.False(s.ShowToast);Assert.Contains("Ctrl+Win+Shift",s.Text);
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
