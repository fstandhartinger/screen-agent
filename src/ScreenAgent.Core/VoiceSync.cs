namespace ScreenAgent.Core;
public enum VoiceStep { None, Start, Stop }
// Decides when to ask the Desk page to start or stop voice. The page only answers once it is the signed-in companion
// page; on the pairing page it never acknowledges. v0.1.1 asked it to stop voice anyway, never got an answer, and
// reported "Stimme nicht bestätigt" for as long as the app ran. Now: a stop is only sent after a start, and only a
// start can fail visibly.
public sealed class VoiceSync
{
    bool? requested;
    bool stopPending,retry;
    long retryAt;
    public bool Failed {get;private set;}
    public const long RetryMs=2000;
    // want = session should be live; voiceOn = what the page last acknowledged; busy = a request is in flight.
    public VoiceStep Next(bool want,bool voiceOn,long now,bool busy)
    {
        if(want&&requested==true&&!voiceOn&&!busy){Failed=true;retry=true;}   // the page dropped the voice by itself
        if(!want)
        {
            Failed=false;
            if(!stopPending){requested=false;retry=false;return VoiceStep.None;}
        }
        if(busy||(requested==want&&(!retry||now<retryAt)))return VoiceStep.None;
        requested=want;
        if(want)stopPending=true;
        return want?VoiceStep.Start:VoiceStep.Stop;
    }
    public void Result(bool wasStart,bool acknowledged,long now)
    {
        retry=!acknowledged;retryAt=now+RetryMs;
        if(wasStart)Failed=!acknowledged;else if(acknowledged)stopPending=false;
    }
    // The page was reloaded or navigated: whatever voice it had is gone.
    public void PageChanged(){requested=null;stopPending=false;retry=false;Failed=false;}
}
