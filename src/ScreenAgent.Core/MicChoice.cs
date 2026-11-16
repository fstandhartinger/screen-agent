namespace ScreenAgent.Core;
// The microphone he picks in the bottom-right overlay or the tray, stored by its Windows name (config "Microphone"; null = Windows default).
// Dictation and the subtitle microphone open it through Windows; the Desk page matches the same name against the browser's devices.
public static class MicChoice
{
    public const int MaxName=200;
    public static bool Valid(string? name)=>name is null||name.Trim().Length is >0 and <=MaxName&&!name.Any(char.IsControl);
    public static string? Clean(string? name)=>string.IsNullOrWhiteSpace(name)?null:name.Trim();
    // The device id for a stored name: the same name first, then a device whose name starts with it (a USB id suffix). null: use the default.
    public static string? Pick(IReadOnlyList<(string Id,string Name)> devices,string? name)
    {
        if(Clean(name) is not {} want)return null;
        foreach(var d in devices)if(string.Equals(d.Name.Trim(),want,StringComparison.OrdinalIgnoreCase))return d.Id;
        foreach(var d in devices)if(d.Name.Trim().StartsWith(want+" (",StringComparison.OrdinalIgnoreCase))return d.Id;
        return null;
    }
    // The browser's track label belongs to the chosen name (Chromium may add " (vid:pid)").
    public static bool Same(string? label,string? name)
    {
        if(Clean(name) is not {} want)return true;
        if(Clean(label) is not {} have)return false;
        return string.Equals(have,want,StringComparison.OrdinalIgnoreCase)||have.StartsWith(want+" (",StringComparison.OrdinalIgnoreCase);
    }
    public static bool SameName(string? a,string? b)=>string.Equals(Clean(a),Clean(b),StringComparison.OrdinalIgnoreCase);
    // For the menu and the note: long driver names are cut.
    public static string Short(string name,int max=40)=>name.Length<=max?name:name[..(max-1)].TrimEnd()+"…";
}
