using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
// v0.1.17: the subtitle's last seconds re-read by the stronger recognisers + correction model, replaced in place.
public class LiveCaptionReviseTests
{
    static string All(LiveCaption c){var (a,b)=c.Shown(10000);return (a+" "+b).Trim();}
    static LiveCaption Heard(params (string Text,double End)[] windows)
    {
        var c=new LiveCaption();
        foreach(var (t,e) in windows)c.AddWindow(t,e,Math.Min(6,e));
        return c;
    }
    [Fact]
    public void Words_get_audio_times_in_order()
    {
        var c=Heard(("wir haben das Problem",3),("das Problem mit den Untertiteln gesehen",6));
        Assert.Equal(c.Words.Count,c.Times.Count);
        for(int i=1;i<c.Times.Count;i++)Assert.True(c.Times[i]>=c.Times[i-1]);
        Assert.InRange(c.Times[^1],3,6);
    }
    [Fact]
    public void The_re_read_replaces_the_misheard_words_in_place()
    {
        var c=Heard(("wir haben das Jeff Bench",3),("Jeff Bench Ergebnis, gestern. veröffentlicht",6));
        var r=c.NextRevise(6);
        Assert.NotNull(r);Assert.Equal(0,r!.From);Assert.Equal(0,r.FromAudio);
        Assert.Null(c.NextRevise(6));   // one in flight
        Assert.True(c.AnswerRevise(r,"wir haben das JevBench-Ergebnis gestern veröffentlicht",6.2));
        Assert.Equal("wir haben das JevBench-Ergebnis gestern veröffentlicht",All(c));
    }
    [Fact]
    public void Words_heard_after_the_request_stay()
    {
        var c=Heard(("wir haben das Jeff Bench Ergebnis",4));
        var r=c.NextRevise(4)!;
        c.AddWindow("Bench Ergebnis gestern veröffentlicht",6,5);
        Assert.True(c.AnswerRevise(r,"wir haben das JevBench-Ergebnis",6.1));
        Assert.Equal("wir haben das JevBench-Ergebnis gestern veröffentlicht",All(c));
    }
    [Fact]
    public void Words_rewritten_by_a_later_window_are_found_by_the_shown_text()
    {
        var c=Heard(("wir haben das Jeff Bench Ergebnis gestern",5));
        var r=c.NextRevise(5)!;
        c.AddWindow("Ergebnis gestern veröffentlicht und dann",7,5);   // rewrites the tail and adds words
        Assert.True(c.AnswerRevise(r,"wir haben das JevBench-Ergebnis gestern",7.1));
        Assert.Equal("wir haben das JevBench-Ergebnis gestern veröffentlicht und dann",All(c));
    }
    [Fact]
    public void A_failed_or_late_re_read_changes_nothing()
    {
        var c=Heard(("wir haben das Problem gesehen",4));
        var before=All(c);
        var r=c.NextRevise(4)!;
        Assert.False(c.AnswerRevise(r,null,5));
        Assert.Equal(before,All(c));
        Assert.False(c.ReviseBusy);
        var r2=c.NextRevise(4,minNewSeconds:0)!;
        c.Settle("Wir haben das Problem gesehen.");   // a chunk came in between: the answer is for words that are gone
        Assert.False(c.AnswerRevise(r2,"völlig anderer Text hier",6));
        Assert.StartsWith("Wir haben das Problem gesehen.",All(c));
    }
    [Fact]
    public void A_whole_different_answer_is_not_taken()
    {
        var c=Heard(("eins zwei drei vier fünf sechs sieben acht",4));
        var r=c.NextRevise(4)!;
        Assert.False(c.AnswerRevise(r,"ja",5));
        Assert.Equal("eins zwei drei vier fünf sechs sieben acht",All(c));
    }
    [Fact]
    public void The_overlap_before_the_span_is_left_out()
    {
        var c=new LiveCaption();
        c.AddWindow("Das ist der erste Satz. Und dann",6,6);
        var r1=c.NextRevise(6)!;
        c.AnswerRevise(r1,"Das ist der erste Satz. Und dann",12);   // 12: the first sentence lies well back -> confirmed
        Assert.Equal(5,c.Confirmed);
        c.AddWindow("Satz und dann kommt der zweite",9,6);
        var r2=c.NextRevise(9)!;
        Assert.Equal(5,r2.From);Assert.True(r2.FromAudio>0);
        Assert.True(c.AnswerRevise(r2,"Satz. Und dann kommt der Zweite",9.2));
        Assert.Equal("Das ist der erste Satz. Und dann kommt der Zweite",All(c));
    }
    [Fact]
    public void Confirmed_words_are_not_changed_by_later_windows_or_re_reads()
    {
        var c=new LiveCaption();
        c.AddWindow("Das ist erledigt. Jetzt geht es weiter",6,6);
        var r=c.NextRevise(6)!;
        c.AnswerRevise(r,"Das ist erledigt. Jetzt geht es weiter",10);
        Assert.Equal(3,c.Confirmed);
        var (conf,_)=c.Shown(10000);
        Assert.Equal("Das ist erledigt.",conf);
        c.AddWindow("Jetzt geht's weiter mit dem Plan",9,6);
        var r2=c.NextRevise(9)!;Assert.Equal(3,r2.From);
        c.AnswerRevise(r2,"Jetzt geht es weiter mit dem Plan",9.5);
        Assert.StartsWith("Das ist erledigt.",All(c));
    }
    [Fact]
    public void Tentative_window_words_show_no_punctuation_and_the_open_end_none()
    {
        var c=Heard(("wir haben, das. Problem gesehen,",4));
        var (conf,tent)=c.Shown(200);
        Assert.Equal("",conf);
        Assert.Equal("wir haben das Problem gesehen",tent);
        var r=c.NextRevise(4)!;
        c.AnswerRevise(r,"Wir haben, wie du weißt, das Problem gesehen,",4.1);
        Assert.Equal("Wir haben, wie du weißt, das Problem gesehen",c.Shown(200).Tentative);   // the re-read's commas stay, its open end has none
    }
    [Fact]
    public void Shown_keeps_to_the_length_from_the_end()
    {
        var c=Heard(("eins zwei drei vier fünf sechs sieben acht neun zehn elf zwölf",6));
        var (a,b)=c.Shown(20);
        Assert.True((a+" "+b).Trim().Length<=20);
        Assert.EndsWith("zwölf",b);
    }
    [Fact]
    public void A_long_sentence_is_confirmed_by_age()
    {
        var c=new LiveCaption();
        c.AddWindow("eins zwei drei vier fünf sechs",6,6);
        c.AddWindow("sieben acht neun zehn elf zwölf",12.5,6);
        c.AddWindow("dreizehn vierzehn fünfzehn sechzehn",19,6);
        var r=c.NextRevise(19)!;
        Assert.True(c.AnswerRevise(r,string.Join(' ',c.Words),19.1)==false);   // the same words: nothing changed
        Assert.True(c.Confirmed>0);   // words more than 12 s back are confirmed although no sentence ended
        Assert.True(c.Times[c.Confirmed-1]<=19-LiveCaption.ConfirmLong);
    }
    [Fact]
    public void The_span_never_exceeds_max_seconds()
    {
        var c=new LiveCaption();
        for(int i=1;i<=8;i++)c.AddWindow($"wort{i}a wort{i}b wort{i}c wort{i}d",i*6,6);
        var r=c.NextRevise(48,maxSeconds:20)!;
        Assert.True(r.ToAudio-r.FromAudio<=20.01);
        Assert.True(c.Confirmed>0);
    }
    [Fact]
    public void Policy_sends_on_a_beat_while_speaking_and_once_for_the_tail()
    {
        var p=new RevisePolicy();var c=Heard(("wir haben das Problem gesehen",4));
        var r=p.Next(c,1000,4,true);Assert.NotNull(r);
        Assert.Null(p.Next(c,1500,4.5,true));   // in flight
        c.AnswerRevise(r!,"wir haben das Problem gesehen",4.6);p.Answered(true,1600);
        c.AddWindow("Problem gesehen und dann",6,6);
        Assert.Null(p.Next(c,3000,6,true));   // the beat: 3.5 s
        Assert.Null(p.Next(c,4400,6.4,true));   // the beat has passed but there are only 1.8 s of new audio
        var tail=p.Next(c,4500,6.5,false);Assert.NotNull(tail);   // he stopped: the tail at once
        c.AnswerRevise(tail!,"Problem gesehen und dann",6.6);p.Answered(true,4700);
        Assert.Null(p.Next(c,9000,9,false));   // nothing new: no call in silence
    }
    [Fact]
    public void Policy_pauses_after_failures_and_stops_without_key()
    {
        var p=new RevisePolicy();
        for(int i=0;i<3;i++){p.Begin(i*4000);p.Answered(false,i*4000+100);}
        Assert.False(p.ShouldSend(12000,true,false));
        Assert.True(p.ShouldSend(8200+30000,true,false));
        p.Begin(40000);p.Answered(false,40100,"caption-budget");
        Assert.True(p.Disabled);
    }
}
