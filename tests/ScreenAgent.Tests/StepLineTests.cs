using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
public class StepLineTests
{
    static ActivityNote Note(string id,string phase,string label="")=>new(id,phase,label,"",0,"");
    [Fact]public void ActiveToolSurvivesSilenceThenFadesFourSecondsAfterDone()
    {
        var s=new StepLine();s.Activity(Note("a","start","Lese Verlauf"),100);
        Assert.Equal("Lese Verlauf",s.View(60000,true).Text);
        s.Activity(Note("a","done"),61000);Assert.Equal("Lese Verlauf",s.View(64600,true).Text);
        Assert.InRange(s.View(64800,true).Opacity,.49,.51);Assert.Equal("",s.View(65000,true).Text);
    }
    [Fact]public void EndingOneOfTwoToolsKeepsTheOtherVisible()
    {
        var s=new StepLine();s.Activity(Note("a","start","Lese"),0);s.Activity(Note("b","start","Suche"),1);
        s.Activity(Note("b","done"),2);Assert.Equal("Lese",s.View(9000,true).Text);
        Assert.Equal("",s.View(9000,false).Text);s.Clear();Assert.Equal("",s.View(9001,true).Text);
    }
    [Fact]public void ComputerActionShowsStepAndResultAcrossToolGapsThenStops()
    {
        var s=new StepLine();s.Computer("computer.run.start",new Dictionary<string,object?>{{"run","r"}},0);
        s.Computer("computer.action",new Dictionary<string,object?>{{"run","r"},{"seq",3},{"tool","chrome_click"},{"phase","intent"}},10);
        Assert.Contains("Schritt 3 · chrome_click · läuft",s.View(20,true).Text);
        s.Computer("computer.action",new Dictionary<string,object?>{{"run","r"},{"seq",3},{"tool","chrome_click"},{"ok",false},{"reason","timeout"}},30);
        Assert.Contains("fehlgeschlagen · timeout",s.View(10000,true).Text);
        s.Computer("computer.run.stop",new Dictionary<string,object?>{{"run","other"},{"why","stop"}},10000);
        Assert.Contains("fehlgeschlagen",s.View(20000,true).Text);
        s.Computer("computer.run.stop",new Dictionary<string,object?>{{"run","r"},{"why","done"}},20000);
        Assert.Contains("beendet",s.View(22000,true).Text);Assert.Equal("",s.View(24000,true).Text);
    }
    [Fact]public void HeardAndThinkingAreTransientAndCannotAppearWithoutVoice()
    {
        var s=new StepLine();s.Activity(Note("","thinking"),0);Assert.Equal("Denkt nach …",s.View(1,true).Text);
        Assert.Equal("",s.View(1,false).Text);Assert.Equal("",s.View(4000,true).Text);
    }
}
