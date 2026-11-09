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
    public bool ScreenTextForSpeech {get;init;}=true;
    public bool SendFrames {get;init;}=true;
    // Client events (hotkeys, frame decisions, ids, lifecycle; no images or text) go to the Desk session trace.
    public bool TraceUpload {get;init;}=true;
    // The drawing panel opens by itself when the agent draws ("auto"); "manual": only the chord, the tray or the Desk button open it.
    public string CanvasPanel {get;init;}="auto";
    // Dictation recordings are written to %LOCALAPPDATA%\ScreenAgent\dictation from the first sample. After DictationMaxMinutes of one dictation the next file is started
    // (the recording never stops by itself). Kept locally: a processed one KeepHours, a failed one KeepFailedDays, everything together at most DictationCapMb (oldest successful first).
    public double DictationMaxMinutes {get;init;}=60;
    public double DictationKeepHours {get;init;}=24;
    public double DictationKeepFailedDays {get;init;}=14;
    public int DictationCapMb {get;init;}=2048;
    // Live subtitles from the microphone: "dictation+voice" (default), "dictation" or "off". Needs the gateway's caption route; the audio of the last 6 s goes there while someone speaks.
    public string Captions {get;init;}="dictation+voice";
    // v0.1.12: the clean-up with the glossary of his world (misheard names, fillers, false sentence ends, speaking grammar, spoken corrections).
    // DictationPolish: one more pass over the whole dictation before it is typed (several chunks). CaptionPolish: the live subtitle while he dictates.
    public bool DictationPolish {get;init;}=true;
    // v0.1.13: light structure in the inserted text (a line break between thoughts, a blank line between topics; same words, no markup). Only where it is pasted.
    public bool DictationFormat {get;init;}=true;
    public bool CaptionPolish {get;init;}=true;
    // While a dictation is being processed the pointer shows the arrow with the little circle (system-wide, and it always comes back). false = the circle in the pill only.
    public bool DictationBusyCursor {get;init;}=true;
    // Live activity overlay: what the agent is doing right now (tool calls, typing the Main Agent prompt), small rows above the status note.
    public bool ActivityOverlay {get;init;}=true;
    // Purple flash plus a small thumbnail when the agent looks at the screen: "visible" (default: session start, on request, highlight), "all" (every frame sent), "off".
    public string ScreenshotFeedback {get;init;}="visible";
    // Accessible names of the Claude Code message box in VS Code (the only place the Main Agent prompt is ever typed into).
    // null in config.json means the default.
    public string[] MainAgentInputNames {get=>inputNames;init=>inputNames=value??DefaultInputNames;}
    static readonly string[] DefaultInputNames=["Message input"];
    string[] inputNames=DefaultInputNames;
    // Plain "Code" (stable VS Code) is only a target for the Main Agent prompt when this is true (default false: the Claude session lives in Insiders).
    public bool MainAgentAllowStableCode {get;init;}=false;
    // Start briefing (v0.1.7): at session start and on request the app tells the Desk which windows and Chrome tabs are open, what OCR last read
    // and the tail of the main Claude Code session. MainSessionPath overrides where that session is looked up (a .jsonl file, a projects folder, an account folder or a home folder).
    // Computer use (v0.1.7 F): the agent may operate the PC (UI Automation, Chrome through its debugging port) on spoken request, with confirmations and a kill key. Off by default; switched in the tray
    // ("Computer-Steuerung erlauben"), which also tells the Desk (POST api/device/computer). Never read from the Desk.
    public bool ComputerUse {get;set;}=false;
    // Florian, 4 Nov 2026: full control without confirmations or site limits, he supervises in real time. false brings the v0.1.8 rules back (confirmations, scope, sensitive lists).
    public bool ComputerUnrestricted {get;init;}=true;
    // Hosts (or domains) that count as sensitive for computer use in addition to the built-in lists: every action there asks. Only ever adds.
    public string[] ComputerSensitive {get;init;}=[];
    public bool Briefing {get;init;}=true;
    public string? MainSessionPath {get;init;}
    // Florian's spec: Ctrl+Win+Shift (any order) starts and ends the voice session; Ctrl+Win alone stays untouched for Wispr Flow.
    // Dictation: hold Alt+Win, or press Alt+Win+Shift once to start and once to end. Mic mute and pause extend the session chord.
    public static Dictionary<string,string> DefaultHotkeys()=>new(){["session"]="Ctrl+Win+Shift",["dictation"]="Alt+Win",["dictationToggle"]="Alt+Win+Shift",["mute"]="Ctrl+Win+Shift+M",["canvas"]="Ctrl+Win+Shift+D",["handsFree"]="Alt+Win+Y",["draw"]="Caps",["pause"]="Ctrl+Win+Shift+P"};
    // Defaults written by v0.1.2/v0.1.3 into config.json (session on Alt+Win+Shift, no Ctrl+Win+Shift).
    public static Dictionary<string,string> V012Hotkeys()=>new(){["session"]="Alt+Win+Shift",["dictation"]="Alt+Win",["handsFree"]="Alt+Win+Y",["draw"]="Caps",["pause"]="Alt+Win+Shift+P"};
    // Defaults written by v0.1.0/v0.1.1. Dictation on Ctrl+Win collided with Wispr's chord.
    public static Dictionary<string,string> LegacyHotkeys()=>new(){["session"]="Ctrl+Win+Shift",["dictation"]="Ctrl+Win",["handsFree"]="Ctrl+Win+Space",["draw"]="Caps",["pause"]="Ctrl+Win+Shift+P"};
    public Dictionary<string,string> Hotkeys {get;init;}=DefaultHotkeys();
    // Set by Parse when an untouched legacy default was replaced; the caller then rewrites config.json.
    [JsonIgnore] public bool HotkeysMigrated {get;init;}
    // Set by Parse when the default canvas chord collided with a custom one and was moved or dropped (empty value = no canvas chord); the caller logs it.
    [JsonIgnore] public string? HotkeyNote {get;init;}
    public const string NoChord="";
    public static bool SameHotkeys(Dictionary<string,string> a,Dictionary<string,string> b)=>a.Count==b.Count&&a.All(k=>b.TryGetValue(k.Key,out var v)&&string.Equals(k.Value,v,StringComparison.OrdinalIgnoreCase));
    public static AppConfig Parse(string json)
    {
        var c=JsonSerializer.Deserialize<AppConfig>(json,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new InvalidDataException("Invalid config");
        if(!Uri.TryCreate(c.DeskUrl,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.AbsolutePath!="/"||uri.Query!=""||uri.Fragment!=""||uri.UserInfo!="")throw new InvalidDataException("Desk URL must be an HTTPS origin");
        if(c.IntervalMs<1000||c.IntervalMs>60000)throw new InvalidDataException("Interval must be 1000..60000 ms");
        if(!new[]{"default","xai","openai","gemini","gemini-fast"}.Contains(c.VoiceProvider))throw new InvalidDataException("Unbekannter Sprach-Anbieter");
        if(c.Indicator is not ("session" or "notices"))throw new InvalidDataException("Indicator must be session or notices");
        if(c.CanvasPanel is not ("auto" or "manual"))throw new InvalidDataException("CanvasPanel must be auto or manual");
        if(!CaptionModes.Valid(c.Captions))throw new InvalidDataException("Captions must be dictation+voice, dictation or off");
        if(c.DictationMaxMinutes<0.05||c.DictationMaxMinutes>600||c.DictationKeepHours<0||c.DictationKeepFailedDays<1||c.DictationCapMb<50)throw new InvalidDataException("Invalid dictation retention settings");
        if(!ScreenAgent.Core.ScreenshotFeedback.Modes.Contains(c.ScreenshotFeedback))throw new InvalidDataException("ScreenshotFeedback must be visible, all or off");
        if(c.MainAgentInputNames.Length==0||c.MainAgentInputNames.Any(string.IsNullOrWhiteSpace))throw new InvalidDataException("MainAgentInputNames needs at least one name");
        if(c.MainSessionPath is {} msp&&(msp.Length>400||msp.Any(char.IsControl)))throw new InvalidDataException("MainSessionPath is not a path");
        if(SameHotkeys(c.Hotkeys,LegacyHotkeys())||SameHotkeys(c.Hotkeys,V012Hotkeys()))c=c.WithHotkeys(DefaultHotkeys(),true);
        else if(HotkeyBindings.Required.Any(k=>!c.Hotkeys.ContainsKey(k)))
        {
            var merged=DefaultHotkeys().Where(d=>!c.Hotkeys.ContainsKey(d.Key)).Concat(c.Hotkeys).ToDictionary(x=>x.Key,x=>x.Value);
            string? note=null;
            if(!c.Hotkeys.ContainsKey("canvas")&&!Valid(merged))
            {
                // A default canvas chord that collides with this config's own keys never stops the app: the first free letter, else no chord (the tray still opens the panel).
                merged["canvas"]=NoChord;note="canvas chord dropped (Ctrl+Win+Shift+D and D, Z, G, J, K are taken)";
                foreach(var letter in new[]{"D","Z","G","J","K"})
                {
                    merged["canvas"]="Ctrl+Win+Shift+"+letter;
                    if(Valid(merged)){note=letter=="D"?null:"canvas chord moved to Ctrl+Win+Shift+"+letter+" (D is taken)";break;}
                    merged["canvas"]=NoChord;
                }
            }
            c=c.WithHotkeys(merged,false,note);
        }
        _=new HotkeyBindings(c.Hotkeys);
        return c;
    }
    static bool Valid(Dictionary<string,string> keys){try{_=new HotkeyBindings(keys);return true;}catch(InvalidDataException){return false;}   /* invalid keys: the defaults apply, HotkeyNote says so */}
    AppConfig WithHotkeys(Dictionary<string,string> keys,bool migrated,string? note=null)=>new()
    {
        DeskUrl=DeskUrl,IntervalMs=IntervalMs,VoiceProvider=VoiceProvider,RetainScreenshots=RetainScreenshots,UserEmail=UserEmail,
        BlockProcesses=BlockProcesses,BlockDomains=BlockDomains,BlockTitlePatterns=BlockTitlePatterns,
        DictationBusyCursor=DictationBusyCursor,ComputerUnrestricted=ComputerUnrestricted,Indicator=Indicator,ScreenTextForSpeech=ScreenTextForSpeech,SendFrames=SendFrames,TraceUpload=TraceUpload,CanvasPanel=CanvasPanel,Briefing=Briefing,MainSessionPath=MainSessionPath,ActivityOverlay=ActivityOverlay,ScreenshotFeedback=ScreenshotFeedback,MainAgentInputNames=MainAgentInputNames,MainAgentAllowStableCode=MainAgentAllowStableCode,ComputerUse=ComputerUse,ComputerSensitive=ComputerSensitive,LearnThreshold=LearnThreshold,DictationMaxMinutes=DictationMaxMinutes,DictationKeepHours=DictationKeepHours,DictationKeepFailedDays=DictationKeepFailedDays,DictationCapMb=DictationCapMb,Captions=Captions,DictationPolish=DictationPolish,DictationFormat=DictationFormat,CaptionPolish=CaptionPolish,Hotkeys=keys,HotkeysMigrated=migrated,HotkeyNote=note
    };
    public RetentionOptions Retention()=>new(TimeSpan.FromHours(DictationKeepHours),TimeSpan.FromDays(DictationKeepFailedDays),DictationCapMb*(1L<<20));
    public PrivacyPolicy Policy()=>new(){ExtraProcesses=BlockProcesses,ExtraDomains=BlockDomains,ExtraTitlePatterns=BlockTitlePatterns};
}
