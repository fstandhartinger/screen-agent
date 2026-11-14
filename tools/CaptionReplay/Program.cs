using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScreenAgent.Core;
namespace CaptionReplay;
// Replays a dictation's live subtitle offline: the caption windows (raw recogniser text with the time each answer arrived), the transcribed chunks and the
// live clean-up, on a simulated clock, through the subtitle logic of v0.1.15 ("old") and of v0.1.16 ("new", "new-nosettle" without the chunk step).
// Input: an evaluation folder with cases.json, windows/<id>.json, final/<id>.json (eval/README.md of job screen-agent-caption-refinement-20261114), or the
// same files written by gateway/scripts/caption-report.mjs from the Desk's device log. The clean-up answers come from a polish server (eval/polish-server.mjs:
// the real gateway code, cached); without --polish the clean-up is left out.
//   dotnet run --project tools/CaptionReplay -- <evalDir> [--polish http://127.0.0.1:8791] [--cases syn-01,syn-02] [--modes old,new] [--json out.json]
static class Program
{
    sealed record Window(double At,double From,double To,string Text,int Ms);
    sealed record ChunkText(double At,string Text);
    sealed record Case(string Id,double Seconds,string Reference,string Typed,List<Window> Windows,List<ChunkText> Chunks);
    static async Task<int> Main(string[] args)
    {
        if(args.Length==0){Console.Error.WriteLine("usage: CaptionReplay <evalDir> [--polish URL] [--cases a,b] [--modes old,new,new-nosettle] [--json out.json] [--show]");return 2;}
        string dir=args[0];string? polishUrl=Arg(args,"--polish"),json=Arg(args,"--json");
        var only=Arg(args,"--cases")?.Split(',').ToHashSet();var modes=(Arg(args,"--modes")??"old,new-nosettle,new").Split(',');bool show=args.Contains("--show");
        var cases=Load(dir).Where(c=>only is null||only.Contains(c.Id)).ToList();
        using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(30)};
        var all=new JsonArray();
        var totals=modes.ToDictionary(m=>m,_=>new Dictionary<string,double>());
        foreach(var c in cases)
        {
            foreach(var mode in modes)
            {
                var r=await Run(c,mode,polishUrl is null?null:(text,before,gw)=>Polish(http,polishUrl,text,before,gw));
                r["case"]=c.Id;r["mode"]=mode;all.Add(r);
                foreach(var (k,v) in r)if(v is JsonValue jv&&jv.GetValueKind()==JsonValueKind.Number){double d=double.Parse(jv.ToJsonString(),System.Globalization.CultureInfo.InvariantCulture);totals[mode].TryGetValue(k,out var t);totals[mode][k]=t+d;}
                if(show)Console.WriteLine($"--- {c.Id} {mode}\n  shown: {r["shown"]}\n  typed: {c.Typed}\n  ref:   {c.Reference}");
            }
        }
        // one table: per mode the sums over all cases (rates recomputed from the sums)
        string[] cols=["refWords","refErrors","typedErrors","repeatsEnd","repeatsSeen","changed","changedLate","polishAsked","polishUsed","polishDropped","appended","windows","audioSec"];
        Console.WriteLine("mode           "+string.Join(" ",cols.Select(x=>x.PadLeft(12))));
        foreach(var m in modes)Console.WriteLine(m.PadRight(15)+string.Join(" ",cols.Select(x=>(totals[m].TryGetValue(x,out var v)?v:0).ToString("0.#").PadLeft(12))));
        foreach(var m in modes)
        {
            var t=totals[m];double W(string k)=>t.TryGetValue(k,out var v)?v:0;
            Console.WriteLine($"{m}: errors against the reference {100*W("refErrors")/Math.Max(1,W("refWords")):0.0} %, against the typed text {100*W("typedErrors")/Math.Max(1,W("typedWords")):0.0} %, doubled words left {W("repeatsEnd")}, updates showing a doubled word {W("repeatsSeen")}, clean-up answers used {W("polishUsed")}/{W("polishAsked")}");
        }
        if(json is not null)File.WriteAllText(json,all.ToJsonString(new JsonSerializerOptions{WriteIndented=true}));
        return 0;
    }
    static string? Arg(string[] a,string name){int i=Array.IndexOf(a,name);return i>=0&&i+1<a.Length?a[i+1]:null;}
    static List<Case> Load(string dir)
    {
        var list=new List<Case>();
        foreach(var c in JsonNode.Parse(File.ReadAllText(Path.Combine(dir,"cases.json")))!.AsArray())
        {
            string id=c!["id"]!.GetValue<string>();
            var w=JsonNode.Parse(File.ReadAllText(Path.Combine(dir,"windows",id+".json")))!;
            var windows=w["windows"]!.AsArray().Select(x=>new Window(x!["at"]!.GetValue<double>(),x["from"]!.GetValue<double>(),x["to"]!.GetValue<double>(),x["text"]?.GetValue<string>()??"",x["ms"]?.GetValue<int>()??0)).OrderBy(x=>x.At).ToList();
            var chunks=new List<ChunkText>();string typed="";
            string fp=Path.Combine(dir,"final",id+".json");
            if(File.Exists(fp))
            {
                var f=JsonNode.Parse(File.ReadAllText(fp))!;
                chunks=f["chunks"]!.AsArray().Select(x=>new ChunkText(x!["at"]!.GetValue<double>(),x["text"]?.GetValue<string>()??"")).ToList();
                typed=f["typed"]?.GetValue<string>()??f["text"]?.GetValue<string>()??"";
            }
            list.Add(new(id,c["seconds"]?.GetValue<double>()??windows.LastOrDefault()?.To??0,c["reference"]?.GetValue<string>()??typed,typed,windows,chunks));
        }
        return list;
    }
    static readonly Dictionary<string,(string Text,int Ms)> polishCache=[];
    static async Task<(string? Text,int Ms)> Polish(HttpClient http,string url,string text,string before,string gateway)
    {
        string key=gateway+"\u0001"+before+"\u0001"+text;
        if(polishCache.TryGetValue(key,out var hit))return hit;
        var res=await http.PostAsJsonAsync(url,new{text,before,gateway});
        var j=JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        var v=(j["text"]?.GetValue<string>()??"",j["ms"]?.GetValue<int>()??0);
        if(!res.IsSuccessStatusCode)return (null,v.Item2);
        polishCache[key]=v;return v;
    }
    // A simulated run: events in time order (window answers, chunk results, clean-up answers). Dictation ends at the end of the audio; after that no clean-up
    // starts, windows still arrive for 4 s, chunks until the last one.
    static Task<JsonObject> Run(Case c,string mode,Func<string,string,string,Task<(string? Text,int Ms)>>? polish)
    {
        var q=new PriorityQueue<Action,double>();
        var log=new CaptionLog();
        bool isNew=mode.StartsWith("new"),settle=mode=="new";
        var live=new LiveCaption();var oldStitch=new CaptionStitcher(60,4000);var oldPolish=new OldLivePolish();
        IReadOnlyList<string> Shown()=>isNew?live.Display():StitchWords.Split(oldPolish.Display(oldStitch.Words,int.MaxValue));
        double end=c.Seconds;var chunkParts=new List<string>();int windows=0;double audio=0;
        double lastPolish=-10;
        void Update(double t,char cause)=>log.Update((long)(t*1000),cause,Shown());
        void AskPolish(double t,bool force)
        {
            if(polish is null||t>end)return;
            if(isNew)
            {
                var req=live.NextPolish(force:force);if(req is null)return;
                lastPolish=t;log.PolishAsked++;
                var (text,ms)=polish(req.Text,req.Before,"new").GetAwaiter().GetResult();
                q.Enqueue(()=>{bool used=live.AnswerPolish(req,text);if(used){log.PolishUsed++;Update(t+ms/1000.0,'p');}else if(text is null)log.PolishFailed++;else log.PolishDropped++;AskPolish(t+ms/1000.0,false);},t+ms/1000.0);
            }
            else
            {
                if(oldPolish.Busy)return;
                var req=oldPolish.Next(oldStitch.Words);if(req is null)return;
                log.PolishAsked++;
                var (text,ms)=polish(req.Text,"","old").GetAwaiter().GetResult();
                q.Enqueue(()=>{oldPolish.Answer(req,text);Update(t+ms/1000.0,'p');log.PolishUsed+=text is not null&&ShownHas(text)?1:0;if(text is null)log.PolishFailed++;},t+ms/1000.0);
            }
        }
        bool ShownHas(string text)=>string.Join(' ',Shown()).Contains(text.Trim(),StringComparison.Ordinal);
        foreach(var w in c.Windows)
        {
            if(w.At>end+4)continue;
            q.Enqueue(()=>
            {
                windows++;audio+=w.To-w.From;
                if(isNew)live.AddWindow(w.Text,w.To,w.To-w.From);else oldStitch.Add(w.Text);
                Update(w.At,'w');AskPolish(w.At,false);
            },w.At);
        }
        if(settle)foreach(var ch in c.Chunks)q.Enqueue(()=>{chunkParts.Add(ch.Text);int how=live.Settle(DictationJoin.Join(chunkParts,chunkParts.Select(_=>false).ToList()));if(how>0){log.Chunks++;if(how==1)log.ChunksPlaced++;}Update(ch.At,'c');},ch.At);
        // the 2 s beat of the clean-up (new only)
        if(isNew)for(double t=2;t<=end;t+=2)q.Enqueue(()=>{},t);
        double now=0;
        while(q.TryDequeue(out var act,out var t))
        {
            now=t;act();
            if(isNew&&polish is not null&&t<=end&&t-lastPolish>=2&&!live.PolishBusy)AskPolish(t,true);
        }
        var shown=Shown();string shownText=string.Join(' ',shown);
        var refKeys=Keys(c.Reference);var typedKeys=Keys(c.Typed);var shownKeys=Keys(shownText);
        int refRepeats=Disfluency.Repeats(StitchWords.Split(c.Reference));
        return Task.FromResult(new JsonObject
        {
            ["refWords"]=refKeys.Count,["refErrors"]=Edit(shownKeys,refKeys),["typedWords"]=typedKeys.Count,["typedErrors"]=typedKeys.Count==0?0:Edit(shownKeys,typedKeys),
            ["repeatsEnd"]=Math.Max(0,Disfluency.Repeats(shown)-refRepeats),["repeatsSeen"]=log.RepeatsShown,["changed"]=log.Changed,["changedLate"]=log.ChangedLate,
            ["changedWindow"]=log.ChangedBy['w'],["changedPolish"]=log.ChangedBy['p'],["changedChunk"]=log.ChangedBy['c'],["correctionAgeMs"]=log.MedianCorrectionAgeMs,
            ["polishAsked"]=log.PolishAsked,["polishUsed"]=log.PolishUsed,["polishDropped"]=log.PolishDropped,["polishFailed"]=log.PolishFailed,
            ["appended"]=isNew?live.Appended:0,["windows"]=windows,["audioSec"]=Math.Round(audio,1),["chunks"]=log.Chunks,["chunksPlaced"]=log.ChunksPlaced,
            ["shown"]=shownText
        });
    }
    static List<string> Keys(string t)=>StitchWords.Split(t).Select(StitchWords.Key).Where(k=>k.Length>0&&!WordMatch.Filler(k)).ToList();
    // word-level edit distance (substitutions, deletions, insertions)
    static int Edit(List<string> a,List<string> b)
    {
        var prev=new int[b.Count+1];var cur=new int[b.Count+1];
        for(int j=0;j<=b.Count;j++)prev[j]=j;
        for(int i=1;i<=a.Count;i++){cur[0]=i;for(int j=1;j<=b.Count;j++)cur[j]=Math.Min(Math.Min(cur[j-1]+1,prev[j]+1),prev[j-1]+(a[i-1]==b[j-1]?0:1));(prev,cur)=(cur,prev);}
        return prev[b.Count];
    }
}
