using System.Text.RegularExpressions;
namespace ScreenAgent.Core;
public sealed record WindowContext(string process,bool blocked);
public sealed record ScreenPayload(string[] ids,WindowContext window,long frameVersion);
public sealed record Box(int x,int y,int w,int h);
public sealed record HighlightPayload(string crop,string context,Box bbox,string[] ids);
public sealed record IdsHighlightPayload(string[] ids,Box bbox,bool noImage=true);
public sealed record DictationContext(string process,string fieldBefore,string[] visibleIds,string[] recentTerms);
public static class DevicePayloads
{
    public static string[] Ids(IEnumerable<string> values)=>values.Select(x=>x.StartsWith("a#",StringComparison.OrdinalIgnoreCase)?x[2..]:x).Where(x=>Regex.IsMatch(x,"^[0-9a-fA-F]{6}$")).Select(x=>x.ToLowerInvariant()).Distinct().Take(20).ToArray();
    public static ScreenPayload Screen(IEnumerable<string> confirmed,string process,long version)=>new(Ids(confirmed),new(process,false),version);
    public static HighlightPayload Highlight(byte[] png,byte[] jpeg,Box bbox,IEnumerable<string> ids)
    {
        if(png.Length>2*1024*1024||jpeg.Length>1024*1024||bbox.w<=0||bbox.h<=0)throw new InvalidDataException("Highlight size limit");
        return new(Convert.ToBase64String(png),Convert.ToBase64String(jpeg),bbox,Ids(ids));
    }
}
