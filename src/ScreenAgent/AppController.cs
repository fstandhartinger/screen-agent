using System.Net.Http;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Threading;
namespace ScreenAgent;
internal sealed class AppController : IDisposable
{
    readonly AppConfig config;
    readonly DeviceClient client;
    readonly AgentBridge bridge;
    readonly KeyboardHook hook;
    readonly Pill pill;
    readonly PrivacyCapture privacy;
    readonly CorrectionLearner learner;
    readonly DispatcherTimer timer;
    readonly System.Windows.Forms.NotifyIcon tray;
    CancellationTokenSource epoch=new();
    readonly List<DrawOverlay> drawings=[];
    bool paused,busy,disposed,privacyBlocked;
    long capturedAt,noticeUntil;
    readonly FrameClock versions=new();
    readonly ScreenSchedule schedule=new();
    readonly OrderedWork transcriptions=new(), lifecycle=new(), screenWrites=new();
    readonly ScreenSchedule clearRetry=new();
    bool clearing,unmuting;
    string frameProcess="";
    bool needsUnmute,voiceFailed;
    string? contextSid;
    bool? voiceRequested;
    long unmuteRetryAt,voiceRetryAt;
    bool voiceSyncing;
    IntPtr lastForeground,underlying,hashForeground;
    ulong? lastHash;
    string[] ids=[],terms=[];
    TextInjector.Target? target,audioTarget;
    string before="",process="";
    DictationCapture? audio;
    string? audioSession;
    Task audioQueue=Task.CompletedTask;
    bool finishingDraw;
    public AppController(AppConfig config,DeviceClient client,AgentBridge bridge,KeyboardHook hook,Pill pill)
    {
        this.config=config;this.client=client;this.bridge=bridge;this.hook=hook;this.pill=pill;
        privacy=new(config.Policy(),()=>Active);learner=new(client,config.Policy());
        bridge.SessionChanged+=()=>{voiceRequested=null;Reset();};
        hook.BindTarget+=window=>bridge.Dispatcher.BeginInvoke(new System.Action(()=>
        {
            if(audio!=null)return;
            try{target=Native.GetForegroundWindow()==window?TextInjector.Bind():null;if(target!=null&&!TextInjector.Safe(target,config.Policy()))target=null;}catch{target=null;}
        }));
        hook.BindDrawing+=window=>bridge.Dispatcher.BeginInvoke(new System.Action(()=>underlying=window));
        hook.Action+=a=>bridge.Dispatcher.BeginInvoke(new System.Action(()=>Action(a)));
        timer=new(){Interval=TimeSpan.FromMilliseconds(100)};timer.Tick+=async (_,_)=>
        {
            try
            {
                if(pendingClear is {} pending&&!clearing&&clearRetry.Ready(Environment.TickCount64))_=ClearContext(pending);
                if(needsUnmute&&audio is null&&!unmuting&&audioQueue.IsCompleted&&Environment.TickCount64>=unmuteRetryAt){unmuteRetryAt=Environment.TickCount64+2000;_=Unmute();}
                if(!hook.Healthy){if(ids.Length>0||audio!=null)Reset();await SyncVoice();Notice("Pausiert · Tastatur");return;}
                if(drawings.Count>0&&!finishingDraw&&drawings.Any(d=>d.LastStrokeAt>0)&&drawings.All(d=>!d.InStroke)&&Environment.TickCount64-drawings.Max(d=>d.LastStrokeAt)>=700)await FinishDraw();
                await SyncVoice();
                if(Environment.TickCount64>noticeUntil)pill.SetMode(needsUnmute?"Desk-Mikro stumm":voiceFailed?"Stimme nicht bestätigt":!schedule.Ready(Environment.TickCount64)?"Desk/Registry nicht erreichbar":paused||hook.Paused?"Pausiert":privacyBlocked?"Privat · nicht erfasst":audio!=null?"Diktat":drawings.Count>0?"Zeichnen":Active?$"Sitzung · {ids.Length} IDs":"Pausiert");
                await Watch();
            }
            catch{Notice("Anfrage fehlgeschlagen");}
        };timer.Start();
        var menu=new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Pause",null,(_,_)=>Pause());
        menu.Items.Add("Desk anzeigen",null,(_,_)=>bridge.Show());
        menu.Items.Add("Gerät trennen",null,(_,_)=>{Pause(true);bridge.Disconnect();});
        menu.Items.Add("Beenden",null,async (_,_)=>{paused=true;Reset();await bridge.Voice(false,config.VoiceProvider);await ClearContext(contextSid??bridge.SessionId);await Unmute();bridge.AllowClose=true;System.Windows.Application.Current.Shutdown();});
        tray=new(){Icon=SystemIcons.Application,Text="Screen Agent",Visible=true,ContextMenuStrip=menu};tray.DoubleClick+=(_,_)=>bridge.Show();
    }
    bool Active=>!disposed&&client.Paired&&bridge.SessionId is not null&&hook.Healthy&&hook.Session&&!hook.Paused&&!paused;
    public void Pause(bool force=false){paused=force||!paused;Reset();}
    void Notice(string text){noticeUntil=Environment.TickCount64+2200;pill.SetMode(text);}
    async Task SyncVoice()
    {
        bool on=Active;
        if(on&&voiceRequested==true&&!bridge.VoiceOn)voiceFailed=true;
        if(voiceSyncing||(voiceRequested==on&&(!voiceFailed||Environment.TickCount64<voiceRetryAt)))return;
        voiceSyncing=true;voiceRequested=on;
        try{await lifecycle.Enqueue(async ()=>{voiceFailed=!await bridge.Voice(on,config.VoiceProvider);voiceRetryAt=Environment.TickCount64+2000;});}
        finally{voiceSyncing=false;}
    }
    async Task ClearContext(string? sid)
    {
        ids=[];lastHash=null;bridge.Screen([]);
        if(sid is null||!client.Paired)return;
        clearing=true;
        try
        {
            await screenWrites.Enqueue(()=>client.Post("api/device/session/"+Uri.EscapeDataString(sid)+"/screen/clear",new{},CancellationToken.None));
            if(pendingClear==sid)pendingClear=null;clearRetry.Success(Environment.TickCount64);
        }
        catch{pendingClear=sid;clearRetry.Failure(Environment.TickCount64);Notice("Kontext-Löschung ausstehend");}
        finally{clearing=false;}
    }
    string? pendingClear;
    async Task Unmute()
    {
        if(unmuting)return;
        unmuting=true;needsUnmute=true;
        try{needsUnmute=!await bridge.Mute(false);}
        finally{unmuting=false;}
        if(needsUnmute)Notice("Desk-Mikro stumm");
    }
    Task PostScreen(string sid,object body,CancellationToken ct)=>screenWrites.Enqueue(()=>
    {
        ct.ThrowIfCancellationRequested();
        return client.Post("api/device/session/"+Uri.EscapeDataString(sid)+"/screen",body,ct);
    });
    void Reset()
    {
        _=ClearContext(contextSid??bridge.SessionId);
        epoch.Cancel();epoch.Dispose();epoch=new();ids=[];lastHash=null;capturedAt=0;lastForeground=IntPtr.Zero;learner.Cancel();
        foreach(var d in drawings)d.Close();drawings.Clear();
        QueueAudio(ActionKind.DictationDiscard);
    }
    void Action(ActionKind action)
    {
        if(action is ActionKind.DictationStart or ActionKind.DictationStop or ActionKind.DictationDiscard){QueueAudio(action);return;}
        if(action is ActionKind.SessionToggle or ActionKind.PauseToggle){Reset();return;}
        if(action==ActionKind.DrawStart&&Active&&drawings.Count==0)
        {
            if(underlying==IntPtr.Zero||Native.GetForegroundWindow()!=underlying)return;
            foreach(var screen in System.Windows.Forms.Screen.AllScreens)
            {
                using var probe=privacy.Capture(screen.Bounds,true,false,underlying);if(probe is null)continue;
                var draw=new DrawOverlay(screen);drawings.Add(draw);draw.Show();
            }
        }
        if(action==ActionKind.DrawStop)_=FinishDraw();
    }
    void QueueAudio(ActionKind action)
    {
        // Capture events serialize only capture/mute, independently of transcription/insertion.
        audioQueue=RunAudioAfter(audioQueue,action);
    }
    async Task RunAudioAfter(Task previous,ActionKind action)
    {
        try{await previous;}catch{ /* A failed lifecycle must not poison future key events. */ }
        try
        {
            if(action==ActionKind.DictationStart)
            {
                if(audio!=null||!hook.Dictating||target is null||paused||hook.Paused||!client.Paired||!TextInjector.Safe(target,config.Policy()))return;
                audioTarget=target;audioSession=bridge.SessionId;
                if(!await bridge.Mute(true)){await Unmute();Notice("Diktat · Mikrofon nicht bestätigt");return;}
                if(!hook.Dictating||paused||hook.Paused||audioTarget is null||!TextInjector.Safe(audioTarget,config.Policy())||audioSession!=bridge.SessionId){await Unmute();return;}
                process=TextInjector.ProcessName(audioTarget.Window);
                before=TextInjector.Terminal(audioTarget.Window)?"":TextInjector.Read(audioTarget.Element)??"";
                audio=new();Notice("Diktat");return;
            }
            if(audio is null)return;
            var recording=audio;audio=null;
            var bound=audioTarget;string prior=before,proc=process;var ct=epoch.Token;
            try
            {
                byte[] wav=await recording.Finish();
                if(action==ActionKind.DictationDiscard||bound is null||paused||hook.Paused||audioSession!=bridge.SessionId)return;
                var context=new DictationContext(proc,prior.Length<=500?prior:prior[^500..],ids.ToArray(),terms.Take(30).ToArray());
                _=transcriptions.Enqueue(async ()=>
                {
                    try
                    {
                        string text=await client.Dictate(wav,context,ct);ct.ThrowIfCancellationRequested();
                        bool injected=await TextInjector.Inject(bound,text,text.Length>=60&&!TextInjector.Terminal(bound.Window),config.Policy());
                        Notice(injected?"Diktat eingefügt":"Ziel geändert · Text kopiert");
                        if(injected)_=learner.Watch(bound,prior,TextInjector.Sanitize(text),ct);
                    }
                    catch(OperationCanceledException){}catch{Notice("Diktat fehlgeschlagen");}
                });
            }
            finally{recording.Dispose();needsUnmute=true;}
        }
        catch(OperationCanceledException){}
        catch{audio?.Dispose();audio=null;await Unmute();Notice("Diktat fehlgeschlagen");}
    }
    async Task Watch()
    {
        var fg=Native.GetForegroundWindow();
        if(busy||!schedule.Ready(Environment.TickCount64)||!Active||drawings.Count>0||(fg==lastForeground&&Environment.TickCount64-capturedAt<config.IntervalMs&&!schedule.RenewalDue(Environment.TickCount64)))return;
        busy=true;capturedAt=Environment.TickCount64;lastForeground=fg;
        var ct=epoch.Token;string sid=bridge.SessionId!;contextSid=sid;
        try
        {
            if(pendingClear is {} oldSid){pendingClear=null;await ClearContext(oldSid);if(pendingClear!=null)throw new HttpRequestException();}
            var monitor=System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds;
            using var frame=privacy.Capture(monitor,true,false);
            if(frame is null)
            {
                privacyBlocked=true;ids=[];lastHash=null;bridge.Screen(ids);Notice("Privat · nicht erfasst");
                await ClearContext(sid);schedule.Success(Environment.TickCount64);
                return;
            }
            privacyBlocked=false;
            ulong hash=PerceptualHash(frame);
            if(lastHash==hash&&hashForeground==fg)
            {
                if(schedule.RenewalDue(Environment.TickCount64))
                {
                    await PostScreen(sid,DevicePayloads.Screen(ids,frameProcess,versions.Next(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),ct);
                    schedule.Success(Environment.TickCount64);
                }
                return;
            }
            var window=privacy.Snapshot(monitor).FirstOrDefault(w=>w.Handle==fg);if(window is null||window.Blocked){await ClearContext(sid);return;}
            double ocrScale=Math.Min(1,2560.0/Math.Max(frame.Width,frame.Height));
            using var ocrFrame=new Bitmap(frame,new Size(Math.Max(1,(int)(frame.Width*ocrScale)),Math.Max(1,(int)(frame.Height*ocrScale))));
            byte[] png=PrivacyCapture.Png(ocrFrame);var ocr=await Task.Run(()=>Ocr.ReadMemory(png),ct);
            var confirmed=await client.Exists(ocr.Ids,ct);
            if(!Active||ct.IsCancellationRequested||bridge.SessionId!=sid||Native.GetForegroundWindow()!=fg)return;
            await PostScreen(sid,DevicePayloads.Screen(confirmed,window.Process,versions.Next(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),ct);
            if(!Active||ct.IsCancellationRequested)return;
            lastHash=hash;hashForeground=fg;frameProcess=window.Process;ids=confirmed;bridge.Screen(ids);schedule.Success(Environment.TickCount64);
            if(terms.Length==0)terms=await client.Dictionary(ct);
        }
        catch(OperationCanceledException){}
        catch{schedule.Failure(Environment.TickCount64);await ClearContext(sid);Notice("Desk/Registry nicht erreichbar");}
        finally{busy=false;}
    }
    static ulong PerceptualHash(Bitmap image)
    {
        using var small=new Bitmap(image,new Size(9,8));ulong hash=0;
        for(int y=0;y<8;y++)for(int x=0;x<8;x++)if(small.GetPixel(x,y).GetBrightness()>small.GetPixel(x+1,y).GetBrightness())hash|=1UL<<(y*8+x);
        return hash;
    }
    async Task FinishDraw()
    {
        if(finishingDraw||drawings.Count==0)return;finishingDraw=true;
        var batch=drawings.ToArray();drawings.Clear();string? sid=bridge.SessionId;var ct=epoch.Token;
        try
        {
            foreach(var d in batch)d.Hide();
            foreach(var d in batch)
            {
                if(!Active||sid is null||ct.IsCancellationRequested)break;
                var points=d.Strokes.SelectMany(s=>s).ToArray();if(points.Length==0)continue;
                // Recheck the actual top-level window under every stroke point with overlays hidden.
                if(points.Any(p=>GetAncestor(WindowFromPoint(new((int)p.X+d.Monitor.X,(int)p.Y+d.Monitor.Y)),2)!=underlying))continue;
                var windows=privacy.Snapshot(d.Monitor);
                bool idsOnly=windows.Any(w=>w.Handle==underlying&&w.IdsOnly);
                using var clean=privacy.Capture(d.Monitor,true,false,underlying,!idsOnly);if(clean is null)continue;
                float minX=points.Min(p=>p.X),maxX=points.Max(p=>p.X),minY=points.Min(p=>p.Y),maxY=points.Max(p=>p.Y);
                int margin=(int)Math.Ceiling(Math.Max(maxX-minX,maxY-minY)*.15)+5;
                var bounds=Rectangle.Intersect(new Rectangle(0,0,clean.Width,clean.Height),Rectangle.FromLTRB((int)minX-margin,(int)minY-margin,(int)maxX+margin+1,(int)maxY+margin+1));
                if(idsOnly)
                {
                    var local=await Ocr.IdsIntersecting(PrivacyCapture.Png(clean),bounds);
                    var confirmed=await client.Exists(local,ct);
                    if(Active&&!ct.IsCancellationRequested&&bridge.SessionId==sid)
                        await client.Post("api/device/session/"+Uri.EscapeDataString(sid)+"/highlight",new IdsHighlightPayload(DevicePayloads.Ids(confirmed),new(bounds.X+d.Monitor.X,bounds.Y+d.Monitor.Y,bounds.Width,bounds.Height)),ct);
                    Notice("Markierung · nur IDs");continue;
                }
                d.Composite(clean);
                using var crop=clean.Clone(bounds,PixelFormat.Format32bppArgb);
                byte[] png=PrivacyCapture.Png(crop);
                int cropWidth=crop.Width,cropHeight=crop.Height;
                while(png.Length>2*1024*1024&&cropWidth>64&&cropHeight>64)
                {
                    cropWidth=Math.Max(1,(int)(cropWidth*.8));cropHeight=Math.Max(1,(int)(cropHeight*.8));
                    using var resized=new Bitmap(crop,new Size(cropWidth,cropHeight));png=PrivacyCapture.Png(resized);
                }
                double scale=Math.Min(1,1280.0/Math.Max(clean.Width,clean.Height));
                using var small=new Bitmap(clean,new Size(Math.Max(1,(int)(clean.Width*scale)),Math.Max(1,(int)(clean.Height*scale))));
                using var stream=new MemoryStream();small.Save(stream,ImageFormat.Jpeg);
                var body=DevicePayloads.Highlight(png,stream.ToArray(),new(bounds.X+d.Monitor.X,bounds.Y+d.Monitor.Y,bounds.Width,bounds.Height),ids);
                if(!Active||ct.IsCancellationRequested||bridge.SessionId!=sid)break;
                await client.Post("api/device/session/"+Uri.EscapeDataString(sid)+"/highlight",body,ct);Notice("Markierung gesendet");
            }
            if(!ct.IsCancellationRequested&&Active)
            {
                foreach(var d in batch){d.Show();d.Fade();}
                await Task.Delay(1500,ct);
            }
        }
        catch(OperationCanceledException){}
        catch(HttpRequestException ex) when(ex.StatusCode==System.Net.HttpStatusCode.Conflict){Notice("Keine aktive Stimme · Markierung nicht gesendet");}
        catch{Notice("Markierung nicht gesendet · Verbindung prüfen");}
        finally{foreach(var d in batch)d.Close();finishingDraw=false;}
    }
    [StructLayout(LayoutKind.Sequential)]readonly record struct Point(int X,int Y);
    [DllImport("user32.dll")]static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]static extern IntPtr GetAncestor(IntPtr window,uint flags);
    public void Dispose()
    {
        _=ClearContext(contextSid??bridge.SessionId);_=bridge.Voice(false,config.VoiceProvider);
        disposed=true;timer.Stop();epoch.Cancel();learner.Cancel();audio?.Dispose();audio=null;
        foreach(var d in drawings)d.Close();drawings.Clear();tray.Visible=false;tray.Dispose();epoch.Dispose();
    }
}
