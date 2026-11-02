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
    public string[] BlockProcesses {get;init;}=[];
    public string[] IdsOnlyDomains {get;init;}=["bittensor-earnings.lovable.app"];
    public string[] BlockDomains {get;init;}=[];
    public string[] BlockTitlePatterns {get;init;}=[];
    // "session": small click-through indicator while a session runs, plus short notices.
    // "notices": only short notices; the tray icon alone shows the state.
    public string Indicator {get;init;}="session";
    // Alt+Win family: Ctrl+Win belongs to Wispr Flow (and Windows itself), so it must stay untouched.
    public static Dictionary<string,string> DefaultHotkeys()=>new(){["session"]="Alt+Win+Shift",["dictation"]="Alt+Win",["handsFree"]="Alt+Win+Y",["draw"]="Caps",["pause"]="Alt+Win+Shift+P"};
    // Defaults written by v0.1.0/v0.1.1 into config.json. They collided with Wispr's Ctrl+Win chord.
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
        if(SameHotkeys(c.Hotkeys,LegacyHotkeys()))c=c.WithHotkeys(DefaultHotkeys(),true);
        _=new HotkeyBindings(c.Hotkeys);
        return c;
    }
    AppConfig WithHotkeys(Dictionary<string,string> keys,bool migrated)=>new()
    {
        DeskUrl=DeskUrl,IntervalMs=IntervalMs,VoiceProvider=VoiceProvider,RetainScreenshots=RetainScreenshots,UserEmail=UserEmail,
        BlockProcesses=BlockProcesses,IdsOnlyDomains=IdsOnlyDomains,BlockDomains=BlockDomains,BlockTitlePatterns=BlockTitlePatterns,
        Indicator=Indicator,Hotkeys=keys,HotkeysMigrated=migrated
    };
    public PrivacyPolicy Policy()=>new(){ExtraProcesses=BlockProcesses,ExtraDomains=BlockDomains,IdsOnlyDomains=IdsOnlyDomains,ExtraTitlePatterns=BlockTitlePatterns};
}
