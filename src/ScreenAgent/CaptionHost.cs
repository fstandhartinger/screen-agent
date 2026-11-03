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
    readonly CaptionPolicy policy=new();readonly CaptionStats stats=new();readonly CaptionStitcher stitch=new();
    readonly object gate=new();
    readonly byte[] ring=new byte[MaxBytes];
    int filled;
    CaptionOverlay? overlay;
    WasapiCapture? tap;
    readonly System.Threading.Timer timer;
    bool active,dictating,sending;long statsAt,startedAt;
    public CaptionHost(AppConfig config,DeviceClient client,Telemetry telemetry,System.Windows.Threading.Dispatcher ui,Func<(string[] Ids,string[] Terms)> context)
    {
        this.client=client;this.telemetry=telemetry;this.ui=ui;this.context=context;mode=CaptionModes.Parse(config.Captions);
        timer=new(_=>Tick(),null,250,250);
    }
    public bool Enabled=>mode!=CaptionMode.Off;
    public bool Disabled=>policy.Disabled;
    public string Text{get{lock(gate)return stitch.Text;}}
    public int Mark(){lock(gate)return stitch.Mark();}
    public string Slice(int from,int to){lock(gate)return stitch.Slice(from,to);}
    // Called from the 100 ms app tick: which source runs. A muted microphone or an ended session/dictation stops it.
    public void Update(bool dictatingNow,bool voiceOn,bool muted)
    {
        bool want=mode!=CaptionMode.Off&&!policy.Disabled&&(dictatingNow||mode==CaptionMode.Both&&voiceOn&&!muted);
        dictating=dictatingNow;
        if(!dictatingNow&&!voiceOn)policy.NewSession();   // a switched-off session is over when nothing is running any more
        if(want&&!active)Begin();else if(!want&&active)End();
        bool tapWanted=want&&!dictatingNow;
        if(tapWanted&&tap is null)StartTap();else if(!tapWanted&&tap is not null)StopTap();
    }
    // A new dictation starts a new text.
    public void NewText(){lock(gate){stitch.Reset();filled=0;}}
    void Begin()
    {
        active=true;startedAt=Environment.TickCount64;statsAt=startedAt;
        lock(gate){filled=0;if(!dictating)stitch.Reset();}
        policy.NewSession();
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
        catch(Exception ex){telemetry.Event("caption.error",Redact.Of(ex,new(){["stage"]="tap"}));tap=null;policy.NewSession();StopTap();}
    }
    void StopTap(){var t=tap;tap=null;try{t?.StopRecording();t?.Dispose();}catch(Exception ex){Diag.Fail("caption-stop-tap",ex);}}
    public void Feed(byte[] buffer,int count)
    {
        if(count<=0)return;
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
            byte[] pcm;lock(gate){if(filled<Wav.BytesPerSecond)return;pcm=ring.AsSpan(0,filled).ToArray();}
            sending=true;int id=policy.Begin(now);
            _=Send(id,Wav.FromPcm(pcm));
        }
        catch(Exception ex){Diag.Fail("caption-tick",ex);}   // the next tick tries again
    }
    async Task Send(int id,byte[] wav)
    {
        try
        {
            var (ids,terms)=context();
            using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var r=await client.Caption(wav,ids,terms,cts.Token);
            long now=Environment.TickCount64;bool ok=r.Status is >=200 and <300;
            bool show=policy.Answered(id,ok,r.Status,now);
            if(ok)stats.Ok(r.Seconds>0?r.Seconds:wav.Length/(double)Wav.BytesPerSecond,r.Ms,r.Cost);
            if(!ok&&policy.Disabled)
            {
                telemetry.Event("caption.off",new(){["status"]=r.Status,["code"]=r.Code??"",["calls"]=stats.Calls});   // captions stay hidden for the rest of the session
            }
            if(show&&!string.IsNullOrWhiteSpace(r.Text))
            {
                string shown;lock(gate){stitch.Add(r.Text);shown=stitch.Last(110);}
                _=ui.BeginInvoke(new System.Action(()=>{try{(overlay??=new CaptionOverlay()).Present(shown);}catch(Exception ex){telemetry.Event("caption.error",Redact.Of(ex,new(){["stage"]="overlay"}));}}));
            }
        }
        catch(Exception ex)
        {
            policy.Answered(id,false,0,Environment.TickCount64);
            telemetry.Event("caption.error",Redact.Of(ex,new(){["stage"]="send",["failures"]=policy.Failed}));
        }
        finally{sending=false;}
    }
    public void Dispose()
    {
        timer.Dispose();if(active)End();StopTap();
        _=ui.BeginInvoke(new System.Action(()=>{overlay?.Close();overlay=null;}));
    }
}
