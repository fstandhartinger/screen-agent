using System.Text.Json;
namespace ScreenAgent;
internal static class ScreenConfig
{
    public static string Folder=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"ScreenAgent");
    public static AppConfig Load()
    {
        Directory.CreateDirectory(Folder);string file=Path.Combine(Folder,"config.json");
        if(!File.Exists(file))File.WriteAllText(file,JsonSerializer.Serialize(new AppConfig(),new JsonSerializerOptions{WriteIndented=true}));
        return AppConfig.Parse(File.ReadAllText(file));
    }
}
