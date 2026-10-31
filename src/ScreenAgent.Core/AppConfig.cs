using System.Text.Json;
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
    public Dictionary<string,string> Hotkeys {get;init;}=new(){["session"]="Ctrl+Win+Shift",["dictation"]="Ctrl+Win",["handsFree"]="Ctrl+Win+Space",["draw"]="Caps",["pause"]="Ctrl+Win+Shift+P"};
    public static AppConfig Parse(string json)
    {
        var c=JsonSerializer.Deserialize<AppConfig>(json,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new InvalidDataException("Invalid config");
        if(!Uri.TryCreate(c.DeskUrl,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.AbsolutePath!="/"||uri.Query!=""||uri.Fragment!=""||uri.UserInfo!="")throw new InvalidDataException("Desk URL must be an HTTPS origin");
        if(c.IntervalMs<1000||c.IntervalMs>60000)throw new InvalidDataException("Interval must be 1000..60000 ms");
        if(!new[]{"default","xai","openai","gemini","gemini-fast"}.Contains(c.VoiceProvider))throw new InvalidDataException("Unbekannter Sprach-Anbieter");
        _=new HotkeyBindings(c.Hotkeys);
        return c;
    }
    public PrivacyPolicy Policy()=>new(){ExtraProcesses=BlockProcesses,ExtraDomains=BlockDomains,IdsOnlyDomains=IdsOnlyDomains,ExtraTitlePatterns=BlockTitlePatterns};
}
