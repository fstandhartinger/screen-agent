using System.Text.Json;
namespace ScreenAgent;
internal sealed class ScreenConfig
{
    public string[] BlockProcesses {get;init;}=[];
    public string[] BlockDomains {get;init;}=[];
    public string[] BlockTitlePatterns {get;init;}=[];
    public PrivacyPolicy Policy()=>new(){ExtraProcesses=BlockProcesses,ExtraDomains=BlockDomains,ExtraTitlePatterns=BlockTitlePatterns};
    public static ScreenConfig Load()
    {
        string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"ScreenAgent");
        Directory.CreateDirectory(folder);string file=Path.Combine(folder,"config.json");
        if(!File.Exists(file)){var defaults=new ScreenConfig();File.WriteAllText(file,JsonSerializer.Serialize(defaults,new JsonSerializerOptions{WriteIndented=true}));return defaults;}
        return JsonSerializer.Deserialize<ScreenConfig>(File.ReadAllText(file),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new InvalidDataException("Invalid Screen Agent config");
    }
}
