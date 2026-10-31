using System.Text.Json;
namespace ScreenAgent.Core;
public static class DeviceProtocol
{
    public static string[] Known(string json,IEnumerable<string> candidates)
    {
        using var document=JsonDocument.Parse(json);var root=document.RootElement;
        if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("known",out var list)||list.ValueKind!=JsonValueKind.Array)return [];
        return DevicePayloads.Ids(list.EnumerateArray().Where(e=>e.ValueKind==JsonValueKind.String).Select(e=>e.GetString()!)).Intersect(DevicePayloads.Ids(candidates)).ToArray();
    }
    public static string[] Terms(string json)
    {
        using var document=JsonDocument.Parse(json);var root=document.RootElement;
        if(root.ValueKind==JsonValueKind.Object){if(root.TryGetProperty("terms",out var terms))root=terms;else if(root.TryGetProperty("items",out var items))root=items;}
        if(root.ValueKind!=JsonValueKind.Array)return [];
        return root.EnumerateArray().Select(e=>e.ValueKind==JsonValueKind.String?e.GetString():e.ValueKind==JsonValueKind.Object&&e.TryGetProperty("term",out var t)&&t.ValueKind==JsonValueKind.String?t.GetString():null).Where(x=>!string.IsNullOrEmpty(x)&&x.Length<=100).Select(x=>x!).Distinct().Take(100).ToArray();
    }
    public static DateTimeOffset Expiry(JsonElement value)=>value.ValueKind==JsonValueKind.Number?DateTimeOffset.FromUnixTimeMilliseconds(value.GetInt64()):value.GetDateTimeOffset();
}
