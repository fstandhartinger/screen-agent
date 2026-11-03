using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
// v0.1.7 fix-app: behaviour in Core, and source scans for what only the Windows app can do (the scans keep the wiring from eroding; the Windows selftests run it for real).
public class FixAppTests
{
    const int R=Wav.Rate;
    static readonly string Root=FindRoot();
    static string FindRoot()
    {
        for(var d=new DirectoryInfo(AppContext.BaseDirectory);d is not null;d=d.Parent)if(Directory.Exists(Path.Combine(d.FullName,"src","ScreenAgent")))return d.FullName;
        throw new DirectoryNotFoundException("repo root not found");
    }
    static string Src(string rel)=>File.ReadAllText(Path.Combine(Root,rel.Replace('/',Path.DirectorySeparatorChar)));
    static short[] Tone(double sec,double amp=3000){var s=new short[(int)(sec*R)];for(int i=0;i<s.Length;i++)s[i]=(short)(amp*Math.Sin(2*Math.PI*220*i/R));return s;}
    static Chunk C(int i,short[] s,bool final=true)=>new(i,s,final,0);
    static readonly Func<TimeSpan,CancellationToken,Task> NoWait=(_,_)=>Task.CompletedTask;
    static DictationJob Job(ChunkSender send,List<string>? ev=null)=>new("abc","",send,(t,_)=>ev?.Add(t),CancellationToken.None,NoWait);
    static JsonElement Json(string s)=>JsonDocument.Parse(s).RootElement;

    // ---- M6: an empty or malformed 2xx is not a success
    [Fact] public void ATwoHundredWithoutTextIsABadReplyNotASuccess()
    {
        foreach(var body in new[]{(JsonElement?)null,Json("{\"ok\":true}"),Json("[1]"),Json("{\"text\":5}")})
        {
            var r=ChunkReplyParse.From(200,body,null);
            Assert.Equal(-2,r.Status);Assert.Equal("bad-reply",r.Code);Assert.False(r.Ok);Assert.True(r.Retryable);
        }
        var good=ChunkReplyParse.From(200,Json("{\"text\":\"hallo\",\"providers\":{\"groq\":{\"ok\":true}}}"),null);
        Assert.True(good.Ok);Assert.Equal("hallo",good.Text);Assert.Equal(["groq:ok"],good.Providers!);
        Assert.Equal(503,ChunkReplyParse.From(503,null,"x").Status);   // a refusal keeps its own status
    }
    [Fact] public async Task AnEmptyAnswerForSpeechIsAFailedChunkAndIsAskedOnceMore()
    {
        int calls=0;var ev=new List<string>();
        var job=Job((r,_)=>{calls++;return Task.FromResult(new ChunkReply(200,""));},ev);
        var o=await job.Complete(C(0,Tone(5)),()=>"Live-Text");
        Assert.Equal(2,calls);Assert.Equal(1,o.Failed);Assert.False(o.Complete);Assert.Equal(-2,o.FailedStatus);Assert.Equal("Live-Text",o.Text);Assert.True(o.FromFallback);
        Assert.Equal(2,ev.Count(e=>e=="dictation.chunk"));
    }
    [Fact] public async Task AnEmptyAnswerForRoomNoiseOrAShortSnippetIsFine()
    {
        var job=Job((_,_)=>Task.FromResult(new ChunkReply(200,"")));
        var o=await job.Complete(C(0,Tone(5,amp:400)));   // quiet: no speech by the gate
        Assert.True(o.Complete);Assert.Equal("",o.Text);
        var o2=await Job((_,_)=>Task.FromResult(new ChunkReply(200,""))).Complete(C(0,Tone(1.5)));   // loud but shorter than 2 s
        Assert.True(o2.Complete);
    }
    [Fact] public void TheSpeechGateNeedsMoreThanTwoSecondsAndAClearPeak()
    {
        Assert.True(DictationGate.HadSpeech(C(0,Tone(3))));
        Assert.False(DictationGate.HadSpeech(C(0,Tone(2))));
        Assert.False(DictationGate.HadSpeech(C(0,Tone(10,amp:300))));
    }

