using ScreenAgent.Core;
namespace ScreenAgent;
// One dictation in flight: the microphone, the recording on disk, the chunk job and where the live captions stood at each cut.
internal sealed class DictationFlow
{
    public required DictationCapture Capture {get;init;}
    public required DictationJob Job {get;init;}
    public required CancellationTokenSource Cts {get;init;}
    public required string Process {get;init;}
    public string FieldBefore {get;init;}="";   // the text before the caret when he started: context for the clean-up
    public readonly List<int> Marks=[0];
    public required DictationCaptions Captions {get;init;}   // the live text of this dictation only
    public Task<DictationOutcome>? Outcome;
    public string? Skipped,StopError,ResultText;
    public double? CaptureLostAt;       // recording seconds when the microphone went away
    public volatile bool CaptureLost;   // ... and was not reopened
    public int Gaps;
    public DictationRecording Rec=>Capture.Recording;
    public DictationMeta Meta=>Capture.Recording.Meta;
}
// Everything between "he pressed the key" and "the text is in his window or he knows why not". AppController only starts, stops and delivers.
// Recording, chunks and retry follow the rules in Core (DictationRecording, DictationJob, DictationStore); this is the glue to the microphone, the Desk, the
// tray, the failure window and the log. A word he said is on disk before anything else happens to it.
internal sealed class DictationManager : IDisposable
{
    readonly AppConfig config;readonly DeviceClient client;readonly Telemetry tel;readonly CaptionHost captions;
    readonly System.Windows.Threading.Dispatcher ui;
    readonly Action<string,Level> notice;readonly Action<string> balloon;
    readonly Func<string,string,DictationContext> context;     // process, text before the caret
    readonly Func<string,Task<bool>> insertHere;                // text into the field that has the focus now, if it is safe: the window's "In aktuelles Feld einfügen"
    readonly CancellationTokenSource life=new();
    readonly System.Threading.Timer sweepTimer;
    readonly List<DictationFlow> active=[];
    readonly List<System.Windows.Forms.ToolStripItem> trayItems=[];
    readonly HashSet<string> retrying=[];
    readonly Dictionary<string,DictationFlow> held=[];   // memory-only audio (the disk refused) of a failed dictation, kept for the retry of this run
    DictationFlow? live;
    public event Action? Started;
    public DictationStore Store {get;}
    public DictationManager(AppConfig config,DeviceClient client,Telemetry tel,CaptionHost captions,System.Windows.Threading.Dispatcher ui,Action<string,Level> notice,Action<string> balloon,
        Func<string,string,DictationContext> context,Func<string,Task<bool>> insertHere,string? folder=null)
    {
        this.config=config;this.client=client;this.tel=tel;this.captions=captions;this.ui=ui;this.notice=notice;this.balloon=balloon;this.context=context;this.insertHere=insertHere;
        Store=new(folder??Path.Combine(ScreenConfig.Folder,"dictation"));
        sweepTimer=new(_=>Sweep(),null,TimeSpan.FromMinutes(10),TimeSpan.FromHours(1));   // never in the start that announces what a crash left
    }
    void Log(string type,Dictionary<string,object?> d)=>tel.Event(type,d);
    public bool Recording=>live is not null;
    int processing;
    // From the moment he lets go until the text is in his field or in front of him. Also a retry.
    public bool Processing=>Volatile.Read(ref processing)>0;
    public string? ProcessingDetail
    {
        get
        {
            DictationFlow? p;lock(active)p=active.FirstOrDefault(a=>a.Outcome is not null);
            return p is {Job:{Enqueued:>1}}?$"{p.Job.Done}/{p.Job.Enqueued}":null;
        }
    }
    public List<DictationMeta> Recent()
    {
        try{return DictationHistory.Recent(Store.All());}
        catch(Exception ex){Log("dictation.fail",Redact.Of(ex,new(){["stage"]="history",["reason"]="list",["path"]=Store.Folder}));return [];}
    }
    public DictationMeta? Latest()=>Recent().FirstOrDefault();
    // A dictation that went well can be transcribed again too (tray, history): the same path as a retry, the result opens in the window with the text.
    public Task Again(DictationMeta m)=>Retry(m,true);
    // The note while he talks and while the chunks are still being transcribed: "Diktat · 3:12 · 4/5 fertig".
    public string? Status
    {
        get
        {
            if(live is {} f)
            {
                var t=TimeSpan.FromSeconds(f.Rec.Seconds);
                return $"Diktat · {(int)t.TotalMinutes}:{t.Seconds:00}"+(f.CaptureLost?" · Mikrofon getrennt":"")+(f.Job.Enqueued>0?$" · {f.Job.Done}/{f.Job.Enqueued} fertig":"");
            }
            var p=active.FirstOrDefault(a=>a.Outcome is not null);
            return p is null?null:$"Diktat wird verarbeitet · {p.Job.Done}/{Math.Max(1,p.Job.Enqueued)} fertig";
        }
    }

