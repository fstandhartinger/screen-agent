using System.Text.Json;
using System.Text.Json.Serialization;
namespace ScreenAgent.Core;
public sealed class AppConfig
{
    public string DeskUrl {get;init;}="https://desk.app.mintapis.com";
    public int IntervalMs {get;init;}=4000;
    public string VoiceProvider {get;init;}="xai";
    public bool RetainScreenshots {get;init;}=false;
    public string? UserEmail {get;init;}
    // Optional lists of windows that are never captured (process name prefixes, domains, title regexes). Empty by default: nothing is excluded.
    // An old config.json that still has IdsOnlyDomains (v0.1.2-v0.1.4) is read without error; the key is ignored.
    public string[] BlockProcesses {get;init;}=[];
    public string[] BlockDomains {get;init;}=[];
    public string[] BlockTitlePatterns {get;init;}=[];
    // "session": small click-through indicator while a session runs, plus short notices.
    // "notices": only short notices; the tray icon alone shows the state.
    public string Indicator {get;init;}="session";
    // Screen vision (Florian's spec): while a voice session runs, a downscaled JPEG goes to the Desk every IntervalMs when the picture changed.
    // Windows matching the three lists above (if you set any) are painted black or not captured. Images stay in memory, never on disk.
    public int LearnThreshold {get;init;}=1;
    public bool SendFrames {get;init;}=true;
    // Client events (hotkeys, frame decisions, ids, lifecycle; no images or text) go to the Desk session trace.
    public bool TraceUpload {get;init;}=true;
    // Florian's spec: Ctrl+Win+Shift (any order) starts and ends the voice session; Ctrl+Win alone stays untouched for Wispr Flow.
    // Dictation: hold Alt+Win, or press Alt+Win+Shift once to start and once to end. Mic mute and pause extend the session chord.
    public static Dictionary<string,string> DefaultHotkeys()=>new(){["session"]="Ctrl+Win+Shift",["dictation"]="Alt+Win",["dictationToggle"]="Alt+Win+Shift",["mute"]="Ctrl+Win+Shift+M",["handsFree"]="Alt+Win+Y",["draw"]="Caps",["pause"]="Ctrl+Win+Shift+P"};
    // Defaults written by v0.1.2/v0.1.3 into config.json (session on Alt+Win+Shift, no Ctrl+Win+Shift).
    public static Dictionary<string,string> V012Hotkeys()=>new(){["session"]="Alt+Win+Shift",["dictation"]="Alt+Win",["handsFree"]="Alt+Win+Y",["draw"]="Caps",["pause"]="Alt+Win+Shift+P"};
    // Defaults written by v0.1.0/v0.1.1. Dictation on Ctrl+Win collided with Wispr's chord.
    public static Dictionary<string,string> LegacyHotkeys()=>new(){["session"]="Ctrl+Win+Shift",["dictation"]="Ctrl+Win",["handsFree"]="Ctrl+Win+Space",["draw"]="Caps",["pause"]="Ctrl+Win+Shift+P"};
    public Dictionary<string,string> Hotkeys {get;init;}=DefaultHotkeys();
    // Set by Parse when an untouched legacy default was replaced; the caller then rewrites config.json.
    [JsonIgnore] public bool HotkeysMigrated {get;init;}
    public static bool SameHotkeys(Dictionary<string,string> a,Dictionary<string,string> b)=>a.Count==b.Count&&a.All(k=>b.TryGetValue(k.Key,out var v)&&string.Equals(k.Value,v,StringComparison.OrdinalIgnoreCase));
    public static AppConfig Parse(string json)
    {
        var c=JsonSerializer.Deserialize<AppConfig>(json,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new InvalidDataException("Invalid config");
        if(!Uri.TryCreate(c.DeskUrl,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.AbsolutePath!="/"||uri.Query!=""||uri.Fragment!=""||uri.UserInfo!="")throw new InvalidDataException("Desk URL must be an HTTPS origin");
        if(c.IntervalMs<1000||c.IntervalMs>60000)throw new InvalidDataException("Interval must be 1000..60000 ms");
        if(!new[]{"default","xai","openai","gemini","gemini-fast"}.Contains(c.VoiceProvider))throw new InvalidDataException("Unbekannter Sprach-Anbieter");
        if(c.Indicator is not ("session" or "notices"))throw new InvalidDataException("Indicator must be session or notices");
        if(SameHotkeys(c.Hotkeys,LegacyHotkeys())||SameHotkeys(c.Hotkeys,V012Hotkeys()))c=c.WithHotkeys(DefaultHotkeys(),true);
        else if(HotkeyBindings.Required.Any(k=>!c.Hotkeys.ContainsKey(k)))c=c.WithHotkeys(DefaultHotkeys().Where(d=>!c.Hotkeys.ContainsKey(d.Key)).Concat(c.Hotkeys).ToDictionary(x=>x.Key,x=>x.Value),false);
        _=new HotkeyBindings(c.Hotkeys);
        return c;
    }
    AppConfig WithHotkeys(Dictionary<string,string> keys,bool migrated)=>new()
    {
        DeskUrl=DeskUrl,IntervalMs=IntervalMs,VoiceProvider=VoiceProvider,RetainScreenshots=RetainScreenshots,UserEmail=UserEmail,
        BlockProcesses=BlockProcesses,BlockDomains=BlockDomains,BlockTitlePatterns=BlockTitlePatterns,
        Indicator=Indicator,SendFrames=SendFrames,TraceUpload=TraceUpload,Hotkeys=keys,HotkeysMigrated=migrated
    };
    public PrivacyPolicy Policy()=>new(){ExtraProcesses=BlockProcesses,ExtraDomains=BlockDomains,ExtraTitlePatterns=BlockTitlePatterns};
}
