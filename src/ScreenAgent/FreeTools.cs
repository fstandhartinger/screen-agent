using System.Text.Json;
using ScreenAgent.Core;
namespace ScreenAgent;
// General computer control (v0.1.13, Florian 9 Nov 2026): in unrestricted mode the toolsets below take every tool they know BEFORE the guarded plans
// of ComputerAgent.Build are considered. No classes, no scope, no confirm: he supervises in real time; the kill chord, hands-on, pause and the caps stay.
// The contract (tool names, arguments, results, the coordinate rule) is briefs/CONTRACT-V2 of the job and testdata/computer-contract.json ("v2").

// What the image the model saw last shows. x,y of click/move/drag/wheel are pixels in that image; the app maps them.
internal enum FrameKind{None,Screen,Window,Region,Tab}
// Screen kinds: physical virtual-desktop pixel (OriginX,OriginY) is image pixel (0,0); one image pixel is 1/Scale physical pixels. Tab: CSS pixel = image pixel / Scale in tab `Tab`.
internal sealed record Frame(FrameKind Kind,int Width,int Height,double OriginX,double OriginY,double Scale,string? Tab=null,IntPtr Hwnd=default,double SourceWidth=0,double SourceHeight=0)
{
    public static readonly Frame None=new(FrameKind.None,0,0,0,0,1);
    public PixelFrame Pixels=>new(Width,Height,OriginX,OriginY,SourceWidth>0?SourceWidth:Width/Scale,SourceHeight>0?SourceHeight:Height/Scale);
    public (double X,double Y) ToTarget(double x,double y)=>Kind==FrameKind.Tab?(OriginX+x/Scale,OriginY+y/Scale):Pixels.Physical(x,y);
    public (double X,double Y) FromTarget(double x,double y)=>Kind==FrameKind.Tab?((x-OriginX)*Scale,(y-OriginY)*Scale):Pixels.Image(x,y);
    public bool Contains(double x,double y)=>x>=0&&y>=0&&x<Width&&y<Height;
}
internal sealed record FreeCall(string Tool,JsonElement Args,ComputerRun Run);
// Text = one short German status (or the data read). Jpeg = plain base64. Rule = machine reason when !Ok ("no-frame", "bad-args", "not-found", "timeout", ...). Stale = code stale-ref.
internal sealed record FreeResult(bool Ok,string Text,string? Jpeg=null,string Rule="free",string? Target=null,string? Process=null,string? Domain=null,bool Stale=false)
{
    public static FreeResult Fail(string rule,string text)=>new(false,text,null,rule);
}
// What a toolset may use of the app. One instance per ComputerAgent, shared by all toolsets.
internal sealed class FreeHost
{
    readonly object sync=new();
    Frame frame=Frame.None;
    public required Action<string,Dictionary<string,object?>> Log {get;init;}      // local computer.log + trace
    public required Func<bool> Stopped {get;init;}                                  // kill, hands-on, pause or close: stop typing / moving in flight (check between steps)
    public required ComputerPolicy Policy {get;init;}
    public required ChromeCdp.ConnectOptions Chrome {get;init;}
    public Frame Frame {get{lock(sync)return frame;}set{lock(sync)frame=value;}}
    // Set by ChromeTools: mouse on a `tab` frame goes through CDP. (tab, kind "move|click|drag|wheel", x,y in CSS px, x2,y2, button, count, dx,dy) -> status text.
    public Func<TabMouse,string>? TabMouseHandler {get;set;}
    // Set by ChromeTools: a fresh image of the tab of the current frame (auto-observe after a mouse action on a tab frame). null when it cannot.
    public Func<string,(string Jpeg,Frame Frame)?>? TabShot {get;set;}
    // Set by DesktopTools: a fresh screenshot like the last screen-kind frame (ChromeTools use it as the fallback picture when CDP is away).
    public Func<(string Jpeg,Frame Frame,string Text)?>? ScreenShot {get;set;}
    public void ThrowIfStopped(){if(Stopped())throw new OperationCanceledException("run stopped");}
}
internal sealed record TabMouse(string Tab,string Kind,double X,double Y,double X2=0,double Y2=0,string Button="left",int Count=1,double Dx=0,double Dy=0,int Ms=600,string Mods="");
internal interface IFreeToolset:IDisposable
{
    // tool name -> true when it sends input to the desktop (the app's input gate is taken for the call, so dictation injection and the run never type at once)
    IReadOnlyDictionary<string,bool> Tools {get;}
    FreeResult Run(FreeCall call);
    void RunStarted(ComputerRun run);
    void RunEnded(ComputerRun run);
}
// Small argument readers shared by the toolsets.
internal static class FreeArgs
{
    public static string? Str(JsonElement a,string n,int max=20000)=>ComputerWire.ArgString(a,n,max);
    public static int? Int(JsonElement a,string n)=>a.ValueKind==JsonValueKind.Object&&a.TryGetProperty(n,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetDouble(out var d)?(int)Math.Round(d):null;
    public static double? Num(JsonElement a,string n)=>a.ValueKind==JsonValueKind.Object&&a.TryGetProperty(n,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetDouble(out var d)?d:null;
    public static bool? Bool(JsonElement a,string n)=>a.ValueKind==JsonValueKind.Object&&a.TryGetProperty(n,out var v)?(v.ValueKind==JsonValueKind.True?true:v.ValueKind==JsonValueKind.False?false:null):null;
    public static double[]? Nums(JsonElement a,string n,int count)
    {
        if(a.ValueKind!=JsonValueKind.Object||!a.TryGetProperty(n,out var v)||v.ValueKind!=JsonValueKind.Array||v.GetArrayLength()!=count)return null;
        var r=new double[count];int i=0;
        foreach(var e in v.EnumerateArray()){if(e.ValueKind!=JsonValueKind.Number||!e.TryGetDouble(out r[i]))return null;i++;}
        return r;
    }
}