    // At app start: what a crash left behind, then the retention sweep. Failed recordings are announced once.
    public void Startup()
    {
        try
        {
            var r=Store.Recover();
            var pending=Store.Pending();
            Log("dictation.recover",new(){["repaired"]=r.Repaired,["failed"]=r.Failed,["orphans"]=r.Orphans,["dropped"]=r.Dropped,["pending"]=pending.Count,["path"]=Store.Folder});
            if(pending.Count>0)balloon(pending.Count==1?$"{pending[0].Label} ist nicht verarbeitet. Tray-Menü → wiederholen.":$"{pending.Count} Diktate sind nicht verarbeitet. Tray-Menü → wiederholen.");
        }
        catch(Exception ex){Log("dictation.fail",Redact.Of(ex,new(){["stage"]="startup",["reason"]="recover",["path"]=Store.Folder}));}
    }
    void Sweep()
    {
        try{var gone=Store.Sweep(config.Retention());if(gone.Count>0)Log("dictation.sweep",new(){["deleted"]=gone.Count,["path"]=Store.Folder});}
        catch(Exception ex){Log("dictation.fail",Redact.Of(ex,new(){["stage"]="sweep",["reason"]="sweep",["path"]=Store.Folder}));}
    }

    // Start recording. Throws (after logging) when the microphone cannot be opened.
    public DictationFlow Start(string process,string fieldBefore,bool inSession)
    {
        var ctx=context(process,"");
        var rec=new DictationRecording(Store,process,Log,config.DictationMaxMinutes);
        var cts=CancellationTokenSource.CreateLinkedTokenSource(life.Token);
        var job=new DictationJob(rec.Meta.Id,fieldBefore,(r,ct)=>client.DictateChunk(r,ctx,ct),Log,cts.Token);
        DictationCapture capture;
        try{capture=new DictationCapture(rec);}
        catch(Exception ex)
        {
            Log("dictation.fail",Redact.Of(ex,new(){["stage"]="start",["reason"]="microphone",["path"]=Store.Folder}));
            cts.Cancel();rec.Dispose();Store.Delete(rec.Meta);cts.Dispose();throw;
        }
        var caps=captions.BeginDictation(rec.Meta.Id);
        job.Joined+=text=>captions.Settle(caps,text);   // what will be typed replaces its part of the subtitle while he still talks
        var flow=new DictationFlow{Capture=capture,Job=job,Cts=cts,Process=process,Captions=caps,FieldBefore=fieldBefore};
        rec.Warning+=w=>ui.BeginInvoke(new System.Action(()=>notice(w,Level.Warn)));
        rec.ChunkReady+=chunk=>
        {
            int to=caps.Mark();int from=flow.Marks[^1];flow.Marks.Add(to);
            job.Enqueue(chunk,()=>caps.Slice(from,to));   // this dictation's own live text, never the next one's or the voice session's
            Log("dictation.cut",new(){["id"]=rec.Meta.Id,["index"]=chunk.Index,["seconds"]=Math.Round(chunk.Seconds,1),["overlap"]=chunk.OverlapSamples>0});
        };
        capture.Block+=captions.Feed;
        capture.Gap+=(missing,at)=>{flow.Gaps++;Log("dictation.gap",new(){["id"]=rec.Meta.Id,["missingSeconds"]=Math.Round(missing,1),["at"]=Math.Round(at,1),["dropped"]=capture.DroppedBlocks});};
        capture.Lost+=(type,at)=>OnLost(flow,type,at);
        live=flow;
        Log("dictation.start",new(){["id"]=rec.Meta.Id,["process"]=process,["session"]=inSession,["disk"]=rec.DiskOk,["path"]=Store.Folder});
        Started?.Invoke();
        if(rec.StartWarning is {} w0)notice(w0,Level.Warn);
        return flow;
    }