    // ---- M5: every dictation has its own live text
    [Fact] public void TheLiveTextOfOneDictationNeverContainsAnotherOnes()
    {
        var first=new DictationCaptions();var second=new DictationCaptions();
        first.Add("eins zwei drei");int mark=first.Mark();
        first.End(1000,4000);
        second.Add("ganz anderer Text");   // the next dictation (or the talk with the agent) goes to its own object
        Assert.Equal("eins zwei drei",first.Slice(0,int.MaxValue));
        Assert.Equal("ganz anderer Text",second.Text);
        Assert.Equal("eins zwei drei",first.Slice(0,mark));
    }
    [Fact] public void AnswersAreAcceptedForTheLiveDictationAndForAShortGraceAfterItEnded()
    {
        var d=new DictationCaptions();
        Assert.True(d.Accepts(long.MaxValue-1));   // live: always
        d.End(10_000,4_000);
        Assert.True(d.Accepts(13_999));Assert.False(d.Accepts(14_001));
    }

    [Fact] public void TheVoiceSessionStitcherIsBoundedAndADictationsIsNot()
    {
        var bounded=new CaptionStitcher(60,100);var all=new CaptionStitcher();
        for(int i=0;i<50;i++){string w=string.Join(' ',Enumerable.Range(i*10,10).Select(n=>"w"+n));bounded.Add(w);all.Add(w);}
        Assert.True(bounded.Count<=100);Assert.EndsWith("w499",bounded.Text);Assert.Equal(500,all.Count);
    }

    // ---- S1: audio that never arrived is noticed
    [Fact] public void GapWatchReportsWhatIsMissingOnlyWhenItGrows()
    {
        var g=new GapWatch();
        Assert.Null(g.Check(1.0,0.9));    // normal latency
        Assert.Null(g.Check(2.0,1.8));
        Assert.Equal(1.5,g.Check(5.0,3.5)!.Value,3);   // 1.5 s never arrived
        Assert.Null(g.Check(6.0,4.5));                  // the same 1.5 s, not reported again
        Assert.NotNull(g.Check(9.0,6.5));               // 1 more
    }

