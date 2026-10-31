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
