using System.Text.RegularExpressions;
namespace ScreenAgent.Core;
public sealed record WindowContext(string process,bool blocked);
public sealed record ScreenPayload(string[] ids,WindowContext window,long frameVersion);
public sealed record Box(int x,int y,int w,int h);
public sealed record HighlightPayload(string crop,string context,Box bbox,string[] ids);
public sealed record DictationContext(string process,string fieldBefore,string[] visibleIds,string[] recentTerms);
public static class DevicePayloads
{
    public static string[] Ids(IEnumerable<string> values)=>values.Select(x=>x.StartsWith("a#",StringComparison.OrdinalIgnoreCase)?x[2..]:x).Where(x=>Regex.IsMatch(x,"^[0-9a-fA-F]{6}$")).Select(x=>x.ToLowerInvariant()).Distinct().Take(20).ToArray();
    // ids from several sources (OCR confirmed by the registry, ids the vision model read and the gateway verified), one list.
    public static string[] Merge(params IEnumerable<string>[] sources)=>Ids(sources.SelectMany(x=>x));
    public static readonly string[] Reasons=["locked","paused","stop","error"];
    // Body of POST /screen/clear and /screen/private: {why} when it is one of the contract's words, otherwise an empty object.
    public static Dictionary<string,object?> Why(string? why)=>Reasons.Contains(why)?new(){["why"]=why}:new();
    public static ScreenPayload Screen(IEnumerable<string> confirmed,string process,long version)=>new(Ids(confirmed),new(process,false),version);
    public static HighlightPayload Highlight(byte[] png,byte[] jpeg,Box bbox,IEnumerable<string> ids)
    {
        if(png.Length>2*1024*1024||jpeg.Length>1024*1024||bbox.w<=0||bbox.h<=0)throw new InvalidDataException("Highlight size limit");
        return new(Convert.ToBase64String(png),Convert.ToBase64String(jpeg),bbox,Ids(ids));
    }
}
// Crop around the strokes: 15 % of the larger side plus 5 px margin (the stroke is 5 px wide), clamped to the monitor. Null when nothing is left.
public static class HighlightGeometry
{
    public static Box? Crop(double minX,double maxX,double minY,double maxY,int width,int height)
    {
        int margin=(int)Math.Ceiling(Math.Max(maxX-minX,maxY-minY)*.15)+5;
        int l=Math.Max(0,(int)minX-margin),t=Math.Max(0,(int)minY-margin),r=Math.Min(width,(int)maxX+margin+1),b=Math.Min(height,(int)maxY+margin+1);
        return r>l&&b>t?new(l,t,r-l,b-t):null;
    }
}