    // ---- M3/M4: the text is never put anywhere behind his back
    [Fact] public void ARetryNeverTypesAndAFailedTypingNeverEndsAsDone()
    {
        Assert.Equal(DeliveryKind.Typed,DictationDelivery.Decide(false,true,true,false));
        Assert.Equal(DeliveryKind.Undelivered,DictationDelivery.Decide(false,true,false,false));   // target changed
        Assert.Equal(DeliveryKind.Undelivered,DictationDelivery.Decide(false,true,null,false));
        Assert.Equal(DeliveryKind.Undelivered,DictationDelivery.Decide(true,true,true,false));     // retry: even if somebody typed, it is shown
        Assert.Equal(DeliveryKind.Undelivered,DictationDelivery.Decide(true,true,null,false));
        Assert.Equal(DeliveryKind.Empty,DictationDelivery.Decide(false,false,null,false));
        Assert.Equal(DeliveryKind.CaptureFailed,DictationDelivery.Decide(false,true,true,true));
        Assert.Equal(DeliveryKind.CaptureFailed,DictationDelivery.Decide(false,false,null,true));
    }
    [Fact] public void AnUndeliveredDictationStaysFailedWithItsTextUntilTheWindowIsClosed()
    {
        var m=new DictationMeta{Id="a",State="processing"};var t0=DateTimeOffset.UtcNow;
        DictationDelivery.MarkUndelivered(m,"der ganze Text",t0);
        Assert.Equal("failed",m.State);Assert.True(m.Pending);Assert.Equal("undelivered",m.Stage);Assert.Equal("der ganze Text",m.PartialText);Assert.True(DictationDelivery.ShowStoredText(m));
        DictationDelivery.MarkDone(m,t0.AddMinutes(5));
        Assert.Equal("done",m.State);Assert.Null(m.PartialText);Assert.Null(m.Stage);Assert.False(DictationDelivery.ShowStoredText(m));
    }
    [Fact] public void ACaptureFailureIsFailedWithStageCaptureAndKeepsNoText()
    {
        var m=new DictationMeta{Id="b",State="processing"};
        DictationDelivery.MarkCaptureFailed(m,"Mikrofon getrennt bei 0:42 · der Rest fehlt",DateTimeOffset.UtcNow);
        Assert.Equal("failed",m.State);Assert.Equal("capture",m.Stage);Assert.Contains("0:42",m.Reason);Assert.True(m.Pending);
    }
    [Fact] public void TheClipboardIsOnlyUsedForAPasteThatPutsHisOwnBack()
    {
        string inj=Src("src/ScreenAgent/TextInjector.cs");
        Assert.Equal(2,Regex.Matches(inj,@"SetTemporary\(").Count);   // its definition and the paste
        foreach(var line in inj.Split('\n').Where(l=>l.Contains("Refused(")&&!l.Contains("static void")))Assert.DoesNotContain("SetTemporary",line);
        Assert.DoesNotContain("Clipboard",Src("src/ScreenAgent/DictationManager.cs"));
        string app=Src("src/ScreenAgent/AppController.cs");
        Assert.DoesNotContain("InjectRetry",app);
        Assert.Equal(2,Regex.Matches(app,@"TextInjector\.Bind\(\)").Count);   // the key-down binding and InsertHere (a click): nothing binds by itself for a retry
    }
    [Fact] public void AClipboardAnotherProcessHoldsFallsBackToTypingNotToAnException()
    {
        string inj=Src("src/ScreenAgent/TextInjector.cs");
        Assert.Contains("COMException",inj);Assert.Matches(@"if\(clipboard&&await Paste\(text\)\)return true;",inj);
    }
    [Fact] public void TheRetryShowsTheStoredTextInsteadOfTranscribingAgain()
    {
        string man=Src("src/ScreenAgent/DictationManager.cs");
        Assert.Matches(@"DictationDelivery\.ShowStoredText\(m\)\)\{ShowText",man);
        Assert.Contains("In aktuelles Feld einfügen",Src("src/ScreenAgent/DictationWindow.cs"));
        Assert.Matches(@"Finish\(m,o,m\.Process,m\.PartialText\?\?"""",null,true\)",man);   // inject = null: a retry has nothing to type with
    }

    // ---- M2: nothing in the delivery path may throw away the text
    [Fact] public void DeliveryIsWrappedAndTheInjectorCallIsGuarded()
    {
        string man=Src("src/ScreenAgent/DictationManager.cs");
        Assert.Single(Regex.Matches(man,@"await inject\("));   // only inside TryInject
        Assert.Matches(@"try\{return await inject\(text\);\}\s*catch\(Exception ex\)\{Log\(""dictation\.fail"",Redact\.Of\(ex,new\(\)\{\[""id""\]=m\.Id,\[""stage""\]=""inject""",man);
        Assert.Matches(@"try\{await DeliverCore\(flow,inject\);\}\s*catch\(Exception ex\)",man);
    }

    // ---- M1 / S1 / S3: the capture
    [Fact] public void ALostMicrophoneIsReportedReopenedAndShownInTheStatus()
    {
        string cap=Src("src/ScreenAgent/DictationCapture.cs"),man=Src("src/ScreenAgent/DictationManager.cs");
        Assert.Contains("Lost?.Invoke(",cap);Assert.Contains("public bool TryRestart()",cap);
        Assert.Contains("capture.Lost+=",man);Assert.Contains("TryRestart()",man);Assert.Contains("Mikrofon getrennt",man);
        Assert.Contains("DictationWindow.ShowNotice(",man);   // stays until closed
        Assert.Contains("DictationDelivery.MarkCaptureFailed(",man);
    }
    [Fact] public void TheCaptureCallbackOnlyCopiesIntoAQueueAndTheShutdownDoesNotWaitOnTheUiThread()
    {
        string cap=Src("src/ScreenAgent/DictationCapture.cs"),man=Src("src/ScreenAgent/DictationManager.cs");
        int cb=cap.IndexOf("c.DataAvailable+=",StringComparison.Ordinal),end=cap.IndexOf("c.RecordingStopped+=",StringComparison.Ordinal);
        string callback=cap[cb..end];
        Assert.DoesNotContain("Recording.Append",callback);Assert.DoesNotContain("Block?.Invoke",callback);Assert.Contains("queue.Writer.TryWrite",callback);
        Assert.Contains("BoundedChannelFullMode.DropWrite",cap);
        Assert.DoesNotContain(".Wait(2000)",man);Assert.Contains("f.Capture.Halt()",man);
    }

