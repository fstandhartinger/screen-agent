namespace ScreenAgent.Core;
// Injectable clock inputs keep restart, renewal and retry behavior independently testable.
public sealed class FrameClock
{
    long previous;
    public long Next(long unixMs) => previous=Math.Max(unixMs,previous+1);
}
public sealed class ScreenSchedule
{
    int failures;
    public long RetryAt {get;private set;}
    public long RenewAt {get;private set;}
    public bool Ready(long now)=>now>=RetryAt;
    public bool RenewalDue(long now)=>now>=RenewAt;
    public void Success(long now){failures=0;RetryAt=0;RenewAt=now+30000;}
    public void Failure(long now){RetryAt=now+Math.Min(30000,1000L<<(Math.Min(failures++,5)));}
}
public sealed class OrderedWork
{
    Task tail=Task.CompletedTask;
    public Task Enqueue(Func<Task> work)=>tail=Run(tail,work);
    static async Task Run(Task prior,Func<Task> work){try{await prior;}catch{} await work();}
}
// The first session after launch can take seconds (WebView2, the page, the microphone). While it starts the user is told so, and every phase is logged
// as session.startup {phase, ms since the hotkey}, once per phase, so a slow start can be told apart afterwards.
public sealed class StartupTimeline(long limitMs=15000)
{
    long begun;
    readonly HashSet<string> marked=[];
    public bool Active=>begun!=0;
    public void Begin(long now){begun=now;marked.Clear();}
    public void End(){begun=0;marked.Clear();}
    // Within the window in which "starting" is a fair thing to say; after it the plain failure text takes over.
    public bool Starting(long now)=>begun!=0&&now-begun<limitMs;
    public int Seconds(long now)=>begun==0?0:(int)((now-begun)/1000);
    // ms since Begin the first time a phase is reported, null afterwards (or when no start is being timed).
    public long? Mark(string phase,long now)=>begun!=0&&marked.Add(phase)?now-begun:null;
}
