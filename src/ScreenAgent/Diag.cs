using ScreenAgent.Core;
namespace ScreenAgent;
// The log for code that has no Telemetry at hand (statics, native wrappers, start-up). Goes through Telemetry once the app has one (rolling log + queue to the Desk),
// before that straight into the rolling log, so a failure while starting is not lost. Never throws.
internal static class Diag
{
    static volatile Telemetry? sink;
    static readonly RollingLog early=new(Telemetry.Folder);
    static readonly Dictionary<string,(long At,int Count)> seen=[];
    internal static void Attach(Telemetry t)=>sink=t;
    internal static void Detach(Telemetry t){if(ReferenceEquals(sink,t))sink=null;}
    public static void Event(string type,Dictionary<string,object?>? data=null)
    {
        try
        {
            if(sink is {} t)t.Event(type,data);
            else early.Write(new(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),type,TraceSanitizer.Clean(data)));
        }
        catch{ /* logging never breaks the app */ }
    }
    // app.error {stage, exType, msg (redacted, capped), hr, ...extra}: the standard record of a caught exception.
    // The same stage and exception type repeats at most every 10 s (a failing loop must not flood the log); the next record says how many it swallowed.
    public static void Fail(string stage,Exception? ex,Dictionary<string,object?>? extra=null)
    {
        string key=stage+"|"+ex?.GetType().Name;long now=Environment.TickCount64;int swallowed=0;
        lock(seen)
        {
            if(seen.TryGetValue(key,out var s)&&now-s.At<10000){seen[key]=(s.At,s.Count+1);return;}
            swallowed=seen.TryGetValue(key,out s)?s.Count:0;seen[key]=(now,0);
        }
        var data=extra??new();data["stage"]=stage;if(swallowed>0)data["suppressed"]=swallowed;
        if(ex is not null)Redact.Of(ex,data);
        Event("app.error",data);
    }
    // The process is going down: log, then make sure the queue file has it, so the next start ships it.
    public static void Crash(string kind,Exception? ex)
    {
        Fail(kind,ex,new(){["fatal"]=true});
        try{sink?.Persist();}catch{ /* the rolling log has it */ }
    }
}
