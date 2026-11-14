namespace ScreenAgent.Core;
// v0.1.16 (Florian, 14 Nov 2026): "wenn ich stottere, wird ein Wort zweimal angezeigt" and "ein System, das während ich rede das bereits Gehörte refined
// ... und somit auch das in dieser Caption Angezeigte sich selbst korrigiert". What was wrong in 0.1.15 (RESULT.md of job screen-agent-caption-refinement-20261114):
//   - the stitch looked for the window's place only through its first four words and an exact run of two; when those were heard a little differently it
//     appended the whole window (four to six seconds of words twice);
//   - the stitch rewrote the last ~15 words with every window, so the live clean-up's answer (which had to match those words exactly) was mostly thrown away;
//   - stutters were never taken out of the live text (the final pass does it, the live prompt did not ask for it);
//   - the transcribed chunks (three recognisers + clean-up, what is finally typed) never reached the subtitle, so subtitle and typed text disagreed.
// Now: a window is placed by a word alignment that tolerates a word heard differently; words two windows agree on are settled and never rewritten by a
// window; the clean-up works on settled words only and its answer replaces them; each transcribed chunk replaces its part of the subtitle; and the
// display takes out stutters, fillers and cut-off word starts the same way the final pass does.
public static class WordMatch
{
    static readonly HashSet<string> Fillers=new(StringComparer.Ordinal){"äh","ähh","ähm","ähmm","öh","öhm","hm","hmm","mhm","ehm","uh","uhm"};
    public static bool Filler(string key)=>Fillers.Contains(key);
    // 2 = the same word, 1 = close (an ending, a letter or two, a word cut at the window's edge), 0 = a different word.
    public static int Score(string a,string b)
    {
        if(a.Length==0||b.Length==0)return 0;
        if(a==b)return 2;
        int min=Math.Min(a.Length,b.Length);
        if(min>=3&&(a.StartsWith(b,StringComparison.Ordinal)||b.StartsWith(a,StringComparison.Ordinal)))return 1;
        if(min>=4&&(a.EndsWith(b,StringComparison.Ordinal)||b.EndsWith(a,StringComparison.Ordinal)))return 1;   // the window began inside the word
        if(min>=4&&Distance(a,b)<=Math.Max(1,min/4))return 1;
        return 0;
    }
    public static int Distance(string a,string b)
    {
        var prev=new int[b.Length+1];var cur=new int[b.Length+1];
        for(int j=0;j<=b.Length;j++)prev[j]=j;
        for(int i=1;i<=a.Length;i++)
        {
            cur[0]=i;
            for(int j=1;j<=b.Length;j++)cur[j]=Math.Min(Math.Min(cur[j-1]+1,prev[j]+1),prev[j-1]+(a[i-1]==b[j-1]?0:1));
            (prev,cur)=(cur,prev);
        }
        return prev[b.Length];
    }
}
// Where the words of b continue the words of a. The END of a must be covered (its last words are what b heard again); a's start is free (older words
// stand before b's audio). b may skip its first maxSkip words (cut at the edge of the audio), each at a small cost. Two words written as one ("Diktat
// Funktion" / "Diktatfunktion") count as a match.
public sealed record Alignment(int AStart,int BStart,int BEnd,int Matches,int Exact,double Score,int[] AtoB);
public static class WordAlign
{
    public static Alignment? Overlap(IReadOnlyList<string> a,IReadOnlyList<string> b,int maxSkip=2,double skipCost=0.5)
    {
        int n=a.Count,m=b.Count;if(n==0||m==0)return null;
        const double NEG=-1e9;
        var D=new double[n+1,m+1];var from=new byte[n+1,m+1];   // 0 start, 1 diag, 2 a deleted, 3 b inserted, 4 two a = one b, 5 one a = two b
        for(int i=0;i<=n;i++)for(int k=0;k<=m;k++){D[i,k]=k<=maxSkip?-skipCost*k:NEG;from[i,k]=0;}
        string Join(IReadOnlyList<string> l,int x)=>l[x]+l[x+1];
        for(int i=1;i<=n;i++)for(int k=1;k<=m;k++)
        {
            int s=WordMatch.Score(a[i-1],b[k-1]);
            void Try(double v,byte f){if(v>D[i,k]){D[i,k]=v;from[i,k]=f;}}
            Try(D[i-1,k-1]+(s==2?2:s==1?1:-1),1);
            Try(D[i-1,k]-1,2);
            Try(D[i,k-1]-1,3);
            if(i>=2&&a[i-2].Length>0&&Join(a,i-2)==b[k-1])Try(D[i-2,k-1]+2,4);
            if(k>=2&&b[k-2].Length>0&&a[i-1]==Join(b,k-2))Try(D[i-1,k-2]+2,5);
        }
        int best=-1;double bs=NEG;
        for(int k=1;k<=m;k++)if(D[n,k]>bs+1e-9){bs=D[n,k];best=k;}
        if(best<0)return null;
        var atob=Enumerable.Repeat(-1,n).ToArray();int matches=0,exact=0;
        int ii=n,kk=best,aStart,bStart;
        while(ii>0&&kk>0&&from[ii,kk]!=0)
        {
            switch(from[ii,kk])
            {
                case 1:{int s=WordMatch.Score(a[ii-1],b[kk-1]);if(s>0){atob[ii-1]=kk-1;matches++;if(s==2)exact++;}ii--;kk--;break;}
                case 2:ii--;break;
                case 3:kk--;break;
                case 4:atob[ii-2]=kk-1;atob[ii-1]=kk-1;matches++;ii-=2;kk--;break;
                default:atob[ii-1]=kk-2;matches++;ii--;kk-=2;break;
            }
        }
        aStart=ii;bStart=kk;   // a[..ii] stands before the aligned part, b[..kk] was skipped
        // a's words before the aligned part stand in front of b; b's skipped first words are dropped
        return new(aStart,bStart,best,matches,exact,bs,atob);
    }
    // Text b (a transcribed chunk) inside a: where in a does b's last word land? Both starts free, b's end must be covered. -1 when they do not match.
    public static int EndOf(IReadOnlyList<string> a,IReadOnlyList<string> b)
    {
        if(a.Count==0||b.Count==0)return -1;
        // swap roles: b's end must be covered; a's start and end are free
        var r=Overlap(b,a,a.Count,0);
        if(r is null||r.Matches<Math.Min(3,Math.Max(1,b.Count/2))||r.Matches*10<b.Count*3)return -1;
        int last=-1;for(int i=b.Count-1;i>=0&&last<0;i--)if(r.AtoB[i]>=0)last=r.AtoB[i]+(b.Count-1-i);
        return Math.Min(a.Count,last+1);
    }
}
// Stutters, fillers and cut-off word starts out of the live text, as the final pass does (dictationContext.js RULES 3; the gateway applies the same
// rules to the typed text: dictationGuard.js dropStutters — keep both in step). Doubled articles stay
// ("die Leute, die die Regeln kennen"): there the second one is a word of its own.
public static class Disfluency
{
    // a doubled article is a word of its own ("die Leute, die die Regeln kennen"); some words are doubled on purpose ("sehr, sehr", "nein, nein")
    static readonly HashSet<string> LegitDouble=new(StringComparer.Ordinal){"die","der","das","den","dem","des","sehr","ganz","viel","viele","nein","ja","bitte","klar","okay","immer","schnell","lange","weit","haha"};
    public static bool Legit(string key)=>LegitDouble.Contains(key);
    // protect: words before this index stay as they are (they came from a transcribed chunk: the typed text); a repeat across the border loses its second copy.
    public static List<string> Clean(IReadOnlyList<string> words,out int removed,int protect=0)
    {
        var w=new List<string>(words.Count);removed=0;
        for(int i=0;i<words.Count;i++){if(i>=protect&&WordMatch.Filler(StitchWords.Key(words[i]))){removed++;continue;}w.Add(words[i]);}
        protect=Math.Min(protect,w.Count);
        bool again=true;
        while(again)
        {
            again=false;
            for(int i=Math.Max(0,protect-3);i<w.Count&&!again;i++)
            {
                string k=StitchWords.Key(w[i]);if(k.Length==0)continue;
                // "Dik- Diktat": a word cut off with a dash that the next word completes
                if(i>=protect&&i+1<w.Count&&w[i].EndsWith('-')&&!w[i].EndsWith("--")&&k.Length>=2&&StitchWords.Key(w[i+1]).StartsWith(k,StringComparison.Ordinal)&&StitchWords.Key(w[i+1]).Length>k.Length)
                {w.RemoveAt(i);removed++;again=true;break;}
                // a restart: "Der Cap- der Caption-Text", "die dick die Diktatfunktion": a word, a cut-off start, the same word again and the whole word
                if(i>=protect&&i+3<w.Count&&StitchWords.Key(w[i+2])==k&&CutStart(StitchWords.Key(w[i+1]),StitchWords.Key(w[i+3]),w[i+1].EndsWith('-')))
                {
                    bool cap=char.IsUpper(w[i][0])&&char.IsLower(w[i+2][0]);
                    w.RemoveRange(i,2);removed+=2;if(cap)w[i]=char.ToUpperInvariant(w[i][0])+w[i][1..];
                    again=true;break;
                }
                for(int n=3;n>=1&&!again;n--)
                {
                    if(i+2*n>w.Count||i+2*n<=protect)continue;
                    bool same=true;
                    for(int j=0;j<n&&same;j++){string x=StitchWords.Key(w[i+j]),y=StitchWords.Key(w[i+n+j]);same=x.Length>0&&x==y&&!x.All(char.IsDigit);}   // figures may repeat ("11 11")
                    if(!same||n==1&&LegitDouble.Contains(k)&&!ArticleStutter(w,i))continue;
                    if(i<protect)
                    {
                        if(i+n<protect)continue;   // both copies belong to the typed text
                        w.RemoveRange(i+n,n);removed+=n;again=true;continue;
                    }
                    // the first copy goes; a capital it had at the start of a sentence moves to the copy that stays
                    bool cap=char.IsUpper(w[i][0])&&char.IsLower(w[i+n][0]);
                    w.RemoveRange(i,n);removed+=n;
                    if(cap)w[i]=char.ToUpperInvariant(w[i][0])+w[i][1..];
                    again=true;
                }
            }
        }
        return w;
    }
    // "dick" before "Diktatfunktion": the start of the word, cut off (a dash, or at most one letter off and at most 5 letters when there is no dash)
    static bool CutStart(string frag,string whole,bool dash)
    {
        if(frag.Length<2||whole.Length<=frag.Length+1)return false;
        if(whole.StartsWith(frag,StringComparison.Ordinal))return true;
        if(dash||frag.Length>5||frag.Length<3)return false;
        for(int len=frag.Length-1;len<=frag.Length+1;len++)if(WordMatch.Distance(frag,whole[..len])<=1)return true;   // "dick" / "Dik(tat)"
        return false;
    }
    // A doubled article is a stutter when nothing makes the second one a relative pronoun: no comma before the pair, no noun right before it
    // ("dass das das letzte Wort", "Ich habe die die Logs") against "die Leute, die die Regeln kennen" and "Frauen die die Kinder".
    static bool ArticleStutter(IReadOnlyList<string> w,int i)
    {
        if(w[i].EndsWith(','))return false;
        if(i==0)return true;
        string prev=w[i-1];
        if(prev.EndsWith(','))return false;
        bool sentenceStart=i==1||".!?:".Contains(w[i-2][^1]);
        return !(char.IsUpper(prev[0])&&!sentenceStart);
    }
    // Immediate repeats left in a text (the measure for the log and the evaluation): "ich ich", "wir haben wir haben"; doubled articles do not count.
    public static int Repeats(IReadOnlyList<string> words)
    {
        int count=0;var keys=words.Select(StitchWords.Key).Where(k=>k.Length>0&&!WordMatch.Filler(k)).ToArray();
        for(int i=0;i<keys.Length;i++)
            for(int n=1;n<=3;n++)
            {
                if(i+2*n>keys.Length)break;
                bool same=true;for(int j=0;j<n&&same;j++)same=keys[i+j]==keys[i+n+j];
                if(same&&!(n==1&&LegitDouble.Contains(keys[i]))){count++;i+=2*n-1;break;}
            }
        return count;
    }
}
// The live text of one dictation (or voice session): settled words that no window changes any more, then the words the latest window heard.
// Thread-unsafe; CaptionHost holds its lock around every call.
public sealed class LiveCaption(int maxWords=int.MaxValue)
{
    public sealed record PolishRequest(int From,int To,string[] Keys,string Text,string Before,int Version);
    readonly List<string> words=[];
    int version;              // grows with every replacement inside the settled words (a chunk): an answer for older positions is dropped
    double lastWindowEnd=-1;  // audio seconds where the previous window ended
    public int Committed {get;private set;}   // words[..Committed] are settled: two windows agreed, or no window covers them any more
    public int Settled {get;private set;}     // words[..Settled] came from the transcribed chunks (what is finally typed)
    public int PolishedTo {get;private set;}  // words[..PolishedTo] went through the clean-up already (or came from a chunk)
    public bool PolishBusy {get;private set;}
    public int Windows {get;private set;}
    public int Appended {get;private set;}    // windows that matched nothing and were added at the end
    public IReadOnlyList<string> Words=>words;
    public string Raw=>string.Join(' ',words);
    public void Reset(){words.Clear();Committed=Settled=PolishedTo=0;PolishBusy=false;version++;lastWindowEnd=-1;Windows=Appended=0;}
    // A window: its text and where its audio ends and how long it is (seconds of the recording). Overlap with the previous window decides whether it is placed or added.
    public void AddWindow(string text,double audioEnd=double.NaN,double seconds=double.NaN)
    {
        var w=StitchWords.Split(text);
        if(w.Length==0)return;
        Windows++;
        double overlap=double.IsNaN(audioEnd)||double.IsNaN(seconds)||lastWindowEnd<0?double.PositiveInfinity:lastWindowEnd-(audioEnd-seconds);
        if(!double.IsNaN(audioEnd))lastWindowEnd=audioEnd;
        if(words.Count==0){words.AddRange(w);return;}
        if(overlap<=0.3){Commit(words.Count);words.AddRange(w);Appended++;Trim();return;}   // a pause longer than a window: nothing of it was heard before
        // only the words the overlapping audio can hold are compared: about 3.5 words a second, a few more for safety (no match far back in the text)
        int tailLen=double.IsInfinity(overlap)?40:Math.Clamp((int)Math.Ceiling(overlap*3.5)+6,6,40);
        int baseAt=Math.Max(0,words.Count-tailLen);
        var tail=words.Skip(baseAt).Select(StitchWords.Key).ToList();var wk=w.Select(StitchWords.Key).ToList();
        var r=WordAlign.Overlap(tail,wk);
        if(r is null||r.Matches<(w.Length<=2?1:2)||r.Score<1)
        {
            Commit(words.Count);words.AddRange(w);Appended++;Trim();return;
        }
        // words in front of the window's audio are settled; the window cannot speak for them any more
        Commit(baseAt+r.AStart);
        // local agreement: from the settled point on, the words this window heard the same way are settled too (not its last two: still being spoken)
        int agree=Committed;
        for(int i=Math.Max(0,Committed-baseAt);i<tail.Count;i++)
        {
            int j=r.AtoB[i];
            if(j<0||j>=w.Length-2||tail[i]!=wk[j])break;
            agree=baseAt+i+1;
        }
        Commit(agree);
        // the window replaces what is not settled: from the first unsettled word on, its own words
        int keep=Committed,wi;
        if(keep<=baseAt+r.AStart)wi=r.BStart;
        else
        {
            wi=r.BStart;
            for(int i=keep-1-baseAt;i>=0;i--)if(r.AtoB[i]>=0){wi=r.AtoB[i]+1;break;}
        }
        // the window's first two words sit at the edge of its audio: where they differ from what stands there, the word that stands stays ("chte" / "möchte")
        for(int i=Math.Max(0,keep-baseAt);i<tail.Count&&wi<Math.Min(2,w.Length);i++)
        {
            if(r.AtoB[i]!=wi)break;
            if(tail[i]==wk[wi])break;
            keep=baseAt+i+1;wi++;
        }
        words.RemoveRange(keep,words.Count-keep);
        words.AddRange(w.Skip(Math.Min(wi,w.Length)));
        Trim();
    }
    void Commit(int to){if(to>Committed)Committed=Math.Min(to,words.Count);}
    void Trim()
    {
        if(words.Count<=maxWords)return;
        int drop=words.Count-maxWords/2;   // an hour-long voice session: the subtitle only needs the end
        words.RemoveRange(0,drop);Committed=Math.Max(0,Committed-drop);Settled=Math.Max(0,Settled-drop);PolishedTo=Math.Max(0,PolishedTo-drop);version++;
    }
    // The transcribed chunks so far (joined the way the final text is joined): they replace the live words they cover.
    // Returns 1 when their end was found in the live text, 2 when they replaced all of it (no match), 0 for nothing to do.
    public int Settle(string joined)
    {
        var s=StitchWords.Split(joined);
        if(s.Length==0)return 0;
        var keys=words.Select(StitchWords.Key).ToList();
        // where does the chunk text end in the live text? its last words against the live words after the part settled before
        int from=Math.Max(0,Settled-5);
        var suffix=s.Skip(Math.Max(0,s.Length-25)).Select(StitchWords.Key).ToList();
        int end=WordAlign.EndOf(keys.Skip(from).ToList(),suffix);
        int how=end<0?2:1;
        end=end<0?words.Count:from+end;   // no match: the chunk text takes everything; the next window adds what follows
        int tailCommitted=Math.Max(0,Committed-end);
        var rest=words.Skip(end).ToList();
        words.Clear();words.AddRange(s);words.AddRange(rest);
        Settled=s.Length;Committed=Math.Max(Settled,Settled+tailCommitted);PolishedTo=Math.Max(Settled,Math.Min(PolishedTo-end+Settled,Committed));
        if(PolishedTo<Settled)PolishedTo=Settled;
        version++;
        return how;
    }
    // The next clean-up request when at least minNew settled words are new (any with force): the last maxWordsPer settled words, also those cleaned before,
    // so the model sees a stutter or a name with enough words around it (measured 14 Nov: pieces of 3 to 6 words came back unchanged 156 of 169 times),
    // and the words before them as context. Words from the chunks are never sent. Null: nothing to do.
    public PolishRequest? NextPolish(int minNew=3,int maxWordsPer=40,bool force=false)
    {
        if(PolishBusy)return null;
        int to=Committed,fresh=Math.Max(PolishedTo,Settled);
        if(to-fresh<(force?1:minNew))return null;
        int from=Math.Max(Settled,to-maxWordsPer);
        var slice=words.Skip(from).Take(to-from).ToArray();
        string before=string.Join(' ',words.Skip(Math.Max(0,from-40)).Take(from-Math.Max(0,from-40)));
        if(before.Length>600)before=before[^600..];
        PolishBusy=true;
        return new(from,to,slice.Select(StitchWords.Key).ToArray(),string.Join(' ',slice),before,version);
    }
    // The answer: replaces exactly the words it was asked for, if they are still there. null = failed (they stay as heard; the next request moves on).
    public bool AnswerPolish(PolishRequest r,string? polished)
    {
        PolishBusy=false;
        bool still=r.Version==version&&r.To<=words.Count&&Enumerable.Range(0,r.Keys.Length).All(i=>StitchWords.Key(words[r.From+i])==r.Keys[i]);
        if(!still)return false;
        PolishedTo=Math.Max(PolishedTo,r.To);
        if(string.IsNullOrWhiteSpace(polished)||!Accept(r.Text,polished))return false;
        var p=StitchWords.Split(polished.Trim());
        int delta=p.Length-(r.To-r.From);
        words.RemoveRange(r.From,r.To-r.From);words.InsertRange(r.From,p);
        Committed+=delta;PolishedTo+=delta;
        return true;
    }
    // A short piece may lose a stutter or a filler and still be his words: the clean-up's 75 % rule with one and a half words of slack.
    public static bool Accept(string before,string after)
    {
        int a=DictationPolish.Words(before),b=DictationPolish.Words(after);
        return b>0&&b>=0.75*a-1.5&&b<=1.3*a+4;
    }
    // What the subtitle shows: the whole text without stutters and fillers (the overlay cuts it to its last characters).
    public List<string> Display()=>Disfluency.Clean(words,out _,Settled);   // the chunks' words exactly as they will be typed
    public string Last(int maxChars)=>Tail(string.Join(' ',Display()),maxChars);
    public static string Tail(string t,int maxChars)
    {
        if(t.Length<=maxChars)return t;
        int cut=t.IndexOf(' ',t.Length-maxChars);return cut<0?t[^maxChars..]:t[(cut+1)..];
    }
}
// What the subtitle showed, for the refinement rounds (Florian, 14 Nov: "falls die Protokolle noch nicht detailliert genug solche Sachen protokollieren").
// Every change of the shown text as "<ms> <cause><kept words> <new words...>" (cause: w window, p clean-up, c chunk), and at the end the measures:
// repeats on screen, words that were shown and changed later (and how late), live text against the typed text.
public sealed class CaptionLog
{
    public sealed record Item(long Ms,char Cause,int Keep,string Words);
    readonly List<string> shown=[];readonly List<long> since=[];
    readonly List<Item> pending=[],all=[];
    public IReadOnlyList<Item> All=>all;
    public int Updates,Changed,ChangedLate,RepeatsShown,PolishAsked,PolishUsed,PolishDropped,PolishFailed,Chunks,ChunksPlaced;
    public readonly Dictionary<char,int> ChangedBy=new(){['w']=0,['p']=0,['c']=0};
    readonly List<long> correctionAges=[];
    public long FirstShownMs=-1;
    public IReadOnlyList<string> Shown=>shown;
    // The shown text changed (whole cleaned text, not only the overlay's last characters). Returns the item to log, or null when nothing changed.
    public Item? Update(long ms,char cause,IReadOnlyList<string> now,int lateMs=3000)
    {
        int keep=0;
        while(keep<shown.Count&&keep<now.Count&&shown[keep]==now[keep])keep++;
        if(keep==shown.Count&&keep==now.Count)return null;
        if(FirstShownMs<0)FirstShownMs=ms;
        // the old words from `keep` on: which of them are gone or different now (word alignment of the two ends)
        var oldEnd=shown.Skip(keep).Select(StitchWords.Key).ToList();var newEnd=now.Skip(keep).Select(StitchWords.Key).ToList();
        var ages=since.Skip(keep).ToList();
        var stay=Lcs(oldEnd,newEnd);
        var newSince=new long[newEnd.Count];for(int j=0;j<newSince.Length;j++)newSince[j]=ms;
        int gone=0;
        for(int i=0;i<oldEnd.Count;i++)
        {
            if(stay[i]>=0){newSince[stay[i]]=ages[i];continue;}
            gone++;long age=ms-ages[i];
            if(age>=lateMs)ChangedLate++;
            if(cause!='w')correctionAges.Add(age);
        }
        // a word still being spoken changes all the time: only words that were already followed by another word count as changed
        int counted=Math.Max(0,gone-(cause=='w'&&oldEnd.Count>0&&stay[^1]<0?1:0));
        Changed+=counted;ChangedBy[cause]+=counted;Updates++;
        shown.RemoveRange(keep,shown.Count-keep);shown.AddRange(now.Skip(keep));
        since.RemoveRange(keep,since.Count-keep);since.AddRange(newSince);
        if(Disfluency.Repeats(now.Skip(Math.Max(0,keep-3)).ToList())>0)RepeatsShown++;
        var item=new Item(ms,cause,keep,string.Join(' ',now.Skip(keep)));
        pending.Add(item);if(all.Count<5000)all.Add(item);
        return item;
    }
    // Items not yet sent, as compact strings of at most maxChars each; at most maxItems or maxTotal characters per batch.
    public List<string>? Take(int maxItems=40,int maxTotal=6000,int maxChars=990)
    {
        if(pending.Count==0)return null;
        var outp=new List<string>();int total=0,n=0;
        foreach(var it in pending)
        {
            var s=$"{it.Ms} {it.Cause}{it.Keep} {it.Words}";
            if(s.Length>maxChars)s=s[..maxChars];
            if(n>0&&(n>=maxItems||total+s.Length>maxTotal))break;
            outp.Add(s);total+=s.Length;n++;
        }
        pending.RemoveRange(0,n);
        return outp;
    }
    public int Pending=>pending.Count;
    public long MedianCorrectionAgeMs=>correctionAges.Count==0?0:correctionAges.OrderBy(x=>x).ElementAt(correctionAges.Count/2);
    // The live text against the text that was typed: words only one of them has (after taking out fillers and punctuation), and a few examples.
    public static (int Missing,int Extra,int FinalWords,List<string> Examples) Compare(string live,string final,int maxExamples=8)
    {
        var a=StitchWords.Split(live).Where(w=>StitchWords.Key(w).Length>0).ToList();var b=StitchWords.Split(final).Where(w=>StitchWords.Key(w).Length>0).ToList();
        var ak=a.Select(StitchWords.Key).ToList();var bk=b.Select(StitchWords.Key).ToList();
        var map=Lcs(ak,bk);
        int missing=map.Count(x=>x<0),extra=bk.Count-map.Count(x=>x>=0);
        var examples=new List<string>();
        int pa=-1,pb=-1;   // the last word both have
        for(int i=0;i<=ak.Count&&examples.Count<maxExamples;i++)
        {
            if(i<ak.Count&&map[i]<0)continue;
            int nb=i<ak.Count?map[i]:bk.Count;
            if(i-pa>1||nb-pb>1)
            {
                string ctx(List<string> l,int at)=>at>=0&&at<l.Count?l[at]:"";
                string l=string.Join(' ',a.Skip(pa+1).Take(i-pa-1)),f=string.Join(' ',b.Skip(pb+1).Take(nb-pb-1));
                examples.Add($"{ctx(a,pa)} [{l}] {ctx(a,i)} | typed: [{f}]".Trim());
            }
            pa=i;pb=nb;
        }
        return (missing,extra,bk.Count,examples);
    }
    // For each word of a: the index of the same word in b on a longest common subsequence, or -1.
    public static int[] Lcs(IReadOnlyList<string> a,IReadOnlyList<string> b)
    {
        int n=a.Count,m=b.Count;var L=new int[n+1,m+1];
        for(int i=n-1;i>=0;i--)for(int j=m-1;j>=0;j--)L[i,j]=a[i].Length>0&&a[i]==b[j]?L[i+1,j+1]+1:Math.Max(L[i+1,j],L[i,j+1]);
        var map=Enumerable.Repeat(-1,n).ToArray();int x=0,y=0;
        while(x<n&&y<m){if(a[x].Length>0&&a[x]==b[y]){map[x]=y;x++;y++;}else if(L[x+1,y]>=L[x,y+1])x++;else y++;}
        return map;
    }
}

