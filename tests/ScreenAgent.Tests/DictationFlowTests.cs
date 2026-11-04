using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
public class DictationFlowTests
{
    const int R=Wav.Rate;
    static Chunk C(int i,double sec=1,int overlap=0,bool final=false)=>new(i,new short[(int)(sec*R)],final,overlap);
    static readonly Func<TimeSpan,CancellationToken,Task> NoWait=(_,_)=>Task.CompletedTask;
    sealed class Script(params Func<ChunkRequest,int,ChunkReply>[] steps)
    {
        public readonly List<ChunkRequest> Seen=[];public readonly List<(int Index,int Attempt)> Calls=[];readonly Dictionary<int,int> tries=[];
        public Task<ChunkReply> Send(ChunkRequest r,CancellationToken ct)
        {
            Seen.Add(r);tries[r.Chunk.Index]=tries.GetValueOrDefault(r.Chunk.Index)+1;Calls.Add((r.Chunk.Index,tries[r.Chunk.Index]));
            return Task.FromResult(steps[Math.Min(Calls.Count-1,steps.Length-1)](r,tries[r.Chunk.Index]));
        }
    }
    static ChunkReply Ok(string t)=>new(200,t,["groq:ok"],Seconds:1,Ms:300);
    static DictationJob Job(Script s,List<string>? events=null,string field="")=>new("abc",field,s.Send,(t,_)=>events?.Add(t),CancellationToken.None,NoWait){LivePatience=TimeSpan.FromMilliseconds(50),StopPatience=TimeSpan.FromMilliseconds(50)};

    [Fact] public async Task ASingleChunkIsOneRequestWithoutChunkField()
    {
        var s=new Script((_,_)=>Ok("Hallo Welt"));var job=Job(s);
        var o=await job.Complete(C(0,3,final:true));
        Assert.Equal("Hallo Welt",o.Text);Assert.True(o.Complete);Assert.Single(s.Seen);Assert.True(s.Seen[0].Only);
    }
    [Fact] public async Task ChunksGoOneAtATimeInOrderAndCarryTheTailOfThePreviousText()
    {
        var s=new Script((r,_)=>Ok(r.Chunk.Index switch{0=>"eins zwei",1=>"drei vier",_=>"fünf"}));var job=Job(s,field:"Vorher:");
        job.Enqueue(C(0,25));job.Enqueue(C(1,25));
        var o=await job.Complete(C(2,5,final:true));
        Assert.Equal("eins zwei drei vier fünf",o.Text);Assert.Equal(3,o.Chunks);
        Assert.Equal(new[]{"Vorher:","Vorher: eins zwei","Vorher: eins zwei drei vier"},s.Seen.Select(r=>r.FieldBefore));
        Assert.All(s.Seen,r=>Assert.False(r.Only));
    }
    [Fact] public async Task TimeoutsAnd503AreRetriedTwiceThenItWorks()
    {
        var s=new Script((_,a)=>a<3?new ChunkReply(a==1?503:-1,Failure:"x"):Ok("geschafft"));var ev=new List<string>();
        var o=await Job(s,ev).Complete(C(0,2,final:true));
        Assert.Equal("geschafft",o.Text);Assert.Equal(3,s.Calls.Count);Assert.Equal(3,ev.Count(e=>e=="dictation.chunk"));
    }
    [Fact] public async Task AFailedChunkUsesTheLiveTextAndTheOthersCarryOn()
    {
        var s=new Script((r,_)=>r.Chunk.Index==1?new ChunkReply(503):Ok(r.Chunk.Index==0?"erster Teil":"letzter Teil"));
        var job=Job(s);job.Enqueue(C(0,25));job.Enqueue(C(1,25),()=>"Teil aus Live-Text");
        var o=await job.Complete(C(2,3,final:true));
        Assert.Equal("erster Teil Teil aus Live-Text letzter Teil",o.Text);Assert.Equal(1,o.Failed);Assert.True(o.FromFallback);Assert.False(o.Complete);Assert.Equal(503,o.FailedStatus);
        Assert.Equal(3,s.Calls.Count(c=>c.Index==1));   // 1 + 2 retries
    }
    [Fact] public async Task ClientErrorsAreNotRetried()
    {
        var s=new Script((_,_)=>new ChunkReply(413,Code:"too-long"));
        var o=await Job(s).Complete(C(0,2,final:true));
        Assert.Single(s.Calls);Assert.Equal(1,o.Failed);Assert.Equal("Aufnahme für eine Anfrage zu lang",o.FailReason);Assert.Equal("",o.Text);
    }
    [Fact] public async Task ALostConnectionWaitsAndContinuesWithoutUsingRetries()
    {
        var s=new Script((_,_)=>new ChunkReply(0,Failure:"HttpRequestException"),(_,_)=>new ChunkReply(0),(_,_)=>new ChunkReply(0),(_,_)=>new ChunkReply(0),(_,_)=>new ChunkReply(0),(_,_)=>Ok("wieder da"));
        var o=await Job(s).Complete(C(0,2,final:true));
        Assert.Equal("wieder da",o.Text);Assert.Equal(6,s.Calls.Count);
    }
    [Fact] public async Task ALostConnectionGivesUpAfterThePatienceAndKeepsGoing()
    {
        var s=new Script((r,_)=>r.Chunk.Index==0?new ChunkReply(0):Ok("zweiter"));
        var job=Job(s);job.Enqueue(C(0,25));
        var o=await job.Complete(C(1,3,final:true));
        Assert.Equal(1,o.Failed);Assert.Equal("Keine Verbindung zum Desk",o.FailReason);Assert.Equal("zweiter",o.Text);
    }
    [Fact] public async Task HardCutOverlapIsRemovedFromTheJoinedText()
    {
        var s=new Script((r,_)=>Ok(r.Chunk.Index==0?"wir treffen uns um zehn":"um zehn Uhr im Büro"));var job=Job(s);
        job.Enqueue(C(0,55));
        var o=await job.Complete(C(1,10,overlap:R/2,final:true));
        Assert.Equal("wir treffen uns um zehn Uhr im Büro",o.Text);
    }
    [Fact] public async Task CancelEndsTheJobWithAReason()
    {
        using var cts=new CancellationTokenSource();
        var job=new DictationJob("x","",(_,ct)=>Task.Delay(Timeout.Infinite,ct).ContinueWith(_=>Ok("")),(_,_)=>{},cts.Token,NoWait);
        job.Enqueue(C(0));cts.Cancel();
        var o=await job.Complete(null);Assert.False(o.Complete);Assert.Equal("Abgebrochen",o.FailReason);
    }