    // ---- S2: audio that only reached memory
    [Fact] public void MemoryOnlyAudioIsWrittenToAFileAtFailTimeAndReadFromThere()
    {
        string blocker=Path.Combine(Directory.CreateTempSubdirectory().FullName,"blocker");File.WriteAllText(blocker,"x");
        var store=new DictationStore(Path.Combine(blocker,"sub"));var ev=new List<string>();
        using var rec=new DictationRecording(store,"notepad",(t,_)=>ev.Add(t));
        var s=Tone(2);rec.Append(System.Runtime.InteropServices.MemoryMarshal.AsBytes<short>(s));rec.Stop();
        Assert.False(rec.DiskOk);
        string tmp=Path.Combine(Directory.CreateTempSubdirectory().FullName,"out");
        Assert.Null(rec.DumpMemory(Path.Combine(blocker,"nope")));   // a folder that refuses: memory stays
        Assert.Equal(2,rec.Blocks().Sum(b=>b.Length)/R);
        string? path=rec.DumpMemory(Path.Combine(blocker,"nope"),tmp);
        Assert.NotNull(path);Assert.True(File.Exists(path));Assert.Contains(path!,rec.Meta.Paths);
        Assert.Equal(2,rec.Blocks().Sum(b=>b.Length)/R);   // now from the file, memory emptied: not twice
        Assert.Equal(s,Wav.ReadBlocks(path!).SelectMany(b=>b).ToArray());
        Assert.Null(rec.DumpMemory(tmp));
    }

    // ---- S4: no window that a screenshot could catch
    [Fact] public void OverlaysAreNotShownWithoutConfirmedExclusion()
    {
        Assert.Contains("GetWindowDisplayAffinity",Src("src/ScreenAgent/Overlays.cs"));
        Assert.Matches(@"if\(!AffinityOk\)\{OverlayStyle\.RefusedOnce\(""caption""\);return;\}",Src("src/ScreenAgent/CaptionOverlay.cs"));
        Assert.Matches(@"requireExclusion&&!CaptureExcluded",Src("src/ScreenAgent/ActivityOverlay.cs"));
        Assert.Contains("capture.exclusion-failed",Src("src/ScreenAgent/Overlays.cs"));
        Assert.Matches(@"!AffinityOk&&box\.Visibility==Visibility\.Visible",Src("src/ScreenAgent/DictationWindow.cs"));
    }

    // ---- S5/S6: the subtitle microphone
    [Fact] public void ATapThatFailsToOpenIsNotRetriedEveryTick()
    {
        string host=Src("src/ScreenAgent/CaptionHost.cs");
        int a=host.IndexOf("void StartTap()",StringComparison.Ordinal),b=host.IndexOf("void StopTap()",StringComparison.Ordinal);
        string start=host[a..b];
        Assert.Contains("tapRetryAt=Environment.TickCount64+30000",start);Assert.DoesNotContain("policy.NewSession()",start);
        Assert.Contains("Environment.TickCount64>=tapRetryAt",host);
    }
    [Fact] public void AMuteInThePageStopsTheTapQuickly()
    {
        Assert.Contains("case \"mic-state\"",Src("src/ScreenAgent/AgentBridge.cs"));
        string app=Src("src/ScreenAgent/AppController.cs");
        Assert.Contains("bridge.MicState+=",app);Assert.Contains("captions.TapRunning?250:1000",app);
        Assert.Equal("dictation+voice",new AppConfig().Captions);   // still the default he asked for
    }

