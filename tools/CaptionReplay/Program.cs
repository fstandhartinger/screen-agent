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
// Modes: v116 (windows + live clean-up + chunks: the app today; Settle without audio end, as v0.1.16), v116-nochunk, old (v0.1.15), new / new-nosettle (v0.1.16 = v116 / v116-nochunk).
// Re-read modes (v0.1.19 seam logic: PauseTracker fed with every 20 ms block, closed re-reads up to a pause, open re-reads from the anchor, maxSeconds 25, Settle with the chunks' audio end and final flag):
//   revise[-gl|-g38][-nochunk][+newg|+newl][-hostaudio]   sources "mai" (revise2 = "mai,openai");
//   -gl / -g38: the revise server's model is gemini-3.5-flash-lite / gemini-3.8-flash (Google API, text only) instead of the gateway default;  -nochunk: no chunk step (a voice session has none);
//   +newg / +newl: the chunks come from the gateway run `newg` / `newl` (dictation text by Gemini), default `new` (the model of today);
//   -f1 / -f2 / -f12: what-ifs of the replay tool, NOT product code: f1 = an open answer whose Shown no longer stands in the words is dropped (instead of replacing a guessed number of words);
//   f2 = a closed request sends only the tentative words that lie inside its audio (estimated word times up to ToAudio + 0.5 s) as Shown; f12 = both;
//   -hostaudio: what the host code literally does (CaptionHost.TickRevise sends the ring from FromAudio to NOW, also for a closed re-read whose ToAudio is the pause); default = [FromAudio, ToAudio] as designed.
static class Program
{
    sealed record Window(double At,double From,double To,string Text,int Ms,double Cost);
    sealed record ChunkText(double From,double To,int Ms,string Text,bool Overlap,double Cost,bool Final)
    {
        // DictationJob.JoinedSeconds adds chunk.Seconds - OverlapSamples/16000 per chunk: the chunk's length without its half-second lead-in
        public double Joined=>To-From-(Overlap?0.5:0);
    }
    sealed record Case(string Id,double Seconds,string Wav,List<Window> Windows,Dictionary<string,List<ChunkText>> Chunks,Dictionary<string,double> FinalCost);
    sealed record Mode(string Name,bool OldStitch,string Live,string Sources,string? Chunks,string Model="",bool HostAudio=false,bool F1=false,bool F2=false)
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
            case "v116+newg":case "v116+newl":return new(m,false,"polish","",m[5..]);   // control: today's logic with the chunk text of the Gemini typed-text runs
            case "new-nosettle":case "v116-nochunk":return new(m,false,"polish","",null);
        }
        if(m.StartsWith("revise"))
        {
            bool two=m.StartsWith("revise2"),nochunk=m.Contains("-nochunk");
            string model=m.Contains("-gl")?"gemini-3.5-flash-lite":m.Contains("-g38")?"gemini-3.8-flash":"";
            string chunks=m.Contains("+newg")?"newg":m.Contains("+newl")?"newl":"new";
            var tok=m.Split('+')[0].Split('-');bool f12=tok.Contains("f12");
            return new(m,false,"revise",two?"mai,openai":"mai",nochunk?null:chunks,model,m.Contains("-hostaudio"),f12||tok.Contains("f1"),f12||tok.Contains("f2"));
        }
        throw new ArgumentException("unknown mode "+m);
    }
    static async Task<int> Main(string[] args)
    {
        if(args.Length==0){Console.Error.WriteLine("usage: CaptionReplay <evalDir> [--modes v116,revise,...] [--polish URL] [--revise URL] [--cases a,b] [--out dir] [--tag rep2]");return 2;}
        string dir=args[0];string? polishUrl=Arg(args,"--polish"),reviseUrl=Arg(args,"--revise"),outDir=Arg(args,"--out");string tag=Arg(args,"--tag")??"";
        var only=Arg(args,"--cases")?.Split(',').ToHashSet();var modes=(Arg(args,"--modes")??"v116").Split(',').Select(ParseMode).ToList();
        var cases=Load(dir,tag).Where(c=>only is null||only.Contains(c.Id)).ToList();
        using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(90)};
        if(outDir is not null)Directory.CreateDirectory(Path.Combine(dir,outDir));
        foreach(var mode in modes)
        {
            var all=new JsonArray();var sum=new Dictionary<string,double>();
            foreach(var c in cases)
            {
                var r=Run(dir,c,mode,polishUrl is null?null:(text,before,gw)=>Post(http,polishUrl,new{text,before,gateway=gw}),
                    reviseUrl is null?null:(cid,from,to,shown,before,open,sources,model)=>Post(http,reviseUrl,new{@case=cid,from,to,shown,before,open,sources,model}));
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
    static List<Case> Load(string dir,string tag="")
    {
        var list=new List<Case>();
        foreach(var c in JsonNode.Parse(File.ReadAllText(Path.Combine(dir,"cases.json")))!.AsArray())
        {
            string id=c!["id"]!.GetValue<string>();
            var w=JsonNode.Parse(File.ReadAllText(Path.Combine(dir,"windows",id+".json")))!;
            var windows=w["windows"]!.AsArray().Select(x=>new Window(x!["at"]!.GetValue<double>(),x["from"]!.GetValue<double>(),x["to"]!.GetValue<double>(),x["text"]?.GetValue<string>()??"",x["ms"]?.GetValue<int>()??0,x["cost"]?.GetValue<double>()??0)).OrderBy(x=>x.At).ToList();
            var chunks=new Dictionary<string,List<ChunkText>>();var finalCost=new Dictionary<string,double>();
            foreach(var gw in new[]{"main","new","newg","newl"})
            {
                string fp=Path.Combine(dir,"final-"+gw+(tag.Length>0?"-"+tag:""),id+".json");   // --tag rep2: the chunks of a cache-bypassed repeat (final-<gw>-rep2)
                if(!File.Exists(fp))continue;
                var f=JsonNode.Parse(File.ReadAllText(fp))!;
                var arr=f["chunks"]!.AsArray();
                chunks[gw]=arr.Select((x,i)=>new ChunkText(x!["from"]?.GetValue<double>()??0,x["to"]!.GetValue<double>(),x["ms"]?.GetValue<int>()??0,x["text"]?.GetValue<string>()??"",x["overlap"]?.GetValue<bool>()??false,x["cost"]?.GetValue<double>()??0,i==arr.Count-1)).ToList();
                finalCost[gw]=f["polish"]?["cost"]?.GetValue<double>()??0;
            }
            list.Add(new(id,c["seconds"]!.GetValue<double>(),c["wav"]!.GetValue<string>(),windows,chunks,finalCost));
        }
        return list;
    }
    // ---- the audio blocks as the app feeds them: 20 ms (640 bytes) each, RMS as CaptionPolicy/PauseTracker (>= 400 = speech, stays "recent" for 2 s)
    static double[] BlockRms(string wavPath)
    {
        var b=File.ReadAllBytes(wavPath);int pos=12,dataAt=-1,dataLen=0;
        while(pos+8<=b.Length){int n=BitConverter.ToInt32(b,pos+4);if(b[pos]=='d'&&b[pos+1]=='a'&&b[pos+2]=='t'&&b[pos+3]=='a'){dataAt=pos+8;dataLen=Math.Min(n,b.Length-dataAt);break;}pos+=8+n+(n&1);}
        var rms=new List<double>();
        if(dataAt<0)return [];
        for(int s=0;s+640<=dataLen;s+=640)
        {
            double sq=0;for(int p=s;p<s+640;p+=2){int v=BitConverter.ToInt16(b,dataAt+p);sq+=(double)v*v;}
            rms.Add(Math.Sqrt(sq/320));
        }
        return rms.ToArray();
    }
    static List<double> SpeechBlockEnds(double[] rms){var ends=new List<double>();for(int i=0;i<rms.Length;i++)if(rms[i]>=400)ends.Add((i+1)*0.02);return ends;}
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
    static JsonObject Run(string dir,Case c,Mode mode,Func<string,string,string,Reply>? polish,Func<string,double,double,string,string,bool,string,string,Reply>? revise)
    {
        var q=new PriorityQueue<Action,(double,int)>();int seq=0;void At(double t,Action a)=>q.Enqueue(a,(t,seq++));
        var log=new CaptionLog();var snaps=new JsonArray();var calls=new JsonArray();string lastConf="\u0002",lastTent="\u0002";
        var live=new LiveCaption();var oldStitch=new CaptionStitcher(60,4000);var oldPolish=new OldLivePolish();var rp=new RevisePolicy();var pauses=new PauseTracker();
        var blocks=BlockRms(Path.Combine(dir,c.Wav));var ends=SpeechBlockEnds(blocks);int fed=0;
        // the host feeds every audio block to the pause tracker (reset at the start of a recording); the replay feeds the 20 ms blocks up to the tick's time
        void FeedPauses(double t){int upto=(int)Math.Floor(t/0.02+1e-9);while(fed<upto&&fed<blocks.Length){pauses.Add((fed+1)*0.02,0.02,blocks[fed]);fed++;}}
        double end=c.Seconds;var chunkParts=new List<string>();var chunkOverlap=new List<bool>();
        int windows=0,reviseAsked=0,reviseUsed=0,reviseFailed=0,reviseLate=0,reviseNoChange=0,reviseClosed=0,reviseOpen=0,reviseStale=0,reviseModelFailed=0,reviseNotMerged=0,polishUsedLate=0;double audio=0,lastPolish=-10;
        var costs=new Dictionary<string,double>{["caption"]=0,["polish"]=0,["revise"]=0,["chunk"]=0,["final"]=0};
        void Call(double t,string kind,int ms,double usd,bool ok,JsonObject? extra=null)
        {
            costs[kind]+=usd;var o=new JsonObject{["t"]=Math.Round(t,3),["kind"]=kind,["ms"]=ms,["usd"]=usd,["ok"]=ok};
            if(extra is not null)foreach(var (k,v) in extra.ToList()){extra.Remove(k);o[k]=v;}
            calls.Add(o);
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
        if(mode.Chunks is not null&&!c.Chunks.ContainsKey(mode.Chunks))throw new InvalidOperationException($"mode {mode.Name}: no chunks '{mode.Chunks}' for case {c.Id} (run final-text.mjs --gateway {mode.Chunks} first)");
        if(mode.Chunks is not null&&c.Chunks.TryGetValue(mode.Chunks,out var chunks))
        {
            double free=0,joinedSeconds=0;
            foreach(var ch in chunks)
            {
                double start=Math.Max(ch.To,free),at=start+ch.Ms/1000.0;free=at;var chc=ch;
                Call(start,"chunk",ch.Ms,ch.Cost,true);
                At(at,()=>
                {
                    chunkParts.Add(chc.Text);chunkOverlap.Add(chc.Overlap);joinedSeconds+=chc.Joined;
                    // v116 = production today: Settle(joined) only; the re-read modes pass what DictationManager passes (job.JoinedSeconds, job.JoinedFinal)
                    int how=mode.Revise?live.Settle(DictationJoin.Join(chunkParts,chunkOverlap),joinedSeconds,chc.Final):live.Settle(DictationJoin.Join(chunkParts,chunkOverlap));
                    if(how>0){log.Chunks++;if(how==1)log.ChunksPlaced++;}Update(at,'c');
                });
            }
            if(c.FinalCost.TryGetValue(mode.Chunks,out var fc)&&fc>0)costs["final"]+=fc;
        }
        // the 3 s beat of the live clean-up (checked every second like the app's timer)
        if(mode.Polish&&!mode.OldStitch)for(double t=1;t<=end;t+=1)At(t,()=>{});
        // the re-read: the app's timer ticks every 250 ms while the dictation is active; RevisePolicy decides (the same class the app uses), the host passes ReviseSeconds = 25
        if(mode.Revise&&revise is not null)
            for(int k=1;k*0.25<=end+1e-9;k++)
            {
                double t=k*0.25;
                At(t,()=>
                {
                    long now=(long)Math.Round(t*1000);bool speaking=Speaking(ends,t);FeedPauses(t);
                    var req=rp.Next(live,now,t,speaking,pauses,25);
                    if(req is null)return;
                    reviseAsked++;if(req.Open)reviseOpen++;else reviseClosed++;
                    // audio of the request: [FromAudio, ToAudio] as designed; -hostaudio: from FromAudio to now (the ring's end), what CaptionHost.TickRevise does
                    double to=mode.HostAudio&&!req.Open?Math.Round(t,3):req.ToAudio;
                    string shownSent=req.Shown;
                    if(mode.F2&&!req.Open)
                    {
                        // what-if: only the tentative words that (by the estimated word times) start inside the piece's audio
                        var sw=StitchWords.Split(req.Shown);int keep=0;
                        while(keep<sw.Length&&req.From+keep<live.Times.Count&&live.Times[req.From+keep]<req.ToAudio+0.5)keep++;
                        shownSent=string.Join(' ',sw.Take(Math.Max(1,keep)));
                    }
                    var r=revise(c.Id,req.FromAudio,to,shownSent,req.Before,req.Open,mode.Sources,mode.Model);
                    bool late=r.Ms>6000;double at=t+Math.Min(r.Ms,6000)/1000.0;
                    Call(t,"revise",r.Ms,r.Usd,r.Ok&&!late,new JsonObject{["open"]=req.Open,["merged"]=r.Raw?["merged"]?.GetValue<bool>(),["modelOk"]=r.Raw?["modelOk"]?.GetValue<bool>(),["modelMs"]=r.Raw?["modelMs"]?.GetValue<int>(),["modelReason"]=r.Raw?["modelReason"]?.GetValue<string>(),["from"]=req.FromAudio,["to"]=to});
                    if(r.Ok&&!late){if(r.Raw?["merged"]?.GetValue<bool>()==false)reviseNotMerged++;if(r.Raw?["modelOk"]?.GetValue<bool>()==false)reviseModelFailed++;}
                    At(at,()=>
                    {
                        bool ok=r.Ok&&!late;rp.Answered(ok,(long)Math.Round(at*1000),r.Code);
                        if(late)reviseLate++;else if(!r.Ok)reviseFailed++;
                        string? answer=ok?r.Text:null;
                        if(mode.F1&&answer is not null&&req.Open)
                        {
                            // what-if: the words the request showed must still stand in the live words (as a prefix, or their end found by alignment); else the answer is stale
                            var cur=live.Words.Skip(req.From).Select(StitchWords.Key).ToList();var snap=StitchWords.Split(req.Shown).Select(StitchWords.Key).ToList();
                            bool stands=snap.Count<=cur.Count&&cur.Take(snap.Count).SequenceEqual(snap)||WordAlign.EndOf(cur,snap.Skip(Math.Max(0,snap.Count-12)).ToList())>=0;
                            if(!stands){answer=null;reviseStale++;}
                        }
                        bool used=live.AnswerRevise(req,answer,Math.Min(at,end));
                        if(used){reviseUsed++;Update(at,'r');}else if(ok&&answer is not null)reviseNoChange++;
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
        var sp=ends;
        double speechSec=Math.Round(sp.Count*0.02,2);
        var counts=new JsonObject
        {
            ["windows"]=windows,["audioSec"]=Math.Round(audio,1),["changed"]=log.Changed,["changedLate"]=log.ChangedLate,["changedWindow"]=log.ChangedBy['w'],["changedPolish"]=log.ChangedBy['p'],["changedChunk"]=log.ChangedBy['c'],["changedRevise"]=log.ChangedBy['r'],
            ["correctionAgeMs"]=log.MedianCorrectionAgeMs,["polishAsked"]=log.PolishAsked,["polishUsed"]=log.PolishUsed,["polishDropped"]=log.PolishDropped,["polishFailed"]=log.PolishFailed,
            ["reviseAsked"]=reviseAsked,["reviseClosed"]=reviseClosed,["reviseStale"]=reviseStale,["reviseOpen"]=reviseOpen,["reviseModelFailed"]=reviseModelFailed,["reviseNotMerged"]=reviseNotMerged,["reviseUsed"]=reviseUsed,["reviseNoChange"]=reviseNoChange,["reviseFailed"]=reviseFailed,["reviseLate"]=reviseLate,["chunks"]=log.Chunks,["chunksPlaced"]=log.ChunksPlaced,
            ["repeatsSeen"]=log.RepeatsShown,["confirmed"]=live.Confirmed,["words"]=shown.Count
        };
        var costObj=new JsonObject();foreach(var (k,v) in costs)costObj[k]=Math.Round(v,6);
        return new JsonObject{["case"]=c.Id,["mode"]=mode.Name,["seconds"]=c.Seconds,["speechSec"]=speechSec,["lastEvent"]=Math.Round(now0,3),["counts"]=counts,["costs"]=costObj,["snapshots"]=snaps,["calls"]=calls,["final"]=new JsonObject{["confirmed"]=finalConf,["tentative"]=finalTent}};
    }
}
