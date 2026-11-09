using System.Diagnostics;
using System.Text.Json;
namespace ScreenAgent;
// Only registered ahead of guarded plans in unrestricted mode. All observations use one coordinate-frame owner.
internal sealed class DesktopTools:IFreeToolset
{
    readonly FreeHost host;readonly DesktopInput input;readonly DesktopScreens screens;readonly DesktopUia uia;readonly DesktopShell shell;
    public DesktopTools(FreeHost host)
    {
        this.host=host;input=new(host);screens=new(host);uia=new(host,input);shell=new(host);
        host.ScreenShot=()=>screens.Capture(default,true);
    }
    public IReadOnlyDictionary<string,bool> Tools {get;}=new Dictionary<string,bool>
    {
        ["screenshot"]=false,["click"]=true,["move"]=true,["drag"]=true,["wheel"]=true,["scroll"]=true,["type"]=true,["key"]=true,["wait"]=false,
        ["ui_windows"]=false,["window_focus"]=true,["window_state"]=true,["open"]=true,["ui_tree"]=false,["ui_find"]=false,["ui_click"]=true,["ui_type"]=true,["ui_read"]=false,
        ["clipboard_read"]=false,["clipboard_write"]=false,["run_powershell"]=false,["run_cmd"]=false,["run_wsl"]=false,["run_python"]=false,["file_read"]=false,["file_write"]=false
    };
    static string Required(JsonElement a,string name)=>FreeArgs.Str(a,name)??throw new ArgumentException(name+" fehlt.");
    static double Number(JsonElement a,string name)=>FreeArgs.Num(a,name) is {} n&&double.IsFinite(n)?n:throw new ArgumentException(name+" fehlt oder ist ungültig.");
    public FreeResult Run(FreeCall call)
    {
        try
        {
            host.ThrowIfStopped();var a=call.Args;string tool=call.Tool;FreeResult result;
            switch(tool)
            {
                case "screenshot":var shot=screens.Capture(a);return new(true,shot.Text,shot.Jpeg);
                case "click":case "move":case "drag":case "wheel":case "scroll":result=Mouse(tool,a);break;
                case "type":var text=Required(a,"text");input.Type(text,FreeArgs.Bool(a,"paste")??(text.Length>200||text.Contains('\n')),FreeArgs.Bool(a,"enter")??false);result=new(true,$"{text.Length} Zeichen eingefügt.");break;
                case "key":input.Keys(Required(a,"key"),Math.Clamp(FreeArgs.Int(a,"times")??1,1,50));result=new(true,"Tasten gesendet.");break;
                case "wait":input.Wait(Math.Clamp(FreeArgs.Int(a,"ms")??0,0,10000));result=new(true,"gewartet");break;
                case "ui_windows":return new(true,DesktopWindows.Listing());
                case "window_focus":DesktopWindows.Focus(DesktopWindows.Resolve(Required(a,"window")));result=new(true,"Fenster im Vordergrund.");break;
                case "window_state":
                    var h=DesktopWindows.Resolve(Required(a,"window"));switch(Required(a,"state")){case "maximize":Native.ShowWindow(h,3);break;case "minimize":Native.ShowWindow(h,6);break;case "restore":Native.ShowWindow(h,9);break;case "close":DesktopNative.PostMessage(h,0x10,IntPtr.Zero,IntPtr.Zero);break;default:throw new ArgumentException("state: maximize, minimize, restore oder close.");}result=new(true,"Fenster geändert.");break;
                case "open":using(var p=Process.Start(new ProcessStartInfo(Required(a,"target"),FreeArgs.Str(a,"args")??""){UseShellExecute=true,WorkingDirectory=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)})){ }result=new(true,"geöffnet");break;
                case "ui_tree":return uia.Tree(a,false);
                case "ui_find":_ = Required(a,"name");return uia.Tree(a,true);
                case "ui_read":return uia.Read(Required(a,"ref"));
                case "ui_click":result=uia.Click(Required(a,"ref"),FreeArgs.Str(a,"button")??"left",FreeArgs.Bool(a,"double")??false);break;
                case "ui_type":result=uia.Type(Required(a,"ref"),Required(a,"text"),FreeArgs.Bool(a,"clear")??true,FreeArgs.Bool(a,"enter")??false);break;
                case "clipboard_read":var clip=ClipboardText.Read();return new(true,clip[..Math.Min(clip.Length,20000)]);
                case "clipboard_write":ClipboardText.Write(Required(a,"text"));return new(true,"Zwischenablage gesetzt.");
                case "run_powershell":case "run_cmd":case "run_wsl":_ = Required(a,"command");return shell.Run(tool,a);
                case "run_python":_ = Required(a,"code");return shell.Run(tool,a);
                case "file_read":case "file_write":return shell.FileTool(tool,a);
                default:return FreeResult.Fail("unknown-tool","Unbekanntes Werkzeug.");
            }
            return result.Ok&&(Tools[tool]||tool=="wait")?Observe(result,a):result;
        }
        catch(UiaStaleException e){ /* ActFree logs the returned failure rule. */ return new(false,e.Message,Rule:"stale-ref",Stale:true);}
        catch(System.Windows.Automation.ElementNotAvailableException){ /* ActFree logs the returned failure rule. */ return new(false,"Element nicht mehr vorhanden; ui_tree erneut aufrufen.",Rule:"stale-ref",Stale:true);}
        catch(ArgumentException e){return FreeResult.Fail("bad-args",e.Message);}
        catch(TimeoutException e){return FreeResult.Fail("timeout",e.Message);}
        catch(IOException e){return FreeResult.Fail("file-error",e.Message);}
        catch(UnauthorizedAccessException){return FreeResult.Fail("access-denied","Zugriff verweigert; Pfad oder Berechtigungen prüfen.");}
    }
    FreeResult Observe(FreeResult result,JsonElement args)
    {
        if(FreeArgs.Bool(args,"observe")==false)return result;
        input.Wait(Math.Clamp(FreeArgs.Int(args,"settle_ms")??350,0,5000));
        string? observedTab=FreeArgs.Str(args,"tab")??(host.Frame.Kind==FrameKind.Tab?host.Frame.Tab:null);
        if(observedTab is not null&&host.TabShot is {} capture)
        {
            var tab=capture(observedTab);if(tab is {} shot){host.Frame=shot.Frame;return result with{Jpeg=shot.Jpeg,Text=$"frame: tab {shot.Frame.Width}x{shot.Frame.Height}\n"+result.Text};}
            return result with{Text=result.Text+"\nTab-Bild nicht verfügbar; screenshot aufrufen."};
        }
        var screen=screens.Capture(default,true);return result with{Jpeg=screen.Jpeg,Text=screen.Text+"\n"+result.Text};
    }
    FreeResult Mouse(string tool,JsonElement a)
    {
        var f=host.Frame;string? tab=FreeArgs.Str(a,"tab");bool explicitWindow=FreeArgs.Str(a,"window") is not null;
        if(tool=="scroll"&&tab is not null)
        {
            if(host.TabMouseHandler is null)return FreeResult.Fail("use-chrome-toolset","Chrome-Steuerung fehlt; screenshot und wheel verwenden.");
            string status=host.TabMouseHandler(new(tab,"wheel",0,0,Dy:Number(a,"dy")));return new(true,status);
        }
        if(f.Kind==FrameKind.None&&tool!="scroll")return FreeResult.Fail("no-frame","Zuerst screenshot aufnehmen.");
        double x,y;DesktopNative.GetCursorPos(out var cursor);var cp=f.Kind==FrameKind.None?(0.0,0.0):f.FromTarget(cursor.X,cursor.Y);
        if(tool is "wheel" or "scroll"){x=FreeArgs.Num(a,"x")??cp.Item1;y=FreeArgs.Num(a,"y")??cp.Item2;}else{x=Number(a,"x");y=Number(a,"y");}
        if(f.Kind==FrameKind.Tab&&tool is "wheel" or "scroll"){x=FreeArgs.Num(a,"x")??f.Width/2.0;y=FreeArgs.Num(a,"y")??f.Height/2.0;}
        double x2=tool=="drag"?Number(a,"x2"):x,y2=tool=="drag"?Number(a,"y2"):y;
        string button=FreeArgs.Str(a,"button")??"left",mods=FreeArgs.Str(a,"mods")??"";int count=Math.Clamp(FreeArgs.Int(a,"count")??1,1,3),ms=Math.Clamp(FreeArgs.Int(a,"ms")??600,100,5000);
        double dy=tool is "wheel" or "scroll"?Number(a,"dy"):0,dx=FreeArgs.Num(a,"dx")??0;
        if(f.Kind==FrameKind.Tab&&!explicitWindow&&host.TabMouseHandler is {} handler)
        {
            if(tool!="scroll"&&(!f.Contains(x,y)||!f.Contains(x2,y2)))throw new ArgumentException("Punkt außerhalb des Bildes.");
            var p=f.ToTarget(x,y);var p2=f.ToTarget(x2,y2);return new(true,handler(new(f.Tab!,tool=="scroll"?"wheel":tool,p.X,p.Y,p2.X,p2.Y,button,count,tool=="scroll"?0:dx*120,tool=="scroll"?dy:dy*120,ms,mods)));
        }
        if(f.Kind==FrameKind.Tab&&!explicitWindow)return FreeResult.Fail("no-frame","Tab-Verbindung fehlt; zuerst Desktop-screenshot aufnehmen.");
        int px,py,px2,py2;
        if(tool=="scroll")
        {
            var h=DesktopWindows.Resolve(FreeArgs.Str(a,"window"));DesktopWindows.Focus(h);Native.GetWindowRect(h,out var r);px=px2=(r.Left+r.Right)/2;py=py2=(r.Top+r.Bottom)/2;dy/=120;
        }
        else if(tool=="wheel"&&FreeArgs.Num(a,"x") is null&&FreeArgs.Num(a,"y") is null){px=px2=cursor.X;py=py2=cursor.Y;}
        else{var p=f.Pixels.Physical(x,y);var p2=f.Pixels.Physical(x2,y2);px=p.X;py=p.Y;px2=p2.X;py2=p2.Y;}
        switch(tool){case "click":input.Click(px,py,button,count,mods);break;case "move":input.Move(px,py);break;case "drag":input.Drag(px,py,px2,py2,button,ms);break;default:input.Move(px,py);input.Wheel(dx,dy);break;}
        return new(true,tool switch{"click"=>"geklickt","move"=>"Maus bewegt","drag"=>"gezogen",_=>"gescrollt"});
    }
    public void RunStarted(ComputerRun run){host.Frame=Frame.None;uia.Reset();}
    public void RunEnded(ComputerRun run){host.Frame=Frame.None;uia.Reset();}
    public void Dispose(){host.ScreenShot=null;uia.Reset();}
}
