using ScreenAgent.Core;
namespace ScreenAgent;
// One dictation in flight: the microphone, the recording on disk, the chunk job and where the live captions stood at each cut.
internal sealed class DictationFlow
{
    public required DictationCapture Capture {get;init;}
    public required DictationJob Job {get;init;}
    public required CancellationTokenSource Cts {get;init;}
    public required string Process {get;init;}
    public readonly List<int> Marks=[0];
    public Task<DictationOutcome>? Outcome;
    public string? Skipped,StopError;
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
    readonly Func<string,string,Task<bool>> injectRetry;        // process, text: true = went into the window that still fits
    readonly CancellationTokenSource life=new();
    readonly System.Threading.Timer sweepTimer;
    readonly List<DictationFlow> active=[];
    readonly List<System.Windows.Forms.ToolStripItem> trayItems=[];
    readonly HashSet<string> retrying=[];
    DictationFlow? live;
    public DictationStore Store {get;}
    public DictationManager(AppConfig config,DeviceClient client,Telemetry tel,CaptionHost captions,System.Windows.Threading.Dispatcher ui,Action<string,Level> notice,Action<string> balloon,
        Func<string,string,DictationContext> context,Func<string,string,Task<bool>> injectRetry,string? folder=null)
    {
        this.config=config;this.client=client;this.tel=tel;this.captions=captions;this.ui=ui;this.notice=notice;this.balloon=balloon;this.context=context;this.injectRetry=injectRetry;
        Store=new(folder??Path.Combine(ScreenConfig.Folder,"dictation"));
        sweepTimer=new(_=>Sweep(),null,TimeSpan.FromHours(1),TimeSpan.FromHours(1));
    }
    void Log(string type,Dictionary<string,object?> d)=>tel.Event(type,d);
    public bool Recording=>live is not null;
    // The note while he talks and while the chunks are still being transcribed: "Diktat · 3:12 · 4/5 fertig".
    public string? Status
    {
        get
        {
            if(live is {} f)
            {
                var t=TimeSpan.FromSeconds(f.Rec.Seconds);
                return $"Diktat · {(int)t.TotalMinutes}:{t.Seconds:00}"+(f.Job.Enqueued>0?$" · {f.Job.Done}/{f.Job.Enqueued} fertig":"");
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
            var gone=Store.Sweep(config.Retention());
            var pending=Store.Pending();
            Log("dictation.recover",new(){["repaired"]=r.Repaired,["failed"]=r.Failed,["orphans"]=r.Orphans,["dropped"]=r.Dropped,["swept"]=gone.Count,["pending"]=pending.Count,["path"]=Store.Folder});
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
        var flow=new DictationFlow{Capture=capture,Job=job,Cts=cts,Process=process};
        rec.Warning+=w=>ui.BeginInvoke(new System.Action(()=>notice(w,Level.Warn)));
        rec.ChunkReady+=chunk=>
        {
            int to=captions.Mark();int from=flow.Marks[^1];flow.Marks.Add(to);
            job.Enqueue(chunk,()=>captions.Slice(from,to));
            Log("dictation.cut",new(){["id"]=rec.Meta.Id,["index"]=chunk.Index,["seconds"]=Math.Round(chunk.Seconds,1),["overlap"]=chunk.OverlapSamples>0});
        };
        capture.Block+=captions.Feed;
        captions.NewText();live=flow;
        Log("dictation.start",new(){["id"]=rec.Meta.Id,["process"]=process,["session"]=inSession,["disk"]=rec.DiskOk,["path"]=Store.Folder});
        if(rec.StartWarning is {} w0)notice(w0,Level.Warn);
        return flow;
    }

    // Stop the microphone (called in key order, so a second dictation can start at once). The returned task is the delivery, to be queued behind earlier ones.
    public async Task<DictationFlow> Stop(DictationFlow flow)
    {
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
        if(flow.Capture.CaptureError is {} ce)Log("dictation.fail",new(){["id"]=rec.Meta.Id,["stage"]="capture",["reason"]="capture-stopped",["exType"]=ce,["seconds"]=Math.Round(rec.Seconds,1),["path"]=Store.Folder});
        // Nothing worth a request: too short or digital silence. No provider call; logged and a small notice.
        if(flow.Job.Enqueued==0&&DictationGate.Reject(rec.Samples,rec.Peak) is {} why)
        {
            flow.Skipped=why;flow.Cts.Cancel();
            rec.Meta.State="discarded";rec.Meta.Reason=why;rec.Meta.Ended=DateTimeOffset.UtcNow;Save(rec.Meta);
            Log("dictation.skip",new(){["id"]=rec.Meta.Id,["why"]=why,["seconds"]=Math.Round(rec.Seconds,2),["peak"]=rec.Peak,["bytes"]=rec.Samples*2});
            return flow;
        }
        if(last is not null&&flow.Job.Enqueued>0&&DictationGate.Reject(last.Samples.Length-last.OverlapSamples,AudioLevel.Peak(last.Samples)) is {} lw)
        {Log("dictation.skip",new(){["id"]=rec.Meta.Id,["why"]="last-"+lw,["index"]=last.Index});last=null;}
        int from=flow.Marks[^1];
        flow.Outcome=flow.Job.Complete(last,()=>captions.Slice(from,int.MaxValue));
        lock(active)active.Add(flow);
        return flow;
    }

    // An explicit discard chord: the words are not delivered, the file is kept for the normal 24 h.
    public async Task Discard(DictationFlow flow,string why)
    {
        if(ReferenceEquals(live,flow))live=null;
        flow.Cts.Cancel();
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
        try{f.Cts.Cancel();f.Capture.Finish().Wait(2000);}catch(Exception ex){Diag.Fail("dictation-abort",ex);}
        var m=f.Meta;m.State="failed";m.Stage="shutdown";m.Reason=reason;m.Seconds=f.Rec.Seconds;m.Ended=DateTimeOffset.UtcNow;m.PartialText=NullIfEmpty(captions.Text);Save(m);
        Log("dictation.fail",new(){["id"]=m.Id,["stage"]="shutdown",["reason"]=reason,["seconds"]=Math.Round(m.Seconds,1),["path"]=m.Paths.FirstOrDefault()??Store.Folder,["bytes"]=m.Bytes});
        f.Capture.Dispose();
    }

    static string? NullIfEmpty(string? s)=>string.IsNullOrWhiteSpace(s)?null:s;
    void Save(DictationMeta m){try{Store.Save(m);}catch(Exception ex){Log("dictation.fail",Redact.Of(ex,new(){["id"]=m.Id,["stage"]="sidecar",["reason"]="save",["path"]=Store.Folder}));}}

    // Delivery after the job finished (queued in order). inject: the text into the window he dictated into; true = typed or pasted there.
    public async Task Deliver(DictationFlow flow,Func<string,Task<bool>> inject)
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
        flow.Capture.Dispose();
        await Finish(m,o,flow.Process,captions.Text,inject,false);
    }

    async Task Finish(DictationMeta m,DictationOutcome o,string process,string caption,Func<string,Task<bool>> inject,bool retry)
    {
        if(o.Complete)
        {
            if(o.Text.Length==0){Log("dictation.empty",new(){["id"]=m.Id,["seconds"]=Math.Round(m.Seconds,1),["chunks"]=o.Chunks,["ms"]=o.Ms});notice("Diktat · nichts erkannt",Level.Warn);}
            else
            {
                bool shown=retry&&m.Stage=="partial-injected";   // a first, partial text is already in his field: do not type it twice
                bool injected=!shown&&await inject(o.Text);
                if(shown||!injected&&retry)
                {
                    _=_=ui.BeginInvoke(new System.Action(()=>DictationWindow.ShowResult(m,shown?"Diktat fertig · hier der vollständige Text":"Diktat fertig · das Fenster passt nicht mehr",o.Text)));
                    notice("Diktat fertig · Text im Fenster",Level.Active);
                }
                else notice(injected?"Diktat eingefügt":"Ziel geändert · Text kopiert",Level.Active);
            }
            m.State="done";m.Reason=null;m.Stage=null;m.PartialText=null;m.Ended=DateTimeOffset.UtcNow;Save(m);
            Log("dictation.done",new(){["id"]=m.Id,["seconds"]=Math.Round(m.Seconds,1),["chunks"]=o.Chunks,["ms"]=o.Ms,["chars"]=o.Text.Length,["degraded"]=o.Degraded,["retry"]=retry});
            return;
        }
        // Something failed. Text that exists (answers of the chunks that worked, live captions for the others) goes to him once; the file stays for a retry.
        string partial=o.Text.Length>0?o.Text:caption;
        bool wentIn=false;
        if(!retry&&o.Text.Length>0){wentIn=await inject(o.Text);}
        if(!retry)m.Stage=wentIn?"partial-injected":"transcribe";
        Fail(m,o.FailReason??"Fehler bei der Verarbeitung","transcribe",o.FailedStatus,"",NullIfEmpty(partial),true,o);
        if(wentIn&&o.FromFallback)notice("Teil aus Live-Text",Level.Warn);
    }

    void Fail(DictationMeta m,string reason,string stage,int status,string exType,string? partial,bool window,DictationOutcome? o=null,Exception? ex=null)
    {
        string path=m.Paths.FirstOrDefault()??Store.Folder;
        m.State="failed";m.Reason=reason;m.Stage=m.Stage is "partial-injected"?m.Stage:stage;m.Ended=DateTimeOffset.UtcNow;m.PartialText=partial;m.Bytes=Store.BytesOf(m);Save(m);
        Log("dictation.fail",new(){["id"]=m.Id,["stage"]=stage,["reason"]=reason,["exType"]=exType,["status"]=status,["path"]=path,["bytes"]=m.Bytes,["seconds"]=Math.Round(m.Seconds,1),["chunks"]=o?.Chunks,["failedChunks"]=o?.Failed,["msg"]=ex is null?"":Redact.Message(ex.Message)});
        notice($"Diktat nicht verarbeitet: {reason} · {path}",Level.Warn);
        if(window)_=ui.BeginInvoke(new System.Action(()=>DictationWindow.ShowFailure(m,reason,path,partial,()=>_=Retry(m))));
    }

    // "Erneut versuchen" and the tray entries: the same chunks and job over the file(s) on disk.
    public async Task Retry(DictationMeta m)
    {
        lock(retrying){if(!retrying.Add(m.Id))return;}
        try
        {
            m=Store.Load(m.MetaPath)??m;
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
            var o=await job.Complete(last);
            await Finish(m,o,m.Process,m.PartialText??"",text=>injectRetry(m.Process,text),true);
        }
        catch(Exception ex){Fail(m,"Wiederholung fehlgeschlagen","retry",0,ex.GetType().Name,m.PartialText,true,null,ex);}
        finally{lock(retrying)retrying.Remove(m.Id);}
    }
    sealed class RetryState{public long Samples;public int Peak;public bool Missing;}
    Chunk? Feed(DictationMeta m,DictationJob job,RetryState st)
    {
        var chunker=new DictationChunker();
        var paths=m.Paths.ToList();
        if(paths.Count==0||paths.All(p=>!File.Exists(p))){st.Missing=true;return null;}
        foreach(var p in paths.Where(File.Exists))
        {
            Wav.Repair(p);
            foreach(var block in Wav.ReadBlocks(p))
            {
                st.Samples+=block.Length;st.Peak=Math.Max(st.Peak,AudioLevel.Peak(block));
                foreach(var c in chunker.Append(block))job.Enqueue(c);
            }
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
    public void Dispose(){sweepTimer.Dispose();Abort("Die App wurde beendet, bevor das Diktat verarbeitet war");life.Cancel();}
}
