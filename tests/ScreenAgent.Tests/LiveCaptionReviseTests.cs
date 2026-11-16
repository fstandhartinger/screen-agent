using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
// v0.1.19: the subtitle's last seconds re-read by the stronger recognisers + correction model, replaced in place.
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
    public void A_repeat_a_window_added_behind_the_open_answer_goes()
    {
        var c=Heard(("wir finden was gut zu bewerben ist",5));
        var r=c.NextRevise(5,null,minNewSeconds:0)!;
        c.AddWindow("zu bewerben ist zum Beispiel dass es starke Mod-",7,5);
        c.AnswerRevise(r,"wir finden, was gut zu bewerben ist, zum Beispiel, dass es starke Mo",7.1);
        Assert.Equal("wir finden, was gut zu bewerben ist, zum Beispiel, dass es starke Mod-",All(c));
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
        var r2=c.NextRevise(4,null,minNewSeconds:0)!;
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
    static PauseTracker Pauses(double total,params (double From,double To)[] silences)
    {
        var p=new PauseTracker();
        for(double t=0.02;t<=total+1e-9;t+=0.02)p.Add(t,0.02,silences.Any(x=>t>x.From&&t<=x.To)?50:3000);
        return p;
    }
    [Fact]
    public void A_pause_closes_the_text_before_it_and_the_next_re_read_starts_there()
    {
        var c=new LiveCaption();
        c.AddWindow("Das ist der erste Satz und dann",6,6);
        var p=Pauses(9,(4.6,5.4));   // he paused after "Satz"
        var r=c.NextRevise(9,p)!;
        Assert.False(r.Open);Assert.Equal(0,r.FromAudio);Assert.InRange(r.ToAudio,4.6,5.0);
        Assert.True(c.AnswerRevise(r,"Das ist der erste Satz.",9));
        Assert.Equal(5,c.Confirmed);Assert.Equal(r.ToAudio,c.AnchorAudio);
        Assert.Equal("Das ist der erste Satz. und dann",All(c));   // the tentative rest waits for the open re-read
        var r2=c.NextRevise(9,p,minNewSeconds:0)!;
        Assert.True(r2.Open);Assert.Equal(c.AnchorAudio,r2.FromAudio);Assert.Equal(5,r2.From);
        Assert.True(c.AnswerRevise(r2,"Und dann",9.1));
        Assert.Equal("Das ist der erste Satz. Und dann",All(c));
        Assert.Equal("Das ist der erste Satz.",c.Shown(10000).Confirmed);
    }
    [Fact]
    public void An_open_re_read_replaces_the_whole_tentative_text_so_nothing_is_lost_or_doubled()
    {
        var c=new LiveCaption();
        c.AddWindow("Wir müssen viel offensiver",4,4);
        c.AddWindow("viel offensiver und positiver vermarkten",7,6);
        var r=c.NextRevise(7,Pauses(7))!;   // no pause: open, from the start
        Assert.True(r.Open);Assert.Equal(0,r.FromAudio);
        c.AnswerRevise(r,"Wir müssen viel offensiver, und positiver vermarkten",7.2);
        Assert.Equal("Wir müssen viel offensiver, und positiver vermarkten",All(c));
        Assert.Equal(0,Disfluency.Repeats(c.Words));
    }
    [Fact]
    public void A_closed_re_read_whose_end_is_misplaced_heals_with_the_next_open_one()
    {
        var c=new LiveCaption();
        c.AddWindow("eins zwei drei vier fünf sechs sieben acht",6,6);
        var p=Pauses(10,(4.4,5.0));
        var r=c.NextRevise(10,p)!;Assert.False(r.Open);
        c.AnswerRevise(r,"eins zwei drei vier",10);   // the pause lies after "vier"
        var r2=c.NextRevise(10,p,minNewSeconds:0)!;
        c.AnswerRevise(r2,"fünf sechs sieben acht",10.1);
        Assert.Equal("eins zwei drei vier fünf sechs sieben acht",All(c));
    }
    [Fact]
    public void A_chunk_moves_the_anchor_and_the_last_chunk_takes_out_leftovers()
    {
        var c=new LiveCaption();
        c.AddWindow("wir haben decision Models.ai. heute",6,6);
        c.Settle("Wir haben decisionmodels.io heute",6,final:true);
        Assert.Equal("Wir haben decisionmodels.io heute",All(c));
        Assert.Equal(6,c.AnchorAudio);
        var d=new LiveCaption();
        d.AddWindow("erster Teil hier und zweiter Teil",8,6);
        d.Settle("Erster Teil hier",3.5);
        Assert.Equal(3.5,d.AnchorAudio);Assert.Equal(3,d.Confirmed);Assert.True(d.Dirty);
        var r=d.NextRevise(8,null,minNewSeconds:0)!;Assert.Equal(3.5,r.FromAudio);Assert.True(r.Open);
    }
    [Fact]
    public void Without_a_pause_for_too_long_the_span_is_cut()
    {
        var c=new LiveCaption();
        for(int i=1;i<=6;i++)c.AddWindow($"wort{i}a wort{i}b wort{i}c wort{i}d",i*6,6);
        var r=c.NextRevise(36,Pauses(36),maxSeconds:25)!;
        Assert.False(r.Open);Assert.True(r.ToAudio-r.FromAudio<=25.01);
    }
    [Fact]
    public void Pause_tracker_finds_the_latest_pause_and_an_ongoing_one()
    {
        var p=Pauses(10,(2,3),(5,5.2),(7,8));
        Assert.Equal(7.25,p.LastCut(0,9)!.Value,1);
        Assert.Equal(2.25,p.LastCut(0,6)!.Value,1);   // the 0.2 s gap is no pause
        Assert.Null(p.LastCut(3,6));
        var q=Pauses(5,(4,5));   // silent since 4 s and still silent
        Assert.Equal(4.25,q.LastCut(0,5)!.Value,1);
    }
    [Fact]
    public void Policy_sends_on_a_beat_while_speaking_closes_at_pauses_and_once_for_the_tail()
    {
        var pol=new RevisePolicy();var c=Heard(("wir haben das Problem gesehen",4));var none=Pauses(4);
        var r=pol.Next(c,1000,4,true,none);Assert.NotNull(r);Assert.True(r!.Open);
        Assert.Null(pol.Next(c,1500,4.5,true,none));   // in flight
        c.AnswerRevise(r,"wir haben das Problem gesehen",4.6);pol.Answered(true,1600);
        c.AddWindow("Problem gesehen und dann",6,6);
        Assert.Null(pol.Next(c,3000,6,true,Pauses(6)));   // the beat: 3.5 s
        var tail=pol.Next(c,4500,6.5,false,Pauses(6.5));Assert.NotNull(tail);   // he stopped: the tail at once
        c.AnswerRevise(tail!,"Problem gesehen und dann",6.6);pol.Answered(true,4700);
        Assert.Null(pol.Next(c,9000,9,false,Pauses(9)));   // nothing new: no call in silence
        c.AddWindow("und dann noch etwas",12,6);
        var close=pol.Next(c,10000,12,true,Pauses(12,(6.5,8.0)));   // a pause 4 s after the anchor, 2.5 s old: closed at once
        Assert.NotNull(close);Assert.False(close!.Open);
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
