namespace ScreenAgent.Core;
// Why the agent looked at the screen, as far as the user can tell. Interval and Foreground frames are the agent's routine; the others he asked for or caused.
public enum ShotReason { Start, Fresh, Highlight, Foreground, Interval }
// The one place that says which screenshots get the purple flash and thumbnail (config ScreenshotFeedback: "visible" default, "all", "off").
public static class ScreenshotFeedback
{
    public static readonly string[] Modes=["visible","all","off"];
    public static ShotReason Of(FrameReason reason)=>reason switch{FrameReason.Start=>ShotReason.Start,FrameReason.Fresh=>ShotReason.Fresh,FrameReason.Foreground=>ShotReason.Foreground,_=>ShotReason.Interval};
    public static bool Fires(string mode,ShotReason reason)=>mode switch{"off"=>false,"all"=>true,_=>reason is ShotReason.Start or ShotReason.Fresh or ShotReason.Highlight};
}
// Mode plus the rate limit (at most one flash per MinGapMs). The slot is only used up by a flash that really happens.
public sealed class FeedbackGate(string mode,int minGapMs=2000)
{
    bool any;long last;
    public string Mode=>mode;
    // why: "" = show, otherwise mode-off | not-visible | rate-limit (logged as screen.feedback shown=false).
    public (bool Show,string Why) Check(ShotReason reason,long now)
    {
        if(mode=="off")return (false,"mode-off");
        if(!ScreenshotFeedback.Fires(mode,reason))return (false,"not-visible");
        if(any&&now-last<minGapMs)return (false,"rate-limit");
        any=true;last=now;return (true,"");
    }
}
