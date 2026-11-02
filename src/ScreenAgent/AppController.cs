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
    readonly StatusToast toast;
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
    readonly VoiceSync voice=new();
    Level shownLevel=Level.Idle;string shownText="";
    string? toastText;Level toastLevel;bool unmuteNoticed;
    readonly System.Windows.Forms.ToolStripMenuItem statusItem=new(){Enabled=false},sessionItem=new(),pauseItem=new();
    string? contextSid;
    long unmuteRetryAt;
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
    public AppController(AppConfig config,DeviceClient client,AgentBridge bridge,KeyboardHook hook,StatusToast toast)
    {
        this.config=config;this.client=client;this.bridge=bridge;this.hook=hook;this.toast=toast;
        privacy=new(config.Policy(),()=>Active);learner=new(client,config.Policy());
        bridge.SessionChanged+=()=>{voice.PageChanged();voiceFailed=false;Reset();};
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
                if(!hook.Healthy){if(ids.Length>0||audio!=null)Reset();await SyncVoice();UpdateStatus();return;}
                if(drawings.Count>0&&!finishingDraw&&drawings.Any(d=>d.LastStrokeAt>0)&&drawings.All(d=>!d.InStroke)&&Environment.TickCount64-drawings.Max(d=>d.LastStrokeAt)>=700)await FinishDraw();
                await SyncVoice();
                UpdateStatus();
                await Watch();
            }
            catch{Notice("Anfrage fehlgeschlagen",Level.Warn);}
        };timer.Start();
        var menu=new System.Windows.Forms.ContextMenuStrip();
        string chord=config.Hotkeys["session"];
        menu.Items.Add(statusItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Desk öffnen",null,(_,_)=>bridge.ShowDesk());
        sessionItem.Click+=(_,_)=>hook.SetSession(!hook.Session);menu.Items.Add(sessionItem);
        pauseItem.Click+=(_,_)=>hook.SetPaused(!hook.Paused);menu.Items.Add(pauseItem);
        menu.Items.Add("Gerät trennen",null,(_,_)=>{Reset();bridge.Disconnect();});
        menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem($"Tastenkürzel: {chord} = Sitzung an/aus"){Enabled=false});
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Beenden",null,async (_,_)=>{paused=true;Reset();await bridge.Voice(false,config.VoiceProvider);await ClearContext(contextSid??bridge.SessionId);await Unmute();bridge.AllowClose=true;System.Windows.Application.Current.Shutdown();});
        menu.Opening+=(_,_)=>
        {
            var st=Current();statusItem.Text=st.Text;
            sessionItem.Text=hook.Session?"Sitzung beenden":"Sitzung starten";
            pauseItem.Text=hook.Paused?"Fortsetzen":"Pause";
        };
        tray=new(){Icon=TrayIcons.For(Level.Idle),Text="Screen Agent",Visible=true,ContextMenuStrip=menu};
        // A left click is the obvious thing to try on a tray icon; the menu stays on the right button.
        tray.MouseClick+=(_,e)=>{if(e.Button==System.Windows.Forms.MouseButtons.Left)bridge.ShowDesk();};
        tray.BalloonTipClicked+=(_,_)=>bridge.ShowDesk();
        if(config.HotkeysMigrated)Balloon($"Tastenkürzel geändert: Sitzung jetzt {chord}. Ctrl+Win bleibt frei für Wispr.");
        else if(new HotkeyBindings(config.Hotkeys).CollidesWithWispr)Balloon("Achtung: Ctrl+Win ist auch die Wispr-Taste. Ändere „Hotkeys“ in config.json (Beispiel: Alt+Win), sonst stören sich beide.");
        else if(!client.Paired)Balloon("Screen Agent läuft im Tray (Symbol unten rechts, evtl. unter ^). Rechtsklick → Desk öffnen, um das Gerät zu verbinden.");
    }
    void Balloon(string text){tray.BalloonTipTitle="Screen Agent";tray.BalloonTipText=text;tray.BalloonTipIcon=System.Windows.Forms.ToolTipIcon.Info;tray.ShowBalloonTip(8000);}
    Status Current()=>StatusLine.Compute(new(hook.Healthy,client.Paired,bridge.SessionId is not null,hook.Session,paused||hook.Paused,voiceFailed,needsUnmute,
        schedule.Ready(Environment.TickCount64),privacyBlocked,audio!=null,drawings.Count>0,ids.Length,config.Hotkeys["session"]));
    // The single place that touches the visible state: tray icon, tray tooltip, and the toast.
    void UpdateStatus()
    {
        var st=Current();bool notice=Environment.TickCount64<noticeUntil;
        if(st.Level!=shownLevel){shownLevel=st.Level;tray.Icon=TrayIcons.For(st.Level);}
        string tip=st.Text.Length>120?st.Text[..120]:st.Text;
        if(tip!=shownText){shownText=tip;tray.Text=tip;}
        if(notice)return;   // the notice keeps the toast until it expires
        if(st.ShowToast&&config.Indicator=="session")
        {
            if(toastText!=st.Text||toastLevel!=st.Level||!toast.IsVisible){toastText=st.Text;toastLevel=st.Level;toast.Present(st.Text,st.Level);}
        }
        else{toastText=null;toast.Dismiss();}
    }
    bool Active=>!disposed&&client.Paired&&bridge.SessionId is not null&&hook.Healthy&&hook.Session&&!hook.Paused&&!paused;
    void Notice(string text,Level level=Level.Active){noticeUntil=Environment.TickCount64+2200;toastText=null;toast.Present(text,level);}
    // Desk voice follows the session; see VoiceSync for why a stop is only sent after a start.
    async Task SyncVoice()
    {
        var step=voice.Next(Active,bridge.VoiceOn,Environment.TickCount64,voiceSyncing);
        voiceFailed=voice.Failed;
        if(step==VoiceStep.None)return;
        voiceSyncing=true;
        try
        {
            await lifecycle.Enqueue(async ()=>
            {
                bool ok=await bridge.Voice(step==VoiceStep.Start,config.VoiceProvider);
                voice.Result(step==VoiceStep.Start,ok,Environment.TickCount64);voiceFailed=voice.Failed;
            });
        }
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
        catch{pendingClear=sid;clearRetry.Failure(Environment.TickCount64);Notice("Kontext-Löschung ausstehend",Level.Warn);}
        finally{clearing=false;}
    }
    string? pendingClear;
    async Task Unmute()
    {
        if(unmuting)return;
        unmuting=true;needsUnmute=true;
        try{needsUnmute=!await bridge.Mute(false);}
        finally{unmuting=false;}
        if(!needsUnmute)unmuteNoticed=false;
        else if(!unmuteNoticed){unmuteNoticed=true;Notice("Desk-Mikro stumm",Level.Warn);}   // once; the status line keeps saying it
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
        if(action is ActionKind.SessionToggle or ActionKind.PauseToggle)
        {
            Reset();
            if(action==ActionKind.SessionToggle)
            {
                // Say what the key did and, if the session cannot work yet, what is missing: never a bare green "an".
                var st=Current();
                if(hook.Session)Notice(st.Level==Level.Warn?st.Text:"Sitzung an",st.Level==Level.Warn?Level.Warn:Level.Active);else Notice("Sitzung aus",Level.Idle);
            }
            else Notice(hook.Paused?"Pausiert":"Fortgesetzt",hook.Paused?Level.Paused:Level.Active);
            return;
        }
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
                if(!await bridge.Mute(true)){await Unmute();Notice("Diktat · Mikrofon nicht bestätigt",Level.Warn);return;}
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
                    catch(OperationCanceledException){}catch{Notice("Diktat fehlgeschlagen",Level.Warn);}
                });
            }
            finally{recording.Dispose();needsUnmute=true;}
        }
        catch(OperationCanceledException){}
        catch{audio?.Dispose();audio=null;await Unmute();Notice("Diktat fehlgeschlagen",Level.Warn);}
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
        catch{schedule.Failure(Environment.TickCount64);await ClearContext(sid);Notice("Desk/Registry nicht erreichbar",Level.Warn);}
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
        catch(HttpRequestException ex) when(ex.StatusCode==System.Net.HttpStatusCode.Conflict){Notice("Keine aktive Stimme · Markierung nicht gesendet",Level.Warn);}
        catch{Notice("Markierung nicht gesendet · Verbindung prüfen",Level.Warn);}
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
