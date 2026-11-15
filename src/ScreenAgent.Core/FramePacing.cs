namespace ScreenAgent.Core;
// One sender owns the active item until Complete. Captures replace a single waiting item;
// clearing a session never releases an HTTP call that is still unwinding after cancellation.
public sealed class LatestFrameSlot<T> where T:class
{
    readonly object gate=new();
    T? waiting;
    bool active;
    public T? Offer(T item){lock(gate){var old=waiting;waiting=item;return old;}}
    public bool TryTake(out T? item)
    {
        lock(gate){item=null;if(active||waiting is null)return false;active=true;item=waiting;waiting=null;return true;}
    }
    public void Complete(){lock(gate)active=false;}
    public T? Clear(){lock(gate){var old=waiting;waiting=null;return old;}}
    public bool InFlight {get{lock(gate)return active;}}
}
// "on: true, state: connecting" only acknowledges the start request. The gateway
// registers the voice session before the page enters listening/thinking/speaking.
public sealed class DeskSessionGate
{
    public static bool RegisteredState(string? state)=>state is "listening" or "thinking" or "speaking";
    long retryAt;
    bool rejected;
    public bool CanPoll(bool registered,long now)=>registered&&now>=retryAt;
    public bool CanSend(bool registered,long now)=>CanPoll(registered,now)&&!rejected;
    public void NoSession(long now){rejected=true;retryAt=now+1500;}
    public void Accepted(){rejected=false;retryAt=0;}
    public void Reset(){rejected=false;retryAt=0;}
}
// Separate from change detection's log breaker: changing frames cannot reset this window.
public sealed class FrameSkipCounter
{
    readonly Dictionary<string,(long At,int Count)> counts=[];
    public int Note(string reason,long now)
    {
        if(!counts.TryGetValue(reason,out var prior)){counts[reason]=(now,0);return 1;}
        int n=prior.Count+1;
        if(now-prior.At<30000){counts[reason]=(prior.At,n);return 0;}
        counts[reason]=(now,0);return n;
    }
}
