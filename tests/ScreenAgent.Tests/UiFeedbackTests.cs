using ScreenAgent.Core;
using System.Text.Json;
using Xunit;
namespace ScreenAgent.Tests;
// v0.1.7 app-ui: live activity rows, screenshot feedback rules, drawing window policy, session start text.
public class UiFeedbackTests
{
    static ActivityEvent Ev(string phase,string id="t1",string label="",string tool="",long ms=0,string reason="")=>new(id,phase,label,tool,ms,reason);
    static string[] Texts(ActivityBoard b,long now)=>b.View(now).Select(r=>r.Text).ToArray();

    // ---- activity rows
    [Fact]public void ThinkingIsOneTransientRowReplacedByTheFirstTool()
    {
        var b=new ActivityBoard();
        Assert.True(b.Apply(Ev("thinking",""),0));
        Assert.False(b.Apply(Ev("heard",""),100));
        Assert.Equal(new[]{"Gehört …"},Texts(b,100));
        b.Apply(Ev("start","a","Suche in der Registry","artifact_search"),200);
        Assert.Equal(new[]{"Suche in der Registry"},Texts(b,200));
        Assert.Equal(RowKind.Running,b.View(200)[0].Kind);
    }
    [Fact]public void DoneShowsTickThenFades()
    {
        var b=new ActivityBoard();b.Apply(Ev("start","a","Zeichne"),0);b.Apply(Ev("done","a","",ms:830),1000);
        var row=b.View(1000)[0];Assert.Equal(RowKind.Done,row.Kind);Assert.Equal("Zeichne · 830 ms",row.Text);Assert.Equal(1,row.Opacity);
        Assert.Equal(1,b.View(1000+2500)[0].Opacity);
        Assert.InRange(b.View(1000+2500+200)[0].Opacity,0.4,0.6);
        Assert.Empty(b.View(1000+2500+400));
    }
    [Fact]public void FailStaysOrangeEightSecondsWithTheReason()
    {
        var b=new ActivityBoard();b.Apply(Ev("start","a","Suche"),0);b.Apply(Ev("fail","a","",reason:"keine Treffer"),500);
        var row=b.View(8500)[0];Assert.Equal(RowKind.Fail,row.Kind);Assert.Equal("Suche · keine Treffer",row.Text);Assert.Equal(1,row.Opacity);
        Assert.Empty(b.View(500+8000+400));
    }
    [Fact]public void AtMostFourRowsFinishedOnesGoFirst()
    {
        var b=new ActivityBoard();
        for(int i=0;i<4;i++)b.Apply(Ev("start","r"+i,"Tool "+i),i);
        b.Apply(Ev("done","r1"),10);
        b.Apply(Ev("start","r4","Tool 4"),20);
        Assert.Equal(4,b.View(20).Count);Assert.DoesNotContain(b.View(20),r=>r.Id=="r1");Assert.Contains(b.View(20),r=>r.Id=="r4");
        b.Apply(Ev("start","r5","Tool 5"),30);   // all running: the oldest running row goes
        Assert.Equal(4,b.View(30).Count);Assert.DoesNotContain(b.View(30),r=>r.Id=="r0");
    }
    [Fact]public void OneRowPerIdAndDoneWithoutStartStillShows()
    {
        var b=new ActivityBoard();b.Apply(Ev("start","a","Eins"),0);b.Apply(Ev("start","a","Eins"),50);
        Assert.Single(b.View(50));
        Assert.True(b.Apply(Ev("done","x","Schnell",ms:12),60));Assert.Equal(2,b.View(60).Count);
    }
    [Fact]public void LostDoneDoesNotLeaveASpinnerForever()
    {
        var b=new ActivityBoard();b.Apply(Ev("start","a","Hängt"),0);
        Assert.Single(b.View(89_000));Assert.False(b.Any(90_000));
        b.Apply(Ev("thinking",""),0);Assert.False(b.Any(12_000));
    }
    [Fact]public void LabelFallsBackToTheToolName()
    {
        var b=new ActivityBoard();b.Apply(Ev("start","a","","look_at_screen"),0);Assert.Equal("look_at_screen",b.View(0)[0].Text);
    }
    [Fact]public void ParseKeepsFieldsAndRejectsGarbage()
    {
        using var ok=JsonDocument.Parse("{\"type\":\"activity\",\"id\":\"c1\",\"phase\":\"START\",\"label\":\"Suche\\n in Registry\",\"tool\":\"artifact_search\",\"ms\":123}");
        Assert.True(ActivityParse.TryParse(ok.RootElement,out var ev,out _));
        Assert.Equal(new ActivityEvent("c1","start","Suche in Registry","artifact_search",123,""),ev);
        using var noId=JsonDocument.Parse("{\"phase\":\"done\"}");
        Assert.False(ActivityParse.TryParse(noId.RootElement,out _,out var why));Assert.Equal("id",why);
        using var bad=JsonDocument.Parse("{\"phase\":\"dance\",\"id\":\"x\"}");
        Assert.False(ActivityParse.TryParse(bad.RootElement,out _,out why));Assert.Equal("phase",why);
        using var thinking=JsonDocument.Parse("{\"phase\":\"thinking\"}");
        Assert.True(ActivityParse.TryParse(thinking.RootElement,out _,out _));
        using var longText=JsonDocument.Parse("{\"phase\":\"fail\",\"id\":\"x\",\"reason\":\""+new string('r',500)+"\",\"ms\":-5}");
        Assert.True(ActivityParse.TryParse(longText.RootElement,out var cut,out _));Assert.Equal(120,cut!.Reason.Length);Assert.Equal(0,cut.Ms);
    }
    [Fact]public void MainPromptStepsBecomeOneRowThatEndsInATick()
    {
        var b=new ActivityBoard();long t=0;
        void Feed(string type,Dictionary<string,object?>? d=null){if(ActivityLocal.FromTrace(type,d) is {} e)b.Apply(e,t+=100);}
        Feed("main.prompt.received");Feed("main.prompt.start");
        Feed("main.prompt.step",new(){["step"]="window"});Feed("main.prompt.step",new(){["step"]="focus"});Feed("main.prompt.route");
        Feed("main.prompt.step",new(){["step"]="typing"});
        Assert.Single(b.View(t));Assert.Equal("Main Agent · tippe den Prompt",b.View(t)[0].Text);
        Feed("main.prompt.done",new(){["code"]="typed",["ms"]=5600L});
        Assert.Equal("Main Agent · Prompt eingetippt · 5600 ms",b.View(t)[0].Text);Assert.Equal(RowKind.Done,b.View(t)[0].Kind);
    }
    [Fact]public void MainPromptFailureNamesTheReason()
    {
        var e=ActivityLocal.FromTrace("main.prompt.done",new Dictionary<string,object?>{["code"]="no-window",["ms"]=40});
        Assert.Equal("fail",e!.Phase);Assert.Contains("kein VS-Code-Insiders-Fenster",e.Reason);
        Assert.Null(ActivityLocal.FromTrace("screen.frame",null));Assert.Null(ActivityLocal.FromTrace("main.prompt.step",new Dictionary<string,object?>{["step"]="other"}));
    }