    static (DictationStore Store,string Dir,List<string> Events) Env()
    {
        string dir=Path.Combine(Directory.CreateTempSubdirectory().FullName,"dictation");return (new DictationStore(dir),dir,[]);
    }
    static void Log(List<string> ev,string t,Dictionary<string,object?> _)=>ev.Add(t);
    static byte[] Pcm(double sec,short v=1000){var s=new short[(int)(sec*R)];Array.Fill(s,v);return Wav.FromSamples(s)[Wav.HeaderBytes..];}
    [Fact] public void RecordingWritesFromTheFirstSampleAndRollsOverAtTheLimit()
    {
        var (store,dir,ev)=Env();var warnings=new List<string>();
        using var rec=new DictationRecording(store,"notepad",(t,d)=>Log(ev,t,d),maxMinutes:2.0/60,headerFixSeconds:0.5);   // 2 s files
        rec.Warning+=warnings.Add;
        Assert.Single(Directory.GetFiles(dir,"*.wav"));Assert.Single(Directory.GetFiles(dir,"*.json"));
        rec.Append(Pcm(1.5));Assert.True(Wav.ReadBlocks(Path.Combine(dir,rec.Meta.Files[0])).Sum(b=>b.Length)>=R);   // already on disk, while the writer still has the file open
        rec.Append(Pcm(2));rec.Append(Pcm(1));
        Assert.Equal(2,rec.Meta.Files.Count);Assert.Contains(rec.Meta.Files[1],f=>true);Assert.Contains("-p2.wav",rec.Meta.Files[1]);
        Assert.Single(warnings);Assert.Contains("dictation.limit",ev);
        rec.Stop();
        Assert.Equal(4.5,rec.Blocks().Sum(b=>b.Length)/(double)R);Assert.Equal("processing",rec.Meta.State);Assert.Equal(4.5,rec.Seconds);
    }
    [Fact] public void ADiskThatRefusesKeepsRecordingInMemoryAndSaysSo()
    {
        string blocker=Path.Combine(Directory.CreateTempSubdirectory().FullName,"blocker");File.WriteAllText(blocker,"x");
        var store=new DictationStore(Path.Combine(blocker,"sub"));var ev=new List<string>();var warnings=new List<string>();
        using var rec=new DictationRecording(store,"notepad",(t,d)=>Log(ev,t,d));rec.Warning+=warnings.Add;
        rec.Append(Pcm(2));rec.Stop();
        Assert.False(rec.DiskOk);Assert.Contains("dictation.disk",ev);Assert.Contains("Festplatte",rec.StartWarning);Assert.Equal(2,rec.Blocks().Sum(b=>b.Length)/R);Assert.False(rec.Meta.DiskOk);
    }
    [Fact] public void RecordingHandsOutChunksWhileItRuns()
    {
        var (store,_,ev)=Env();var got=new List<Chunk>();using var rec=new DictationRecording(store,"code",(t,d)=>Log(ev,t,d));rec.ChunkReady+=got.Add;
        var loud=new short[R];Array.Fill(loud,(short)3000);for(int i=0;i<60;i++)rec.Append(System.Runtime.InteropServices.MemoryMarshal.AsBytes<short>(loud));
        Assert.Single(got);Assert.Equal(55*R,got[0].Samples.Length);Assert.Equal(R/2,rec.Stop()!.OverlapSamples);
    }
    [Fact] public void RecoverRepairsARecordingThatNeverEndedAndAdoptsAnOrphan()
    {
        var (store,dir,_)=Env();
        var m=store.Begin("notepad");var path=store.NextFile(m);store.Save(m);   // state: recording
        var w=new WavWriter(path,60);w.Append(Pcm(3));File.Copy(path,path+".c");w.Dispose();File.Delete(path);File.Move(path+".c",path);   // crash copy: header says 0
        string orphan=Path.Combine(dir,"20261103-101010-deadbeef.wav");File.WriteAllBytes(orphan,Wav.FromPcm(Pcm(2)));
        var tiny=Path.Combine(dir,"20261103-101011-cafe0000.wav");File.WriteAllBytes(tiny,Wav.FromPcm(Pcm(0.1)));
        var rep=store.Recover();
        Assert.Equal(2,rep.Failed);Assert.Equal(1,rep.Orphans);Assert.Equal(1,rep.Dropped);Assert.False(File.Exists(tiny));
        var pending=store.Pending();Assert.Equal(2,pending.Count);
        var recovered=pending.Single(p=>p.Id==m.Id);Assert.Equal(3,recovered.Seconds,1);Assert.Equal("failed",recovered.State);Assert.Contains("beendet",recovered.Reason);
        Assert.Equal("deadbeef",pending.Single(p=>p.Id!=m.Id).Id);
        Assert.Equal(0,store.Recover().Failed);   // idempotent
    }
    static DictationRetention.Entry E(string id,string state,double hoursOld,long mb,DateTimeOffset now)=>new(id,state,now.AddHours(-hoursOld),mb<<20);
    [Fact] public void RetentionKeepsSuccess24hFailed14dAndNeverDeletesAYoungFailure()
    {
        var now=DateTimeOffset.UtcNow;var o=RetentionOptions.Default;
        var drop=DictationRetention.Plan([E("ok-old","done",25,1,now),E("ok-new","done",23,1,now),E("bad-old","failed",14*24+1,1,now),E("bad-young","failed",13*24,1,now),E("disc","discarded",30,1,now),E("run","recording",500,1,now)],now,o);
        Assert.Equal(new[]{"bad-old","disc","ok-old"},drop.OrderBy(x=>x));
    }
    [Fact] public void RetentionCapRemovesTheOldestSuccessfulFirstAndSparesYoungFailures()
    {
        var now=DateTimeOffset.UtcNow;var o=RetentionOptions.Default with{CapBytes=1000L<<20};
        var drop=DictationRetention.Plan([E("f",   "failed",1,600,now),E("a","done",5,300,now),E("b","done",3,300,now),E("c","done",1,300,now)],now,o);
        Assert.Equal(new[]{"a","b"},drop);   // 1500 MB, cap 1000: a -> 1200 still over, b -> 900; the young failure is never in the plan
    }
    [Fact] public void RetentionCapNeverTouchesFailedEvenWhenOverTheCap()
    {
        var now=DateTimeOffset.UtcNow;var o=RetentionOptions.Default with{CapBytes=100L<<20};
        var drop=DictationRetention.Plan([E("f1","failed",1,600,now),E("f2","failed",2,600,now),E("a","done",5,300,now)],now,o);
        Assert.Equal(new[]{"a"},drop);
    }
    [Fact] public void SweepDeletesFilesAndSidecar()
    {
        var (store,dir,_)=Env();Directory.CreateDirectory(dir);var m=store.Begin("x");File.WriteAllBytes(store.NextFile(m),Wav.FromPcm(Pcm(1)));m.State="done";m.Ended=DateTimeOffset.UtcNow.AddHours(-30);store.Save(m);
        var young=store.Begin("y");File.WriteAllBytes(store.NextFile(young),Wav.FromPcm(Pcm(1)));young.State="done";young.Ended=DateTimeOffset.UtcNow;store.Save(young);
        var gone=store.Sweep(RetentionOptions.Default);
        Assert.Equal(new[]{m.Id},gone);Assert.Single(Directory.GetFiles(dir,"*.wav"));Assert.Single(Directory.GetFiles(dir,"*.json"));
    }