    // ---- S7, S8, S17, S18: AppController wiring
    [Fact] public void TheFrameDoesNotWaitForTheFeedbackWindow()
    {
        string app=Src("src/ScreenAgent/AppController.cs");
        int g=app.IndexOf("async Task GiveFeedback",StringComparison.Ordinal);string fn=app[g..app.IndexOf("// `ids` in the gateway's reply",g,StringComparison.Ordinal)];
        Assert.Matches(@"Make\(shot[\s\S]*bridge\.Dispatcher\.BeginInvoke[\s\S]*ScreenFlash\.Show",fn);
    }
    [Fact] public void AThrowInCaptionsOrActivityDoesNotStarveTheTick()
    {
        string app=Src("src/ScreenAgent/AppController.cs");
        Assert.Matches(@"try\{captions\.Update\(audio!=null,bridge\.VoiceOn,micMuted\);\}\s*catch\(Exception ex\)\{TickFailed\(""captions"",ex\);\}",app);
        Assert.Matches(@"try\{activity\?\.Tick\(\);\}\s*catch\(Exception ex\)\{TickFailed\(""activity"",ex\);\}",app);
        Assert.Contains("\"tick.error\"",app);
    }
    [Fact] public void ADeadHookStopsTheDictationOnceAndPauseSaysItStillDelivers()
    {
        string app=Src("src/ScreenAgent/AppController.cs");
        Assert.Matches(@"if\(!hookDeadHandled&&\(ids\.Length>0\|\|audio!=null\)\)\{hookDeadHandled=true;Reset\(""error""\);\}",app);
        Assert.Contains("hookDeadHandled=false;",app);
        Assert.Contains("Pausiert · das Diktat wird noch eingefügt",app);
    }
    [Fact] public void ShutdownFlushesAfterTheDictationAndTheCaptionsAreGone()
    {
        string app=Src("src/ScreenAgent/AppController.cs");string d=app[app.IndexOf("public void Dispose()",StringComparison.Ordinal)..];
        int dict=d.IndexOf("dictation.Dispose()",StringComparison.Ordinal),caps=d.IndexOf("captions.Dispose()",StringComparison.Ordinal),flush=d.IndexOf("telemetry.Flush(",StringComparison.Ordinal),dispose=d.IndexOf("telemetry.Dispose()",StringComparison.Ordinal);
        Assert.True(dict>0&&caps>dict&&flush>caps&&dispose>flush,$"{dict} {caps} {flush} {dispose}");
    }

    // ---- S9: a prompt that dies still ends its row
    [Fact] public void AnInterruptedPromptEndsTheLocalRowAsFailed()
    {
        var e=ActivityLocal.FromTrace("main.prompt.done",new Dictionary<string,object?>{["code"]=PromptCodes.Interrupted,["chars"]=0});
        Assert.NotNull(e);Assert.Equal("fail",e!.Phase);
        Assert.Matches(@"Log\(""main\.prompt\.done"",new\(\)\{\[""code""\]=PromptCodes\.Interrupted",Src("src/ScreenAgent/PromptChannel.cs"));
    }

    // ---- S10/S11: the drawing panel
    [Fact] public void AMinimisedPanelStaysMinimisedAcrossTheEndOfASession()
    {
        var p=new CanvasPolicy();
        Assert.Equal(PanelAct.ShowFront,p.Pending().Act);p.Opened("first");p.Minimized();
        Assert.False(p.HideAtSessionEnd);   // the panel leaves the window alone
        p.SessionStarted();   // next session
        Assert.Equal(PanelAct.None,p.Pending().Act);Assert.Equal("minimized",p.Pending().Why);
        p.Restored();Assert.True(p.HideAtSessionEnd);
        Assert.Contains("!policy.HideAtSessionEnd",Src("src/ScreenAgent/CanvasPanel.cs"));
    }
    [Fact] public void AFailedFirstOpenGivesTheFrontBackSoTheNextDiagramTriesAgain()
    {
        var p=new CanvasPolicy();
        var before=p.State;bool front=p.FrontDone;
        Assert.Equal("first",p.Pending().Why);p.Opened("first");
        Assert.Equal("open",p.Pending().Why);   // as it would block every retry
        p.OpenFailed(before,front);
        Assert.Equal(PanelAct.ShowFront,p.Pending().Act);Assert.Equal("first",p.Pending().Why);
        Assert.Matches(@"policy\.OpenFailed\(stateBefore,frontBefore\)",Src("src/ScreenAgent/CanvasPanel.cs"));
    }