    // ---- screenshot feedback
    [Theory]
    [InlineData("visible",ShotReason.Start,true)][InlineData("visible",ShotReason.Fresh,true)][InlineData("visible",ShotReason.Highlight,true)]
    [InlineData("visible",ShotReason.Interval,false)][InlineData("visible",ShotReason.Foreground,false)]
    [InlineData("all",ShotReason.Interval,true)][InlineData("all",ShotReason.Foreground,true)][InlineData("all",ShotReason.Start,true)]
    [InlineData("off",ShotReason.Start,false)][InlineData("off",ShotReason.Fresh,false)][InlineData("off",ShotReason.Highlight,false)]
    public void WhichReasonsFire(string mode,ShotReason reason,bool fires)=>Assert.Equal(fires,ScreenshotFeedback.Fires(mode,reason));
    [Fact]public void FrameReasonsMapToUserVisibleReasons()
    {
        Assert.Equal(ShotReason.Start,ScreenshotFeedback.Of(FrameReason.Start));Assert.Equal(ShotReason.Fresh,ScreenshotFeedback.Of(FrameReason.Fresh));
        Assert.Equal(ShotReason.Interval,ScreenshotFeedback.Of(FrameReason.Interval));Assert.Equal(ShotReason.Foreground,ScreenshotFeedback.Of(FrameReason.Foreground));
    }
    [Fact]public void AtMostOneFlashPerTwoSeconds()
    {
        var g=new FeedbackGate("visible");
        Assert.True(g.Check(ShotReason.Start,10_000).Show);
        Assert.Equal((false,"rate-limit"),g.Check(ShotReason.Fresh,11_999));
        Assert.Equal((false,"not-visible"),g.Check(ShotReason.Interval,12_500));   // a routine frame never uses up the slot
        Assert.True(g.Check(ShotReason.Fresh,12_000).Show);
        Assert.Equal((false,"mode-off"),new FeedbackGate("off").Check(ShotReason.Start,0));
    }
    [Fact]public void ConfigDefaultsAndValidation()
    {
        var c=AppConfig.Parse("{}");Assert.True(c.ActivityOverlay);Assert.Equal("visible",c.ScreenshotFeedback);
        var all=AppConfig.Parse("{\"ScreenshotFeedback\":\"all\",\"ActivityOverlay\":false}");Assert.Equal("all",all.ScreenshotFeedback);Assert.False(all.ActivityOverlay);
        Assert.Throws<InvalidDataException>(()=>AppConfig.Parse("{\"ScreenshotFeedback\":\"sometimes\"}"));
        var migrated=AppConfig.Parse("{\"Hotkeys\":{\"session\":\"Alt+Win+Shift\",\"dictation\":\"Alt+Win\",\"handsFree\":\"Alt+Win+Y\",\"draw\":\"Caps\",\"pause\":\"Alt+Win+Shift+P\"},\"ScreenshotFeedback\":\"off\",\"ActivityOverlay\":false}");
        Assert.Equal("off",migrated.ScreenshotFeedback);Assert.False(migrated.ActivityOverlay);   // the hotkey migration copies the new keys too
    }

