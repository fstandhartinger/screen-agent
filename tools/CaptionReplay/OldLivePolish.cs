using ScreenAgent.Core;
namespace CaptionReplay;
// The live clean-up of v0.1.15 (removed from Core in v0.1.16), kept here only to replay the old behaviour for before/after numbers.
public sealed class OldLivePolish(int volatileWords=6,int windowWords=70,int minNewWords=3)
{
    public sealed record Request(int From,int To,string[] Keys,string Text);
    int from,to=-1,lastEnd;string[] keys=[];string? text;bool busy;
    public bool Busy=>busy;
    public void Reset(){from=0;to=-1;lastEnd=0;keys=[];text=null;busy=false;}
    public Request? Next(IReadOnlyList<string> words)
    {
        if(busy)return null;
        int end=words.Count-volatileWords;
        if(end<=0||end-lastEnd<minNewWords)return null;
        int start=Math.Max(0,end-windowWords);
        var slice=words.Skip(start).Take(end-start).ToArray();
        busy=true;lastEnd=end;
        return new(start,end,slice.Select(StitchWords.Key).ToArray(),string.Join(' ',slice));
    }
    // null: the request failed; the raw text stays, the next one may go.
    public void Answer(Request r,string? polished)
    {
        busy=false;
        if(string.IsNullOrWhiteSpace(polished)||!DictationPolish.Accept(r.Text,polished))return;
        from=r.From;to=r.To;keys=r.Keys;text=polished.Trim();
    }
    public string Display(IReadOnlyList<string> words,int maxChars)
    {
        string full;
        if(text is not null&&to<=words.Count&&Same(words))
            full=string.Join(' ',words.Take(from).Append(text).Concat(words.Skip(to)));
        else full=string.Join(' ',words);
        if(full.Length<=maxChars)return full;
        int cut=full.IndexOf(' ',full.Length-maxChars);return cut<0?full[^maxChars..]:full[(cut+1)..];
    }
    bool Same(IReadOnlyList<string> words)
    {
        for(int i=0;i<keys.Length;i++)if(StitchWords.Key(words[from+i])!=keys[i])return false;
        return true;
    }
}
