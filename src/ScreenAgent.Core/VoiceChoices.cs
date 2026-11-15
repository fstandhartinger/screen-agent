using System.Text.Json;
namespace ScreenAgent.Core;
public sealed record VoiceChoice(string Id,string Label,string? Note);
public static class VoiceChoices
{
    public static readonly string[] Providers=["default","xai","openai","gemini","gemini-fast"];
    public static bool ValidId(string? id)=>id is {Length:>=2 and <=20}&&id.All(c=>c is >= 'a' and <= 'z' or >= 'A' and <= 'Z');
    public static string CatalogProvider(string provider)=>provider switch {"default"=>"xai","gemini-fast"=>"gemini",_=>provider};
    public static VoiceChoice[] Catalog(JsonElement root,string provider)
    {
        var list=root.GetProperty("providers").GetProperty(CatalogProvider(provider)).GetProperty("voices");
        return list.EnumerateArray().Take(100).Select(v=>new VoiceChoice(v.GetProperty("id").GetString()!,v.GetProperty("label").GetString()!,
            v.TryGetProperty("note",out var note)&&note.ValueKind==JsonValueKind.String?note.GetString():null))
            .Where(v=>ValidId(v.Id)&&!string.IsNullOrWhiteSpace(v.Label)&&v.Label.Length<=100).DistinctBy(v=>v.Id).ToArray();
    }
    public static Uri CompanionUrl(AppConfig config,string version,string commit)
    {
        string name=config.VoiceNames.TryGetValue(config.VoiceProvider,out var id)&&ValidId(id)?"&voiceName="+Uri.EscapeDataString(id):"";
        return new Uri(new Uri(config.DeskUrl),"/?companion=1&voice="+Uri.EscapeDataString(config.VoiceProvider)+name+
            "&client=screen-agent&cv="+Uri.EscapeDataString(version)+"&cc="+Uri.EscapeDataString(commit)+"&device=windows");
    }
}
