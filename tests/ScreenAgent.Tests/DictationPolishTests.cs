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
    // ---- v0.1.13: light structure
    static string Long(int words)=>string.Join(' ',Enumerable.Range(1,words).Select(i=>"wort"+i));
    [Fact] public void TheGatewayPassIsFinalForSeveralChunksAndFormatForOneLongChunk()
    {
        Assert.Equal("final",DictationPolish.Mode(Outcome(Long(10),2),polish:true,format:true));
        Assert.Equal("final",DictationPolish.Mode(Outcome(Long(10),1,degraded:true),true,true));
        Assert.Equal("format",DictationPolish.Mode(Outcome(Long(60),1),true,true));
        Assert.Null(DictationPolish.Mode(Outcome(Long(44),1),true,true));          // a short single chunk is done already
        Assert.Null(DictationPolish.Mode(Outcome(Long(60),1),true,false));         // DictationFormat off: as in v0.1.12
        Assert.Equal("final",DictationPolish.Mode(Outcome(Long(60),3),true,false));
        Assert.Equal("format",DictationPolish.Mode(Outcome(Long(60),3),false,true)); // clean-up off, structure on
        Assert.Null(DictationPolish.Mode(Outcome(Long(60),3),false,false));
        Assert.Null(DictationPolish.Mode(Outcome(Long(60),3,failed:1),true,true));  // a failed dictation is shown as it is
        Assert.Null(DictationPolish.Mode(Outcome("  ",3),true,true));
        Assert.Equal(4,DictationPolish.Words("um das zu tun"));                     // "um" is a word, not a filler
    }
    [Fact] public void LineBreaksSurviveForPastingAndBecomeOneSpaceForTyping()
    {
        string text="Erster Absatz. Noch ein Satz. \n\nZweiter Absatz.\nNeue Zeile.\n";
        Assert.Equal("Erster Absatz. Noch ein Satz.\r\n\r\nZweiter Absatz.\r\nNeue Zeile.",DictationLayout.ForPaste(text));
        Assert.Equal("Erster Absatz. Noch ein Satz. Zweiter Absatz. Neue Zeile. ",DictationLayout.Flat(text));
        // never more than one blank line, no tabs or other control characters, mixed line ends
        Assert.Equal("a\r\n\r\nb\r\nc d",DictationLayout.ForPaste("\n\na \r\n\r\n\r\n\n b\rc\td\u0007\n\n"));
        Assert.Equal("a b c",DictationLayout.Flat("a\r\nb\tc\u0007"));
        Assert.Equal("ohne Umbruch",DictationLayout.ForPaste("ohne Umbruch"));Assert.Equal(" mit Rand ",DictationLayout.ForPaste(" mit Rand "));Assert.Equal("a b",DictationLayout.ForPaste("a\tb"));Assert.Equal("",DictationLayout.ForPaste(null));Assert.Equal("",DictationLayout.Flat(null));
        Assert.True(DictationLayout.HasBreaks("a\nb"));Assert.False(DictationLayout.HasBreaks("a b"));Assert.False(DictationLayout.HasBreaks(null));
        // the same words either way: what the clean-up guard and the correction learner count
        Assert.Equal(DictationPolish.Words(text),DictationPolish.Words(DictationLayout.Flat(text)));
        Assert.True(DictationPolish.Accept(DictationLayout.Flat(text),text));
        // the learner compares one line with one line: a pasted text with breaks is found again in the field
        string field="Davor. "+DictationLayout.ForPaste(text);
        Assert.True(Corrections.InsertedSpan(DictationLayout.Flat("Davor. "),DictationLayout.Flat(field),DictationLayout.Flat(DictationLayout.ForPaste(text)),out int at));Assert.Equal(7,at);
    }
    [Fact] public void FillersMayGoButHisWordsStay()
    {
        Assert.Equal(3,DictationPolish.Words("Äh ich ähm meine das"));
        Assert.True(DictationPolish.Accept("Wir wollen äh Sichtbarkeit gegenüber potenzieller Kunden. Verbessern.","Wir wollen Sichtbarkeit gegenüber potenziellen Kunden verbessern."));
        Assert.False(DictationPolish.Accept("eins zwei drei vier fünf sechs sieben acht","eins zwei"));
        Assert.False(DictationPolish.Accept("eins zwei drei","   "));
        Assert.True(DictationPolish.Budget(new string('x',100000))<=TimeSpan.FromSeconds(9));
    }
}
