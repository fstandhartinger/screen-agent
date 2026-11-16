using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScreenAgent.Core;
namespace CaptionReplay;
// Replays a recording's live subtitle offline on a simulated clock through the Core logic of the app (LiveCaption, RevisePolicy, DictationJoin) and writes the
// SNAPSHOT LOG: every change of what the overlay shows ({t, confirmed, tentative}) plus every paid call ({t, kind, ms, usd}). The numbers (errors against a
// reference, stray punctuation, latency, cost per hour) are made by harness/score.mjs from that log; this tool only replays.
// Input: an evaluation folder (job screen-agent-live-caption-llm-correction-20261116, harness/):
//   cases.json                  [{id, seconds, wav (relative to the folder), ...}]
//   windows/<id>.json           the app's MAI windows with their real latency and cost (simulate-windows.mjs)
//   final-main/<id>.json, final-new/<id>.json   the chunks (at, ms, text, overlap, cost) of the production gateway / of the new gateway (final-text.mjs)
// Servers (harness/polish-server.mjs, harness/revise-server.mjs): the real gateway code of a checkout, cached, with the real latency of the first call.
//   CaptionReplay <evalDir> --modes v116,revise [--polish http://127.0.0.1:8791] [--revise http://127.0.0.1:8792] [--cases a,b] [--out runs]
// Modes: v116 (windows + live clean-up + chunks: the app today), v116-nochunk, revise (windows + re-read of the last seconds, sources "mai", + chunks), revise2 (sources
// "mai,openai"), revise-nochunk / revise2-nochunk (no chunk step: a voice session has none), revise-max25[-nochunk] (what-if: maxSeconds is a constant 25 instead of min(25, audio so far)),
// old (v0.1.15), new / new-nosettle (v0.1.16 = v116 / v116-nochunk).
static class Program
{
    sealed record Window(double At,double From,double To,string Text,int Ms,double Cost);
    sealed record ChunkText(double To,int Ms,string Text,bool Overlap,double Cost);
    sealed record Case(string Id,double Seconds,string Wav,List<Window> Windows,Dictionary<string,List<ChunkText>> Chunks,Dictionary<string,double> FinalCost);
    sealed record Mode(string Name,bool OldStitch,string Live,string Sources,string? Chunks,bool Max25=false)
    {
        public bool Revise=>Live=="revise";
        public bool Polish=>Live=="polish";
    }
    static Mode ParseMode(string m)
    {
        switch(m)
        {
            case "old":return new(m,true,"polish","",null);
            case "new":case "v116":return new(m,false,"polish","","main");
            case "new-nosettle":case "v116-nochunk":return new(m,false,"polish","",null);
        }
        if(m.StartsWith("revise"))
        {
            bool two=m.StartsWith("revise2"),nochunk=m.Contains("-nochunk"),max25=m.Contains("-max25");
            return new(m,false,"revise",two?"mai,openai":"mai",nochunk?null:"new",max25);
        }
        throw new ArgumentException("unknown mode "+m);
    }
    static async Task<int> Main(string[] args)
    {
        if(args.Length==0){Console.Error.WriteLine("usage: CaptionReplay <evalDir> [--modes v116,revise,...] [--polish URL] [--revise URL] [--cases a,b] [--out dir]");return 2;}
        string dir=args[0];string? polishUrl=Arg(args,"--polish"),reviseUrl=Arg(args,"--revise"),outDir=Arg(args,"--out");
        var only=Arg(args,"--cases")?.Split(',').ToHashSet();var modes=(Arg(args,"--modes")??"v116").Split(',').Select(ParseMode).ToList();
        var cases=Load(dir).Where(c=>only is null||only.Contains(c.Id)).ToList();
        using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(90)};
        if(outDir is not null)Directory.CreateDirectory(Path.Combine(dir,outDir));
        foreach(var mode in modes)
        {
            var all=new JsonArray();var sum=new Dictionary<string,double>();
            foreach(var c in cases)
            {
                var r=Run(dir,c,mode,polishUrl is null?null:(text,before,gw)=>Post(http,polishUrl,new{text,before,gateway=gw}),
                    reviseUrl is null?null:(cid,from,to,shown,before,sources)=>Post(http,reviseUrl,new{@case=cid,from,to,shown,before,open=true,sources}));
                all.Add(r);
                foreach(var (k,v) in r["counts"]!.AsObject())if(v is JsonValue jv&&jv.GetValueKind()==JsonValueKind.Number){sum.TryGetValue(k,out var t);sum[k]=t+double.Parse(jv.ToJsonString(),CultureInfo.InvariantCulture);}
                foreach(var (k,v) in r["costs"]!.AsObject())if(v is JsonValue jv){sum.TryGetValue("usd_"+k,out var t);sum["usd_"+k]=t+double.Parse(jv.ToJsonString(),CultureInfo.InvariantCulture);}
                sum.TryGetValue("speechSec",out var s0);sum["speechSec"]=s0+double.Parse(r["speechSec"]!.ToJsonString(),CultureInfo.InvariantCulture);
            }
            Console.WriteLine($"{mode.Name,-16} "+string.Join(" ",sum.OrderBy(x=>x.Key).Select(x=>x.Key+"="+x.Value.ToString("0.####",CultureInfo.InvariantCulture))));
            if(outDir is not null)File.WriteAllText(Path.Combine(dir,outDir,mode.Name+".json"),all.ToJsonString(new JsonSerializerOptions{WriteIndented=false}));
        }
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
            var windows=w["windows"]!.AsArray().Select(x=>new Window(x!["at"]!.GetValue<double>(),x["from"]!.GetValue<double>(),x["to"]!.GetValue<double>(),x["text"]?.GetValue<string>()??"",x["ms"]?.GetValue<int>()??0,x["cost"]?.GetValue<double>()??0)).OrderBy(x=>x.At).ToList();
            var chunks=new Dictionary<string,List<ChunkText>>();var finalCost=new Dictionary<string,double>();
            foreach(var gw in new[]{"main","new"})
            {
                string fp=Path.Combine(dir,"final-"+gw,id+".json");
                if(!File.Exists(fp))continue;
                var f=JsonNode.Parse(File.ReadAllText(fp))!;
                chunks[gw]=f["chunks"]!.AsArray().Select(x=>new ChunkText(x!["to"]!.GetValue<double>(),x["ms"]?.GetValue<int>()??0,x["text"]?.GetValue<string>()??"",x["overlap"]?.GetValue<bool>()??false,x["cost"]?.GetValue<double>()??0)).ToList();
                finalCost[gw]=f["polish"]?["cost"]?.GetValue<double>()??0;
            }
            list.Add(new(id,c["seconds"]!.GetValue<double>(),c["wav"]!.GetValue<string>(),windows,chunks,finalCost));
        }
        return list;
    }
    // ---- the speech gate of the app (CaptionPolicy.Heard: RMS >= 400 per audio block, speech stays "recent" for 2 s)
    static List<double> SpeechBlockEnds(string wavPath)
    {
        var b=File.ReadAllBytes(wavPath);int pos=12,dataAt=-1,dataLen=0;
        while(pos+8<=b.Length){int n=BitConverter.ToInt32(b,pos+4);if(b[pos]=='d'&&b[pos+1]=='a'&&b[pos+2]=='t'&&b[pos+3]=='a'){dataAt=pos+8;dataLen=Math.Min(n,b.Length-dataAt);break;}pos+=8+n+(n&1);}
        var ends=new List<double>();
        if(dataAt<0)return ends;
        for(int s=0;s+640<=dataLen;s+=640)
        {
            double sq=0;for(int p=s;p<s+640;p+=2){int v=BitConverter.ToInt16(b,dataAt+p);sq+=(double)v*v;}
            if(Math.Sqrt(sq/320)>=400)ends.Add((s+640)/32000.0);
        }
        return ends;
    }
    static bool Speaking(List<double> ends,double t)
    {
        int lo=0,hi=ends.Count;while(lo<hi){int m=(lo+hi)/2;if(ends[m]<=t+1e-9)lo=m+1;else hi=m;}
        return lo>0&&t-ends[lo-1]<=2+1e-9;
    }
    static readonly Dictionary<string,JsonNode> postCache=[];
    sealed record Reply(bool Ok,string? Text,int Ms,double Usd,double Cost,string? Code,JsonNode? Raw);
    static Reply Post(HttpClient http,string url,object body)
    {
        string payload=JsonSerializer.Serialize(body);
        if(!postCache.TryGetValue(url+payload,out var j))
        {
            using var res=http.PostAsJsonAsync(url,body).GetAwaiter().GetResult();
            j=JsonNode.Parse(res.Content.ReadAsStringAsync().GetAwaiter().GetResult())??new JsonObject();
            j["_ok"]=res.IsSuccessStatusCode;postCache[url+payload]=j;
        }
        bool ok=j["_ok"]!.GetValue<bool>();
        double usd=j["actualUsd"]?.GetValue<double>()??j["cost"]?.GetValue<double>()??0;
        return new(ok,ok?j["text"]?.GetValue<string>()??"":null,j["ms"]?.GetValue<int>()??0,usd,j["cost"]?.GetValue<double>()??0,j["code"]?.GetValue<string>(),j);
    }
    static JsonObject Run(string dir,Case c,Mode mode,Func<string,string,string,Reply>? polish,Func<string,double,double,string,string,string,Reply>? revise)
    {
        var q=new PriorityQueue<Action,(double,int)>();int seq=0;void At(double t,Action a)=>q.Enqueue(a,(t,seq++));
        var log=new CaptionLog();var snaps=new JsonArray();var calls=new JsonArray();string lastConf="\u0002",lastTent="\u0002";
        var live=new LiveCaption();var oldStitch=new CaptionStitcher(60,4000);var oldPolish=new OldLivePolish();var rp=new RevisePolicy();
        var ends=mode.Revise?SpeechBlockEnds(Path.Combine(dir,c.Wav)):[];
        double end=c.Seconds;var chunkParts=new List<string>();var chunkOverlap=new List<bool>();
        int windows=0,reviseAsked=0,reviseUsed=0,reviseFailed=0,reviseLate=0,reviseNoChange=0,polishUsedLate=0;double audio=0,lastPolish=-10;
        var costs=new Dictionary<string,double>{["caption"]=0,["polish"]=0,["revise"]=0,["chunk"]=0,["final"]=0};
        void Call(double t,string kind,int ms,double usd,bool ok)
        {
            costs[kind]+=usd;calls.Add(new JsonObject{["t"]=Math.Round(t,3),["kind"]=kind,["ms"]=ms,["usd"]=usd,["ok"]=ok});
        }
        IReadOnlyList<string> ShownWords(out string conf,out string tent)
        {
            if(mode.Revise)
            {
                var (a,b)=live.Shown(1_000_000);conf=a;tent=b;
                return StitchWords.Split(a+" "+b);
            }
            IReadOnlyList<string> w=mode.OldStitch?StitchWords.Split(oldPolish.Display(oldStitch.Words,int.MaxValue)):live.Display();
            conf=string.Join(' ',w);tent="";return w;
        }
        void Update(double t,char cause)
        {
            var words=ShownWords(out var conf,out var tent);
            log.Update((long)(t*1000),cause,words);
            if(conf==lastConf&&tent==lastTent)return;
            lastConf=conf;lastTent=tent;
            snaps.Add(new JsonObject{["t"]=Math.Round(t,3),["cause"]=cause.ToString(),["confirmed"]=conf,["tentative"]=tent});
        }
        void AskPolish(double t,bool force)
        {
            if(polish is null||t>end||!mode.Polish)return;
            if(!mode.OldStitch)
            {
                var req=live.NextPolish(force:force);if(req is null)return;
                lastPolish=t;log.PolishAsked++;
                var r=polish(req.Text,req.Before,"main");double at=t+r.Ms/1000.0;
                Call(t,"polish",r.Ms,r.Usd,r.Ok);
                At(at,()=>{bool used=live.AnswerPolish(req,r.Ok?r.Text:null);if(used){log.PolishUsed++;Update(at,'p');}else if(!r.Ok)log.PolishFailed++;else log.PolishDropped++;AskPolish(at,false);});
            }
            else
            {
                if(oldPolish.Busy)return;
                var req=oldPolish.Next(oldStitch.Words);if(req is null)return;
                log.PolishAsked++;
                var r=polish(req.Text,"","main");double at=t+r.Ms/1000.0;
                Call(t,"polish",r.Ms,r.Usd,r.Ok);
                At(at,()=>{oldPolish.Answer(req,r.Ok?r.Text:null);Update(at,'p');log.PolishUsed+=r.Ok&&string.Join(' ',ShownWords(out _,out _)).Contains((r.Text??"").Trim(),StringComparison.Ordinal)?1:0;if(!r.Ok)log.PolishFailed++;});
            }
        }
        // the windows (their answers arrive at w.At)
        foreach(var w in c.Windows)
        {
            Call(w.At-w.Ms/1000.0,"caption",w.Ms,w.Cost,true);
            if(w.At>end+4)continue;
            At(w.At,()=>
            {
                windows++;audio+=w.To-w.From;
                if(!mode.OldStitch)live.AddWindow(w.Text,w.To,w.To-w.From);else oldStitch.Add(w.Text);
                Update(w.At,'w');AskPolish(w.At,false);
            });
        }
        // the transcribed chunks: sent one at a time in order (DictationJob), the text of all chunks so far replaces its part of the subtitle
        if(mode.Chunks is not null&&c.Chunks.TryGetValue(mode.Chunks,out var chunks))
        {
            double free=0;
            foreach(var ch in chunks)
            {
                double start=Math.Max(ch.To,free),at=start+ch.Ms/1000.0;free=at;var chc=ch;
                Call(start,"chunk",ch.Ms,ch.Cost,true);
                At(at,()=>{chunkParts.Add(chc.Text);chunkOverlap.Add(chc.Overlap);int how=live.Settle(DictationJoin.Join(chunkParts,chunkOverlap));if(how>0){log.Chunks++;if(how==1)log.ChunksPlaced++;}Update(at,'c');});
            }
            if(c.FinalCost.TryGetValue(mode.Chunks,out var fc)&&fc>0)costs["final"]+=fc;
        }
        // the 3 s beat of the live clean-up (checked every second like the app's timer)
        if(mode.Polish&&!mode.OldStitch)for(double t=1;t<=end;t+=1)At(t,()=>{});
        // the re-read: the app's timer ticks every 250 ms while the dictation is active; RevisePolicy decides (the same class the app uses)
        if(mode.Revise&&revise is not null)
            for(int k=1;k*0.25<=end+1e-9;k++)
            {
                double t=k*0.25;
                At(t,()=>
                {
                    long now=(long)Math.Round(t*1000);bool speaking=Speaking(ends,t);
                    var req=rp.Next(live,now,t,speaking,mode.Max25?25:Math.Min(25,t));   // the app's host passes min(25, audio so far); -max25 = what-if with a constant 25
                    if(req is null)return;
                    reviseAsked++;var r=revise(c.Id,req.FromAudio,req.ToAudio,req.Shown,req.Before,mode.Sources);
                    bool late=r.Ms>6000;double at=t+Math.Min(r.Ms,6000)/1000.0;
                    Call(t,"revise",r.Ms,r.Usd,r.Ok&&!late);
                    At(at,()=>
                    {
                        bool ok=r.Ok&&!late;rp.Answered(ok,(long)Math.Round(at*1000),r.Code);
                        if(late)reviseLate++;else if(!r.Ok)reviseFailed++;
                        bool used=live.AnswerRevise(req,ok?r.Text:null,Math.Min(at,end));
                        if(used){reviseUsed++;Update(at,'r');}else if(ok)reviseNoChange++;
                    });
                });
            }
        double now0=0;
        while(q.TryDequeue(out var act,out var tt))
        {
            now0=tt.Item1;act();
            // the 3 s beat of the live clean-up: after any event (the app checks every second)
            if(mode.Polish&&!mode.OldStitch&&polish is not null&&now0<=end&&now0-lastPolish>=3&&!live.PolishBusy)AskPolish(now0,true);
        }
        var shown=ShownWords(out var finalConf,out var finalTent);
        // speech seconds (RMS gate): for the cost per hour of speech
        var sp=mode.Revise?ends:SpeechBlockEnds(Path.Combine(dir,c.Wav));
        double speechSec=Math.Round(sp.Count*0.02,2);
        var counts=new JsonObject
        {
            ["windows"]=windows,["audioSec"]=Math.Round(audio,1),["changed"]=log.Changed,["changedLate"]=log.ChangedLate,["changedWindow"]=log.ChangedBy['w'],["changedPolish"]=log.ChangedBy['p'],["changedChunk"]=log.ChangedBy['c'],["changedRevise"]=log.ChangedBy['r'],
            ["correctionAgeMs"]=log.MedianCorrectionAgeMs,["polishAsked"]=log.PolishAsked,["polishUsed"]=log.PolishUsed,["polishDropped"]=log.PolishDropped,["polishFailed"]=log.PolishFailed,
            ["reviseAsked"]=reviseAsked,["reviseUsed"]=reviseUsed,["reviseNoChange"]=reviseNoChange,["reviseFailed"]=reviseFailed,["reviseLate"]=reviseLate,["chunks"]=log.Chunks,["chunksPlaced"]=log.ChunksPlaced,
            ["repeatsSeen"]=log.RepeatsShown,["confirmed"]=live.Confirmed,["words"]=shown.Count
        };
        var costObj=new JsonObject();foreach(var (k,v) in costs)costObj[k]=Math.Round(v,6);
        return new JsonObject{["case"]=c.Id,["mode"]=mode.Name,["seconds"]=c.Seconds,["speechSec"]=speechSec,["lastEvent"]=Math.Round(now0,3),["counts"]=counts,["costs"]=costObj,["snapshots"]=snaps,["calls"]=calls,["final"]=new JsonObject{["confirmed"]=finalConf,["tentative"]=finalTent}};
    }
}
