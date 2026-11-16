namespace ScreenAgent.Core;
public enum CaptionMode{Off,Dictation,Both}
public static class CaptionModes
{
    public static CaptionMode Parse(string s)=>s switch{"off"=>CaptionMode.Off,"dictation"=>CaptionMode.Dictation,_=>CaptionMode.Both};
    public static bool Valid(string s)=>s is "off" or "dictation" or "dictation+voice";
}
// When a caption request may go out. Every IntervalMs while the recent audio shows speech (the RMS gate: silence costs nothing), at most one in flight,
// 3 failures in a row stop it for 15 s; only "no key" and "budget used up" switch captions off for the rest of the session (one slow answer used to, 4 Nov). An answer older than one already shown is dropped.
public sealed class CaptionPolicy(int intervalMs=1500,double speechRms=400,int maxFailures=3,int pauseMs=15000)
{
    long lastSendAt=long.MinValue/2,pausedUntil,lastSpeechAt=long.MinValue/2;
    int failures,seq,shownSeq;
    public bool InFlight {get;private set;}
    public bool Disabled {get;private set;}
    public string? DisabledWhy {get;private set;}
    public int Sent {get;private set;}
    public int Dropped {get;private set;}
    public int Failed {get;private set;}
    // Feed the level of each audio block; speech = RMS over the threshold.
    public void Heard(long now,double rms){if(rms>=speechRms)lastSpeechAt=now;}
    public bool Speaking(long now)=>now-lastSpeechAt<=2000;
    public bool SpeechSince(long now,int ms)=>now-lastSpeechAt<=ms;
    public bool ShouldSend(long now)=>!Disabled&&!InFlight&&now>=pausedUntil&&now-lastSendAt>=intervalMs&&Speaking(now);
    public int Begin(long now){InFlight=true;lastSendAt=now;Sent++;return ++seq;}
    // Returns true when the answer may be shown.
    public bool Answered(int id,bool ok,int status,long now,string? code=null)
    {
        InFlight=false;
        if(!ok)
        {
            Failed++;
            if(code is "no-key" or "caption-budget"){Disabled=true;DisabledWhy=code;return false;}
            if(++failures>=maxFailures){pausedUntil=now+pauseMs;failures=0;}
            return false;
        }
        failures=0;
        if(id<shownSeq){Dropped++;return false;}
        shownSeq=id;return true;
    }
    public void NewSession(){Disabled=false;DisabledWhy=null;failures=0;pausedUntil=0;InFlight=false;}
}
// v0.1.19: when the subtitle's last seconds are re-read (config CaptionRevise). Every IntervalMs while he speaks, and once more when he stops (the tail);
// one in flight; 3 failures in a row pause it 30 s; no key, budget or a switched-off gateway stop it for the session. A failed or late re-read changes
// nothing: the fast text stays. The app and the replay tool (tools/CaptionReplay) both decide through Next().
public sealed class RevisePolicy(int intervalMs=3500,int maxFailures=3,int pauseMs=30000,int tailMs=800)
{
    long lastSendAt=long.MinValue/2,pausedUntil;int failures;
    public bool InFlight {get;private set;}
    public bool Disabled {get;private set;}
    public string? DisabledWhy {get;private set;}
    public int Sent {get;private set;}
    public int Failed {get;private set;}
    public bool ShouldSend(long now,bool speaking,bool tailPending)=>!Disabled&&!InFlight&&now>=pausedUntil&&(speaking?now-lastSendAt>=intervalMs:tailPending&&now-lastSendAt>=tailMs);
    public void Begin(long now){InFlight=true;lastSendAt=now;Sent++;}
    public void Answered(bool ok,long now,string? code=null)
    {
        InFlight=false;
        if(ok){failures=0;return;}
        Failed++;
        if(code is "no-key" or "caption-budget" or "revise-off"){Disabled=true;DisabledWhy=code;return;}
        if(++failures>=maxFailures){failures=0;pausedUntil=now+pauseMs;}
    }
    public void NewSession(){Disabled=false;DisabledWhy=null;failures=0;pausedUntil=0;InFlight=false;}
    // The one decision per tick: a request to send (Begin already called) or null. audioNow = seconds of audio fed so far; speaking = speech in the last 2 s.
    public ReviseRequest? Next(LiveCaption m,long now,double audioNow,bool speaking,double maxSeconds=25)
    {
        bool tail=!speaking&&m.Dirty;
        if(!ShouldSend(now,speaking,tail))return null;
        var r=m.NextRevise(audioNow,maxSeconds,speaking?2.5:0);
        if(r is not null)Begin(now);
        return r;
    }
}
// Counters and the cost line for caption.stats: no text, ever.
public sealed class CaptionStats
{
    public int Calls,Failures,Dropped;
    public double AudioSeconds,SpeechSeconds,CostUsd;
    public long TotalMs;
    public bool HasCost;
    public void Ok(double seconds,long ms,double? cost){Calls++;AudioSeconds+=seconds;TotalMs+=ms;if(cost is {} c){CostUsd+=c;HasCost=true;}}
    public int PolishCalls,PolishOk;public long PolishMs;
    public int ReviseCalls,ReviseOk,ReviseUsed;public long ReviseMs;public double ReviseCost;
    public void Revise(bool ok,bool used,long ms,double? cost){ReviseCalls++;if(ok){ReviseOk++;ReviseMs+=ms;}if(used)ReviseUsed++;if(cost is {} c){ReviseCost+=c;CostUsd+=c;HasCost=true;}}
    public void Polish(bool ok,long ms){PolishCalls++;if(ok){PolishOk++;PolishMs+=ms;}}
    public Dictionary<string,object?> Snapshot(CaptionPolicy p)=>new()
    {
        ["calls"]=Calls,["failures"]=p.Failed,["dropped"]=p.Dropped,["sentSec"]=Math.Round(AudioSeconds,1),["speechSec"]=Math.Round(SpeechSeconds,1),
        ["avgMs"]=Calls==0?0:TotalMs/Calls,["costUsd"]=HasCost?Math.Round(CostUsd,4):null,["usdPerSpeechHour"]=HasCost&&SpeechSeconds>=60?Math.Round(CostUsd/(SpeechSeconds/3600),3):null,["disabled"]=p.DisabledWhy,
        ["polishCalls"]=PolishCalls,["polishOk"]=PolishOk,["polishAvgMs"]=PolishOk==0?0:PolishMs/PolishOk,
        ["reviseCalls"]=ReviseCalls,["reviseOk"]=ReviseOk,["reviseUsed"]=ReviseUsed,["reviseAvgMs"]=ReviseOk==0?0:ReviseMs/ReviseOk,["reviseCostUsd"]=Math.Round(ReviseCost,4)
    };
}