    // ---- drawing window
    [Fact]public void FirstDiagramComesToTheFrontLaterOnesDoNot()
    {
        var p=new CanvasPolicy();
        Assert.Equal(new PanelDecision(PanelAct.ShowFront,"first"),p.Pending());p.Opened("first");
        Assert.Equal(new PanelDecision(PanelAct.None,"open"),p.Pending());
        p.Hidden(false);   // the session ended
        Assert.Equal(new PanelDecision(PanelAct.Show,"again"),p.Pending());
    }
    [Fact]public void FailedFirstOpenKeepsTheFrontForTheNextTry()
    {
        var p=new CanvasPolicy();Assert.Equal(PanelAct.ShowFront,p.Pending().Act);
        Assert.Equal(PanelAct.ShowFront,p.Pending().Act);   // Opened was never reported
    }
    [Fact]public void MinimisedStaysMinimisedWhateverTheAgentDraws()
    {
        var p=new CanvasPolicy();p.Opened("first");p.Minimized();
        for(int i=0;i<3;i++){Assert.Equal(new PanelDecision(PanelAct.None,"minimized"),p.Pending());Assert.Equal(PanelState.Minimized,p.State);}
        p.Restored();Assert.Equal(PanelState.Open,p.State);Assert.Equal("open",p.Pending().Why);
    }
    [Fact]public void ClosedStaysClosedUntilTheNextSessionButTheUserCanAlwaysOpenIt()
    {
        var p=new CanvasPolicy();p.Opened("first");p.Hidden(true);
        Assert.Equal(new PanelDecision(PanelAct.None,"closed"),p.Pending());
        Assert.Equal(PanelAct.ShowFront,p.UserToggle().Act);p.Opened("user");p.Hidden(false);p.Hidden(true);
        p.SessionStarted();Assert.Equal(PanelAct.Show,p.Pending().Act);
    }
    [Fact]public void UserToggleAndTrayRestoreAMinimisedWindow()
    {
        var p=new CanvasPolicy();p.Opened("first");p.Minimized();
        Assert.Equal(new PanelDecision(PanelAct.Restore,"user"),p.UserShow());
        Assert.Equal(PanelAct.Restore,p.UserToggle().Act);
        p.Restored();Assert.Equal(PanelAct.Hide,p.UserToggle().Act);
    }
    [Fact]public void UserOpenedPanelDoesNotSpendTheFirstFront()
    {
        var p=new CanvasPolicy();p.Opened("user");p.Hidden(false);
        Assert.Equal("first",p.Pending().Why);
    }
    [Fact]public void ManualModeNeverOpensByItself()=>Assert.Equal(new PanelDecision(PanelAct.None,"manual"),new CanvasPolicy(true).Pending());

    // ---- session start
    [Fact]public void StartingSessionSaysSoAndCounts()
    {
        StatusInputs I(bool starting,int s=0,bool failed=false)=>new(true,true,true,true,false,failed,false,true,false,false,false,0,"Ctrl+Win+Shift",Starting:starting,StartingSeconds:s);
        var a=StatusLine.Compute(I(true));Assert.Equal("Sitzung startet …",a.Text);Assert.True(a.ShowToast);Assert.Equal(Level.Busy,a.Level);
        Assert.Equal("Sitzung startet … 5 s",StatusLine.Compute(I(true,5)).Text);
        Assert.Contains("Stimme",StatusLine.Compute(I(true,5,true)).Text);   // a failure still wins
        Assert.Equal("Sitzung · 0 IDs",StatusLine.Compute(I(false)).Text);
    }
}