    // ---- S12: a recovered recording is announced before it can be swept
    [Fact] public void ARecoveredRecordingIsNotSweptTheMomentItIsRecovered()
    {
        string dir=Path.Combine(Directory.CreateTempSubdirectory().FullName,"dictation");
        var now=DateTimeOffset.Now;var store=new DictationStore(dir,()=>now);
        var m=store.Begin("notepad");m.Started=now.AddDays(-30).ToUniversalTime();var path=store.NextFile(m);store.Save(m);
        var w=new WavWriter(path,60);w.Append(Wav.FromSamples(Tone(3))[Wav.HeaderBytes..]);w.Dispose();File.SetLastWriteTimeUtc(path,now.AddDays(-30).UtcDateTime);
        string orphan=Path.Combine(dir,"20261003-101010-deadbeef.wav");File.WriteAllBytes(orphan,Wav.FromSamples(Tone(2)));File.SetLastWriteTimeUtc(orphan,now.AddDays(-30).UtcDateTime);
        var rep=store.Recover();
        Assert.Equal(2,rep.Failed);
        Assert.Empty(store.Sweep(RetentionOptions.Default));
        Assert.Equal(2,store.Pending().Count);
        Assert.All(store.Pending(),p=>Assert.True((now-p.Ended).TotalMinutes<1));
        // two weeks later it is old enough
        var later=new DictationStore(dir,()=>now.AddDays(15));Assert.Equal(2,later.Sweep(RetentionOptions.Default).Count);
        Assert.DoesNotContain("Sweep(",Regex.Match(Src("src/ScreenAgent/DictationManager.cs"),@"public void Startup\(\)[\s\S]*?\n    \}").Value.Replace("void Sweep","").Replace("Store.Sweep(config","X"));
    }

    // ---- S13: the briefing's screen text
    [Fact] public void ScreenTextIsOnlyKeptForAPictureThatIsStillCurrentAndTheStartBriefingWaitsForIt()
    {
        string app=Src("src/ScreenAgent/AppController.cs");
        int run=app.IndexOf("async Task RunOcr",StringComparison.Ordinal);string fn=app[run..];
        Assert.True(fn.IndexOf("ocrGrid=null;return;}",StringComparison.Ordinal)<fn.IndexOf("briefing.NoteScreenText(found.Text)",StringComparison.Ordinal));
        string col=Src("src/ScreenAgent/BriefingCollector.cs");
        Assert.Contains("ScreenTextNow() is null",col);Assert.Matches(@"waited<1500",col);
    }

    // ---- S14: the secret filter
    [Theory]
    [InlineData("key sk_"+"live_51Hq8aBcDeFgHiJkLmNoPqRs ok","live_51Hq8")]
    [InlineData("rk_"+"live_abcdefghijklmnop1234 x","live_abcdef")]
    [InlineData("AI"+"zaSyA-1234567890abcdefghijklmnopqrstuv end","zaSyA-12345")]
    [InlineData("token h"+"f_AbCdEfGhIjKlMnOpQrStUvWx","f_AbCdEf")]
    [InlineData("bot 123456789:"+"testtesttesttesttesttesttesttesttes end","testtesttesttesttesttesttesttesttes")]
    [InlineData("Authorization: Basic dXNlcjpwYXNzd29yZDEyMzQ=","dXNlcjpwYXNzd29yZDEyMzQ")]
    [InlineData("-----BEGIN RSA "+"PRIVATE KEY-----\nMIIEowIBAAKCAQEA1234\nabcd\n-----END RSA "+"PRIVATE KEY----- tail","MIIEowIBAAKCAQEA1234")]
    [InlineData("the password is hunter2xyz, ok","hunter2xyz")]
    [InlineData("blob AbCdEfGhIjKlMnOpQrStUvWx0123456789 end","AbCdEfGhIjKlMnOpQrStUvWx0123456789")]   // 34 characters, mixed
    public void TheSecretFilterCatchesTheKeysHeIsLikelyToHave(string input,string secret)
    {
        string r=SecretFilter.Redact(input);
        Assert.DoesNotContain(secret,r);Assert.Contains("[redacted]",r);
    }
    [Theory]
    [InlineData("C:\\Users\\flori\\Documents\\notes")]
    [InlineData("password reset page for the shop")]
    [InlineData("basic information about the project")]
    [InlineData("https://github.com/fstandhartinger/screen-agent/pull/12")]
    public void OrdinaryTextStaysAsItIs(string text)=>Assert.Equal(text,SecretFilter.Redact(text));

