using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ScreenAgent.Core;
public static class ComputerCodes
{
    public const string Done="done",Refused="refused",NeedsConfirm="needs-confirm",StaleRef="stale-ref",Killed="killed",HandsOn="handson",Unclear="unclear",NoRun="no-run",Denied="denied";
    public static readonly string[] All=[Done,Refused,NeedsConfirm,StaleRef,Killed,HandsOn,Unclear,NoRun,Denied];
}
// Two digits drawn by the app, shown only on the capture-excluded overlay. The gateway forwards what he said as digits; the app compares once.
public static class ConfirmCode
{
    public static string Draw(Func<int,int,int>? next=null)
    {
        next??=(lo,hi)=>RandomNumberGenerator.GetInt32(lo,hi);
        return next(1,10).ToString()+next(0,10).ToString();   // 10..99: never a leading zero, so it can be said as two plain digits
    }
    static readonly Dictionary<string,char> Words=new(StringComparer.OrdinalIgnoreCase)
    {
        ["null"]='0',["eins"]='1',["ein"]='1',["zwei"]='2',["zwo"]='2',["drei"]='3',["vier"]='4',["fünf"]='5',["fuenf"]='5',["sechs"]='6',["sieben"]='7',["acht"]='8',["neun"]='9',
        ["zero"]='0',["one"]='1',["two"]='2',["three"]='3',["four"]='4',["five"]='5',["six"]='6',["seven"]='7',["eight"]='8',["nine"]='9'
    };
    // "47", "4 7", "vier sieben", "ja vier sieben": the digits in order. Anything left over that is not "ja"/"yes" makes it unusable (null).
    public static string? Digits(string? spoken)
    {
        if(string.IsNullOrWhiteSpace(spoken)||spoken.Length>80)return null;
        var sb=new StringBuilder();
        foreach(var w in Regex.Split(spoken.Trim(),@"[\s,.\-;:!]+").Where(w=>w.Length>0))
        {
            if(w.All(char.IsAsciiDigit)){sb.Append(w);continue;}
            if(Words.TryGetValue(w,out var d)){sb.Append(d);continue;}
            if(w.Equals("ja",StringComparison.OrdinalIgnoreCase)||w.Equals("yes",StringComparison.OrdinalIgnoreCase))continue;
            return null;
        }
        return sb.Length==0?null:sb.ToString();
    }
    public static bool Matches(string expected,string? spoken)
    {
        var got=Digits(spoken);
        return got is not null&&expected.Length==2&&CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected),Encoding.ASCII.GetBytes(got.PadRight(2)))&&got.Length==2;
    }
}
// What an R2 action is bound to. Captured when it is proposed, recomputed right before it runs; any difference aborts with no re-resolve.
// CDP: targetId, frameId, loaderId, origin+path, backendNodeId, role, name, digest of the form's field values (+ the hit test in the driver).
// UIA: RuntimeId, pid, hwnd, name, type, rect, foreground. Plus the action itself (tool and arguments), so typed text cannot change between ask and run.
public sealed class ActionBinding
{
    public SortedDictionary<string,string> Fields {get;}=new(StringComparer.Ordinal);
    public ActionBinding Clone(){var c=new ActionBinding();foreach(var (k,v) in Fields)c.Fields[k]=v;return c;}
    public ActionBinding Set(string key,object? value){Fields[key]=value?.ToString()??"";return this;}
    public string Hash()
    {
        var sb=new StringBuilder();
        foreach(var (k,v) in Fields)sb.Append(k).Append('\u0001').Append(v).Append('\u0002');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant()[..32];
    }
    // Names of the fields that differ (for the log; never their values).
    public List<string> Diff(ActionBinding other)
    {
        var keys=Fields.Keys.Union(other.Fields.Keys).ToList();
        return keys.Where(k=>!Fields.TryGetValue(k,out var a)||!other.Fields.TryGetValue(k,out var b)||a!=b).ToList();
    }
    public bool Same(ActionBinding other)=>Diff(other).Count==0;
    public static string Digest(IEnumerable<string> values)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u0001',values)))).ToLowerInvariant()[..16];
}
public sealed record PendingAsk(string Tool,string Hash,string Spoken,string Code,long At,ActionBinding Binding,JsonElement Args,string Class,string Rule);
public sealed record RunCaps(long MaxMs=10*60_000,int MaxActions=150,int StepReport=25,long PendingMs=60_000,int MaxAsks=8,int MaxExpiries=2,long IdleMs=60_000,long GatewayQuietMs=15_000);
public enum RunState { Running, HandsOn, Paused, Closed }
// The app owns the run. The kill flag is set from the keyboard hook callback (an Interlocked write: no allocation, no lock, no I/O) and the input worker looks at it before every SendInput batch and CDP call.
public sealed class ComputerRun
{
    readonly Func<long> clock;
    readonly object gate=new();
    int revoked,handsOn,paused,closed,physical,tainted,armed;
    string? killVia;
    long lastAction,lastGateway;
    public string Id {get;}
    public string Goal {get;private set;}
    public ComputerScope Scope {get;}
    public RunCaps Caps {get;}
    public long StartedAt {get;}
    public int Actions {get;private set;}
    public int StepsSinceFinal {get;private set;}
    public int Asks {get;private set;}
    public int Expiries {get;private set;}
    public string? ClosedWhy {get;private set;}
    public PendingAsk? Pending {get;private set;}
    public ComputerRun(string id,string goal,ComputerScope scope,Func<long>? clock=null,RunCaps? caps=null,bool armed=true)
    {
        Id=id;Goal=goal;Scope=scope;Caps=caps??new();this.armed=armed?1:0;this.clock=clock??(()=>Environment.TickCount64);
        StartedAt=lastAction=lastGateway=this.clock();
    }
    public bool Revoked=>Volatile.Read(ref revoked)!=0;
    public string? KillVia=>Volatile.Read(ref killVia);
    public bool IsHandsOn=>Volatile.Read(ref handsOn)!=0;
    public bool IsPaused=>Volatile.Read(ref paused)!=0;
    public bool IsClosed=>Volatile.Read(ref closed)!=0;
    // The run is armed once its start was accepted (a start with a non-empty scope waits for his code or chord). Nothing but the start's own confirm runs before.
    public bool Armed=>Volatile.Read(ref armed)!=0;
    public void Arm(){Interlocked.Exchange(ref armed,1);}
    // A sensitive read (or a server_read reported by the gateway) happened: every outward action asks from here on. It can only be raised, never lowered.
    public bool Tainted=>Volatile.Read(ref tainted)!=0;
    public void Taint(){Interlocked.Exchange(ref tainted,1);}
    /// <summary>The go after hands-on or after the 25-step stop: adds no scope and no goal, voids a pending ask. The gateway sends it only for a real spoken sentence.</summary>
    public void Resume(){lock(gate){Interlocked.Exchange(ref handsOn,0);StepsSinceFinal=0;Pending=null;Interlocked.Exchange(ref physical,0);lastAction=clock();}}
    public RunState State=>IsClosed||Revoked?RunState.Closed:IsHandsOn?RunState.HandsOn:IsPaused?RunState.Paused:RunState.Running;
    public string StateName=>State switch{RunState.Running=>"running",RunState.HandsOn=>"handson",RunState.Paused=>"paused",_=>Revoked?"killed":"ended"};
    // From the hook callback: nothing but an Interlocked write. The run token is revoked for good; the gateway cannot resume it.
    public void Kill(string via)
    {
        Interlocked.CompareExchange(ref killVia,via,null);
        Interlocked.Exchange(ref revoked,1);
    }
    public void HandsOn(){Interlocked.Exchange(ref handsOn,1);}
    public void Pause(bool on){Interlocked.Exchange(ref paused,on?1:0);}
    // He pressed the confirm chord on the keyboard (the hook only passes keys that are not injected): the pending ask may run on the next confirm command.
    public void PhysicalConfirm(){Interlocked.Exchange(ref physical,1);}
    public bool PhysicalConfirmed=>Volatile.Read(ref physical)!=0;
    public void Close(string why){lock(gate){ClosedWhy??=why;Pending=null;}Interlocked.Exchange(ref closed,1);}
    public void Seen(){Interlocked.Exchange(ref lastGateway,clock());}
    // A new spoken final that carries a trigger: appends to goal and scope, ends hands-on (all refs are the driver's business: they die), lifts the 25-step stop.
    public void Extend(string goal,ComputerScope more)
    {
        lock(gate)
        {
            Goal=(Goal+" / "+goal).Length>600?Goal:Goal+" / "+goal;
            foreach(var h in more.Hosts)Scope.Hosts.Add(h);foreach(var a in more.Apps)Scope.Apps.Add(a);
            StepsSinceFinal=0;Interlocked.Exchange(ref handsOn,0);Pending=null;Interlocked.Exchange(ref physical,0);lastAction=clock();
        }
    }
    // Null = the action may go ahead. Otherwise the code and rule the result carries. Called for every action, R2 proposals included; run state does the counting.
    public (string Code,string Rule)? Gate(bool forStart=false)
    {
        lock(gate)
        {
            Seen();
            if(Revoked)return (ComputerCodes.Killed,"killed");
            if(IsClosed)return (ComputerCodes.NoRun,ClosedWhy??"closed");
            if(!Armed&&!forStart)return (ComputerCodes.Refused,"not-armed");
            long now=clock();
            if(IsHandsOn)return (ComputerCodes.HandsOn,"handson");
            if(IsPaused)return (ComputerCodes.Refused,"paused");
            if(now-StartedAt>Caps.MaxMs){Close("time-cap");return (ComputerCodes.Refused,"time-cap");}
            if(Actions>=Caps.MaxActions){Close("action-cap");return (ComputerCodes.Refused,"action-cap");}
            if(StepsSinceFinal>=Caps.StepReport)return (ComputerCodes.Refused,"step-report");
            return null;
        }
    }
    public void Counted(){lock(gate){Actions++;StepsSinceFinal++;lastAction=clock();}}
    // An R2 proposal. A new one voids the old one; the fourth ask stops the run.
    public (string Code,string Rule)? Propose(PendingAsk ask)
    {
        lock(gate)
        {
            if(Asks>=Caps.MaxAsks){Close("asks-cap");return (ComputerCodes.Refused,"asks-cap");}
            Asks++;Pending=ask;Interlocked.Exchange(ref physical,0);lastAction=clock();return null;
        }
    }
    public enum ConfirmResult { Ok, NoPending, HashMismatch, Expired, Wrong }
    // One use: whatever the answer, the ask is over afterwards (a wrong code, a timeout, or the go). Two expiries stop the run.
    public ConfirmResult Confirm(string hash,string? spokenDigits,out PendingAsk? ask)
    {
        lock(gate)
        {
            ask=Pending;
            if(ask is null)return ConfirmResult.NoPending;
            if(ask.Hash!=hash)return ConfirmResult.HashMismatch;   // not consumed: it was not this ask he answered
            Pending=null;
            bool physicalGo=Interlocked.Exchange(ref physical,0)!=0;
            if(clock()-ask.At>Caps.PendingMs)
            {
                Expiries++;if(Expiries>=Caps.MaxExpiries)Close("expired-twice");
                return ConfirmResult.Expired;
            }
            if(physicalGo||ConfirmCode.Matches(ask.Code,spokenDigits))return ConfirmResult.Ok;
            return ConfirmResult.Wrong;
        }
    }
    // The 60 s are up with no answer at all (nobody sent a confirm): called from the timer so an unanswered ask still counts as an expiry.
    public bool ExpirePending()
    {
        lock(gate)
        {
            if(Pending is not {} p||clock()-p.At<=Caps.PendingMs)return false;
            Pending=null;Expiries++;if(Expiries>=Caps.MaxExpiries)Close("expired-twice");return true;
        }
    }
    public void Void(){lock(gate)Pending=null;}
    // Reasons to close that the clock decides: 60 s idle, the gateway gone quiet (heartbeat), the time cap.
    public string? Due()
    {
        long now=clock();
        if(IsClosed||Revoked)return null;
        if(now-StartedAt>Caps.MaxMs)return "time-cap";
        if(Pending is null&&now-Volatile.Read(ref lastAction)>Caps.IdleMs&&!IsHandsOn)return "idle";
        if(now-Volatile.Read(ref lastGateway)>Caps.GatewayQuietMs)return "gateway-quiet";
        return null;
    }
}
// Intent text and goal are redacted before they reach any log: emails, URLs, long digit runs and token-like strings go; 100 characters at most.
public static class ComputerRedact
{
    static readonly Regex Rx=new(@"([\w.+-]+@[\w-]+(\.[\w-]+)+)|(https?://\S+)|(\b[A-Za-z0-9_\-]{24,}\b)|(\d[\d ./-]{5,}\d)",RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));
    public static string Goal(string? s)
    {
        s=new string((s??"").Where(c=>!char.IsControl(c)).ToArray());
        try{s=Rx.Replace(s,"…");}catch(RegexMatchTimeoutException){s="…";}   // a timeout leaves only the ellipsis, nothing from the text
        return s.Length<=100?s:s[..100];
    }
}
