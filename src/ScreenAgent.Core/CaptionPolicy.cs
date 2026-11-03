namespace ScreenAgent.Core;
public enum CaptionMode{Off,Dictation,Both}
public static class CaptionModes
{
    public static CaptionMode Parse(string s)=>s switch{"off"=>CaptionMode.Off,"dictation"=>CaptionMode.Dictation,_=>CaptionMode.Both};
    public static bool Valid(string s)=>s is "off" or "dictation" or "dictation+voice";
}
// When a caption request may go out. Every IntervalMs while the recent audio shows speech (the RMS gate: silence costs nothing), at most one in flight,
// 5 failures in a row stop it for 60 s, a 429/503 switches captions off for the rest of the session. An answer older than one already shown is dropped.
public sealed class CaptionPolicy(int intervalMs=1500,double speechRms=400,int maxFailures=5,int pauseMs=60000)
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
    public bool Answered(int id,bool ok,int status,long now)
    {
        InFlight=false;
        if(!ok)
        {
            Failed++;
            if(status is 429 or 503){Disabled=true;DisabledWhy="http-"+status;return false;}
            if(++failures>=maxFailures){pausedUntil=now+pauseMs;failures=0;}
            return false;
        }
        failures=0;
        if(id<shownSeq){Dropped++;return false;}
        shownSeq=id;return true;
    }
    public void NewSession(){Disabled=false;DisabledWhy=null;failures=0;pausedUntil=0;InFlight=false;}
}
// Counters and the cost line for caption.stats: no text, ever.
public sealed class CaptionStats
{
    public int Calls,Failures,Dropped;
    public double AudioSeconds,SpeechSeconds,CostUsd;
    public long TotalMs;
    public bool HasCost;
    public void Ok(double seconds,long ms,double? cost){Calls++;AudioSeconds+=seconds;TotalMs+=ms;if(cost is {} c){CostUsd+=c;HasCost=true;}}
    public Dictionary<string,object?> Snapshot(CaptionPolicy p)=>new()
    {
        ["calls"]=Calls,["failures"]=p.Failed,["dropped"]=p.Dropped,["audioSec"]=Math.Round(AudioSeconds,1),["speechSec"]=Math.Round(SpeechSeconds,1),
        ["avgMs"]=Calls==0?0:TotalMs/Calls,["costUsd"]=HasCost?Math.Round(CostUsd,4):null,["usdPerSpeechHour"]=HasCost&&SpeechSeconds>=60?Math.Round(CostUsd/(SpeechSeconds/3600),3):null,["disabled"]=p.DisabledWhy
    };
}
