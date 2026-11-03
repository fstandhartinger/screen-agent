using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using ScreenAgent.Core;
namespace ScreenAgent;
// --selftest-dictation <speech.wav>: the real recorder (disk writer, chunker, roll-over), the real chunk job and the real DeviceClient against an in-process Desk
// stand-in, fed from a test wav (16 kHz mono). Covers: chunks while it runs, retry on 503, crash-copy repair, a failed dictation that stays pending and succeeds on retry,
// retention, the failure window (buttons, no activation, capture exclusion) and the subtitle overlay. No audio device needed. Exit 0 passed, 1 failed.
internal static class DictationSelftest
{
    sealed class Desk : HttpMessageHandler
    {
        public readonly List<(string Path,double Seconds,bool HasChunk,int Index,string FieldBefore,bool WavOk)> Requests=[];
        public Func<int,int,HttpStatusCode> Status=(index,attempt)=>HttpStatusCode.OK;   // chunk index, attempt -> status
        readonly Dictionary<int,int> tries=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            string path=request.RequestUri!.AbsolutePath.TrimStart('/');
            if(path.EndsWith("api/device/caption"))
            {
                _=await request.Content!.ReadAsByteArrayAsync(ct);
                return Json(request.Headers.Authorization is null?HttpStatusCode.Unauthorized:HttpStatusCode.OK,"{\"text\":\"Untertitel Probe\",\"ms\":420,\"seconds\":6.0,\"usage\":{\"cost\":0.0012}}");
            }
            byte[] wav=[];string ctx="{}";string? chunk=null;
            foreach(var (name,body) in Parts(request.Content!,await request.Content!.ReadAsByteArrayAsync(ct)))
            {
                if(name=="audio")wav=body;else if(name=="context")ctx=System.Text.Encoding.UTF8.GetString(body);else if(name=="chunk")chunk=System.Text.Encoding.UTF8.GetString(body);
            }
            int index=0;if(chunk is not null)index=JsonDocument.Parse(chunk).RootElement.GetProperty("index").GetInt32();
            tries[index]=tries.GetValueOrDefault(index)+1;
            double seconds=Math.Max(0,wav.Length-Wav.HeaderBytes)/(double)Wav.BytesPerSecond;
            bool wavOk=wav.Length>Wav.HeaderBytes&&System.Text.Encoding.ASCII.GetString(wav,0,4)=="RIFF"&&BitConverter.ToInt32(wav,40)==wav.Length-Wav.HeaderBytes;
            string before=JsonDocument.Parse(ctx).RootElement.GetProperty("fieldBefore").GetString()??"";
            Requests.Add((path,seconds,chunk is not null,index,before,wavOk));
            var status=Status(index,tries[index]);
            return status==HttpStatusCode.OK?Json(status,JsonSerializer.Serialize(new{text=$"teil{index} wort",providers=new{groq=new{ok=true,ms=300,status=200}},degraded=false,merged=true,seconds,ms=300})):Json(status,JsonSerializer.Serialize(new{error="stub",code="stub-"+(int)status,providers=new{groq=new{ok=false,status=(int)status}}}));
        }
        // multipart/form-data, byte-exact (Latin-1 keeps one char per byte so the audio survives the search).
        static IEnumerable<(string Name,byte[] Body)> Parts(HttpContent content,byte[] raw)
        {
            string boundary="--"+content.Headers.ContentType!.Parameters.First(p=>p.Name=="boundary").Value!.Trim('"');
            string text=System.Text.Encoding.Latin1.GetString(raw);
            foreach(var part in text.Split(boundary).Skip(1))
            {
                int split=part.IndexOf("\r\n\r\n",StringComparison.Ordinal);if(split<0)continue;
                string head=part[..split];string body=part[(split+4)..];if(body.EndsWith("\r\n"))body=body[..^2];
                var m=System.Text.RegularExpressions.Regex.Match(head,"name=\"?([^\";\r\n]+)");
                if(m.Success)yield return (m.Groups[1].Value,System.Text.Encoding.Latin1.GetBytes(body));
            }
        }
        static HttpResponseMessage Json(HttpStatusCode s,string body)=>new(s){Content=new StringContent(body,System.Text.Encoding.UTF8,"application/json")};
    }
    static short[] ReadWav(string path)
    {
        var b=File.ReadAllBytes(path);int at=12;
        while(at+8<=b.Length)
        {
            string id=System.Text.Encoding.ASCII.GetString(b,at,4);int size=BitConverter.ToInt32(b,at+4);
            if(id=="data"){int n=Math.Min(size,b.Length-at-8);return Wav.Samples(b.AsSpan(at+8,n));}
            at+=8+size+(size&1);
        }
        throw new InvalidDataException("no data chunk");
    }
    static readonly List<object> Results=[];
    static int failures;
    static void Check(string name,bool ok,string detail=""){Results.Add(new{name,ok,detail});if(!ok){failures++;Console.Error.WriteLine($"FAIL {name} {detail}");}}
    public static int Run(string[] args)
    {
        string wavPath=args.Length>1?args[1]:Path.Combine("testdata","e2e-actions.wav");
        string dir=Path.Combine(Path.GetTempPath(),"sa-dict-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(dir);
        var events=new List<string>();void Log(string t,Dictionary<string,object?> d){lock(events)events.Add(t);}
        try
        {
            var speech=ReadWav(wavPath);
            var config=AppConfig.Parse("{}");
            var desk=new Desk();
            using var client=new DeviceClient(config,desk,Path.Combine(dir,"device.bin"));
            client.Pair(new("A"+new string('b',42),Guid.NewGuid().ToString(),DateTimeOffset.UtcNow.AddDays(1)));
            var ctx=new DictationContext("selftest","Davor:",["332179"],["Reax"]);
            var store=new DictationStore(Path.Combine(dir,"dictation"));
            int fedSeconds=(int)(speech.Length/(double)Wav.Rate);
            Check("input is long speech",fedSeconds>100,$"{fedSeconds}s");

            // 1. The long dictation, through the real recorder, with a 503 on the second chunk and a roll-over every minute.
            desk.Status=(i,a)=>i==1&&a==1?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK;
            using var rec=new DictationRecording(store,"selftest",Log,maxMinutes:1,headerFixSeconds:1);
            var warnings=new List<string>();rec.Warning+=warnings.Add;
            using var cts=new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var job=new DictationJob(rec.Meta.Id,"Davor:",(r,ct)=>client.DictateChunk(r,ctx,ct),Log,cts.Token,(_,_)=>Task.CompletedTask);
            rec.ChunkReady+=c=>job.Enqueue(c);
            string? crashCopy=null;
            int block=Wav.Rate/10;
            for(int i=0;i<speech.Length;i+=block)
            {
                var pcm=System.Runtime.InteropServices.MemoryMarshal.AsBytes(speech.AsSpan(i,Math.Min(block,speech.Length-i)));
                rec.Append(pcm);
                if(crashCopy is null&&i>=Wav.Rate*30)   // the process "dies" here: the file as it is on disk right now
                {
                    crashCopy=Path.Combine(dir,"crash.wav");
                    File.Copy(Path.Combine(store.Folder,rec.Meta.Files[0]),crashCopy);
                }
            }
            var last=rec.Stop();
            var outcome=job.Complete(last).GetAwaiter().GetResult();
            Check("long dictation completes",outcome.Complete&&outcome.Chunks>=2,$"chunks={outcome.Chunks} failed={outcome.Failed} reason={outcome.FailReason}");
            Check("every request <= 60 s and a valid wav",desk.Requests.All(r=>r.Seconds<=60.01&&r.WavOk),string.Join(",",desk.Requests.Select(r=>r.Seconds.ToString("0.0"))));
            Check("chunk field on a long dictation",desk.Requests.All(r=>r.HasChunk));
            Check("503 was retried",desk.Requests.Count(r=>r.Index==1)==2,$"requests for chunk 1: {desk.Requests.Count(r=>r.Index==1)}");
            Check("text joined in order",Enumerable.Range(0,outcome.Chunks).Select(i=>"teil"+i).SequenceEqual(outcome.Text.Split(' ').Where(w=>w.StartsWith("teil"))),outcome.Text);
            Check("fieldBefore carries the previous chunk",desk.Requests.First(r=>r.Index==1).FieldBefore.Contains("teil0 wort"));
            Check("roll-over into more files, none lost",rec.Meta.Files.Count>=2&&rec.Meta.Paths.Sum(p=>Wav.Repair(p).Pcm)==(long)speech.Length*2,$"files={rec.Meta.Files.Count}");
            Check("limit warning raised",warnings.Count>=1&&events.Contains("dictation.limit"));
            var crash=Wav.Repair(crashCopy!);
            Check("crash copy readable and repaired",crash.Readable&&crash.Pcm>=Wav.BytesPerSecond*25,$"pcm={crash.Pcm} repaired={crash.Repaired}");

            // 2. A single short dictation is one request without a chunk field.
            desk.Requests.Clear();desk.Status=(_,_)=>HttpStatusCode.OK;
            var shortJob=new DictationJob("short","",(r,ct)=>client.DictateChunk(r,ctx,ct),Log,cts.Token);
            var one=shortJob.Complete(new Chunk(0,speech[..(Wav.Rate*8)],true,0)).GetAwaiter().GetResult();
            Check("short dictation is one plain request",one.Complete&&desk.Requests.Count==1&&!desk.Requests[0].HasChunk,$"requests={desk.Requests.Count}");

            // 3. Failure: everything 500. The file stays, pending after a "restart", the retry from the file works.
            desk.Requests.Clear();desk.Status=(_,_)=>HttpStatusCode.InternalServerError;
            var bad=new DictationRecording(store,"selftest",Log);
            var badJob=new DictationJob(bad.Meta.Id,"",(r,ct)=>client.DictateChunk(r,ctx,ct),Log,cts.Token,(_,_)=>Task.CompletedTask);
            bad.Append(System.Runtime.InteropServices.MemoryMarshal.AsBytes(speech.AsSpan(0,Wav.Rate*12)));
            var failed=badJob.Complete(bad.Stop()).GetAwaiter().GetResult();
            bad.Meta.State="failed";bad.Meta.Reason=failed.FailReason;bad.Meta.Ended=DateTimeOffset.UtcNow;store.Save(bad.Meta);bad.Dispose();
            Check("failure reports reason and status",!failed.Complete&&failed.FailedStatus==500&&failed.FailReason!=null&&desk.Requests.Count==3,$"requests={desk.Requests.Count} reason={failed.FailReason}");
            var restarted=new DictationStore(store.Folder);
            Check("failed recording is pending after a restart",restarted.Pending().Any(p=>p.Id==bad.Meta.Id&&p.Reason==failed.FailReason));
            desk.Requests.Clear();desk.Status=(_,_)=>HttpStatusCode.OK;
            var again=restarted.Pending().First(p=>p.Id==bad.Meta.Id);
            var retryJob=new DictationJob(again.Id,"",(r,ct)=>client.DictateChunk(r,ctx,ct),Log,cts.Token);
            var chunker=new DictationChunker();
            foreach(var p in again.Paths)foreach(var b in Wav.ReadBlocks(p))foreach(var c in chunker.Append(b))retryJob.Enqueue(c);
            var retried=retryJob.Complete(chunker.Finish()).GetAwaiter().GetResult();
            Check("retry from the file succeeds",retried.Complete&&retried.Text.Contains("teil0"),retried.Text);

            // 4. Retention: a processed dictation older than 24 h goes, a failed one younger than 14 days stays.
            var old=restarted.Begin("selftest");File.WriteAllBytes(restarted.NextFile(old),Wav.FromPcm(new byte[32000]));old.State="done";old.Ended=DateTimeOffset.UtcNow.AddHours(-25);restarted.Save(old);
            var gone=restarted.Sweep(config.Retention());
            Check("sweep removes old success, keeps young failure",gone.Contains(old.Id)&&!gone.Contains(again.Id)&&restarted.Pending().Any(p=>p.Id==again.Id));

            // 5. Caption route against the stand-in: text, latency, cost.
            var cap=client.Caption(Wav.FromSamples(speech.AsSpan(0,Wav.Rate*6)),["332179"],["Reax"],cts.Token).GetAwaiter().GetResult();
            Check("caption reply parsed",cap.Status==200&&cap.Text=="Untertitel Probe"&&cap.Ms==420&&cap.Cost is 0.0012,$"{cap.Status} {cap.Ms} {cap.Cost}");

            // 6. Windows: the failure window and the subtitle overlay.
            WindowChecks(again);
        }
        catch(Exception ex){Check("selftest ran",false,ex.GetType().Name+": "+ex.Message);}
        finally{try{Directory.Delete(dir,true);}catch{ }}
        Console.WriteLine(JsonSerializer.Serialize(new{test="dictation",failures,results=Results}));
        return failures==0?0:1;
    }
    static void WindowChecks(DictationMeta meta)
    {
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};   // closing the last test window must not end the run (the finally shuts down)
        app.Startup+=async (_,_)=>
        {
            try
            {
                var w=DictationWindow.ShowFailure(meta,"Transkription nicht erreichbar (HTTP 500)",meta.Paths.First(),"teil0 wort",()=>{});
                long ex=Native.GetWindowLongPtr(new System.Windows.Interop.WindowInteropHelper(w).Handle,-20).ToInt64();
                Check("failure window shows reason, path and the four buttons",w.IsVisible&&w.Heading.StartsWith("Diktat nicht verarbeitet: Transkription")&&w.Path.Contains(meta.Paths.First())&&w.ButtonNames.SequenceEqual(["Erneut versuchen","Ordner öffnen","Text kopieren","Schließen"]),string.Join("|",w.ButtonNames));
                Check("failure window topmost, no activation, excluded from capture",w.Topmost&&(ex&0x08000000L)!=0&&w.AffinityOk,$"exstyle={ex:x} affinity={w.AffinityOk}");
                var w2=DictationWindow.ShowFailure(meta,"Zeitüberschreitung",meta.Paths.First(),null,()=>{});
                Check("no copy button without a text",!w2.ButtonNames.Contains("Text kopieren")&&w2.ButtonNames.Count==3,string.Join("|",w2.ButtonNames));
                w2.Close();
                bool closedReported=false;
                var r=DictationWindow.ShowResult(meta,"Diktat fertig · Text zum Einfügen","der ganze Text",_=>Task.FromResult(true),()=>closedReported=true);
                Check("result window offers insert, copy and close",r.ButtonNames.SequenceEqual(["In aktuelles Feld einfügen","Text kopieren","Schließen"]),string.Join("|",r.ButtonNames));
                r.Close();
                Check("closing the result window is reported (the dictation counts as delivered)",closedReported);
                var cap0=DictationWindow.ShowFailure(meta,"Mikrofon getrennt",meta.Paths.First(),null,null);
                Check("a failure without a retry has no retry button",!cap0.ButtonNames.Contains("Erneut versuchen"),string.Join("|",cap0.ButtonNames));cap0.Close();
                var note=DictationWindow.ShowNotice("Mikrofon getrennt bei 0:42 · wieder verbunden","Was in der Lücke gesagt wurde, fehlt.");
                Check("notice window only closes",note.ButtonNames.SequenceEqual(["Schließen"])&&note.IsVisible,string.Join("|",note.ButtonNames));note.Close();
                var cap=new CaptionOverlay();cap.Present("Das ist ein Probe-Untertitel");await Task.Delay(400);   // a layered window reports IsVisible only after its first render
                long cx=Native.GetWindowLongPtr(new System.Windows.Interop.WindowInteropHelper(cap).Handle,-20).ToInt64();
                Check("subtitle click-through, no focus, excluded from capture",cap.IsVisible&&(cx&0x20L)!=0&&(cx&0x08000000L)!=0&&cap.AffinityOk,$"visible={cap.IsVisible} exstyle={cx:x} affinity={cap.AffinityOk} text={cap.Text.Length}");
                cap.Close();
            }
            catch(Exception ex){Check("window checks ran",false,ex.GetType().Name+": "+ex.Message);}
            finally{DictationWindow.CloseCurrent();app.Shutdown();}
        };
        app.Run();
    }
}