    // ---- S15: a stuck source is not started twice
    [Fact] public void AStuckSourceIsBusyUntilItReturnsAndNoSecondThreadIsStarted()
    {
        var flight=new Timeboxed.SingleFlight();var release=new ManualResetEventSlim();int started=0;
        Part<List<BriefingWindow>> Windows(CancellationToken _)=>new(new List<BriefingWindow>());
        Part<List<TabInfo>> Tabs(CancellationToken _)=>new(new List<TabInfo>());
        Part<MainSession> Stuck(CancellationToken _){Interlocked.Increment(ref started);release.Wait(10_000);return new(null,"main-session:path-missing");}
        var first=BriefingGather.Run(Windows,Tabs,Stuck,300,flight);
        Assert.Contains("main-session:timeout",first.Missing);
        var second=BriefingGather.Run(Windows,Tabs,Stuck,300,flight);
        Assert.Contains("main-session:busy",second.Missing);Assert.Equal(1,started);
        release.Set();SpinWait.SpinUntil(()=>!flight.IsRunning("main"),3000);
        var third=BriefingGather.Run(Windows,Tabs,_=>new(null,"main-session:path-missing"),300,flight);
        Assert.DoesNotContain("main-session:busy",third.Missing);
    }
    [Fact] public void ARefusedRefreshStillGetsAnAnswerWithItsCommandId()
    {
        string col=Src("src/ScreenAgent/BriefingCollector.cs");
        Assert.Matches(@"if\(commandId is not null\)\{try\{await Post\(sid,BriefingPayload\.Build\(\[\],\[\],null,null,commandId\)",col);
    }

    // ---- S16: the newest session is found from the newest folders, and a clock that runs out keeps what was found
    [Fact] public void OnlyTheNewestProjectFoldersAreListedAndACancelledSearchDoesNotThrow()
    {
        string home=Directory.CreateTempSubdirectory().FullName;var now=DateTime.UtcNow;
        string projects=Path.Combine(home,".claude-A","projects");
        for(int i=0;i<12;i++)
        {
            string d=Path.Combine(projects,"p"+i);Directory.CreateDirectory(d);
            string f=Path.Combine(d,"s.jsonl");File.WriteAllText(f,"x\n");
            File.SetLastWriteTimeUtc(f,now.AddMinutes(-i*10));Directory.SetLastWriteTimeUtc(d,now.AddMinutes(-i*10));   // p0 newest
        }
        // an old folder whose file is (oddly) the newest: with 12 folders and a limit of 8 it is not reached
        string odd=Path.Combine(projects,"p11","s.jsonl");File.SetLastWriteTimeUtc(odd,now.AddMinutes(+5));Directory.SetLastWriteTimeUtc(Path.Combine(projects,"p11"),now.AddDays(-30));
        var hit=MainSessionSource.Newest(home,CancellationToken.None);
        Assert.NotNull(hit);Assert.EndsWith(Path.Combine("p0","s.jsonl"),hit!.Value.File);
        using var cts=new CancellationTokenSource();cts.Cancel();
        Assert.Null(MainSessionSource.Newest(home,cts.Token));   // nothing found yet, and no exception
    }
}