// v0.1.16 (off by default, config DictationRefinementKeep): the audio of a dictation with what its subtitle showed, kept for a refinement round in
// %APPDATA%\ScreenAgent\refinement\<stamp>-<id>\ (audio.wav parts + caption.json), 14 days, at most 1 GB together (oldest first).
public sealed class RefinementStore(string folder,TimeSpan? keep=null,long capBytes=1L<<30)
{
    public string Folder=>folder;
    public string Save(DictationMeta m,IReadOnlyList<(long Ms,double AudioEnd,string Text)> windows,IReadOnlyList<CaptionLog.Item> shown,string typed,Dictionary<string,object?> summary,DateTimeOffset now)
    {
        string dir=Path.Combine(folder,$"{m.Stamp}-{m.Id}");
        Directory.CreateDirectory(dir);
        int n=0;foreach(var p in m.Paths.Where(File.Exists))File.Copy(p,Path.Combine(dir,n++==0?"audio.wav":$"audio-{n}.wav"),true);
        var doc=new Dictionary<string,object?>
        {
            ["id"]=m.Id,["started"]=m.Started,["seconds"]=m.Seconds,["process"]=m.Process,["typed"]=typed,
            ["windows"]=windows.Select(w=>new{ms=w.Ms,audioEnd=Math.Round(w.AudioEnd,2),text=w.Text}).ToArray(),
            ["shown"]=shown.Select(i=>new{ms=i.Ms,cause=i.Cause.ToString(),keep=i.Keep,words=i.Words}).ToArray(),
            ["summary"]=summary
        };
        File.WriteAllText(Path.Combine(dir,"caption.json"),System.Text.Json.JsonSerializer.Serialize(doc,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        Sweep(now);
        return dir;
    }
    public int Sweep(DateTimeOffset now)
    {
        if(!Directory.Exists(folder))return 0;
        var dirs=new DirectoryInfo(folder).GetDirectories().Select(x=>(Dir:x,At:x.CreationTimeUtc,Bytes:x.EnumerateFiles("*",SearchOption.AllDirectories).Sum(f=>f.Length))).OrderBy(x=>x.At).ToList();
        int gone=0;long total=dirs.Sum(x=>x.Bytes);var limit=now.UtcDateTime-(keep??TimeSpan.FromDays(14));
        foreach(var x in dirs)
        {
            if(x.At>=limit&&total<=capBytes)continue;
            try{x.Dir.Delete(true);total-=x.Bytes;gone++;}catch(IOException){ /* in use: the next sweep */ }catch(UnauthorizedAccessException){ /* the next sweep */ }
        }
        return gone;
    }
}