    [Fact] public void CaptionsOnlyWhenSpeechAndOneInFlightAndOutOfOrderAnswersDropped()
    {
        var p=new CaptionPolicy();long t=10000;
        Assert.False(p.ShouldSend(t));                 // nobody spoke: silence costs nothing
        p.Heard(t,1000);Assert.True(p.ShouldSend(t));
        int a=p.Begin(t);Assert.False(p.ShouldSend(t+2000));   // one in flight
        Assert.True(p.Answered(a,true,200,t+800));
        p.Heard(t+2000,1000);Assert.False(p.ShouldSend(t+1000));Assert.True(p.ShouldSend(t+1600));
        p.Heard(t+4000,0);p.Heard(t+7000,10);Assert.False(p.ShouldSend(t+9000));   // 2 s after the last speech
        // out of order: ids are handed out in order, the older answer arrives after the newer one
        var q=new CaptionPolicy();q.Heard(0,1000);int first=q.Begin(0);q.Answered(first,true,200,10);q.Heard(2000,1000);int second=q.Begin(2000);
        Assert.True(q.Answered(second,true,200,2100));Assert.Equal(0,q.Dropped);
        Assert.False(q.Answered(first,true,200,2200));Assert.Equal(1,q.Dropped);
    }
    [Fact] public void ThreeFailuresPauseFifteenSecondsAndOnlyNoKeyOrBudgetEndTheSession()
    {
        var p=new CaptionPolicy();long t=0;
        for(int i=0;i<3;i++){t+=2000;p.Heard(t,1000);Assert.True(p.ShouldSend(t));p.Answered(p.Begin(t),false,503,t+10,"mai-down");}
        Assert.False(p.Disabled);p.Heard(t+2000,1000);Assert.False(p.ShouldSend(t+2000));
        p.Heard(t+16000,1000);Assert.True(p.ShouldSend(t+16000));
        int id=p.Begin(t+16000);Assert.False(p.Answered(id,false,503,t+16100,"no-key"));Assert.True(p.Disabled);Assert.Equal("no-key",p.DisabledWhy);
        var q=new CaptionPolicy();q.Answered(q.Begin(0),false,429,10,"caption-budget");Assert.True(q.Disabled);
        p.NewSession();Assert.False(p.Disabled);
    }
    [Theory]
    [InlineData("Klicke auf diesen Details-Link.",true)]
    [InlineData("Klick diesen blauen Button",true)]
    [InlineData("öffne diese Seite",true)]
    [InlineData("hier klicken",true)]
    [InlineData("Klick auf den Link",false)]
    [InlineData("Diese Hierarchie ist gut",false)]
    public void HisPointingCountsAsHere(string goal,bool here)=>Assert.Equal(here,ComputerScope.MentionsHere(goal));
    [Fact] public void TheWaveformRingKeepsTheNewestLevelsAndCutsAudioIntoBars()
    {
        var r=new LevelRing(4);foreach(var v in new double[]{1,2,3,4,5,6})r.Add(v);
        Assert.Equal(new double[]{4,5,6},r.Last(3));Assert.Equal(new double[]{3,4,5,6},r.Last(9));
        var bars=new LevelRing();bars.AddPcm(new byte[LevelRing.SliceBytes*3]);Assert.Equal(3,bars.Last(10).Length);
        Assert.Equal(4,LevelRing.BarHeight(0,4,30));Assert.Equal(30,LevelRing.BarHeight(60000,4,30));Assert.InRange(LevelRing.BarHeight(1500,4,30),15,20);
    }
    [Fact] public void DictionaryEntriesAndHistoryLines()
    {
        var e=DictionaryEntry.Parse("{\"items\":[{\"term\":\"Jev\",\"replace_from\":[\"Jeff\"],\"source\":\"wispr\"},{\"term\":\"Reax\"},{\"x\":1}]}");
        Assert.Equal(2,e.Length);Assert.Equal(new[]{"Jeff"},e[0].ReplaceFrom);Assert.Equal("wispr",e[0].Source);Assert.Empty(e[1].ReplaceFrom);
        Assert.Equal(new[]{"Karpati","Karpaty"},DictionaryEntry.SplitForms(" Karpati, Karpaty,,Karpati "));
        var done=new DictationMeta{Id="a",State="done",Seconds=63,Started=DateTimeOffset.Now,Text="Fertiger Text"};
        var dropped=new DictationMeta{Id="b",State="discarded",Started=DateTimeOffset.Now.AddMinutes(1)};
        var failed=new DictationMeta{Id="c",State="failed",Started=DateTimeOffset.Now.AddMinutes(-1),PartialText="Teil"};
        var recent=DictationHistory.Recent([failed,done,dropped]);
        Assert.Equal(new[]{"a","c"},recent.Select(m=>m.Id));
        Assert.Equal("Fertiger Text",DictationHistory.TextOf(done));Assert.Equal("Teil",DictationHistory.TextOf(failed));
        Assert.Contains("1:03 · Fertiger Text",DictationHistory.Line(done));
    }
    [Fact] public void CaptionStatsReportCostPerSpeechHourWithoutText()
    {
        var p=new CaptionPolicy();var s=new CaptionStats();s.Ok(6,400,0.001);s.Ok(6,600,0.001);s.SpeechSeconds=120;
        var d=s.Snapshot(p);Assert.Equal(2,d["calls"]);Assert.Equal(500L,d["avgMs"]);Assert.Equal(0.002,(double)d["costUsd"]!,6);Assert.Equal(0.06,(double)d["usdPerSpeechHour"]!,3);
        Assert.DoesNotContain(d.Keys,k=>k.Contains("text",StringComparison.OrdinalIgnoreCase));
    }
    [Fact] public void TraceQueueKeeps1000For24hAndSurvivesARestart()
    {
        string path=Path.Combine(Directory.CreateTempSubdirectory().FullName,"q.jsonl");long now=1_800_000_000_000;
        var q=new TraceQueue(path,clock:()=>now);
        for(int i=0;i<1005;i++)q.Add("e",new(){["i"]=i,["ok"]=true,["why"]="x"},now-1000);
        Assert.Equal(1000,q.Count);Assert.Equal(5,q.Dropped);
        Assert.Equal(1000,q.Peek(5000).Count(e=>e.type=="e"));
        var aged=new TraceQueue(path+"2",clock:()=>now);aged.Add("old",null,now-25*3600_000L);aged.Add("new",null,now-1000);Assert.Equal("new",Assert.Single(aged.Peek(10)).type);
        var first=q.Peek(100);Assert.Equal(100,first.Count);q.Remove(100);q.Save();
        var again=new TraceQueue(path,clock:()=>now);again.Load();
        Assert.Equal(900,again.Count);var e0=again.Peek(1)[0];Assert.Equal(105L,Convert.ToInt64(e0.data!["i"]));Assert.Equal(true,e0.data["ok"]);Assert.Equal("x",e0.data["why"]);
        now+=24*3600_000L;Assert.Empty(again.Peek(10));   // a day later they are gone
    }
}
public class DictationConfigTests
{
    [Fact] public void DefaultsAndOverridesSurviveTheHotkeyMigrationCopy()
    {
        var c=AppConfig.Parse("{}");Assert.Equal(60,c.DictationMaxMinutes);Assert.Equal("dictation+voice",c.Captions);Assert.Equal(2048L<<20,c.Retention().CapBytes);
        var m=AppConfig.Parse("{\"Hotkeys\":{\"session\":\"Ctrl+Win+Shift\",\"dictation\":\"Alt+Win\"},\"DictationMaxMinutes\":5,\"DictationKeepHours\":2,\"DictationKeepFailedDays\":3,\"DictationCapMb\":100,\"Captions\":\"off\"}");
        Assert.Equal(5,m.DictationMaxMinutes);Assert.Equal("off",m.Captions);Assert.Equal(TimeSpan.FromHours(2),m.Retention().Success);Assert.Equal(TimeSpan.FromDays(3),m.Retention().Failed);Assert.Equal(100L<<20,m.Retention().CapBytes);
    }
    [Theory][InlineData("{\"Captions\":\"always\"}")][InlineData("{\"DictationCapMb\":1}")][InlineData("{\"DictationKeepFailedDays\":0}")]
    public void InvalidSettingsStopStartup(string json)=>Assert.Throws<InvalidDataException>(()=>AppConfig.Parse(json));
}
