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
    bool paused,busy,disposed;
    long capturedAt,version,noticeUntil;
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
        bridge.SessionChanged+=Reset;
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
                if(!hook.Healthy){Reset();Notice("Pausiert · Tastatur");return;}
                if(drawings.Count>0&&!finishingDraw&&drawings.Any(d=>d.LastStrokeAt>0)&&drawings.All(d=>!d.InStroke)&&Environment.TickCount64-drawings.Max(d=>d.LastStrokeAt)>=700)await FinishDraw();
                if(Environment.TickCount64>noticeUntil)pill.SetMode(paused||hook.Paused?"Pausiert":audio!=null?"Diktat":drawings.Count>0?"Zeichnen":Active?$"Sitzung · {ids.Length} IDs":"Pausiert");
                await Watch();
            }
            catch{Notice("Anfrage fehlgeschlagen");}
        };timer.Start();
        var menu=new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Pause",null,(_,_)=>Pause());
        menu.Items.Add("Desk anzeigen",null,(_,_)=>bridge.Show());
        menu.Items.Add("Gerät trennen",null,(_,_)=>{Pause(true);bridge.Disconnect();});
        menu.Items.Add("Beenden",null,(_,_)=>{bridge.AllowClose=true;System.Windows.Application.Current.Shutdown();});
        tray=new(){Icon=SystemIcons.Application,Text="Screen Agent",Visible=true,ContextMenuStrip=menu};tray.DoubleClick+=(_,_)=>bridge.Show();
    }
    bool Active=>!disposed&&client.Paired&&bridge.SessionId is not null&&hook.Healthy&&hook.Session&&!hook.Paused&&!paused;
    public void Pause(bool force=false){paused=force||!paused;Reset();}
    void Notice(string text){noticeUntil=Environment.TickCount64+2200;pill.SetMode(text);}
    void Reset()
    {
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
        // Continuations resume on the WPF context. Never overlap recording/mute lifecycles.
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
                if(!await bridge.Mute(true)){await bridge.Mute(false);Notice("Diktat · Mikrofon nicht bestätigt");return;}
                if(!hook.Dictating||paused||hook.Paused||audioTarget is null||!TextInjector.Safe(audioTarget,config.Policy())||audioSession!=bridge.SessionId){await bridge.Mute(false);return;}
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
                string text=await client.Dictate(wav,new(proc,prior.Length<=500?prior:prior[^500..],ids,terms.Take(30).ToArray()),ct);
                ct.ThrowIfCancellationRequested();
                bool injected=await TextInjector.Inject(bound,text,text.Length>=60&&!TextInjector.Terminal(bound.Window),config.Policy());
                Notice(injected?"Diktat eingefügt":"Ziel geändert · Text kopiert");
                if(injected)_=learner.Watch(bound,prior,TextInjector.Sanitize(text),ct);
            }
            finally{recording.Dispose();await bridge.Mute(false);}
        }
        catch(OperationCanceledException){}
        catch{audio?.Dispose();audio=null;await bridge.Mute(false);Notice("Diktat fehlgeschlagen");}
    }
    async Task Watch()
    {
        var fg=Native.GetForegroundWindow();
        if(busy||!Active||drawings.Count>0||(fg==lastForeground&&Environment.TickCount64-capturedAt<config.IntervalMs))return;
        busy=true;capturedAt=Environment.TickCount64;lastForeground=fg;
        var ct=epoch.Token;string sid=bridge.SessionId!;
        try
        {
            var monitor=System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).Bounds;
            using var frame=privacy.Capture(monitor,true,false);
            if(frame is null)
            {
                ids=[];lastHash=null;bridge.Screen(ids);Notice("Privat · nicht erfasst");
                if(Active&&!ct.IsCancellationRequested&&bridge.SessionId==sid)await client.Post("api/device/session/"+Uri.EscapeDataString(sid)+"/screen",new ScreenPayload([],new("",true),++version),ct);
                return;
            }
            ulong hash=PerceptualHash(frame);if(lastHash==hash&&hashForeground==fg)return;
            var window=privacy.Snapshot(monitor).FirstOrDefault(w=>w.Handle==fg);if(window is null||window.Blocked)return;
            double ocrScale=Math.Min(1,2560.0/Math.Max(frame.Width,frame.Height));
            using var ocrFrame=new Bitmap(frame,new Size(Math.Max(1,(int)(frame.Width*ocrScale)),Math.Max(1,(int)(frame.Height*ocrScale))));
            byte[] png=PrivacyCapture.Png(ocrFrame);var ocr=await Task.Run(()=>Ocr.ReadMemory(png),ct);
            var confirmed=await client.Exists(ocr.Ids,ct);
            if(!Active||ct.IsCancellationRequested||bridge.SessionId!=sid||Native.GetForegroundWindow()!=fg)return;
            await client.Post("api/device/session/"+Uri.EscapeDataString(sid)+"/screen",DevicePayloads.Screen(confirmed,window.Process,++version),ct);
            if(!Active||ct.IsCancellationRequested)return;
            lastHash=hash;hashForeground=fg;ids=confirmed;bridge.Screen(ids);
            if(terms.Length==0)terms=await client.Dictionary(ct);
        }
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
                using var clean=privacy.Capture(d.Monitor,true,false,underlying);if(clean is null)continue;
                d.Composite(clean);
                float minX=points.Min(p=>p.X),maxX=points.Max(p=>p.X),minY=points.Min(p=>p.Y),maxY=points.Max(p=>p.Y);
                int margin=(int)Math.Ceiling(Math.Max(maxX-minX,maxY-minY)*.15)+5;
                var bounds=Rectangle.Intersect(new Rectangle(0,0,clean.Width,clean.Height),Rectangle.FromLTRB((int)minX-margin,(int)minY-margin,(int)maxX+margin+1,(int)maxY+margin+1));
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
        catch{Notice("Markierung nicht gesendet");}
        finally{foreach(var d in batch)d.Close();finishingDraw=false;}
    }
    [StructLayout(LayoutKind.Sequential)]readonly record struct Point(int X,int Y);
    [DllImport("user32.dll")]static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]static extern IntPtr GetAncestor(IntPtr window,uint flags);
    public void Dispose()
    {
        disposed=true;timer.Stop();epoch.Cancel();learner.Cancel();audio?.Dispose();audio=null;
        foreach(var d in drawings)d.Close();drawings.Clear();tray.Visible=false;tray.Dispose();epoch.Dispose();
    }
}
