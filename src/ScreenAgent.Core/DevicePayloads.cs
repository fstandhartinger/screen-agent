using System.Text.RegularExpressions;
namespace ScreenAgent.Core;
public sealed record WindowContext(string process,bool blocked);
public sealed record ScreenPayload(string[] ids,WindowContext window,long frameVersion);
public sealed record Box(int x,int y,int width,int height);
public sealed record HighlightPayload(string crop,string context,Box bbox,string[] ids);
public sealed record DictationContext(string process,string fieldBefore,string[] visibleIds,string[] recentTerms);
public static class DevicePayloads
{
    public static string[] Ids(IEnumerable<string> values)=>values.Where(x=>Regex.IsMatch(x,"^a#[0-9a-f]{6}$")).Distinct().Take(20).ToArray();
    public static ScreenPayload Screen(IEnumerable<string> confirmed,string process,long version)=>new(Ids(confirmed),new(process,false),version);
    public static HighlightPayload Highlight(byte[] png,byte[] jpeg,Box bbox,IEnumerable<string> ids)
    {
        if(png.Length>2*1024*1024||jpeg.Length>1024*1024||bbox.width<=0||bbox.height<=0)throw new InvalidDataException("Highlight size limit");
        return new(Convert.ToBase64String(png),Convert.ToBase64String(jpeg),bbox,Ids(ids));
    }
}
