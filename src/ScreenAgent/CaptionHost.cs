using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenAgent.Core;
namespace ScreenAgent;
// Live subtitles (config Captions). While dictating, the dictation capture feeds the audio; in a voice session a second shared-mode capture runs in parallel.
// Every 1.5 s while the recent audio shows speech the last 6 s go to /api/device/caption; the answers are placed into one growing text (LiveCaption: aligned,
// settled where two windows agree) and the last two lines show in the subtitle overlay. While he dictates, settled words go through the live clean-up and each
// transcribed chunk replaces its part (v0.1.16). Each dictation's own stitched text is the fallback for a chunk the transcription could not deliver.
// Policy (CaptionPolicy): one request in flight, 3 failures pause it 15 s, no key / budget switch it off for the session. The caption text goes to the Desk's log only
// with CaptionLog on (caption.shown, caption.summary; the raw windows are logged by the Desk itself), never to the local log.
internal sealed class CaptionHost : IDisposable
{
    const int WindowSeconds=6,MaxBytes=Wav.BytesPerSecond*WindowSeconds,ShownChars=110;
    readonly DeviceClient client;readonly Telemetry telemetry;readonly System.Windows.Threading.Dispatcher ui;readonly CaptionMode mode;
    readonly Func<(string[] Ids,string[] Terms)> context;
    readonly CaptionPolicy policy=new();readonly CaptionStats stats=new();readonly LiveCaption voiceText=new(4000);
    readonly object gate=new();
    readonly byte[] ring=new byte[MaxBytes];
    int filled;long fedBytes;   // the audio of the last seconds, and how much was fed since the subtitle started (window positions)
    CaptionOverlay? overlay;
    WasapiCapture? tap;
    readonly System.Threading.Timer timer;
    bool active,dictating,sending;long statsAt,startedAt,tapRetryAt;
    DictationVisual visual;string? visualDetail;
    readonly LevelRing levels=new();
    readonly List<DictationCaptions> dictations=[];   // the dictations whose own text the answers are added to (live ones and ones that ended a moment ago)
    DictationCaptions? current;                      // the dictation the subtitle shows (null: a voice session)
    // v0.1.12: the live clean-up of the subtitle while he dictates (config CaptionPolish). v0.1.16: a timeout no longer switches it off; no key or budget does,
    // three failures in a row pause it 15 s. It runs on a steady beat (every new settled words, and at least every 2 s while there are any).
    readonly bool polishOn;bool polishOff;long polishPausedUntil,lastPolishAt;int polishFailures;
    public CaptionHost(AppConfig config,DeviceClient client,Telemetry telemetry,System.Windows.Threading.Dispatcher ui,Func<(string[] Ids,string[] Terms)> context)
    {
        this.client=client;this.telemetry=telemetry;this.ui=ui;this.context=context;mode=CaptionModes.Parse(config.Captions);polishOn=config.CaptionPolish;
        timer=new(_=>Tick(),null,250,250);
    }
    public bool Enabled=>mode!=CaptionMode.Off;
    public bool Disabled=>policy.Disabled;
    LiveCaption Model=>current?.Live??voiceText;   // under gate
    public string Text{get{lock(gate)return string.Join(' ',Model.Display());}}
    public bool TapRunning=>tap is not null;
    // Called from the 100 ms app tick: which source runs. A muted microphone or an ended session/dictation stops it.
    public void Update(bool dictatingNow,bool voiceOn,bool muted)
    {
        bool want=mode!=CaptionMode.Off&&!policy.Disabled&&(dictatingNow||mode==CaptionMode.Both&&voiceOn&&!muted);
        dictating=dictatingNow;
        if(!dictatingNow&&!voiceOn)policy.NewSession();   // a switched-off session is over when nothing is running any more
        if(want&&!active)Begin();else if(!want&&active)End();
        bool tapWanted=want&&!dictatingNow;
        if(tapWanted&&tap is null&&Environment.TickCount64>=tapRetryAt)StartTap();else if(!tapWanted&&tap is not null)StopTap();
    }
    // A new dictation starts a new text: its own LiveCaption and log, fed by the answers for as long as it is live (and EndGraceMs after).
    const int EndGraceMs=4000;
    public DictationCaptions BeginDictation(string id="")
    {
        var d=new DictationCaptions(id,Environment.TickCount64);
        lock(gate){filled=0;fedBytes=0;dictations.RemoveAll(x=>!x.Accepts(Environment.TickCount64));dictations.Add(d);current=d;}
        return d;
    }
    public void EndDictation(DictationCaptions d)=>d.End(Environment.TickCount64,EndGraceMs);
    // From the app tick (UI thread): waveform while he talks, the circle while it is processed. Works with captions switched off too.
    public void Visual(DictationVisual v,string? detail)
    {
        if(v==visual&&detail==visualDetail)return;
        visual=v;visualDetail=detail;
        if(v==DictationVisual.None&&overlay is null)return;
        try{(overlay??=new CaptionOverlay()).SetVisual(v,detail,levels);}
        catch(Exception ex){telemetry.Event("caption.error",Redact.Of(ex,new(){["stage"]="visual"}));}
    }
    void Begin()
    {
        active=true;startedAt=Environment.TickCount64;statsAt=startedAt;
        lock(gate){filled=0;fedBytes=0;if(!dictating){current=null;voiceText.Reset();}}
        policy.NewSession();polishOff=false;polishFailures=0;polishPausedUntil=0;
    }
    void End()
    {
        active=false;StopTap();
        if(stats.Calls>0||policy.Sent>0)telemetry.Event("caption.stats",stats.Snapshot(policy));
        _=ui.BeginInvoke(new System.Action(()=>overlay?.Dismiss()));
    }
    void StartTap()
    {
        try
        {
            var c=new WasapiCapture();c.WaveFormat=new WaveFormat(16000,16,1);
            c.DataAvailable+=(_,e)=>Feed(e.Buffer,e.BytesRecorded);
            c.RecordingStopped+=(_,_)=>{};
            c.StartRecording();tap=c;
        }
        catch(Exception ex)
        {
            tapRetryAt=Environment.TickCount64+30000;   // the microphone is held by another program or blocked: one try, one log line, then 30 s of quiet (not every 100 ms tick)
            telemetry.Event("caption.error",Redact.Of(ex,new(){["stage"]="tap",["retryInSeconds"]=30}));tap=null;StopTap();
        }
    }
    void StopTap(){var t=tap;tap=null;try{t?.StopRecording();t?.Dispose();}catch(Exception ex){Diag.Fail("caption-stop-tap",ex);}}
    public void Feed(byte[] buffer,int count)
    {
        if(count<=0)return;
        levels.AddPcm(buffer.AsSpan(0,count));
        lock(gate)
        {
            fedBytes+=count;
            if(count>=MaxBytes){Buffer.BlockCopy(buffer,count-MaxBytes,ring,0,MaxBytes);filled=MaxBytes;}
            else
            {
                if(filled+count>MaxBytes){int drop=filled+count-MaxBytes;Buffer.BlockCopy(ring,drop,ring,0,filled-drop);filled-=drop;}
                Buffer.BlockCopy(buffer,0,ring,filled,count);filled+=count;
            }
        }
        double rms=AudioLevel.Rms(System.Runtime.InteropServices.MemoryMarshal.Cast<byte,short>(buffer.AsSpan(0,count&~1)));
        long now=Environment.TickCount64;policy.Heard(now,rms);
        if(rms>=400)stats.SpeechSeconds+=count/(double)Wav.BytesPerSecond;
    }
    bool PolishAllowed(long now)=>polishOn&&!polishOff&&dictating&&now>=polishPausedUntil;
    void Tick()
    {
        try
        {
            long now=Environment.TickCount64;
            if(active&&now-statsAt>=60000){statsAt=now;telemetry.Event("caption.stats",stats.Snapshot(policy));}
            // the steady beat of the clean-up: settled words that waited 2 s go even when fewer than three
            if(active&&PolishAllowed(now)&&now-lastPolishAt>=2000)
            {
                LiveCaption.PolishRequest? beat=null;DictationCaptions? d;
                lock(gate){d=current;if(d is not null&&d.Accepts(now))beat=d.Live.NextPolish(force:true);}
                if(beat is not null){lastPolishAt=now;_=Polish(beat,d!);}
            }
            if(!active||sending||!policy.ShouldSend(now))return;
            byte[] pcm;DictationCaptions[] owners;DictationCaptions? owner;double end,seconds;int seq=0;
            lock(gate)
            {
                if(filled<Wav.BytesPerSecond)return;
                pcm=ring.AsSpan(0,filled).ToArray();owners=dictations.Where(x=>x.Accepts(now)).ToArray();   // the audio belongs to the dictations live when it was cut
                if(current is not null&&!current.Accepts(now))current=null;
                owner=current;end=fedBytes/(double)Wav.BytesPerSecond;seconds=filled/(double)Wav.BytesPerSecond;
                if(owner is not null)seq=++owner.Seq;
            }
            sending=true;int id=policy.Begin(now);
            _=Send(id,Wav.FromPcm(pcm),owners,owner,end,seconds,seq);
        }
        catch(Exception ex){Diag.Fail("caption-tick",ex);}   // the next tick tries again
    }
    async Task Send(int id,byte[] wav,DictationCaptions[] owners,DictationCaptions? owner,double end,double seconds,int seq)
    {
        try
        {
            var (ids,terms)=context();
            using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var r=await client.Caption(wav,ids,terms,cts.Token,owner?.Id,seq);
            long now=Environment.TickCount64;bool ok=r.Status is >=200 and <300;
            bool show=policy.Answered(id,ok,r.Status,now,r.Code);
            if(ok)stats.Ok(r.Seconds>0?r.Seconds:wav.Length/(double)Wav.BytesPerSecond,r.Ms,r.Cost);
            if(!ok&&policy.Disabled)
            {
                telemetry.Event("caption.off",new(){["status"]=r.Status,["code"]=r.Code??"",["calls"]=stats.Calls});   // captions stay hidden for the rest of the session
            }
            if(show&&!string.IsNullOrWhiteSpace(r.Text))
            {
                string? shown;LiveCaption.PolishRequest? req=null;
                lock(gate)
                {
                    var m=owner?.Live??voiceText;
                    m.AddWindow(r.Text,end,seconds);
                    if(owner is not null){owner.Windows.Add((now-owner.StartedMs,end,r.Text));LogUpdate(owner,'w',now);}
                    shown=ReferenceEquals(m,Model)?m.Last(ShownChars):null;
                    if(owner is not null&&ReferenceEquals(owner,current)&&PolishAllowed(now))req=m.NextPolish();
                }
                foreach(var d in owners)d.Add(r.Text);
                if(shown is not null)Present(shown);
                if(req is not null){lastPolishAt=now;_=Polish(req,owner!);}
            }
        }
        catch(Exception ex)
        {
            policy.Answered(id,false,0,Environment.TickCount64);
            telemetry.Event("caption.error",Redact.Of(ex,new(){["stage"]="send",["failures"]=policy.Failed}));
        }
        finally{sending=false;}
    }
    // One change of a dictation's shown text into its log; a full batch goes to the Desk (caption.shown). Under gate.
    void LogUpdate(DictationCaptions d,char cause,long now)
    {
        d.Log.Update(now-d.StartedMs,cause,d.Live.Display());
        if(d.Log.Pending>=40)FlushLog(d);
    }
    void FlushLog(DictationCaptions d)
    {
        for(var items=d.Log.Take();items is not null;items=d.Log.Take())telemetry.Text("caption.shown",new(){["id"]=d.Id,["items"]=items});
    }
    void Present(string shown)=>_=ui.BeginInvoke(new System.Action(()=>{try{if(active||visual==DictationVisual.Processing)(overlay??=new CaptionOverlay()).Present(shown);}catch(Exception ex){telemetry.Event("caption.error",Redact.Of(ex,new(){["stage"]="overlay"}));}}));
    // Settled words of the subtitle through the clean-up (one request at a time, with the words before as context); the answer replaces exactly those words.
    async Task Polish(LiveCaption.PolishRequest req,DictationCaptions d)
    {
        string? text=null;int status=0;string? code=null;long t0=Environment.TickCount64;
        try
        {
            using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var r=await client.Polish(req.Text,"live","","",cts.Token,false,req.Before,string.IsNullOrEmpty(d.Id)?null:d.Id);status=r.Status;code=r.Code;
            if(r.Status is >=200 and <300)text=r.Text;
        }
        catch(Exception ex){telemetry.Event("caption.error",Redact.Of(ex,new(){["stage"]="polish"}));}
        long now=Environment.TickCount64;bool ok=status is >=200 and <300;
        if(ok)polishFailures=0;
        else if(status==429||code is "caption-budget" or "no-key"){polishOff=true;telemetry.Event("caption.polish.off",new(){["status"]=status,["code"]=code??""});}
        else if(++polishFailures>=3){polishFailures=0;polishPausedUntil=now+15000;telemetry.Event("caption.polish.pause",new(){["status"]=status,["code"]=code??""});}
        string? shown=null;LiveCaption.PolishRequest? next=null;
        lock(gate)
        {
            bool used=d.Live.AnswerPolish(req,text);
            d.Log.PolishAsked++;
            if(used){d.Log.PolishUsed++;LogUpdate(d,'p',now);}else if(text is null)d.Log.PolishFailed++;else d.Log.PolishDropped++;
            if(used&&ReferenceEquals(d,current))shown=d.Live.Last(ShownChars);
            if(ReferenceEquals(d,current)&&d.Accepts(now)&&PolishAllowed(now))next=d.Live.NextPolish();   // more settled words came in meanwhile
        }
        stats.Polish(ok,now-t0);
        if(shown is not null)Present(shown);
        if(next is not null){lastPolishAt=now;_=Polish(next,d);}
    }
    // A chunk of the dictation is transcribed (what will be typed): it replaces its part of the subtitle, so both say the same.
    public void Settle(DictationCaptions d,string joined)
    {
        string? shown=null;long now=Environment.TickCount64;
        lock(gate)
        {
            int how=d.Live.Settle(joined);
            if(how==0)return;
            d.Log.Chunks++;if(how==1)d.Log.ChunksPlaced++;
            LogUpdate(d,'c',now);
            if(ReferenceEquals(d,current))shown=d.Live.Last(ShownChars);
        }
        if(shown is not null)Present(shown);
    }
    // The dictation is over and its text typed (or not): the rest of its log and the summary with the measures go to the Desk. Returns the summary.
    public Dictionary<string,object?> Summary(DictationCaptions d,string typed)
    {
        Dictionary<string,object?> data;List<(string Type,Dictionary<string,object?> Data)> texts;
        lock(gate){FlushLog(d);data=CaptionSummary.Build(d,typed,Environment.TickCount64);texts=CaptionSummary.Texts(d,typed);}
        telemetry.Text("caption.summary",data);
        foreach(var (type,part) in texts)telemetry.Text(type,part);
        return data;
    }
    // Copies of what a dictation's subtitle heard and showed (for the refinement keep), taken under the lock.
    public (List<(long Ms,double AudioEnd,string Text)> Windows,List<CaptionLog.Item> Shown) Snapshot(DictationCaptions d){lock(gate)return ([..d.Windows],[..d.Log.All]);}
    public void Dispose()
    {
        timer.Dispose();if(active)End();StopTap();
        _=ui.BeginInvoke(new System.Action(()=>{overlay?.Close();overlay=null;}));
    }
}
