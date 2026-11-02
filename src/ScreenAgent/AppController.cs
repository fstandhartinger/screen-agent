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
    readonly Telemetry telemetry;
    readonly FramePlanner planner;
    string? freshId,commandsSid;
    bool frameStopped,micMuted,manualSession,prevVoice,prevActive;
    long frameBackoffUntil,mutePollAt;
    public AppController(AppConfig config,DeviceClient client,AgentBridge bridge,KeyboardHook hook,StatusToast toast)
    {
        this.config=config;this.client=client;this.bridge=bridge;this.hook=hook;this.toast=toast;
        privacy=new(config.Policy(),()=>Active);learner=new(client,config.Policy());
        telemetry=new(config,client,()=>bridge.SessionId);planner=new(config.IntervalMs);
        telemetry.Event("client.app",new(){["version"]=AppInfo.Version,["commit"]=AppInfo.Commit,["device"]="windows",["session"]=config.Hotkeys["session"],["dictation"]=config.Hotkeys["dictation"],["dictationToggle"]=config.Hotkeys["dictationToggle"],["mute"]=config.Hotkeys["mute"],["frames"]=config.SendFrames,["hookHealthy"]=hook.Healthy});
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
                await Lifecycle();
                UpdateStatus();
                await Watch();
            }
            catch{Notice("Anfrage fehlgeschlagen",Level.Warn);}
        };timer.Start();
        var menu=new System.Windows.Forms.ContextMenuStrip();
        string chord=KeyNames.Display(config.Hotkeys["session"]);
        menu.Items.Add(statusItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Desk öffnen",null,(_,_)=>bridge.ShowDesk());
        sessionItem.Click+=(_,_)=>hook.SetSession(!hook.Session);menu.Items.Add(sessionItem);
        pauseItem.Click+=(_,_)=>hook.SetPaused(!hook.Paused);menu.Items.Add(pauseItem);
        menu.Items.Add("Gerät trennen",null,(_,_)=>{Reset();bridge.Disconnect();});
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        foreach(var line in KeyHelp(config.Hotkeys))menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem(line){Enabled=false});
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
        if(config.HotkeysMigrated)Balloon($"Tastenkürzel: Sitzung jetzt {chord} (wie ursprünglich), Diktat an/aus {KeyNames.Display(config.Hotkeys["dictationToggle"])}. Strg+Win allein bleibt frei für Wispr.");
        else if(new HotkeyBindings(config.Hotkeys).CollidesWithWispr)Balloon("Achtung: Strg+Win als Diktat-Taste ist auch die Wispr-Taste. Ändere „Hotkeys“ in config.json (Beispiel: Alt+Win), sonst stören sich beide.");
        else if(!client.Paired)Balloon($"Screen Agent läuft im Tray (Symbol unten rechts, evtl. unter ^). Rechtsklick → Desk öffnen, um das Gerät zu verbinden. Danach: {chord} startet und beendet das Gespräch.");
    }
    // The tray lists every key, so the user never has to remember them or read the README.
    static string[] KeyHelp(Dictionary<string,string> k)=>
    [
        $"{KeyNames.Display(k["session"])}: Gespräch an/aus (Reihenfolge egal)",
        $"{KeyNames.Display(k["mute"])}: Mikro stumm/an",
        $"{KeyNames.Display(k["pause"])}: Pause",
        $"{KeyNames.Display(k["dictation"])} halten: Diktat",
        $"{KeyNames.Display(k["dictationToggle"])}: Diktat an/aus",
        $"{(k["draw"]=="Caps"?"Feststelltaste":k["draw"])} halten: zeichnen",
    ];
    void Balloon(string text){tray.BalloonTipTitle="Screen Agent";tray.BalloonTipText=text;tray.BalloonTipIcon=System.Windows.Forms.ToolTipIcon.Info;tray.ShowBalloonTip(8000);}
    Status Current()=>StatusLine.Compute(new(hook.Healthy,client.Paired,bridge.SessionId is not null,hook.Session,paused||hook.Paused,voiceFailed,needsUnmute,
        schedule.Ready(Environment.TickCount64),privacyBlocked,audio!=null,drawings.Count>0,ids.Length,KeyNames.Display(config.Hotkeys["session"]),micMuted&&bridge.VoiceOn,KeyNames.Display(config.Hotkeys["mute"])));
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
        planner.Reset();freshId=null;commandsSid=null;frameStopped=false;
        foreach(var d in drawings)d.Close();drawings.Clear();
        QueueAudio(ActionKind.DictationDiscard);
    }
    static string HotkeyEvent(ActionKind a)=>a switch{ActionKind.SessionToggle=>"hotkey.session",ActionKind.PauseToggle=>"hotkey.pause",ActionKind.MuteToggle=>"hotkey.mute",ActionKind.DictationStart=>"hotkey.dictation.start",ActionKind.DictationStop=>"hotkey.dictation.stop",ActionKind.DictationDiscard=>"hotkey.dictation.discard",ActionKind.DrawStart=>"hotkey.draw.start",ActionKind.DrawStop=>"hotkey.draw.stop",_=>"hotkey."+a.ToString().ToLowerInvariant()};
    void Action(ActionKind action)
    {
        if(action is not (ActionKind.CapsTap or ActionKind.DictationBind or ActionKind.DrawBind))
            telemetry.Event(HotkeyEvent(action),new(){["session"]=hook.Session,["paused"]=hook.Paused,["voice"]=bridge.VoiceOn,["muted"]=micMuted,["dictating"]=hook.Dictating});
        if(action==ActionKind.MuteToggle){_=ToggleMute();return;}
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
    // Called every tick: the bits of session life that are not a hotkey (dialog started in the Desk window, mute state, lifecycle log).
    async Task Lifecycle()
    {
        bool voice=bridge.VoiceOn,active=Active;
        // A dialog started with the purple button in the Desk window is a session too: same indicator, same screen context.
        if(voice&&!prevVoice&&!hook.Session&&!paused&&client.Paired){manualSession=true;hook.SetSession(true);}
        if(!voice&&prevVoice&&manualSession){manualSession=false;hook.SetSession(false);}
        if(voice!=prevVoice){telemetry.Event("client.voice",new(){["on"]=voice,["manual"]=manualSession});prevVoice=voice;if(!voice)micMuted=false;}
        if(active!=prevActive)
        {
            telemetry.Event("client.session",new(){["state"]=active?"start":"stop",["version"]=AppInfo.Version,["commit"]=AppInfo.Commit,["device"]=config.SendFrames?"windows":"windows-noframes"});
            prevActive=active;
            if(!active){micMuted=false;_=telemetry.Flush(contextSid??bridge.SessionId);}
        }
        // The dock button can mute too: follow the page so the toast and tray never disagree with the microphone.
        if(voice&&active&&Environment.TickCount64>=mutePollAt)
        {
            mutePollAt=Environment.TickCount64+1000;
            if(await bridge.MicMuted() is {} now&&now!=micMuted){micMuted=now;telemetry.Event("hotkey.mute.state",new(){["muted"]=now,["source"]="page"});}
        }
    }
    async Task ToggleMute()
    {
        if(!Active||!bridge.VoiceOn){Notice("Kein Gespräch aktiv · nichts zu stummschalten",Level.Warn);return;}
        var muted=await bridge.ToggleMicMute();
        if(muted is null){Notice("Stumm nicht möglich · Desk antwortet nicht",Level.Warn);return;}
        micMuted=muted.Value;mutePollAt=Environment.TickCount64+1000;
        telemetry.Event("hotkey.mute.state",new(){["muted"]=micMuted,["source"]="hotkey"});
        Notice(micMuted?"Mikro stumm":"Mikro an",micMuted?Level.Muted:Level.Active);
    }
    // Long poll: the voice agent asks for a fresh picture ("was siehst du jetzt?") through look_at_screen.
    async Task CommandLoop(string sid,CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {
                using var reply=await client.Raw(HttpMethod.Get,"api/device/session/"+Uri.EscapeDataString(sid)+"/commands",null,ct);
                if(reply.Status==409&&reply.Stop||reply.Code=="no-session"){frameStopped=true;telemetry.Event("screen.stop",new(){["why"]="no-session",["via"]="commands"});return;}
                if(reply.Ok&&reply.Json is {} j&&j.RootElement.TryGetProperty("fresh",out var f)&&f.ValueKind==System.Text.Json.JsonValueKind.True&&j.RootElement.TryGetProperty("id",out var id)&&id.ValueKind==System.Text.Json.JsonValueKind.String)
                {freshId=id.GetString();telemetry.Event("screen.fresh.request",new(){["id"]=freshId});}
                else if(!reply.Ok)await Task.Delay(3000,ct);
            }
            catch(OperationCanceledException){return;}
            catch{try{await Task.Delay(3000,ct);}catch(OperationCanceledException){return;}}
        }
    }
    async Task Watch()
    {
        var fg=Native.GetForegroundWindow();long now=Environment.TickCount64;
        bool voice=bridge.VoiceOn;
        if(!voice&&(planner.Started||frameStopped||freshId!=null)){planner.Reset();frameStopped=false;freshId=null;commandsSid=null;}
        bool frames=config.SendFrames&&voice&&!frameStopped&&now>=frameBackoffUntil;
        bool idsDue=!(fg==lastForeground&&now-capturedAt<config.IntervalMs&&!schedule.RenewalDue(now));
        bool frameDue=frames&&planner.CaptureDue(now,(long)fg,freshId!=null);
        if(busy||!schedule.Ready(now)||!Active||drawings.Count>0||!(idsDue||frameDue))return;
        busy=true;capturedAt=now;lastForeground=fg;planner.Captured(now,(long)fg);
        var ct=epoch.Token;string sid=bridge.SessionId!;contextSid=sid;
        if(frames&&commandsSid!=sid){commandsSid=sid;_=CommandLoop(sid,ct);}
        try
        {
            if(pendingClear is {} oldSid){pendingClear=null;await ClearContext(oldSid);if(pendingClear!=null)throw new HttpRequestException();}
            var monitor=System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds;
            using var frame=privacy.Capture(monitor,true,false);
            if(frame is null)
            {
                privacyBlocked=true;ids=[];lastHash=null;bridge.Screen(ids);Notice("Privat · nicht erfasst");
                telemetry.Event("screen.frame",new(){["decision"]="skipped",["why"]="private-or-secure-desktop"});
                freshId=null;await ClearContext(sid);schedule.Success(Environment.TickCount64);
                return;
            }
            privacyBlocked=false;
            var luma=Luma(frame);ulong hash=DHash.Compute(luma);
            var windows=privacy.Snapshot(monitor);var window=windows.FirstOrDefault(w=>w.Handle==fg);
            if(lastHash==hash&&hashForeground==fg)
            {
                if(schedule.RenewalDue(Environment.TickCount64))
                {
                    await PostScreen(sid,DevicePayloads.Screen(ids,frameProcess,versions.Next(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),ct);
                    schedule.Success(Environment.TickCount64);
                }
            }
            else
            {
                if(window is null||window.Blocked){telemetry.Event("screen.frame",new(){["decision"]="masked",["why"]="foreground-blocked"});freshId=null;await ClearContext(sid);return;}
                double ocrScale=Math.Min(1,2560.0/Math.Max(frame.Width,frame.Height));
                using var ocrFrame=new Bitmap(frame,new Size(Math.Max(1,(int)(frame.Width*ocrScale)),Math.Max(1,(int)(frame.Height*ocrScale))));
                byte[] png=PrivacyCapture.Png(ocrFrame);var ocr=await Task.Run(()=>Ocr.ReadMemory(png),ct);
                var confirmed=await client.Exists(ocr.Ids,ct);
                if(!Active||ct.IsCancellationRequested||bridge.SessionId!=sid||Native.GetForegroundWindow()!=fg)return;
                await PostScreen(sid,DevicePayloads.Screen(confirmed,window.Process,versions.Next(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),ct);
                if(!Active||ct.IsCancellationRequested)return;
                bool changed=!confirmed.SequenceEqual(ids);
                lastHash=hash;hashForeground=fg;frameProcess=window.Process;ids=confirmed;bridge.Screen(ids);schedule.Success(Environment.TickCount64);
                if(changed)telemetry.Event("screen.ids",new(){["ids"]=confirmed,["count"]=confirmed.Length,["process"]=window.Process});
                if(terms.Length==0)terms=await client.Dictionary(ct);
            }
            if(frames&&Active&&!ct.IsCancellationRequested&&bridge.SessionId==sid)await SendFrame(frame,windows,window,fg,hash,sid,ct);
        }
        catch(OperationCanceledException){}
        catch{schedule.Failure(Environment.TickCount64);telemetry.Event("screen.error",new(){["where"]="watch"});await ClearContext(sid);Notice("Desk/Registry nicht erreichbar",Level.Warn);}
        finally{busy=false;}
    }
    // One screenshot to the vision model. Order of the fences: blocked window (never captured), dedupe, Earnings app (ids only, no image),
    // then black rectangles over every blocked or ids-only window still visible, then encode in memory and send.
    async Task SendFrame(Bitmap frame,List<WindowInfo> windows,WindowInfo? window,IntPtr fg,ulong hash,string sid,CancellationToken ct)
    {
        string? command=freshId;bool fresh=command!=null;long now=Environment.TickCount64;
        if(window is null||window.Blocked){freshId=null;telemetry.Event("screen.frame",new(){["decision"]="masked",["why"]="foreground-blocked"});return;}
        var decision=planner.Decide(now,hash,(long)fg,fresh);
        if(decision.Verdict==FrameVerdict.Wait)return;
        if(decision.Verdict==FrameVerdict.Deduped){telemetry.Event("screen.frame",new(){["decision"]="deduped",["why"]="unchanged"});return;}
        if(window.IdsOnly)
        {
            // The Earnings app: ids only, never an image of it. The ids already went out through /screen.
            freshId=null;planner.Sent(now,hash,(long)fg);
            telemetry.Event("screen.frame",new(){["decision"]="skipped",["why"]="ids-only-window",["reason"]=decision.Reason.ToString().ToLowerInvariant(),["process"]=window.Process});
            return;
        }
        var monitor=System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds;
        int masked=0;
        using(var g=Graphics.FromImage(frame))
            foreach(var w in windows.Where(w=>w.Handle!=fg&&(w.Blocked||w.IdsOnly)))
            {
                var r=Rectangle.Intersect(monitor,w.Rect);if(r.IsEmpty)continue;
                r.Offset(-monitor.X,-monitor.Y);g.FillRectangle(Brushes.Black,r);masked++;
            }
        var (jpeg,width,height)=await Task.Run(()=>FrameJpeg.Encode(frame),ct);
        if(!Active||ct.IsCancellationRequested||bridge.SessionId!=sid||Native.GetForegroundWindow()!=fg&&!fresh)return;
        string? url=window.Url is {} u&&(u.StartsWith("http://")||u.StartsWith("https://"))?(u.Length>300?u[..300]:u):null;
        var body=new Dictionary<string,object?>
        {
            ["deviceId"]=client.DeviceId,["frameVersion"]=versions.Next(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),["jpeg"]=Convert.ToBase64String(jpeg),
            ["process"]=window.Process.Length>100?window.Process[..100]:window.Process,["title"]=window.Title.Length>200?window.Title[..200]:window.Title,
            ["masked"]=Math.Min(200,masked),["ids"]=ids,["reason"]=decision.Reason.ToString().ToLowerInvariant()
        };
        if(url!=null)body["url"]=url;
        if(fresh)body["commandId"]=command;
        using var reply=await client.Raw(HttpMethod.Post,"api/device/session/"+Uri.EscapeDataString(sid)+"/frame",body,ct);
        freshId=null;
        string vision=reply.Json is {} j&&j.RootElement.ValueKind==System.Text.Json.JsonValueKind.Object&&j.RootElement.TryGetProperty("vision",out var v)&&v.ValueKind==System.Text.Json.JsonValueKind.String?v.GetString()??"":"";
        if(reply.Ok)
        {
            planner.Sent(now,hash,(long)fg);
            telemetry.Event("screen.frame",new(){["decision"]="sent",["reason"]=decision.Reason.ToString().ToLowerInvariant(),["bytes"]=jpeg.Length,["w"]=width,["h"]=height,["masked"]=masked,["ids"]=ids.Length,["vision"]=vision,["process"]=window.Process});
            if(reply.Json is {} r2&&r2.RootElement.TryGetProperty("fresh",out var f)&&f.ValueKind==System.Text.Json.JsonValueKind.Object&&f.TryGetProperty("id",out var fid)&&fid.ValueKind==System.Text.Json.JsonValueKind.String)freshId=fid.GetString();
            return;
        }
        // 409 no-session: the voice dialog is gone, stop for good until the next one. stale-frame: drop it and carry on.
        if(reply.Status==409&&(reply.Stop||reply.Code=="no-session")){frameStopped=true;telemetry.Event("screen.stop",new(){["why"]="no-session",["via"]="frame"});return;}
        if(reply.Status==429){frameBackoffUntil=Environment.TickCount64+Math.Clamp(reply.RetryAfter,2,60)*1000L;}
        telemetry.Event("screen.frame",new(){["decision"]="skipped",["why"]="rejected",["status"]=reply.Status,["code"]=reply.Code??"",["bytes"]=jpeg.Length});
    }
    // 9x8 brightness grid for the dHash (area-averaged by the resize, so one-pixel changes do not flip bits).
    static float[] Luma(Bitmap image)
    {
        using var small=new Bitmap(DHash.Width,DHash.Height);
        using(var g=Graphics.FromImage(small)){g.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;g.DrawImage(image,new Rectangle(0,0,DHash.Width,DHash.Height));}
        var grid=new float[DHash.Width*DHash.Height];
        for(int y=0;y<DHash.Height;y++)for(int x=0;x<DHash.Width;x++)grid[y*DHash.Width+x]=small.GetPixel(x,y).GetBrightness();
        return grid;
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
        telemetry.Event("client.app.stop");_=telemetry.Flush(contextSid??bridge.SessionId);
        disposed=true;timer.Stop();epoch.Cancel();learner.Cancel();audio?.Dispose();audio=null;
        foreach(var d in drawings)d.Close();drawings.Clear();tray.Visible=false;tray.Dispose();epoch.Dispose();
    }
}