    // v0.1.12: the whole dictation once more through the clean-up (several chunks: seams, corrections across chunks). Never holds the text back for long and
    // never loses it: a refusal, a timeout or an answer that dropped his words leaves the text as the chunks gave it. The text itself is never logged.
    async Task<DictationOutcome> Polished(Task<DictationOutcome> job,string id,string process,string fieldBefore)
    {
        var o=await job;
        string? mode=DictationPolish.Mode(o,config.DictationPolish,config.DictationFormat);
        if(mode is null)return o;
        var clock=System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var cts=CancellationTokenSource.CreateLinkedTokenSource(life.Token);cts.CancelAfter(DictationPolish.Budget(o.Text));
            var r=await client.Polish(o.Text,mode,process,fieldBefore.Length>500?fieldBefore[^500..]:fieldBefore,cts.Token,config.DictationFormat);
            bool ok=r.Status is >=200 and <300&&DictationPolish.Accept(o.Text,r.Text);
            Log("dictation.polish",new(){["id"]=id,["mode"]=mode,["status"]=r.Status,["code"]=r.Code??"",["ok"]=ok,["changed"]=r.Changed,["breaks"]=ok&&DictationLayout.HasBreaks(r.Text),["ms"]=clock.ElapsedMilliseconds,["chars"]=o.Text.Length});
            return ok?o with{Text=r.Text.Trim()}:o;
        }
        catch(Exception ex){Log("dictation.polish",Redact.Of(ex,new(){["id"]=id,["ok"]=false,["ms"]=clock.ElapsedMilliseconds,["chars"]=o.Text.Length}));return o;}
    }
    static string Clock(double seconds){var t=TimeSpan.FromSeconds(seconds);return $"{(int)t.TotalMinutes}:{t.Seconds:00}";}
    // The microphone went away while he talks (unplugged, switched, driver reset). He is told at once and stays told; the default device is opened again into the same recording.
    void OnLost(DictationFlow flow,string type,double at)
    {
        try
        {
            flow.CaptureLostAt??=at;
            Log("dictation.capture.lost",new(){["id"]=flow.Meta.Id,["exType"]=type,["seconds"]=Math.Round(at,1),["path"]=flow.Meta.Paths.FirstOrDefault()??Store.Folder});
            bool back=flow.Capture.TryRestart();
            flow.CaptureLost=!back;
            Log("dictation.capture.restart",new(){["id"]=flow.Meta.Id,["ok"]=back,["restarts"]=flow.Capture.Restarts});
            string head=back?$"Mikrofon getrennt bei {Clock(at)} · wieder verbunden":$"Mikrofon getrennt bei {Clock(at)} · das Diktat nimmt nichts mehr auf";
            string detail=back?"Was in der Lücke gesagt wurde, fehlt. Alles danach wird aufgenommen.":"Beende das Diktat: der Text bis dahin wird eingefügt.";
            notice(head,Level.Warn);
            _=ui.BeginInvoke(new System.Action(()=>{try{DictationWindow.ShowNotice(head,detail);}catch(Exception ex){Log("dictation.fail",Redact.Of(ex,new(){["id"]=flow.Meta.Id,["stage"]="window",["reason"]="capture-lost-window"}));}}));
        }
        catch(Exception ex){Log("dictation.fail",Redact.Of(ex,new(){["id"]=flow.Meta.Id,["stage"]="capture",["reason"]="lost-handler"}));}
    }

    // Stop the microphone (called in key order, so a second dictation can start at once). The returned task is the delivery, to be queued behind earlier ones.
    public async Task<DictationFlow> Stop(DictationFlow flow)
    {
        Interlocked.Increment(ref processing);
        if(ReferenceEquals(live,flow))live=null;
        try{return await StopCore(flow);}
        catch(Exception ex)
        {
            flow.Cts.Cancel();flow.StopError=ex.GetType().Name;
            try{flow.Capture.Recording.Stop();}catch(Exception inner){Diag.Fail("dictation-recording-stop",inner);}
            Log("dictation.fail",Redact.Of(ex,new(){["id"]=flow.Meta.Id,["stage"]="stop",["reason"]="exception",["path"]=flow.Meta.Paths.FirstOrDefault()??Store.Folder,["seconds"]=Math.Round(flow.Rec.Seconds,1)}));
            return flow;
        }
    }
    async Task<DictationFlow> StopCore(DictationFlow flow)
    {
        var last=await flow.Capture.Finish();
        var rec=flow.Rec;
        captions.EndDictation(flow.Captions);   // answers that are still on their way for the last seconds still go to this dictation
        if(flow.Capture.DroppedBlocks>0)Log("dictation.gap",new(){["id"]=rec.Meta.Id,["dropped"]=flow.Capture.DroppedBlocks,["why"]="queue-full"});
        if(flow.Capture.CaptureError is {} ce)Log("dictation.fail",new(){["id"]=rec.Meta.Id,["stage"]="capture",["reason"]="capture-stopped",["exType"]=ce,["seconds"]=Math.Round(rec.Seconds,1),["path"]=Store.Folder});
        // Nothing worth a request: too short or digital silence. No provider call; logged and a small notice.
        if(flow.Job.Enqueued==0&&DictationGate.Reject(rec.Samples,rec.Peak) is {} why)
        {
            flow.Skipped=why;flow.Cts.Cancel();
            rec.Meta.State="discarded";rec.Meta.Reason=why;rec.Meta.Ended=DateTimeOffset.UtcNow;Save(rec.Meta);
            Log("dictation.skip",new(){["id"]=rec.Meta.Id,["why"]=why,["seconds"]=Math.Round(rec.Seconds,2),["peak"]=rec.Peak,["bytes"]=rec.Samples*2});
            return flow;
        }
        if(last is not null&&flow.Job.Enqueued>0&&DictationGate.RejectTail(last.Samples.Length-last.OverlapSamples,AudioLevel.Peak(last.Samples),rec.Peak) is {} lw)
        {Log("dictation.skip",new(){["id"]=rec.Meta.Id,["why"]="last-"+lw,["index"]=last.Index,["seconds"]=Math.Round(last.Seconds-last.OverlapSamples/(double)Wav.Rate,2),["peak"]=AudioLevel.Peak(last.Samples)});last=null;}
        int from=flow.Marks[^1];
        flow.Outcome=Polished(flow.Job.Complete(last,()=>flow.Captions.Slice(from,int.MaxValue)),rec.Meta.Id,flow.Process,flow.FieldBefore);
        lock(active)active.Add(flow);
        return flow;
    }

    // An explicit discard chord: the words are not delivered, the file is kept for the normal 24 h.
    public async Task Discard(DictationFlow flow,string why)
    {
        if(ReferenceEquals(live,flow))live=null;
        flow.Cts.Cancel();captions.EndDictation(flow.Captions);
        try{await flow.Capture.Finish();}catch(Exception ex){Diag.Fail("dictation-abort-finish",ex);}
        var m=flow.Meta;m.State="discarded";m.Reason=why;m.Ended=DateTimeOffset.UtcNow;m.Seconds=flow.Rec.Seconds;Save(m);
        Log("dictation.discard",new(){["id"]=m.Id,["why"]=why,["seconds"]=Math.Round(m.Seconds,1),["path"]=m.Paths.FirstOrDefault()??Store.Folder});
        flow.Capture.Dispose();
    }

    // The app closes while he is still talking: what is on disk stays, as a failed recording he can retry.
    public void Abort(string reason)
    {
        if(live is not {} f)return;
        live=null;
        try{f.Cts.Cancel();f.Capture.Halt();}catch(Exception ex){Diag.Fail("dictation-abort",ex);}   // Halt does not wait for the UI thread that is running this
        var m=f.Meta;m.State="failed";m.Stage="shutdown";m.Reason=reason;m.Seconds=f.Rec.Seconds;m.Ended=DateTimeOffset.UtcNow;m.PartialText=NullIfEmpty(f.Captions.Text);m.Bytes=Store.BytesOf(m);Save(m);
        Log("dictation.fail",new(){["id"]=m.Id,["stage"]="shutdown",["reason"]=reason,["seconds"]=Math.Round(m.Seconds,1),["path"]=m.Paths.FirstOrDefault()??Store.Folder,["bytes"]=m.Bytes});
        f.Capture.Dispose();
    }

    static string? NullIfEmpty(string? s)=>string.IsNullOrWhiteSpace(s)?null:s;
    void Save(DictationMeta m){try{Store.Save(m);}catch(Exception ex){Log("dictation.fail",Redact.Of(ex,new(){["id"]=m.Id,["stage"]="sidecar",["reason"]="save",["path"]=Store.Folder}));}}

    // Delivery after the job finished (queued in order). inject: the text into the window he dictated into; true = typed or pasted there. Nothing in here may throw or
    // leave the dictation at "processing": whatever goes wrong, the text (or the wav) is in front of him and the sidecar says failed.
    public async Task Deliver(DictationFlow flow,Func<string,Task<bool>> inject)
    {
        try{await DeliverCore(flow,inject);}
        catch(Exception ex)
        {
            try{Fail(flow.Meta,"Fehler bei der Zustellung","deliver",0,ex.GetType().Name,NullIfEmpty(flow.ResultText)??NullIfEmpty(flow.Captions.Text),true,null,ex);}
            catch(Exception inner){Diag.Fail("dictation-deliver-fail",inner);}   // the sidecar of the recording still says what the last save said
            lock(active)active.Remove(flow);
            bool isHeld;lock(held)isHeld=held.ContainsKey(flow.Meta.Id);
            if(!isHeld)try{flow.Capture.Dispose();}catch(Exception disposeEx){Diag.Fail("dictation-deliver-dispose",disposeEx);}
        }
        finally{Interlocked.Decrement(ref processing);}
    }
    async Task DeliverCore(DictationFlow flow,Func<string,Task<bool>> inject)
    {
        var m=flow.Meta;
        if(flow.StopError is {} se){Fail(m,"Fehler beim Beenden der Aufnahme","stop",0,se,null,true);flow.Capture.Dispose();return;}
        if(flow.Skipped is {} skipped)
        {
            notice(skipped=="silent"?"Diktat · nichts aufgenommen (stumm)":"Diktat zu kurz · nichts gesendet",Level.Warn);
            flow.Capture.Dispose();return;
        }
        DictationOutcome o;
        try{o=await flow.Outcome!;}
        catch(Exception ex){Fail(m,"Verarbeitung abgebrochen","deliver",0,ex.GetType().Name,null,false,null,ex);flow.Capture.Dispose();return;}
        finally{lock(active)active.Remove(flow);}
        flow.ResultText=o.Text;
        CaptionDone(flow,o.Text);
        // The disk refused part of it and the transcription failed: what only reached memory goes to a file now, and the memory stays alive for the retry of this run.
        bool keepMemory=!o.Complete&&!flow.Rec.DiskOk;
        if(keepMemory)
        {
            string? dumped=flow.Rec.DumpMemory(Store.Folder,Path.Combine(Path.GetTempPath(),"ScreenAgent-dictation"));
            Log("dictation.dump",new(){["id"]=m.Id,["ok"]=dumped is not null,["path"]=dumped??Store.Folder,["bytes"]=flow.Rec.Samples*2});
            Save(m);
            lock(held)held[m.Id]=flow;
        }
        else flow.Capture.Dispose();
        await Finish(m,o,flow.Process,flow.Captions.Text,inject,false,flow);
    }
    // v0.1.16: the subtitle's log and measures to the Desk (config CaptionLog), and with DictationRefinementKeep the audio and the log on disk for a refinement round.
    void CaptionDone(DictationFlow flow,string typed)
    {
        try
        {
            var summary=captions.Summary(flow.Captions,typed);
            if(!config.DictationRefinementKeep)return;
            var store=new RefinementStore(Path.Combine(ScreenConfig.Folder,"refinement"));
            var (windows,shown)=captions.Snapshot(flow.Captions);
            string dir=store.Save(flow.Meta,windows,shown,typed,summary,DateTimeOffset.UtcNow);
            Log("dictation.refinement.keep",new(){["id"]=flow.Meta.Id,["path"]=dir});
        }
        catch(Exception ex){Log("dictation.fail",Redact.Of(ex,new(){["id"]=flow.Meta.Id,["stage"]="caption",["reason"]="summary"}));}
    }
    void Release(string id)
    {
        DictationFlow? f;lock(held){held.Remove(id,out f);}
        try{f?.Capture.Dispose();}catch(Exception ex){Diag.Fail("dictation-release",ex);}
    }

    // inject is null for a retry: a retry never types by itself (the field in front of him now may be anything).
    async Task Finish(DictationMeta m,DictationOutcome o,string process,string caption,Func<string,Task<bool>>? inject,bool retry,DictationFlow? flow=null)
    {
        string? lost=!retry&&flow is {CaptureLostAt:{} at,CaptureLost:true}?$"Mikrofon getrennt bei {Clock(at)} · der Rest fehlt":null;
        string gap=!retry&&flow is {CaptureLostAt:{} at2,CaptureLost:false}?$" · Lücke bei {Clock(at2)}":"";
        if(o.Text.Length>0)m.Text=o.Text;   // kept for copy and history, also when something else goes wrong below
        if(o.Complete)
        {
            bool shown=retry&&m.Stage=="partial-injected";   // a first, partial text is already in his field: do not type it twice
            bool? injected=null;
            if(!retry&&inject is not null&&o.Text.Length>0)injected=await TryInject(m,inject,o.Text);   // a retry never types by itself
            switch(DictationDelivery.Decide(retry,o.Text.Length>0,injected,lost is not null))
            {
                case DeliveryKind.Undelivered:
                    string head=shown?"Diktat fertig · hier der vollständige Text":retry?"Diktat fertig · Text zum Einfügen":"Diktat fertig · der Text passt nicht mehr in das Feld, in dem du diktiert hast";
                    Undeliver(m,o.Text,head+(lost is null?"":" · "+lost),retry?"retry":"target");
                    return;
                case DeliveryKind.CaptureFailed:
                    if(o.Text.Length>0)notice("Diktat eingefügt"+gap,Level.Active);
                    FailCapture(m,lost!);return;
                case DeliveryKind.Empty:
                    Log("dictation.empty",new(){["id"]=m.Id,["seconds"]=Math.Round(m.Seconds,1),["chunks"]=o.Chunks,["ms"]=o.Ms});notice("Diktat · nichts erkannt",Level.Warn);break;
                default:notice("Diktat eingefügt"+gap,Level.Active);break;
            }
            DictationDelivery.MarkDone(m,DateTimeOffset.UtcNow);Save(m);
            Log("dictation.done",new(){["id"]=m.Id,["seconds"]=Math.Round(m.Seconds,1),["chunks"]=o.Chunks,["ms"]=o.Ms,["chars"]=o.Text.Length,["degraded"]=o.Degraded,["retry"]=retry,["gaps"]=flow?.Gaps??0});
            return;
        }
        // Something failed. Text that exists (answers of the chunks that worked, live captions for the others) goes to him once; the file stays for a retry.
        string partial=o.Text.Length>0?o.Text:caption;
        bool wentIn=false;
        if(!retry&&inject is not null&&o.Text.Length>0)wentIn=await TryInject(m,inject,o.Text);
        if(!retry)m.Stage=wentIn?"partial-injected":"transcribe";
        Fail(m,o.FailReason??"Fehler bei der Verarbeitung","transcribe",o.FailedStatus,"",NullIfEmpty(partial),true,o);
        if(wentIn&&o.FromFallback)notice("Teil aus Live-Text",Level.Warn);
    }
    // The text into his window; an exception (clipboard held by another process, window gone) counts as "not typed", is logged, and the text is shown instead.
    async Task<bool> TryInject(DictationMeta m,Func<string,Task<bool>> inject,string text)
    {
        try{return await inject(text);}
        catch(Exception ex){Log("dictation.fail",Redact.Of(ex,new(){["id"]=m.Id,["stage"]="inject",["reason"]="exception",["chars"]=text.Length}));return false;}
    }
    // The text exists but did not go into a field: it stays in a window until he closes it, in the sidecar until then (state failed, stage undelivered), and on no clipboard.
    void Undeliver(DictationMeta m,string text,string head,string why)
    {
        DictationDelivery.MarkUndelivered(m,text,DateTimeOffset.UtcNow);m.Bytes=Store.BytesOf(m);Save(m);
        Log("dictation.undelivered",new(){["id"]=m.Id,["why"]=why,["chars"]=text.Length,["seconds"]=Math.Round(m.Seconds,1),["path"]=m.Paths.FirstOrDefault()??Store.Folder});
        notice("Diktat fertig · Text im Fenster",Level.Active);
        ShowText(m,text,head);
    }
    void ShowText(DictationMeta m,string text,string head)=>_=ui.BeginInvoke(new System.Action(()=>
    {
        try{DictationWindow.ShowResult(m,head,text,insertHere,()=>Dismiss(m));}
        catch(Exception ex){Log("dictation.fail",Redact.Of(ex,new(){["id"]=m.Id,["stage"]="window",["reason"]="result-window"}));}
    }));
    // He closed the window with the text (or it went into a field): now the dictation counts as delivered and the wav follows the normal retention.
    void Dismiss(DictationMeta m)
    {
        DictationDelivery.MarkDone(m,DateTimeOffset.UtcNow);Save(m);Release(m.Id);
        Log("dictation.done",new(){["id"]=m.Id,["seconds"]=Math.Round(m.Seconds,1),["via"]="window"});
    }
    // The text is in, but the microphone was gone for the rest: failed, with the reason, so it is not mistaken for a complete dictation.
    void FailCapture(DictationMeta m,string reason)
    {
        string path=m.Paths.FirstOrDefault()??Store.Folder;
        DictationDelivery.MarkCaptureFailed(m,reason,DateTimeOffset.UtcNow);m.Bytes=Store.BytesOf(m);Save(m);
        Log("dictation.fail",new(){["id"]=m.Id,["stage"]="capture",["reason"]=reason,["exType"]="CaptureLost",["status"]=0,["path"]=path,["bytes"]=m.Bytes,["seconds"]=Math.Round(m.Seconds,1)});
        notice($"Diktat unvollständig: {reason}",Level.Warn);
        _=ui.BeginInvoke(new System.Action(()=>{try{DictationWindow.ShowFailure(m,reason,path,null,null);}catch(Exception ex){Log("dictation.fail",Redact.Of(ex,new(){["id"]=m.Id,["stage"]="window",["reason"]="failure-window"}));}}));
    }

    void Fail(DictationMeta m,string reason,string stage,int status,string exType,string? partial,bool window,DictationOutcome? o=null,Exception? ex=null)
    {
        string path=m.Paths.FirstOrDefault()??Store.Folder;
        m.State="failed";m.Reason=reason;m.Stage=m.Stage is "partial-injected"?m.Stage:stage;m.Ended=DateTimeOffset.UtcNow;m.PartialText=partial;m.Bytes=Store.BytesOf(m);Save(m);
        Log("dictation.fail",new(){["id"]=m.Id,["stage"]=stage,["reason"]=reason,["exType"]=exType,["status"]=status,["path"]=path,["bytes"]=m.Bytes,["seconds"]=Math.Round(m.Seconds,1),["chunks"]=o?.Chunks,["failedChunks"]=o?.Failed,["msg"]=ex is null?"":Redact.Message(ex.Message)});
        notice($"Diktat nicht verarbeitet: {reason} · {path}",Level.Warn);
        if(window)_=ui.BeginInvoke(new System.Action(()=>
        {
            try{DictationWindow.ShowFailure(m,reason,path,partial,()=>_=Retry(m));}
            catch(Exception ex){Log("dictation.fail",Redact.Of(ex,new(){["id"]=m.Id,["stage"]="window",["reason"]="failure-window"}));}
        }));
    }

    // "Erneut versuchen" and the tray entries: the same chunks and job over the file(s) on disk.
    public Task Retry(DictationMeta m)=>Retry(m,false);
    async Task Retry(DictationMeta m,bool fresh)
    {
        lock(retrying){if(!retrying.Add(m.Id))return;}
        Interlocked.Increment(ref processing);
        try
        {
            m=Store.Load(m.MetaPath)??m;
            // The text is already there, only not in a field: show it again, no new transcription.
            if(!fresh&&DictationDelivery.ShowStoredText(m)){ShowText(m,m.PartialText!,"Diktat fertig · Text zum Einfügen");return;}
            m.Attempts++;m.State="processing";Save(m);
            Log("dictation.retry",new(){["id"]=m.Id,["attempt"]=m.Attempts,["seconds"]=Math.Round(m.Seconds,1),["path"]=m.Paths.FirstOrDefault()??Store.Folder,["bytes"]=m.Bytes});
            notice("Diktat · neuer Versuch",Level.Active);
            var ctx=context(m.Process,"");
            using var cts=CancellationTokenSource.CreateLinkedTokenSource(life.Token);
            var job=new DictationJob(m.Id,"",(r,ct)=>client.DictateChunk(r,ctx,ct),Log,cts.Token);
            var state=new RetryState();
            var last=await Task.Run(()=>Feed(m,job,state));
            if(state.Missing||state.Samples==0){Fail(m,state.Missing?"Die Aufnahmedatei fehlt":"Die Aufnahmedatei ist leer","retry",0,"",m.PartialText,true);return;}
            if(job.Enqueued==0&&DictationGate.Reject(state.Samples,state.Peak) is {} why){m.State="discarded";m.Reason=why;m.Ended=DateTimeOffset.UtcNow;Save(m);notice(why=="silent"?"Diktat · nichts aufgenommen (stumm)":"Diktat zu kurz",Level.Warn);return;}
            var o=await Polished(job.Complete(last),m.Id,m.Process,"");
            await Finish(m,o,m.Process,m.PartialText??"",null,true);
            if(m.State=="done")Release(m.Id);
        }
        catch(Exception ex){Fail(m,"Wiederholung fehlgeschlagen","retry",0,ex.GetType().Name,m.PartialText,true,null,ex);}
        finally{Interlocked.Decrement(ref processing);lock(retrying)retrying.Remove(m.Id);}
    }
    sealed class RetryState{public long Samples;public int Peak;public bool Missing;}
    Chunk? Feed(DictationMeta m,DictationJob job,RetryState st)
    {
        var chunker=new DictationChunker();
        DictationFlow? kept;lock(held)held.TryGetValue(m.Id,out kept);
        IEnumerable<short[]> blocks;
        if(kept is not null)blocks=kept.Rec.Blocks();   // the disk refused: files so far, then what only reached memory
        else
        {
            var paths=m.Paths.ToList();
            if(paths.Count==0||paths.All(p=>!File.Exists(p))){st.Missing=true;return null;}
            blocks=paths.Where(File.Exists).SelectMany(p=>{Wav.Repair(p);return Wav.ReadBlocks(p);});
        }
        foreach(var block in blocks)
        {
            st.Samples+=block.Length;st.Peak=Math.Max(st.Peak,AudioLevel.Peak(block));
            foreach(var c in chunker.Append(block))job.Enqueue(c);
        }
        return chunker.Finish();
    }

    // Tray: one entry per failed recording that is still on disk, put in just before `before`.
    public void FillTray(System.Windows.Forms.ToolStripItemCollection items,System.Windows.Forms.ToolStripItem before)
    {
        foreach(var i in trayItems)items.Remove(i);
        trayItems.Clear();
        int at=items.IndexOf(before);
        foreach(var m in Store.Pending().OrderByDescending(m=>m.Started).Take(8))
        {
            var meta=m;var item=new System.Windows.Forms.ToolStripMenuItem($"{meta.Label} wiederholen");
            item.ToolTipText=meta.Reason??"";item.Click+=(_,_)=>_=Retry(meta);
            items.Insert(at++,item);trayItems.Add(item);
        }
        if(trayItems.Count>0){var sep=new System.Windows.Forms.ToolStripSeparator();items.Insert(at,sep);trayItems.Add(sep);}
    }
    public void Dispose()
    {
        sweepTimer.Dispose();Abort("Die App wurde beendet, bevor das Diktat verarbeitet war");life.Cancel();
        List<DictationFlow> rest;lock(held){rest=held.Values.ToList();held.Clear();}
        foreach(var f in rest){try{f.Capture.Dispose();}catch(Exception ex){Diag.Fail("dictation-dispose-held",ex);}}
    }
}
