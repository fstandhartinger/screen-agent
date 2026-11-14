using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
public class LiveCaptionTests
{
    static string Shown(LiveCaption c)=>string.Join(' ',c.Display());
    [Fact]
    public void A_window_heard_a_little_differently_is_placed_not_appended()
    {
        // 0.1.15: no exact run of two among the window's first four words -> the whole window was appended (doubled words)
        var old=new CaptionStitcher();
        old.Add("wir haben das Problem mit den Untertiteln gesehen");
        old.Add("Problemen mit dem Untertitel gesehen und dann");
        Assert.Contains("gesehen Problemen",old.Text);
        var c=new LiveCaption();
        c.AddWindow("wir haben das Problem mit den Untertiteln gesehen",6,6);
        c.AddWindow("Problemen mit dem Untertitel gesehen und dann",7.5,6);
        Assert.Equal("wir haben das Problem mit dem Untertitel gesehen und dann",Shown(c));   // the edge word keeps the reading that stood
        Assert.Equal(0,Disfluency.Repeats(c.Display()));
    }
    [Fact]
    public void A_window_that_starts_with_a_cut_word_is_placed()
    {
        var c=new LiveCaption();
        c.AddWindow("ich möchte dass die Diktatfunktion",4,4);
        c.AddWindow("chte dass die Diktatfunktion besser wird und schneller",7,6);
        Assert.Equal("ich möchte dass die Diktatfunktion besser wird und schneller",Shown(c));
    }
    [Fact]
    public void Words_written_as_one_or_two_still_align()
    {
        var c=new LiveCaption();
        c.AddWindow("wir nutzen die Diktat Funktion jeden Tag",6,6);
        c.AddWindow("die Diktatfunktion jeden Tag und sie",7.5,6);
        Assert.Equal(1,c.Display().Count(w=>StitchWords.Key(w).StartsWith("diktat")));
        Assert.EndsWith("jeden Tag und sie",Shown(c));
    }
    [Fact]
    public void After_a_pause_longer_than_a_window_the_window_is_added()
    {
        var c=new LiveCaption();
        c.AddWindow("das ist der erste Satz",5,5);
        c.AddWindow("das ist der zweite Satz",14,6);   // no audio in common
        Assert.Equal("das ist der erste Satz das ist der zweite Satz",Shown(c));
        Assert.Equal(1,c.Appended);
    }
    [Fact]
    public void Words_two_windows_agree_on_are_settled_and_kept()
    {
        var c=new LiveCaption();
        c.AddWindow("heute wollen wir die neue Version testen",6,6);
        c.AddWindow("wir die neue Version testen und danach",7.5,6);
        // "heute wollen" stand before the window; "wir die neue Version testen" both heard alike (not the window's last two words)
        Assert.True(c.Committed>=7,$"committed {c.Committed}");
        c.AddWindow("Version testen und danach veröffentlichen",9,6);
        Assert.Equal("heute wollen wir die neue Version testen und danach veröffentlichen",Shown(c));
    }
    [Fact]
    public void A_settled_word_is_not_rewritten_by_a_later_window()
    {
        var c=new LiveCaption();
        c.AddWindow("wir schauen uns die JevBench Ergebnisse an",6,6);
        c.AddWindow("uns die JevBench Ergebnisse an und dann",7.5,6);
        c.AddWindow("die Jeff Bench Ergebnisse an und dann weiter",9,6);
        Assert.Contains("JevBench",Shown(c));
        Assert.DoesNotContain("Jeff",Shown(c));
    }
    [Fact]
    public void The_clean_up_answer_lands_although_a_window_came_in_between()
    {
        var c=new LiveCaption();
        c.AddWindow("wir haben die Jeff Bench Ergebnisse noch mal angeschaut",6,6);
        c.AddWindow("die Jeff Bench Ergebnisse noch mal angeschaut und dann",7.5,6);
        var req=c.NextPolish();
        Assert.NotNull(req);
        Assert.Contains("Jeff Bench",req!.Text);
        // 0.1.15 threw the answer away when any word of the polished range changed; now the range is settled and only the tail moves
        c.AddWindow("noch mal angeschaut und dann gesehen dass",9,6);
        Assert.True(c.AnswerPolish(req,req.Text.Replace("Jeff Bench","JevBench")));
        Assert.Contains("JevBench Ergebnisse",Shown(c));
        Assert.EndsWith("gesehen dass",Shown(c));
        Assert.Null(c.NextPolish());   // nothing new settled since
    }
    [Fact]
    public void The_clean_up_request_carries_the_words_before_as_context()
    {
        var c=new LiveCaption();
        c.AddWindow("eins zwei drei vier fünf sechs sieben acht",6,6);
        c.AddWindow("vier fünf sechs sieben acht neun zehn",7.5,6);
        var r1=c.NextPolish();Assert.NotNull(r1);c.AnswerPolish(r1!,r1!.Text);
        c.AddWindow("acht neun zehn elf zwölf dreizehn vierzehn",9,6);
        c.AddWindow("zwölf dreizehn vierzehn fünfzehn sechzehn",10.5,6);
        var r2=c.NextPolish(maxWordsPer:6);
        Assert.NotNull(r2);
        Assert.StartsWith("neun",r2!.Text);
        Assert.EndsWith("acht",r2.Before);
        // with the default the words cleaned before are sent again, for context
        c.AnswerPolish(r2,null);
        c.AddWindow("vierzehn fünfzehn sechzehn siebzehn achtzehn neunzehn",12,6);
        c.AddWindow("siebzehn achtzehn neunzehn zwanzig einundzwanzig",13.5,6);
        var r3=c.NextPolish();
        Assert.NotNull(r3);
        Assert.StartsWith("eins",r3!.Text);
    }
    [Fact]
    public void A_clean_up_that_lost_his_words_is_not_used()
    {
        var c=new LiveCaption();
        c.AddWindow("das hier ist ein ziemlich langer Satz mit vielen Wörtern",6,6);
        c.AddWindow("ziemlich langer Satz mit vielen Wörtern und mehr",7.5,6);
        var r=c.NextPolish()!;
        Assert.False(c.AnswerPolish(r,"Satz."));
        Assert.Contains("ziemlich langer Satz",Shown(c));
    }
    [Fact]
    public void A_transcribed_chunk_replaces_the_live_words_it_covers()
    {
        var c=new LiveCaption();
        c.AddWindow("ich habe die Jeff Bench Zahlen angeschaut",6,6);
        c.AddWindow("Jeff Bench Zahlen angeschaut und die neuen",7.5,6);
        Assert.Equal(1,c.Settle("Ich habe die JevBench-Zahlen angeschaut"));
        Assert.Equal("Ich habe die JevBench-Zahlen angeschaut und die neuen",Shown(c));
        Assert.Equal(5,c.Settled);
        // the next window does not bring the old reading back
        c.AddWindow("angeschaut und die neuen Modelle",9,6);
        Assert.Equal("Ich habe die JevBench-Zahlen angeschaut und die neuen Modelle",Shown(c));
    }
    [Fact]
    public void Stutters_fillers_and_cut_word_starts_leave_the_display()
    {
        string Clean(string s)=>string.Join(' ',Disfluency.Clean(StitchWords.Split(s),out _));
        Assert.Equal("ich möchte das",Clean("ich ich möchte das"));
        Assert.Equal("Ich möchte das",Clean("Ich ich möchte das"));
        Assert.Equal("wir haben das gemacht",Clean("wir haben wir haben das gemacht"));
        Assert.Equal("dass das Diktat",Clean("dass dass das Diktat"));
        Assert.Equal("die Diktatfunktion",Clean("die Dik- Diktatfunktion"));
        Assert.Equal("das ist gut",Clean("das äh ist ähm gut"));
        Assert.Equal("die Leute, die die Regeln kennen",Clean("die Leute, die die Regeln kennen"));
        Assert.Equal("Gesamt- Kontext",Clean("Gesamt- Kontext"));
        Assert.Equal("das ist sehr, sehr wichtig",Clean("das ist sehr, sehr wichtig"));
        Assert.Equal("Der Caption-Text springt",Clean("Der Cap- der Caption-Text springt"));
        Assert.Equal("Die Diktatfunktion im",Clean("Die dick Die Diktatfunktion im"));
        Assert.Equal("Ich möchte, dass das letzte Wort",Clean("Ich möchte, dass das das letzte Wort"));
        Assert.Equal("viele Frauen die die Kinder",Clean("viele Frauen die die Kinder"));
        Assert.Equal("der Hund der der Katze folgt",Clean("der Hund der der Katze folgt"));
        Assert.Equal("ich gehe in die Stadt",Clean("ich gehe in die Stadt"));
        Assert.Equal("die Nummer ist 11 11",Clean("die Nummer ist 11 11"));
        Assert.Equal("wenn das der Fall ist, ist es gut",Clean("wenn das der Fall ist, ist es gut"));
        Assert.Equal("Ich möchte",Clean("Ich, ich möchte"));
        // words from a transcribed chunk stay as they will be typed; a repeat across the border loses the live copy
        Assert.Equal("der neue Worker, der neue Worker für",string.Join(' ',Disfluency.Clean(StitchWords.Split("der neue Worker, der neue Worker für"),out _,6)));
        Assert.Equal("wir haben das",string.Join(' ',Disfluency.Clean(StitchWords.Split("wir haben wir haben das"),out _,2)));
    }
    [Fact]
    public void Repeats_are_counted_without_doubled_articles()
    {
        Assert.Equal(2,Disfluency.Repeats(StitchWords.Split("ich ich möchte, dass wir haben wir haben")));
        Assert.Equal(0,Disfluency.Repeats(StitchWords.Split("die Leute, die die Regeln kennen")));
    }
    [Fact]
    public void The_log_counts_words_that_changed_after_they_were_shown()
    {
        var log=new CaptionLog();
        Assert.NotNull(log.Update(0,'w',StitchWords.Split("wir haben die Jeff")));
        Assert.Null(log.Update(500,'w',StitchWords.Split("wir haben die Jeff")));
        log.Update(1500,'w',StitchWords.Split("wir haben die Jeff Bench Zahlen"));
        Assert.Equal(0,log.Changed);   // nothing was taken back
        log.Update(4000,'p',StitchWords.Split("wir haben die JevBench Zahlen"));
        Assert.Equal(2,log.Changed);
        Assert.Equal(2,log.ChangedBy['p']);
        Assert.Equal(1,log.ChangedLate);   // "Jeff" was on screen 4 s, "Bench" 2.5 s
        var items=log.Take()!;
        Assert.Equal(3,items.Count);
        Assert.Equal("4000 p3 JevBench Zahlen",items[2]);
        Assert.Null(log.Take());
    }
    [Fact]
    public void The_log_compares_the_live_text_with_the_typed_text()
    {
        var (missing,extra,words,ex)=CaptionLog.Compare("wir haben die Jeff Bench Zahlen gesehen","Wir haben die JevBench-Zahlen gesehen.");
        Assert.Equal(3,missing);Assert.Equal(1,extra);Assert.Equal(5,words);
        Assert.Single(ex);
        Assert.Contains("[Jeff Bench Zahlen]",ex[0]);
        Assert.Contains("[JevBench-Zahlen]",ex[0]);
    }
    [Fact]
    public void The_summary_fits_the_desk_event_size_and_the_texts_go_in_parts()
    {
        var d=new DictationCaptions("ab12cd34",0);
        string longText=string.Join(' ',Enumerable.Range(0,1500).Select(i=>"wort"+i));
        d.Live.AddWindow(longText,6,6);d.Log.Update(10,'w',d.Live.Display());
        var data=CaptionSummary.Build(d,longText+" ende",1000,out var live);
        CaptionSummary.AddComparison(data,(string)data["__live"]!,longText+" ende");data.Remove("__live");
        Assert.True(System.Text.Json.JsonSerializer.Serialize(TraceSanitizer.CleanText(data)).Length<7000);
        Assert.Equal(1,data["extraInTyped"]);
        var parts=CaptionSummary.Texts(d.Id,live,longText);
        Assert.All(parts,p=>Assert.True(System.Text.Json.JsonSerializer.Serialize(TraceSanitizer.CleanText(p.Data)).Length<4000));
        Assert.Equal(longText,string.Concat(parts.Where(p=>p.Type=="caption.typed").Select(p=>(string)p.Data["text"]!)));
    }
    [Fact]
    public void The_refinement_keep_copies_the_audio_and_writes_the_log()
    {
        string root=Path.Combine(Path.GetTempPath(),"capref-"+Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var m=new DictationMeta{Id="ab12cd34",Stamp="20261114-150000",Folder=root,Files=["20261114-150000-ab12cd34.wav"]};
            File.WriteAllBytes(Path.Combine(root,m.Files[0]),Wav.FromPcm(new byte[3200]));
            var store=new RefinementStore(Path.Combine(root,"refinement"));
            string dir=store.Save(m,[(1500,6.0,"hallo welt")],[new CaptionLog.Item(1500,'w',0,"hallo welt")],"Hallo Welt.",new(){["changed"]=0},DateTimeOffset.UtcNow);
            Assert.True(File.Exists(Path.Combine(dir,"audio.wav")));
            Assert.Contains("hallo welt",File.ReadAllText(Path.Combine(dir,"caption.json")));
            Assert.Equal(1,new RefinementStore(Path.Combine(root,"refinement"),TimeSpan.Zero).Sweep(DateTimeOffset.UtcNow.AddMinutes(1)));
        }
        finally{try{Directory.Delete(root,true);}catch(IOException){}}
    }
    [Fact]
    public void Review_findings_14_Nov()
    {
        string Clean(string t)=>string.Join(' ',Disfluency.Clean(StitchWords.Split(t),out _));
        // a sentence end is never a stutter
        foreach(var keep in new[]{"Ich mache das. Das dauert nicht lange.","Wir brauchen mehr Zeit. Zeit ist knapp.","Ich bin fertig. Fertig.","das ist sehr sehr gut","Ja ja genau","Er sagte nein nein nein",
            "Sie kennen die Frauen die Frauenärzte behandeln","Wir brauchen ein Konzept ein Konzeptpapier"})
            Assert.Equal(keep,Clean(keep));
        // the first seconds: growing windows from the start of the audio never double a word
        var c=new LiveCaption();c.AddWindow("Hallo Welt",1.5,1.5);c.AddWindow("Hallo Wald heute machen wir",3,3);
        Assert.Equal("Hallo Wald heute machen wir",string.Join(' ',c.Display()));
        var c2=new LiveCaption();c2.AddWindow("ich möchte",1.5,1.5);c2.AddWindow("ach möchte heute etwas sagen",3,3);
        Assert.Equal("ach möchte heute etwas sagen",string.Join(' ',c2.Display()));
        // a chunk that adds one word does not throw the newer live words away
        var c3=new LiveCaption();
        var all=Enumerable.Range(0,120).Select(i=>"w"+i).ToArray();
        c3.AddWindow(string.Join(' ',all),60,6);
        Assert.Equal(1,c3.Settle(string.Join(' ',all.Take(60))));
        c3.Settle(string.Join(' ',all.Take(61)));
        Assert.Equal(120,c3.Words.Count);
        // a window of punctuation only is nothing
        var c4=new LiveCaption();c4.AddWindow("eins zwei drei",3,3);c4.AddWindow("...",4.5,4.5);
        Assert.Equal("eins zwei drei",string.Join(' ',c4.Display()));
        // an invented window is not settled: the next window still corrects what came before
        var c5=new LiveCaption();
        c5.AddWindow("wir schauen uns die Jeff Bench Ergebnisse an",6,6);
        c5.AddWindow("Vielen Dank.",7.5,6);
        Assert.Equal(0,c5.Committed>8?1:0);
        // a long voice-session text: the overlay's end is cheap and right
        var v=new LiveCaption(4000);
        v.AddWindow(string.Join(' ',Enumerable.Range(0,3000).Select(i=>i%37==0?"ich ich":"w"+i)),6,6);
        var sw=System.Diagnostics.Stopwatch.StartNew();for(int i=0;i<20;i++)v.Last(110);
        Assert.True(sw.ElapsedMilliseconds<500,$"{sw.ElapsedMilliseconds} ms");
        Assert.DoesNotContain("ich ich",v.Last(110));
    }
    [Fact]
    public void Caption_text_survives_the_trace_queue()
    {
        string file=Path.Combine(Path.GetTempPath(),"capref-q-"+Guid.NewGuid().ToString("N")+".jsonl");
        try
        {
            var q=new TraceQueue(file);
            var data=TraceSanitizer.CleanText(new(){["id"]="ab12cd34",["n"]=1,["of"]=1,["text"]=new string('x',3000),["items"]=Enumerable.Range(0,40).Select(i=>"1500 w0 "+new string('y',300)).ToArray()});
            q.AddRaw("caption.typed",data,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var e=q.Peek(1)[0];
            Assert.Equal(3000,((string)e.data!["text"]!).Length);
            Assert.Equal(40,((string[])e.data["items"]!).Length);
        }
        finally{File.Delete(file);}
    }
}
