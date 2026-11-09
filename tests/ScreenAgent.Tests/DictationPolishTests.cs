using ScreenAgent.Core;
using Xunit;
public class DictationPolishTests
{
    static DictationOutcome Outcome(string text,int chunks,bool degraded=false,int failed=0)=>new(text,chunks,failed,degraded,false,null,0,0);
    [Fact] public void OnlySeveralChunksOrADegradedChunkGetTheExtraPass()
    {
        Assert.False(DictationPolish.Wanted(Outcome("Hallo Welt",1)));
        Assert.True(DictationPolish.Wanted(Outcome("Hallo Welt",2)));
        Assert.True(DictationPolish.Wanted(Outcome("Hallo Welt",1,degraded:true)));
        Assert.False(DictationPolish.Wanted(Outcome("Hallo Welt",3,failed:1)));
        Assert.False(DictationPolish.Wanted(Outcome("  ",3)));
    }
    [Fact] public void FillersMayGoButHisWordsStay()
    {
        Assert.Equal(3,DictationPolish.Words("Äh ich ähm meine das"));
        Assert.True(DictationPolish.Accept("Wir wollen äh Sichtbarkeit gegenüber potenzieller Kunden. Verbessern.","Wir wollen Sichtbarkeit gegenüber potenziellen Kunden verbessern."));
        Assert.False(DictationPolish.Accept("eins zwei drei vier fünf sechs sieben acht","eins zwei"));
        Assert.False(DictationPolish.Accept("eins zwei drei","   "));
        Assert.True(DictationPolish.Budget(new string('x',100000))<=TimeSpan.FromSeconds(9));
    }
    [Fact] public void LiveCleanUpReplacesOnlyTheStableWordsWhileTheyStayUnchanged()
    {
        var lp=new LivePolish(volatileWords:2,windowWords:70,minNewWords:3);
        var words="die Use Buttons in Jeff Bench hereingekommen und dann".Split(' ');
        var req=lp.Next(words)!;
        Assert.Equal("die Use Buttons in Jeff Bench hereingekommen",req.Text);
        Assert.Null(lp.Next(words));   // one at a time
        lp.Answer(req,"die Use Buttons in JevBench hereingekommen");
        Assert.Equal("die Use Buttons in JevBench hereingekommen und dann",lp.Display(words,200));
        // the next caption window revised only the newest words: the cleaned part stays on screen
        var more="die Use Buttons in Jeff Bench hereingekommen und dann haben wir".Split(' ');
        Assert.Equal("die Use Buttons in JevBench hereingekommen und dann haben wir",lp.Display(more,200));
        Assert.EndsWith("haben wir",lp.Display(more,20));
        // a window changed words inside the cleaned part: raw again until the next answer
        var changed="die Use Buttons im Jeff Bench hereingekommen und dann".Split(' ');
        Assert.Equal(string.Join(' ',changed),lp.Display(changed,200));
        // a failed request frees the slot and keeps the raw text
        var r2=lp.Next(more)!;lp.Answer(r2,null);Assert.False(lp.Busy);
        // an answer that dropped his words is not shown
        var lp2=new LivePolish(volatileWords:2);var r3=lp2.Next("a b c d e f g h i j k l".Split(' '))!;lp2.Answer(r3,"a");
        Assert.Equal("a b c d e f g h i j k l",lp2.Display("a b c d e f g h i j k l".Split(' '),200));
        lp.Reset();Assert.Equal("x y",lp.Display(["x","y"],200));
    }
}
