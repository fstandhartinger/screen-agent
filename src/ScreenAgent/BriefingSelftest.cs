using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using ScreenAgent.Core;
namespace ScreenAgent;
// --selftest-briefing: the start briefing against a stub Desk, with real windows, real UI Automation (a WPF tab control stands in for Chrome's tab strip) and a 50 MB generated transcript.
// Proves: the tail read stays within 512 kB, the body follows the route's contract, secrets are filtered, a 409 is retried, a requested refresh carries its commandId,
// a missing path is named and not an error, the log events carry counts and reasons only. Exit 0 passed, 1 failed.
internal static class BriefingSelftest
{
    delegate void CaseFn(string name,bool pass,object? detail=null);
    const string Secret="sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789";
    sealed class Desk : HttpMessageHandler
    {
        public readonly List<(string Path,string Body)> Posts=[];
        public int Conflicts;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            string body=request.Content is null?"":await request.Content.ReadAsStringAsync(ct);
            bool conflict=false;
            lock(Posts){Posts.Add((request.RequestUri!.AbsolutePath,body));if(Conflicts>0){Conflicts--;conflict=true;}}
            return new HttpResponseMessage(conflict?HttpStatusCode.Conflict:HttpStatusCode.OK){Content=new StringContent(conflict?"{\"code\":\"no-session\"}":"{\"ok\":true}",Encoding.UTF8,"application/json")};
        }
    }
    public static int Run()
    {
        var results=new List<object>();bool failed=false;
        void Case(string name,bool pass,object? detail=null){results.Add(new{name,pass,detail});if(!pass)failed=true;}
        var app=new Application();
        app.Startup+=(_,_)=>
        {
            var window=new Window{Title="Briefing-Selftest-Window",Width=500,Height=300,ShowActivated=false,Content=new TabControl{ItemsSource=new[]{"Alpha","Beta","Gamma"}.Select(h=>new TabItem{Header=h}).ToArray(),SelectedIndex=1}};
            window.Show();IntPtr hwnd=new WindowInteropHelper(window).EnsureHandle();
            Task.Run(()=>
            {
                string temp=Path.Combine(Path.GetTempPath(),"ScreenAgent-briefing-"+Guid.NewGuid());Directory.CreateDirectory(temp);
                try{Checks(Case,hwnd,temp);}
                catch(Exception e){Case("selftest-crashed",false,e.ToString());}
                finally{try{Directory.Delete(temp,true);}catch{}app.Dispatcher.BeginInvoke(new System.Action(()=>app.Shutdown(failed?1:0)));}
            });
        };
        app.Run();
        Console.WriteLine(JsonSerializer.Serialize(new{test="briefing",status=failed?"failed":"passed",results}));
        return failed?1:0;
    }
    static void Checks(CaseFn Case,IntPtr hwnd,string temp)
    {
        // windows and tabs through the real Win32 / UI Automation paths
        var all=BriefingCollector.VisibleWindows(true,CancellationToken.None);
        Case("window-list-has-the-fixture-window",all.Any(w=>w.Title=="Briefing-Selftest-Window"),new{count=all.Count});
        Case("own-windows-are-left-out-by-default",!BriefingCollector.VisibleWindows(false,CancellationToken.None).Any(w=>w.Title=="Briefing-Selftest-Window"));
        var tabs=BriefingCollector.TabsOf(AutomationElement.FromHandle(hwnd),"Chrome 1");
        Case("tabs-are-read-through-ui-automation",tabs.Select(t=>t.Title).SequenceEqual(["Alpha","Beta","Gamma"])&&tabs.Single(t=>t.Active).Title=="Beta",new{tabs=tabs.Select(t=>t.Title+(t.Active?"*":""))});

        // a 50 MB transcript, the stub Desk, a secret in the OCR text
        string file=Path.Combine(temp,"main.jsonl");
        string filler=JsonSerializer.Serialize(new{type="user",message=new{role="user",content=new object[]{new{type="tool_result",content=new string('x',30000)}}}});
        using(var w=new StreamWriter(file,false,new UTF8Encoding(false)))
            for(int i=0;i<1700;i++)
            {
                w.Write(JsonSerializer.Serialize(new{type="user",message=new{role="user",content="frage "+i+(i==1699?" "+Secret:"")},timestamp="2026-11-03T10:00:00Z"})+"\n");w.Write(filler+"\n");
                w.Write(JsonSerializer.Serialize(new{type="assistant",message=new{role="assistant",content=new object[]{new{type="text",text="antwort "+i}}},timestamp="2026-11-03T10:00:01Z"})+"\n");
            }
        Case("fixture-is-50-mb",new FileInfo(file).Length>50_000_000);
        var desk=new Desk{Conflicts=1};var events=new List<(string,Dictionary<string,object?>)>();
        var config=AppConfig.Parse(JsonSerializer.Serialize(new{DeskUrl="https://example.com",MainSessionPath=file}));
        using var client=new DeviceClient(config,desk,Path.Combine(temp,"device.bin"));
        client.Pair(new(new string('t',43),Guid.NewGuid().ToString(),DateTimeOffset.UtcNow.AddDays(1)));
        string? sid="sid1";
        var collector=new BriefingCollector(config,client,(t,d)=>{lock(events)events.Add((t,TraceSanitizer.Clean(d)!));},()=>sid,_=>null){RetryDelays=[TimeSpan.FromMilliseconds(100),TimeSpan.FromMilliseconds(100)],IncludeOwnWindows=true};
        collector.NoteScreenText("Rechnung 4711\n\n\n\nZugang: "+Secret+" fertig");
        collector.Start("sid1",CancellationToken.None).Wait(20000);
        var first=desk.Posts.ToList();
        Case("a-409-is-retried-and-the-second-post-lands",first.Count==2&&first.All(p=>p.Path=="/api/device/session/sid1/briefing"),new{posts=first.Count});
        using var doc=JsonDocument.Parse(first[^1].Body);var root=doc.RootElement;
        var msgs=root.GetProperty("mainSession").GetProperty("messages");
        Case("twelve-messages-newest-last",msgs.GetArrayLength()==12&&msgs[11].GetProperty("text").GetString()=="antwort 1699",new{n=msgs.GetArrayLength()});
        Case("source-is-a-label-not-a-path",root.GetProperty("mainSession").GetProperty("source").GetString()=="custom");
        Case("windows-include-the-fixture",root.TryGetProperty("windows",out var wl)&&wl.EnumerateArray().Any(x=>x.GetProperty("title").GetString()=="Briefing-Selftest-Window"));
        Case("screen-text-is-tidied-and-redacted",root.GetProperty("screenText").GetString()=="Rechnung 4711\n\nZugang: [redacted] fertig");
        Case("no-secret-anywhere-in-the-body",!first[^1].Body.Contains("sk-ant")&&!first[^1].Body.Contains("abcdefghijklmnopqrstuvwxyz"));
        Case("no-command-id-on-the-start-briefing",!root.TryGetProperty("commandId",out _));
        (string,Dictionary<string,object?>) collect;lock(events)collect=events.First(e=>e.Item1=="briefing.collect");
        long tail=Convert.ToInt64(collect.Item2["tailBytes"]);
        Case("tail-read-stays-within-512-kb",tail>0&&tail<=512*1024,new{tail});
        Case("collect-log-has-counts",Convert.ToInt32(collect.Item2["messages"])==12&&Convert.ToInt64(collect.Item2["ms"])<2500&&collect.Item2.ContainsKey("missing")&&collect.Item2.ContainsKey("bytes"),new{ms=collect.Item2["ms"],bytes=collect.Item2["bytes"]});
        string log;lock(events)log=JsonSerializer.Serialize(events);
        Case("log-carries-no-contents",!log.Contains("frage")&&!log.Contains("antwort")&&!log.Contains("Rechnung")&&!log.Contains("Briefing-Selftest-Window")&&!log.Contains("sk-ant"));
        Case("sent-event-reports-attempts",events.Any(e=>e.Item1=="briefing.sent"&&Convert.ToInt32(e.Item2["attempts"])==2));

        // a requested refresh carries the commandId
        desk.Posts.Clear();
        collector.Request("sid1","abcd1234",CancellationToken.None).Wait(20000);
        using var asked=JsonDocument.Parse(desk.Posts.Single().Body);
        Case("requested-refresh-carries-the-command-id",asked.RootElement.GetProperty("commandId").GetString()=="abcd1234");

        // a main session that is not there: named, nothing else breaks
        desk.Posts.Clear();events.Clear();
        var noPath=AppConfig.Parse(JsonSerializer.Serialize(new{DeskUrl="https://example.com",MainSessionPath=Path.Combine(temp,"gibt-es-nicht")}));
        var c2=new BriefingCollector(noPath,client,(t,d)=>{lock(events)events.Add((t,TraceSanitizer.Clean(d)!));},()=>sid,_=>null){IncludeOwnWindows=true};
        c2.Request("sid1","ffff0000",CancellationToken.None).Wait(20000);
        using var none=JsonDocument.Parse(desk.Posts.Single().Body);
        Case("missing-path-sends-no-main-session",!none.RootElement.TryGetProperty("mainSession",out _)&&none.RootElement.TryGetProperty("windows",out _));
        Case("missing-path-is-named-in-the-log",events.Any(e=>e.Item1=="briefing.collect"&&e.Item2["missing"] is string[] m&&m.Contains("main-session:path-missing")));

        // a stopped WSL is never opened, and a switched-off briefing posts nothing
        desk.Posts.Clear();
        var wsl=AppConfig.Parse(JsonSerializer.Serialize(new{DeskUrl="https://example.com",MainSessionPath=@"\\wsl.localhost\Ubuntu\home\flori"}));
        string? asked2=null;var c3=new BriefingCollector(wsl,client,(_,_)=>{},()=>sid,d=>{asked2=d;return false;});
        var g3=c3.Collect();
        Case("stopped-wsl-is-not-opened",asked2=="Ubuntu"&&g3.Main is null&&g3.Missing.Contains("main-session:wsl-not-running"),new{g3.Missing});
        var off=AppConfig.Parse(JsonSerializer.Serialize(new{DeskUrl="https://example.com",Briefing=false}));
        new BriefingCollector(off,client,(_,_)=>{},()=>sid).Start("sid1",CancellationToken.None).Wait(2000);
        Case("briefing-false-posts-nothing",desk.Posts.Count==0);

        // the session ended while the post was failing: give up quietly
        desk.Conflicts=5;sid="other";events.Clear();
        new BriefingCollector(config,client,(t,d)=>{lock(events)events.Add((t,TraceSanitizer.Clean(d)!));},()=>sid,_=>null){RetryDelays=[TimeSpan.FromMilliseconds(50)]}.Request("sid1","11112222",CancellationToken.None).Wait(20000);
        Case("ended-session-is-not-posted",events.Any(e=>e.Item1=="briefing.fail"&&e.Item2["reason"] as string=="session-ended"&&e.Item2["stage"] as string=="post")&&desk.Posts.Count==0);
    }
}
