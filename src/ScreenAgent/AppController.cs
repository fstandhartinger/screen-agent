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
    bool paused,busy,disposed,locked,excluded;
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
    IntPtr lastForeground,underlying,ocrForeground;
    byte[]? ocrGrid;
    bool ocrRunning;
    int ocrGen;
    // ids = what the agent is told: OCR candidates the registry confirmed plus ids the vision model read (verified by the gateway).
    string[] ids=[],ocrIds=[],visionIds=[],terms=[];
    readonly RunLog quiet=new();
    TextInjector.Target? target,audioTarget;
    string before="",process="";
    DictationCapture? audio;
    string? audioSession;
    Task audioQueue=Task.CompletedTask;
    readonly Telemetry telemetry;
    readonly FramePlanner planner;
    string? freshId,commandsSid;
    readonly MainAgentPrompt prompts;
    readonly PromptChannel promptChannel;
    CanvasPanel? canvas;
    readonly InputGate inputGate=new();
    (bool ByUser,string Why)? panelQueued;
    internal CanvasPanel? CanvasForTests=>canvas;
    bool canvasNoted;
    readonly ActivityOverlay? activity;
    readonly FeedbackGate feedback;
    readonly StartupTimeline startupTimeline=new();
    bool micMuted,manualSession,prevVoice,prevActive;
    long frameBackoffUntil,mutePollAt;
    public AppController(AppConfig config,DeviceClient client,AgentBridge bridge,KeyboardHook hook,StatusToast toast)
    {
        this.config=config;this.client=client;this.bridge=bridge;this.hook=hook;this.toast=toast;
        privacy=new(config.Policy(),()=>Active);learner=new(client,config.Policy(),config.LearnThreshold);
        telemetry=new(config,client,()=>bridge.SessionId);planner=new(config.IntervalMs);feedback=new(config.ScreenshotFeedback);
        if(config.ActivityOverlay)activity=new ActivityOverlay(()=>toast.IsVisible?(int)Math.Ceiling(toast.ActualHeight*System.Windows.Media.VisualTreeHelper.GetDpi(toast).DpiScaleY)+6:0);
        bridge.Activity+=e=>OnActivity(e,false);bridge.ActivityRejected+=why=>telemetry.Event("activity.invalid",new(){["reason"]=why});
        bridge.Milestone+=(name,ms)=>telemetry.Event("session.startup",new(){["phase"]="app-"+name,["ms"]=ms});   // before any session: it reaches the trace with the first one
        prompts=new(config.MainAgentInputNames,Trace,config.MainAgentAllowStableCode);
        promptChannel=new(client,prompts.Run,Trace,
            (code,text,chars)=>bridge.Dispatcher.BeginInvoke(new System.Action(()=>{try{PromptFallbackWindow.Show(code,text,chars);}catch{ /* the result still tells */ }})),
            sid=>bridge.SessionId==sid&&bridge.VoiceOn,inputGate);
        // The drawing panel waits while a prompt is typed (it could take the focus back), and opens when the prompt is over.
        inputGate.PromptEnded+=()=>bridge.Dispatcher.BeginInvoke(new System.Action(()=>{if(panelQueued is {} q){panelQueued=null;_=ShowCanvas(q.ByUser,q.Why);}}));
        bridge.CanvasPending+=OnCanvasPending;bridge.CanvasToggle+=ToggleCanvas;
        if(config.HotkeyNote is {} hotkeyNote)telemetry.Event("config.note",new(){["why"]=hotkeyNote});
        telemetry.Event("client.app",new(){["version"]=AppInfo.Version,["commit"]=AppInfo.Commit,["device"]="windows",["session"]=config.Hotkeys["session"],["dictation"]=config.Hotkeys["dictation"],["dictationToggle"]=config.Hotkeys["dictationToggle"],["mute"]=config.Hotkeys["mute"],["draw"]=config.Hotkeys["draw"],["pause"]=config.Hotkeys["pause"],["frames"]=config.SendFrames,["lists"]=config.Policy().HasLists,["hookHealthy"]=hook.Healthy});
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
                activity?.Tick();
                if(pendingClear is {} pending&&!clearing&&clearRetry.Ready(Environment.TickCount64))_=ClearContext(pending,pendingWhy??"stop");
                if(needsUnmute&&audio is null&&!unmuting&&audioQueue.IsCompleted&&Environment.TickCount64>=unmuteRetryAt){unmuteRetryAt=Environment.TickCount64+2000;_=Unmute();}
                if(!hook.Healthy){if(ids.Length>0||audio!=null)Reset("error");await SyncVoice();UpdateStatus();return;}
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
        menu.Items.Add("Zeichnung zeigen",null,(_,_)=>{_=ShowCanvas(true,"tray");});
        sessionItem.Click+=(_,_)=>hook.SetSession(!hook.Session);menu.Items.Add(sessionItem);
        pauseItem.Click+=(_,_)=>hook.SetPaused(!hook.Paused);menu.Items.Add(pauseItem);
        menu.Items.Add("Gerät trennen",null,(_,_)=>{Reset("stop");bridge.Disconnect();});
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
        k["canvas"].Length==0?"Zeichnung: über das Tray-Menü":$"{KeyNames.Display(k["canvas"])}: Zeichnung zeigen/verbergen",
        $"{KeyNames.Display(k["dictation"])} halten: Diktat",
        $"{KeyNames.Display(k["dictationToggle"])}: Diktat an/aus",
        $"{(k["draw"]=="Caps"?"Feststelltaste":k["draw"])} halten: zeichnen",
    ];
    void Balloon(string text){tray.BalloonTipTitle="Screen Agent";tray.BalloonTipText=text;tray.BalloonTipIcon=System.Windows.Forms.ToolTipIcon.Info;tray.ShowBalloonTip(8000);}
    Status Current()=>StatusLine.Compute(new(hook.Healthy,client.Paired,bridge.SessionId is not null,hook.Session,paused||hook.Paused,voiceFailed,needsUnmute,
        schedule.Ready(Environment.TickCount64),locked,audio!=null,drawings.Count>0,ids.Length,KeyNames.Display(config.Hotkeys["session"]),micMuted&&bridge.VoiceOn,KeyNames.Display(config.Hotkeys["mute"]),excluded,
        hook.Session&&!bridge.VoiceOn&&startupTimeline.Active&&startupTimeline.Starting(Environment.TickCount64),startupTimeline.Seconds(Environment.TickCount64)));
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
    // session.startup {phase, ms since the hotkey}: each phase once per start.
    void StartMark(string phase,Dictionary<string,object?>? extra=null)
    {
        if(startupTimeline.Mark(phase,Environment.TickCount64) is not {} ms)return;
        extra??=new();extra["phase"]=phase;extra["ms"]=ms;telemetry.Event("session.startup",extra);
    }
    // Trace events of the Main Agent prompt: logged, and shown as one live row. Raised on a worker thread, drawn on the UI thread.
    void Trace(string type,Dictionary<string,object?> data)
    {
        telemetry.Event(type,data);
        if(activity is not null&&ActivityLocal.FromTrace(type,data) is {} e)bridge.Dispatcher.BeginInvoke(new System.Action(()=>OnActivity(e,true)));
    }
    void OnActivity(ActivityNote e,bool local)
    {
        if(activity is null||disposed)return;
        try{if(activity.Present(e))telemetry.Event("activity.shown",new(){["phase"]=e.Phase,["tool"]=e.Tool,["local"]=local});}
        catch(Exception ex){telemetry.Event("activity.error",new(){["type"]=ex.GetType().Name});}
    }
    // Desk voice follows the session; see VoiceSync for why a stop is only sent after a start.
    async Task SyncVoice()
    {
        var step=voice.Next(Active,bridge.VoiceOn,Environment.TickCount64,voiceSyncing);
        voiceFailed=voice.Failed;
        if(step==VoiceStep.None)return;
        voiceSyncing=true;
        if(step==VoiceStep.Start)StartMark("voice-request");
        try
        {
            await lifecycle.Enqueue(async ()=>
            {
                bool ok=await bridge.Voice(step==VoiceStep.Start,config.VoiceProvider);
                voice.Result(step==VoiceStep.Start,ok,Environment.TickCount64);voiceFailed=voice.Failed;
                if(step==VoiceStep.Start&&!ok)StartMark("voice-unacked");
            });
        }
        finally{voiceSyncing=false;}
    }
    // why: the contract's word for the gateway (locked | paused | stop | error). The agent's picture is gone afterwards, so the next frame starts over.
    async Task ClearContext(string? sid,string why="stop")
    {
        ForgetLocal();
        if(sid is null||!client.Paired)return;
        clearing=true;
        try
        {
            await screenWrites.Enqueue(()=>client.Post("api/device/session/"+Uri.EscapeDataString(sid)+"/screen/clear",DevicePayloads.Why(why),CancellationToken.None));
            if(pendingClear==sid)pendingClear=null;clearRetry.Success(Environment.TickCount64);
        }
        catch{pendingClear=sid;pendingWhy=why;clearRetry.Failure(Environment.TickCount64);Notice("Kontext-Löschung ausstehend",Level.Warn);}
        finally{clearing=false;}
    }
    string? pendingClear,pendingWhy;
    void ForgetLocal(){ids=ocrIds=visionIds=[];ocrGrid=null;planner.Reset();bridge.Screen([]);}
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
    void Reset(string why="stop")
    {
        _=ClearContext(contextSid??bridge.SessionId,why);
        epoch.Cancel();epoch.Dispose();epoch=new();ocrRunning=false;ocrGen++;capturedAt=0;lastForeground=IntPtr.Zero;learner.Cancel();
        locked=excluded=false;quiet.Break();freshId=null;commandsSid=null;
        foreach(var d in drawings)d.Close();drawings.Clear();
        QueueAudio(ActionKind.DictationDiscard);
    }
    static string HotkeyEvent(ActionKind a)=>a switch{ActionKind.SessionToggle=>"hotkey.session",ActionKind.PauseToggle=>"hotkey.pause",ActionKind.MuteToggle=>"hotkey.mute",ActionKind.CanvasToggle=>"hotkey.canvas",ActionKind.DictationStart=>"hotkey.dictation.start",ActionKind.DictationStop=>"hotkey.dictation.stop",ActionKind.DictationDiscard=>"hotkey.dictation.discard",ActionKind.DrawStart=>"hotkey.draw.start",ActionKind.DrawStop=>"hotkey.draw.stop",_=>"hotkey."+a.ToString().ToLowerInvariant()};
    void Action(ActionKind action)
    {
        if(action is not (ActionKind.CapsTap or ActionKind.DictationBind or ActionKind.DrawBind))
        {
            var data=new Dictionary<string,object?>{["session"]=hook.Session,["paused"]=hook.Paused,["voice"]=bridge.VoiceOn,["muted"]=micMuted,["dictating"]=hook.Dictating};
            if(action is ActionKind.DrawStart or ActionKind.DrawStop)data["key"]=config.Hotkeys["draw"];   // the log proves which key drew
            telemetry.Event(HotkeyEvent(action),data);
        }
        if(action==ActionKind.MuteToggle){_=ToggleMute();return;}
        if(action==ActionKind.CanvasToggle){ToggleCanvas();return;}
        if(action is ActionKind.DictationStart or ActionKind.DictationStop or ActionKind.DictationDiscard){QueueAudio(action);return;}
        if(action is ActionKind.SessionToggle or ActionKind.PauseToggle)
        {
            Reset(action==ActionKind.PauseToggle&&hook.Paused?"paused":"stop");
            if(action==ActionKind.SessionToggle)
            {
                // Say what the key did and, if the session cannot work yet, what is missing: never a bare green "an".
                if(hook.Session&&!bridge.VoiceOn){startupTimeline.Begin(Environment.TickCount64);StartMark("hotkey",new(){["browser"]=bridge.BrowserReady,["page"]=bridge.SessionId is not null});}
                else startupTimeline.End();
                var st=Current();bool say=st.Level is Level.Warn or Level.Busy;   // "Sitzung startet …" at once, not a bare "an" and then seconds of nothing
                if(hook.Session)Notice(say?st.Text:"Sitzung an",say?st.Level:Level.Active);else Notice("Sitzung aus",Level.Idle);
            }
            else Notice(hook.Paused?"Pausiert":"Fortgesetzt",hook.Paused?Level.Paused:Level.Active);
            return;
        }
        if(action==ActionKind.DrawStart&&Active&&drawings.Count==0)
        {
            // Always draws: whatever is in front, whatever is under the strokes. Whether and what to send is decided at the end.
            foreach(var screen in System.Windows.Forms.Screen.AllScreens){var draw=new DrawOverlay(screen);drawings.Add(draw);draw.Show();}
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
                var context=new DictationContext(proc,prior.Length<=500?prior:prior[^500..],ids.ToArray(),(string.IsNullOrWhiteSpace(config.UserEmail)?terms.Take(30):terms.Take(29).Prepend(config.UserEmail)).ToArray());
                _=transcriptions.Enqueue(async ()=>
                {
                    try
                    {
                        string text=await client.Dictate(wav,context,ct);ct.ThrowIfCancellationRequested();
                        bool injected=await TextInjector.Inject(bound,text,text.Length>=60&&!TextInjector.Terminal(bound.Window),config.Policy(),inputGate);
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
        if(startupTimeline.Active)
        {
            if(bridge.BrowserReady)StartMark("browser-ready");
            if(bridge.SessionId is not null)StartMark("page-session");
            if(voice){StartMark("voice-on");startupTimeline.End();}
            else if(!hook.Session)startupTimeline.End();
            else if(!startupTimeline.Starting(Environment.TickCount64))StartMark("gave-up-waiting");
        }
        // A dialog started with the purple button in the Desk window is a session too: same indicator, same screen context.
        if(voice&&!prevVoice&&!hook.Session&&!paused&&client.Paired){manualSession=true;hook.SetSession(true);}
        if(!voice&&prevVoice&&manualSession){manualSession=false;hook.SetSession(false);}
        if(voice!=prevVoice)
        {
            telemetry.Event("client.voice",new(){["on"]=voice,["manual"]=manualSession});prevVoice=voice;if(!voice)micMuted=false;
            if(voice)canvas?.SessionStarted();else{canvas?.HidePanel(false);activity?.Clear();}   // a closed panel stays closed until the next voice session; at the end of a session it goes away
        }
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
        while(!ct.IsCancellationRequested&&bridge.VoiceOn&&commandsSid==sid)
        {
            try
            {
                using var reply=await client.Raw(HttpMethod.Get,"api/device/session/"+Uri.EscapeDataString(sid)+"/commands",null,ct);
                // The page says voice is on before the gateway has its session (connecting), and a dialog can end: wait, the loop ends with VoiceOn.
                if(reply.Status==409&&reply.Stop||reply.Code=="no-session"){telemetry.Event("screen.wait",new(){["why"]="no-session",["via"]="commands"});await Task.Delay(3000,ct);continue;}
                if(reply.Ok&&reply.Json is {} pj&&PromptCommand.Parse(pj.RootElement) is {} request)_=promptChannel.Handle(sid,request,ct);   // typing runs on its own; the long poll is parked again at once
                else if(reply.Ok&&reply.Json is {} j&&j.RootElement.TryGetProperty("fresh",out var f)&&f.ValueKind==System.Text.Json.JsonValueKind.True&&j.RootElement.TryGetProperty("id",out var id)&&id.ValueKind==System.Text.Json.JsonValueKind.String)
                {freshId=id.GetString();telemetry.Event("screen.fresh.request",new(){["id"]=freshId});}
                else if(!reply.Ok)await Task.Delay(3000,ct);
            }
            catch(OperationCanceledException){return;}
            catch{try{await Task.Delay(3000,ct);}catch(OperationCanceledException){return;}}
        }
    }
    // Drawing panel. Auto-open only when allowed (config, not closed by him this session) and only with the capture exclusion confirmed.
    CanvasPanel Panel()=>canvas??=new CanvasPanel(bridge,config.CanvasPanel=="manual",(type,data)=>telemetry.Event(type,data));
    async Task ShowCanvas(bool byUser,string why)
    {
        try
        {
            if(inputGate.PromptActive){panelQueued=(byUser,why);return;}   // not while a prompt is typed; PromptEnded opens it
            var panel=Panel();
            if(!panel.Prepare())
            {
                if(!canvasNoted){canvasNoted=true;telemetry.Event("canvas.unavailable",new(){["why"]="affinity"});Notice("Zeichnung nicht verfügbar · Windows nimmt das Fenster nicht aus der Aufnahme",Level.Warn);}
                return;
            }
            // CanvasPolicy decides (first diagram: to the front; minimised: stays minimised; closed: stays closed) and logs canvas.panel.
            bool opened=byUser?await panel.UserShow(why):await panel.Pending();
            if(opened)telemetry.Event("canvas.open",new(){["why"]=why,["ok"]=true,["restores"]=panel.ForegroundRestores});
        }
        catch(Exception ex){telemetry.Event("canvas.error",new(){["type"]=ex.GetType().Name});}
    }
    void OnCanvasPending(){if(!bridge.VoiceOn)return;   // a drawing from the text chat, or one replayed at page load, opens nothing
        _=ShowCanvas(false,"pending");}
    void ToggleCanvas(){if(canvas is {PanelState:PanelState.Open} open)open.HidePanel(true);else _=ShowCanvas(true,"toggle");}
    async Task Watch()
    {
        var fg=Native.GetForegroundWindow();long now=Environment.TickCount64;
        bool voice=bridge.VoiceOn;
        if(!voice&&(planner.Started||commandsSid!=null||freshId!=null)){planner.Reset();freshId=null;commandsSid=null;}
        bool frames=config.SendFrames&&voice&&now>=frameBackoffUntil;
        bool idsDue=!(fg==lastForeground&&now-capturedAt<config.IntervalMs&&!schedule.RenewalDue(now));
        bool frameDue=frames&&planner.CaptureDue(now,(long)fg,freshId!=null);
        if(busy||!schedule.Ready(now)||!Active||drawings.Count>0||!(idsDue||frameDue))return;
        busy=true;capturedAt=now;lastForeground=fg;planner.Captured(now,(long)fg);
        var ct=epoch.Token;string sid=bridge.SessionId!;contextSid=sid;
        if(voice&&commandsSid!=sid){commandsSid=sid;_=CommandLoop(sid,ct);}   // also without frames: prompts for the Main Agent arrive on this channel
        try
        {
            if(pendingClear is {} oldSid){pendingClear=null;await ClearContext(oldSid,pendingWhy??"stop");if(pendingClear!=null)throw new HttpRequestException();}
            var monitor=System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds;
            CaptureInfo? info=null;
            using var frame=await Task.Run(()=>privacy.Capture(monitor,true,false,out info),ct);
            if(frame is null||info is null){await Skipped(privacy.LastSkip,sid,ct);return;}
            if(locked||excluded){locked=excluded=false;quiet.Break();}
            if(!Active||ct.IsCancellationRequested||bridge.SessionId!=sid)return;
            var grid=await Task.Run(()=>FrameGrid.Of(frame),ct);
            var window=info.Foreground;
            // OCR never delays the frame: it runs in the background on a copy of the native picture and posts the ids when it is done.
            if(!ocrRunning&&(ocrGrid is null||fg!=ocrForeground||ChangeDetector.Changed(ocrGrid,grid)))StartOcr(frame,grid,fg,window.Process,sid,ct);
            else if(schedule.RenewalDue(Environment.TickCount64))
            {
                await PostScreen(sid,DevicePayloads.Screen(ids,window.Process,versions.Next(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),ct);
                schedule.Success(Environment.TickCount64);
            }
            if(frames&&Active&&!ct.IsCancellationRequested&&bridge.SessionId==sid)await SendFrame(frame,info,fg,grid,monitor,sid,ct);
        }
        catch(OperationCanceledException){}
        catch{schedule.Failure(Environment.TickCount64);telemetry.Event("screen.error",new(){["where"]="watch"});await ClearContext(sid,"error");Notice("Desk/Registry nicht erreichbar",Level.Warn);}
        finally{busy=false;}
    }
    // No frame this tick. Only a lock screen (or a window from the user's own list) is told to the user and the agent; everything else is retried on the next tick without a word.
    async Task Skipped(string why,string sid,CancellationToken ct)
    {
        long now=Environment.TickCount64;
        if(quiet.ShouldLog("skip:"+why,now))telemetry.Event("screen.frame",new(){["decision"]="skipped",["why"]=why});
        if(why is not ("locked" or "config-list")){if(why=="capture-failed")schedule.Success(now);return;}
        bool entering=why=="locked"?!locked:!excluded;
        locked=why=="locked";excluded=why=="config-list";freshId=null;
        if(entering)
        {
            if(locked){Notice("Bildschirm gesperrt");await ClearContext(sid,"locked");}
            else
            {
                Notice("Fenster ausgenommen (Liste)");ForgetLocal();
                using var said=await client.Raw(HttpMethod.Post,"api/device/session/"+Uri.EscapeDataString(sid)+"/screen/private",new Dictionary<string,object?>{["why"]="excluded"},ct);   // the agent must not keep describing the previous window
            }
        }
        schedule.Success(now);
    }
    void StartOcr(Bitmap frame,byte[] grid,IntPtr fg,string process,string sid,CancellationToken ct)
    {
        var copy=new Bitmap(frame);ocrGrid=grid;ocrForeground=fg;ocrRunning=true;
        _=RunOcr(copy,fg,process,sid,ct,ocrGen);
    }
    async Task RunOcr(Bitmap copy,IntPtr fg,string process,string sid,CancellationToken ct,int gen)
    {
        try
        {
            using(copy)
            {
                var clock=System.Diagnostics.Stopwatch.StartNew();
                var found=await Task.Run(()=>Ocr.FindIds(copy,ct),ct);long ms=clock.ElapsedMilliseconds;
                var confirmed=await client.Exists(found.Ids,ct);
                if(gen!=ocrGen||!Active||ct.IsCancellationRequested||bridge.SessionId!=sid||Native.GetForegroundWindow()!=fg){ocrGrid=null;return;}   // the picture moved on: look again
                ocrIds=confirmed;var merged=DevicePayloads.Merge(ocrIds,visionIds);
                await PostScreen(sid,DevicePayloads.Screen(merged,process,versions.Next(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),ct);
                if(gen!=ocrGen||!Active||ct.IsCancellationRequested)return;
                telemetry.Event("screen.ocr",new(){["ms"]=ms,["engine"]=found.Engine,["candidates"]=found.Ids.Length,["confirmed"]=confirmed.Length});
                SetIds(merged,process,"ocr");frameProcess=process;schedule.Success(Environment.TickCount64);
                if(terms.Length==0)terms=await client.Dictionary(ct);
            }
        }
        catch(OperationCanceledException){}
        catch(Exception) when(gen==ocrGen)
        {
            ocrGrid=null;schedule.Failure(Environment.TickCount64);telemetry.Event("screen.error",new(){["where"]="ocr"});
            await ClearContext(sid,"error");Notice("Desk/Registry nicht erreichbar",Level.Warn);
        }
        finally{if(gen==ocrGen)ocrRunning=false;}
    }
    void SetIds(string[] merged,string process,string source)
    {
        bool changed=!merged.SequenceEqual(ids);ids=merged;bridge.Screen(ids);
        if(changed)telemetry.Event("screen.ids",new(){["ids"]=merged,["count"]=merged.Length,["process"]=process,["source"]=source});
    }
    // One screenshot to the vision model: change test against the last SENT frame, encode in memory, send with the ids known now.
    async Task SendFrame(Bitmap frame,CaptureInfo info,IntPtr fg,byte[] grid,System.Drawing.Rectangle monitor,string sid,CancellationToken ct)
    {
        string? command=freshId;bool fresh=command!=null;long now=Environment.TickCount64;var window=info.Foreground;
        var decision=planner.Decide(now,grid,(long)fg,fresh);
        if(decision.Verdict==FrameVerdict.Wait)return;
        if(decision.Verdict==FrameVerdict.Deduped){if(quiet.ShouldLog("deduped",now))telemetry.Event("screen.frame",new(){["decision"]="deduped",["why"]="unchanged"});return;}
        quiet.Break();
        int masked=info.Windows.Count(w=>w.Blocked);
        var (jpeg,width,height)=await Task.Run(()=>FrameJpeg.Encode(frame),ct);
        if(!Active||ct.IsCancellationRequested||bridge.SessionId!=sid||Native.GetForegroundWindow()!=fg&&!fresh)return;
        await GiveFeedback(frame,monitor,ScreenshotFeedback.Of(decision.Reason));   // after the capture, before the upload: he sees the moment the agent looks
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
            planner.Sent(now,grid,(long)fg);   // "queued" counts as sent; only an accepted frame becomes the reference
            if(vision is "ok" or "unchanged")visionIds=VisionIds(reply); // a queued or capped frame says nothing about the ids
            telemetry.Event("screen.frame",new(){["decision"]="sent",["reason"]=decision.Reason.ToString().ToLowerInvariant(),["bytes"]=jpeg.Length,["w"]=width,["h"]=height,["masked"]=masked,["ids"]=ids.Length,["visionIds"]=visionIds.Length,["vision"]=vision,["process"]=window.Process});
            if(visionIds.Length>0||ids.Except(ocrIds).Any())SetIds(DevicePayloads.Merge(ocrIds,visionIds),window.Process,"vision");
            if(reply.Json is {} r2&&r2.RootElement.TryGetProperty("fresh",out var f)&&f.ValueKind==System.Text.Json.JsonValueKind.Object&&f.TryGetProperty("id",out var fid)&&fid.ValueKind==System.Text.Json.JsonValueKind.String)freshId=fid.GetString();
            return;
        }
        // 409 no-session: the gateway has no session yet or any more; nothing is sent until it does. stale-frame: drop it and carry on. Not sent = not the reference.
        if(reply.Status==409&&(reply.Stop||reply.Code=="no-session")){frameBackoffUntil=Environment.TickCount64+3000;telemetry.Event("screen.wait",new(){["why"]="no-session",["via"]="frame"});return;}
        if(reply.Status==429){frameBackoffUntil=Environment.TickCount64+Math.Clamp(reply.RetryAfter,2,60)*1000L;}
        telemetry.Event("screen.frame",new(){["decision"]="skipped",["why"]="rejected",["status"]=reply.Status,["code"]=reply.Code??"",["bytes"]=jpeg.Length});
    }
    // Purple flash plus a thumbnail of exactly this picture, on the monitor that was captured. Never throws: feedback must not cost a frame or a highlight.
    async Task GiveFeedback(Bitmap shot,System.Drawing.Rectangle monitor,ShotReason reason)
    {
        var (show,why)=feedback.Check(reason,Environment.TickCount64);
        string name=reason.ToString().ToLowerInvariant();
        if(!show)
        {
            if(why=="rate-limit"||why=="not-visible"&&reason!=ShotReason.Interval)telemetry.Event("screen.feedback",new(){["reason"]=name,["shown"]=false,["why"]=why});
            return;
        }
        try
        {
            var clock=System.Diagnostics.Stopwatch.StartNew();
            var thumb=await Task.Run(()=>FeedbackThumb.Make(shot,FeedbackOverlay.ThumbWidthPx));
            var (shown,problem)=ScreenFlash.Show(monitor,thumb);
            telemetry.Event("screen.feedback",shown?new(){["reason"]=name,["shown"]=true,["ms"]=clock.ElapsedMilliseconds}:new(){["reason"]=name,["shown"]=false,["why"]=problem});
        }
        catch(Exception ex){telemetry.Event("screen.feedback",new(){["reason"]=name,["shown"]=false,["why"]="error:"+ex.GetType().Name});}
    }
    // `ids` in the gateway's reply: a#... the vision model read on the picture and the registry confirmed.
    static string[] VisionIds(RawReply reply)=>reply.Json is {} j&&j.RootElement.ValueKind==System.Text.Json.JsonValueKind.Object&&j.RootElement.TryGetProperty("ids",out var list)&&list.ValueKind==System.Text.Json.JsonValueKind.Array
        ?DevicePayloads.Ids(list.EnumerateArray().Where(e=>e.ValueKind==System.Text.Json.JsonValueKind.String).Select(e=>e.GetString()!)):[];
    // Finish at Caps release, never earlier: he holds the key, draws, lets go. From that instant the overlay is click-through (his next click
    // must reach the window below) while the strokes fade; the picture is taken without the overlay (it is excluded from capture, or made invisible when it cannot be).
    async Task FinishDraw()
    {
        if(drawings.Count==0)return;   // each drawing carries its own batch: a second circle while the first is still being sent gets its own overlay and closes it again
        var batch=drawings.ToArray();drawings.Clear();string? sid=bridge.SessionId;var ct=epoch.Token;
        long t0=Environment.TickCount64;
        foreach(var d in batch)d.ClickThrough();
        void Trace(bool ok,string why,int bytes=0)=>telemetry.Event("screen.highlight",new(){["ok"]=ok,["why"]=why,["ms"]=Environment.TickCount64-t0,["bytes"]=bytes});
        try
        {
            if(batch.All(d=>d.Strokes.All(s=>s.Count==0))){Trace(false,"no-stroke");return;}   // held the key, drew nothing: nothing is sent
            if(sid is null||!Active){Trace(false,"not-active");return;}
            foreach(var d in batch)
            {
                if(ct.IsCancellationRequested||!Active)break;
                if(d.Strokes.All(s=>s.Count==0))continue;
                Bitmap? clean=null;
                for(int attempt=0;attempt<3&&clean is null;attempt++)
                {
                    if(!d.CaptureExcluded){d.Opacity=0;await Task.Delay(50,ct);}
                    try{clean=await Task.Run(()=>privacy.Capture(d.Monitor,true,false,underlying),ct);}
                    finally{if(!d.CaptureExcluded)d.Opacity=1;}
                    if(clean is null&&privacy.LastSkip!="windows-changed")break;
                    if(clean is null)await Task.Delay(150,ct);
                }
                if(clean is null)
                {
                    string why=privacy.LastSkip is {Length:>0} w?w:"capture-failed";Trace(false,why);
                    Notice(why switch{"locked"=>"Markierung nicht gesendet · Bildschirm gesperrt","config-list"=>"Markierung nicht gesendet · Fenster ausgenommen (Liste)","not-active"=>"Markierung nicht gesendet · Sitzung nicht aktiv",_=>"Markierung nicht gesendet · Aufnahme fehlgeschlagen"},Level.Warn);
                    continue;
                }
                using(clean)
                {
                    var made=HighlightBuilder.Build(d,clean);
                    if(made is null){Trace(false,"offscreen");Notice("Markierung nicht gesendet · außerhalb des Bildschirms",Level.Warn);continue;}
                    foreach(var other in batch)other.Fade();
                    await GiveFeedback(clean,d.Monitor,ShotReason.Highlight);
                    var body=DevicePayloads.Highlight(made.Png,made.Jpeg,made.Bbox,ids);
                    if(!Active||ct.IsCancellationRequested||bridge.SessionId!=sid){Trace(false,"cancelled");break;}
                    using var reply=await client.Raw(HttpMethod.Post,"api/device/session/"+Uri.EscapeDataString(sid)+"/highlight",body,ct);
                    int bytes=made.Png.Length+made.Jpeg.Length;
                    if(reply.Ok)
                    {
                        Trace(true,"sent",bytes);Notice("Markierung gesendet");
                        var more=VisionIds(reply);if(more.Length>0){visionIds=DevicePayloads.Merge(visionIds,more);SetIds(DevicePayloads.Merge(ocrIds,visionIds),"","highlight");}
                    }
                    else if(reply.Status==409){Trace(false,"no-session",bytes);Notice("Keine aktive Stimme · Markierung nicht gesendet",Level.Warn);}
                    else{Trace(false,"rejected-"+reply.Status,bytes);Notice($"Markierung nicht gesendet · Desk antwortet {reply.Status}",Level.Warn);}
                }
            }
            foreach(var d in batch)d.Fade();   // idempotent; also after a failure the strokes go away on their own
            if(!ct.IsCancellationRequested)await Task.Delay(Math.Max(0,(int)(DrawOverlay.FadeMs-(Environment.TickCount64-t0))),ct);
        }
        catch(OperationCanceledException){}
        catch(Exception ex){Trace(false,"error:"+ex.GetType().Name);Notice("Markierung nicht gesendet · Verbindung prüfen",Level.Warn);}
        finally{foreach(var d in batch)d.Close();}
    }
    public void Dispose()
    {
        _=ClearContext(contextSid??bridge.SessionId);_=bridge.Voice(false,config.VoiceProvider);
        telemetry.Event("client.app.stop");_=telemetry.Flush(contextSid??bridge.SessionId);
        disposed=true;canvas?.Terminate();activity?.Close();timer.Stop();epoch.Cancel();learner.Cancel();audio?.Dispose();audio=null;
        foreach(var d in drawings)d.Close();drawings.Clear();tray.Visible=false;tray.Dispose();epoch.Dispose();
    }
}
