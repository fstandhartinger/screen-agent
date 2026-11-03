namespace ScreenAgent.Core;
public static class StitchWords
{
    public static string[] Split(string text)=>text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
    // The comparison key of a word: letters and digits only, lower case ("Hallo," and "hallo" are the same word).
    public static string Key(string w)=>new(w.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
// Chunks cut without a pause overlap by half a second, so the end of one text and the start of the next say the same words. Those are taken out once.
public static class DictationJoin
{
    public const int MaxOverlapWords=8;
    public static string Join(IReadOnlyList<string> parts,IReadOnlyList<bool> overlapped)
    {
        var words=new List<string>();
        for(int i=0;i<parts.Count;i++)
        {
            var next=StitchWords.Split(parts[i]);
            if(next.Length==0)continue;
            int drop=0;
            if(i<overlapped.Count&&overlapped[i]&&words.Count>0)
            {
                for(int k=Math.Min(MaxOverlapWords,Math.Min(words.Count,next.Length));k>=1;k--)
                {
                    bool same=true;
                    for(int j=0;j<k&&same;j++)same=StitchWords.Key(words[words.Count-k+j])==StitchWords.Key(next[j])&&StitchWords.Key(next[j]).Length>0;
                    if(same){drop=k;break;}
                }
            }
            words.AddRange(next.Skip(drop));
        }
        return string.Join(' ',words);
    }
}
// Live captions arrive as rolling windows (the last 6 s every 1.5 s), each a fresh transcript of its own: the same words again, often a little different.
// The stitch finds the longest run the window shares with the end of what it has and puts the window's version from there on; so the text grows and
// only its last words may change. Without a shared run (a pause longer than a window) the window is appended.
public sealed class CaptionStitcher(int lookback=60)
{
    readonly List<string> words=[];
    public int Count=>words.Count;
    public string Text=>string.Join(' ',words);
    public string Last(int maxChars)
    {
        var t=Text;if(t.Length<=maxChars)return t;
        var cut=t.IndexOf(' ',t.Length-maxChars);return cut<0?t[^maxChars..]:t[(cut+1)..];
    }
    public void Reset()=>words.Clear();
    public void Add(string window)
    {
        var w=StitchWords.Split(window);
        if(w.Length==0)return;
        var wk=w.Select(StitchWords.Key).ToArray();
        int bestLen=0,bestJ=-1,bestS=0;
        int from=Math.Max(0,words.Count-lookback);
        for(int j=from;j<words.Count;j++)
        {
            var key=StitchWords.Key(words[j]);if(key.Length==0)continue;
            for(int s=0;s<Math.Min(w.Length,4);s++)   // the window may start in the middle of a word: its first words are not reliable
            {
                if(wk[s]!=key)continue;
                int len=0;while(j+len<words.Count&&s+len<w.Length&&StitchWords.Key(words[j+len])==wk[s+len])len++;
                if(len>bestLen||len==bestLen&&j>bestJ){bestLen=len;bestJ=j;bestS=s;}
            }
        }
        int need=w.Length==1?1:2;
        if(bestLen>=need)
        {
            words.RemoveRange(bestJ,words.Count-bestJ);
            words.AddRange(w.Skip(bestS));
        }
        else words.AddRange(w);
    }
    // Position now (for the chunk boundaries) and the words between two positions: what the live text says for the part of a chunk that failed.
    public int Mark()=>words.Count;
    public string Slice(int from,int to)
    {
        from=Math.Clamp(from,0,words.Count);to=Math.Clamp(to,from,words.Count);
        return string.Join(' ',words.GetRange(from,to-from));
    }
}
