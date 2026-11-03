namespace ScreenAgent.Core;
public enum Level { Idle, Active, Busy, Paused, Warn, Muted }
public readonly record struct Status(string Text,Level Level,bool ShowToast);
public sealed record StatusInputs(bool HookHealthy,bool Paired,bool DeskLoggedIn,bool SessionRequested,bool Paused,bool VoiceFailed,
    bool NeedsUnmute,bool ServicesReachable,bool Locked,bool Dictating,bool Drawing,int IdCount,string SessionHotkey,bool Muted=false,string MuteHotkey="",bool Excluded=false,string? DictationDetail=null,bool Starting=false,int StartingSeconds=0);
// One place decides what the user is told, so the tray tooltip, the toast and the tests cannot drift apart.
// Order matters: a stuck warning must never hide the answer to "did my hotkey do anything?".
public static class StatusLine
{
    public static Status Compute(StatusInputs s)
    {
        if(!s.HookHealthy)return new("Tastatur-Hook nicht aktiv · Tray: Beenden und neu starten",Level.Warn,true);
        if(!s.SessionRequested)
            return !s.Paired?new("Nicht verbunden · Tray → Desk öffnen",Level.Warn,false)
                :!s.DeskLoggedIn?new("Desk nicht angemeldet · Tray → Desk öffnen",Level.Warn,false)
                :new($"Bereit · {s.SessionHotkey} startet die Sitzung",Level.Idle,false);
        if(!s.Paired)return new("Sitzung wartet · nicht verbunden · Tray → Desk öffnen",Level.Warn,true);
        if(!s.DeskLoggedIn)return new("Sitzung wartet · Desk nicht angemeldet · Tray → Desk öffnen",Level.Warn,true);
        if(s.Paused)return new("Pausiert",Level.Paused,true);
        if(s.Muted)return new($"Mikro stumm{(s.MuteHotkey==""?"":" · "+s.MuteHotkey+" hebt auf")}",Level.Muted,true);
        // The first session after launch can take seconds (WebView2, page, microphone): say so at once, then count.
        if(s.Starting)return new(s.StartingSeconds>=3?$"Sitzung startet … {s.StartingSeconds} s":"Sitzung startet …",Level.Busy,true);
        if(s.VoiceFailed)return new("Desk-Stimme startet nicht · Tray → Desk öffnen, Mikrofon erlauben",Level.Warn,true);
        if(s.NeedsUnmute)return new("Desk-Mikro stumm · Tray → Desk öffnen",Level.Warn,true);
        if(!s.ServicesReachable)return new("Desk/Registry nicht erreichbar",Level.Warn,true);
        if(s.Locked)return new("Bildschirm gesperrt",Level.Active,true);
        if(s.Excluded)return new("Fenster ausgenommen (Liste) · nicht erfasst",Level.Active,true);
        if(s.Dictating)return new(s.DictationDetail??"Diktat",Level.Busy,true);
        if(s.Drawing)return new("Zeichnen",Level.Busy,true);
        return new($"Sitzung · {s.IdCount} IDs",Level.Active,true);
    }
}
public static class KeyNames
{
    // German keyboards print Strg; config files keep the English names.
    public static string Display(string chord)=>chord.Replace("Ctrl","Strg");
}
