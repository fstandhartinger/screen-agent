using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenAgent.Core;
namespace ScreenAgent;
// Live subtitles (config Captions). While dictating, the dictation capture feeds the audio; in a voice session a second shared-mode capture runs in parallel.
// Every 1.5 s while the recent audio shows speech the last 6 s go to /api/device/caption; the answers are stitched into one growing text (CaptionStitcher) and the
// last two lines show in the subtitle overlay. The same text is the fallback for a dictation chunk the transcription could not deliver.
// Policy (CaptionPolicy): one request in flight, 5 failures pause it 60 s, 429/503 switch it off for the session. Caption text is never logged, only counts, latency and cost.
internal sealed class CaptionHost : IDisposable
{
    const int WindowSeconds=6,MaxBytes=Wav.BytesPerSecond*WindowSeconds;
    readonly DeviceClient client;readonly Telemetry telemetry;readonly System.Windows.Threading.Dispatcher ui;readonly CaptionMode mode;
    readonly Func<(string[] Ids,string[] Terms)> context;
    readonly CaptionPolicy policy=new();readonly CaptionStats stats=new();readonly CaptionStitcher stitch=new(60,4000);
    readonly object gate=new();
    readonly byte[] ring=new byte[MaxBytes];
    int filled;
    CaptionOverlay? overlay;
    WasapiCapture? tap;
    readonly System.Threading.Timer timer;
    bool active,dictating,sending;long statsAt,startedAt,tapRetryAt;
    DictationVisual visual;string? visualDetail;
    readonly LevelRing levels=new();
    readonly List<DictationCaptions> dictations=[];
    // v0.1.12: the live clean-up of the subtitle while he dictates (config CaptionPolish). Off for the rest of the session after a 503/429.
    readonly bool polishOn;readonly LivePolish live=new();bool polishOff;   // the dictations whose own text the answers are added to (live ones and ones that ended a moment ago)
    public CaptionHost(AppConfig config,DeviceClient client,Telemetry telemetry,System.Windows.Threading.Dispatcher ui,Func<(string[] Ids,string[] Terms)> context)
    {
        this.client=client;this.telemetry=telemetry;this.ui=ui;this.context=context;mode=CaptionModes.Parse(config.Captions);polishOn=config.CaptionPolish;
        timer=new(_=>Tick(),null,250,250);
    }
    public bool Enabled=>mode!=CaptionMode.Off;
    public bool Disabled=>policy.Disabled;
    public string Text{get{lock(gate)return stitch.Text;}}
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
    // A new dictation starts a new text: its own stitcher, fed by the answers for as long as it is live (and EndGraceMs after).
    const int EndGraceMs=4000;
    public DictationCaptions BeginDictation()
    {
        var d=new DictationCaptions();
        lock(gate){stitch.Reset();live.Reset();filled=0;dictations.RemoveAll(x=>!x.Accepts(Environment.TickCount64));dictations.Add(d);}
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
        lock(gate){filled=0;if(!dictating){stitch.Reset();live.Reset();}}
        policy.NewSession();polishOff=false;
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
    void Tick()
    {
        try
        {
            long now=Environment.TickCount64;
            if(active&&now-statsAt>=60000){statsAt=now;telemetry.Event("caption.stats",stats.Snapshot(policy));}
            if(!active||sending||!policy.ShouldSend(now))return;
            byte[] pcm;DictationCaptions[] owners;
            lock(gate){if(filled<Wav.BytesPerSecond)return;pcm=ring.AsSpan(0,filled).ToArray();owners=dictations.Where(x=>x.Accepts(now)).ToArray();}   // the audio belongs to the dictations live when it was cut
            sending=true;int id=policy.Begin(now);
            _=Send(id,Wav.FromPcm(pcm),owners);
        }
        catch(Exception ex){Diag.Fail("caption-tick",ex);}   // the next tick tries again
    }
    async Task Send(int id,byte[] wav,DictationCaptions[] owners)
    {
        try
        {
            var (ids,terms)=context();
            using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var r=await client.Caption(wav,ids,terms,cts.Token);
            long now=Environment.TickCount64;bool ok=r.Status is >=200 and <300;
            bool show=policy.Answered(id,ok,r.Status,now,r.Code);
            if(ok)stats.Ok(r.Seconds>0?r.Seconds:wav.Length/(double)Wav.BytesPerSecond,r.Ms,r.Cost);
            if(!ok&&policy.Disabled)
            {
                telemetry.Event("caption.off",new(){["status"]=r.Status,["code"]=r.Code??"",["calls"]=stats.Calls});   // captions stay hidden for the rest of the session
            }
            if(show&&!string.IsNullOrWhiteSpace(r.Text))
            {
                string shown;LivePolish.Request? req=null;
                lock(gate)
                {
                    stitch.Add(r.Text);var words=stitch.Words;
                    shown=polishOn?live.Display(words,110):stitch.Last(110);
                    if(polishOn&&!polishOff&&dictating)req=live.Next(words);
                }
                foreach(var d in owners)d.Add(r.Text);
                Present(shown);
                if(req is not null)_=Polish(req);
            }
        }
        catch(Exception ex)
        {
            policy.Answered(id,false,0,Environment.TickCount64);
            telemetry.Event("caption.error",Redact.Of(ex,new(){["stage"]="send",["failures"]=policy.Failed}));
        }
        finally{sending=false;}
    }
    void Present(string shown)=>_=ui.BeginInvoke(new System.Action(()=>{try{if(active||visual==DictationVisual.Processing)(overlay??=new CaptionOverlay()).Present(shown);}catch(Exception ex){telemetry.Event("caption.error",Redact.Of(ex,new(){["stage"]="overlay"}));}}));
    // The stable part of the subtitle through the clean-up (one request at a time); the answer is shown at once if those words are still on screen. Text is never logged.
    async Task Polish(LivePolish.Request req)
    {
        string? text=null;int status=0;long t0=Environment.TickCount64;
        try
        {
            using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var r=await client.Polish(req.Text,"live","","",cts.Token);status=r.Status;
            if(r.Status is >=200 and <300)text=r.Text;
            else if(r.Status is 429 or 503)
            {
                polishOff=true;
                telemetry.Event("caption.polish.off",new(){["status"]=r.Status,["code"]=r.Code??""});
            }
        }
        catch(Exception ex){telemetry.Event("caption.error",Redact.Of(ex,new(){["stage"]="polish"}));}
        string shown;
        lock(gate){live.Answer(req,text);shown=live.Display(stitch.Words,110);}
        stats.Polish(status is >=200 and <300,Environment.TickCount64-t0);
        if(text is not null)Present(shown);
    }
    public void Dispose()
    {
        timer.Dispose();if(active)End();StopTap();
        _=ui.BeginInvoke(new System.Action(()=>{overlay?.Close();overlay=null;}));
    }
}
