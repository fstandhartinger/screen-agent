using System.Text.RegularExpressions;
namespace ScreenAgent.Core;
public sealed record Correction(string From,string To);
public sealed record PendingCorrection(string From,string To,int Count);
public static class Corrections
{
    public static Correction? Diff(string before,string after)
    {
        var a=before.Split(' ',StringSplitOptions.RemoveEmptyEntries);var b=after.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        int start=0;while(start<a.Length&&start<b.Length&&a[start]==b[start])start++;
        int endA=a.Length,endB=b.Length;while(endA>start&&endB>start&&a[endA-1]==b[endB-1]){endA--;endB--;}
        if(endA-start is <1 or >3||endB-start is <1 or >3)return null;
        string from=string.Join(' ',a[start..endA]),to=string.Join(' ',b[start..endB]);
        return Regex.IsMatch(from,@"^\p{L}+( \p{L}+){0,2}$")&&Regex.IsMatch(to,@"^\p{L}+( \p{L}+){0,2}$")?new(from,to):null;
    }
    public static bool Observe(List<PendingCorrection> pending,Correction pair)
    {
        if(Diff(pair.From,pair.To)!=pair)return false;
        int i=pending.FindIndex(p=>p.From==pair.From&&p.To==pair.To);
        if(i<0){pending.Add(new(pair.From,pair.To,1));return false;}
        pending[i]=pending[i] with{Count=Math.Min(2,pending[i].Count+1)};return pending[i].Count>=2;
    }
    public static bool InsertedSpan(string before,string after,string inserted,out int start)
    {
        start=after.IndexOf(inserted,StringComparison.Ordinal);
        return inserted.Length>0&&start>=0&&after.IndexOf(inserted,start+inserted.Length,StringComparison.Ordinal)<0&&after.Remove(start,inserted.Length)==before;
    }
}
