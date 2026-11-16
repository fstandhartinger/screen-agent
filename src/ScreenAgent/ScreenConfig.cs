using System.Text.Json;
namespace ScreenAgent;
internal static class ScreenConfig
{
    public static string Folder=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"ScreenAgent");
    public static AppConfig Load()
    {
        Directory.CreateDirectory(Folder);string file=Path.Combine(Folder,"config.json");
        if(!File.Exists(file))File.WriteAllText(file,JsonSerializer.Serialize(new AppConfig(),new JsonSerializerOptions{WriteIndented=true}));
        string text=File.ReadAllText(file);var config=AppConfig.Parse(text);
        if(config.HotkeysMigrated)
        {
            // v0.1.1 wrote Ctrl+Win defaults that fight with Wispr. Keep the old file once, then store the new defaults.
            string backup=file+".v0.1.1.bak";if(!File.Exists(backup))File.WriteAllText(backup,text);
            string temp=file+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(config,new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,file,true);
        }
        return config;
    }
    public static void SetVoiceNames(Dictionary<string,string> names)
    {
        string file=Path.Combine(Folder,"config.json");
        var node=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        // Replace a differently cased key too (config parsing is case insensitive).
        foreach(var key in node.Select(k=>k.Key).Where(k=>string.Equals(k,"VoiceNames",StringComparison.OrdinalIgnoreCase)).ToArray())node.Remove(key);
        node["VoiceNames"]=JsonSerializer.SerializeToNode(names);
        string temp=file+".tmp";File.WriteAllText(temp,node.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,file,true);
    }
    // Only the microphone key is rewritten; null removes it (Windows default).
    public static void SetMicrophone(string? name)
    {
        string file=Path.Combine(Folder,"config.json");
        var node=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        foreach(var key in node.Select(k=>k.Key).Where(k=>string.Equals(k,"Microphone",StringComparison.OrdinalIgnoreCase)).ToArray())node.Remove(key);
        if(name is not null)node["Microphone"]=name;
        string temp=file+".tmp";File.WriteAllText(temp,node.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,file,true);
    }
    // Only this one key is rewritten, everything else in config.json stays as the user wrote it.
    public static void SetComputerUse(bool on)
    {
        string file=Path.Combine(Folder,"config.json");
        var node=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        node["ComputerUse"]=on;
        string temp=file+".tmp";File.WriteAllText(temp,node.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,file,true);
    }
}
