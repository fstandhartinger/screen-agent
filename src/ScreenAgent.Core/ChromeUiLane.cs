namespace ScreenAgent.Core;
// One provider call at a time. A timed-out provider occupies the lane until it returns;
// callers never create replacement threads that could accumulate delayed input.
public static class ChromeUiLane
{
    static readonly object sync=new();
    static Task? pending;
    public static T Run<T>(Func<Func<bool>,T> work,Func<bool> stopped,int timeoutMs)
    {
        long end=Environment.TickCount64+timeoutMs;int revoked=0;
        bool Allowed()=>Volatile.Read(ref revoked)==0&&!stopped()&&Environment.TickCount64<end;
        Task<T> task;
        lock(sync)
        {
            if(pending is {IsCompleted:false})throw new TimeoutException("Chrome UI Automation ist noch beschäftigt.");
            if(!Allowed())throw new OperationCanceledException("run stopped");
            task=Task.Factory.StartNew(()=>{if(!Allowed())throw new OperationCanceledException();return work(Allowed);},CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);pending=task;
            _=task.ContinueWith(t=>{_ = t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
        }
        try
        {
            while(!task.IsCompleted)
            {
                if(stopped())throw new OperationCanceledException("run stopped");
                if(Environment.TickCount64>=end)throw new TimeoutException("Chrome UI Automation antwortet nicht.");
                Thread.Sleep(25);
            }
            if(stopped())throw new OperationCanceledException("run stopped");
            if(Environment.TickCount64>=end)throw new TimeoutException("Chrome UI Automation antwortet nicht.");
            return task.GetAwaiter().GetResult();
        }
        finally{Interlocked.Exchange(ref revoked,1);}
    }
}
