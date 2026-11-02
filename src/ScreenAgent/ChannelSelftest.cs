using System.Net;
using System.Net.Http;
using System.Text.Json;
using ScreenAgent.Core;
namespace ScreenAgent;
// --selftest-prompt-channel: the command channel against a fake Desk. Proves the order and content of what the app reports: received at once, then one result;
// a repeated id is ignored; a second prompt while one runs is answered "busy"; no session fails closed; a crash reports "interrupted"; the text is never in a report.
internal static class ChannelSelftest
{
    sealed class Desk : HttpMessageHandler
    {
        public readonly List<(string Path,string Body)> Posts=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            string body=request.Content is null?"":await request.Content.ReadAsStringAsync(ct);
            lock(Posts)Posts.Add((request.RequestUri!.AbsolutePath,body));
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{}",System.Text.Encoding.UTF8,"application/json")};
        }
    }
    public static int Run()
    {
        var results=new List<object>();bool failed=false;
        void Case(string name,bool pass,object? detail=null){results.Add(new{name,pass,detail});if(!pass)failed=true;}
        try
        {
            const string secret="GEHEIMER-PROMPT-TEXT a#abc123";
            var config=AppConfig.Parse("{\"deskUrl\":\"https://example.com\"}");
            string temp=Path.Combine(Path.GetTempPath(),"ScreenAgent-channel-"+Guid.NewGuid());Directory.CreateDirectory(temp);
            var desk=new Desk();using var client=new DeviceClient(config,desk,Path.Combine(temp,"device.bin"));
            client.Pair(new(new string('t',43),Guid.NewGuid().ToString(),DateTimeOffset.UtcNow.AddDays(1)));
            var release=new TaskCompletionSource<bool>();var events=new List<string>();var fallbacks=new List<string>();bool sessionOk=true;bool crash=false;bool logBroken=false;
            var channel=new PromptChannel(client,async (text,ct,_)=>
            {
                if(crash)throw new InvalidOperationException("boom");
                await release.Task;return new PromptResult(PromptCodes.Typed,true,text.Length,"element",1,false,5,IntPtr.Zero);
            },(t,d)=>{if(logBroken)throw new InvalidOperationException("log broken");events.Add(t);},(code,_,_)=>fallbacks.Add(code),_=>sessionOk);
            string P(string id)=>"/api/device/session/sid1/commands/"+id+"/result";
            string id1="p0123456789abcdef",id2="pfedcba9876543210",id3="p1111111111111111",id4="p2222222222222222";
            var first=channel.Handle("sid1",new(id1,secret),CancellationToken.None);
            await_(Until(()=>desk.Posts.Count>=1));
            Case("received-is-posted-at-once-while-typing-has-not-finished",desk.Posts.Count==1&&desk.Posts[0].Path==P(id1)&&desk.Posts[0].Body=="{\"state\":\"received\"}"&&!first.IsCompleted,new{posts=desk.Posts.Select(p=>p.Path+" "+p.Body)});
            channel.Handle("sid1",new(id1,secret),CancellationToken.None).Wait();
            Case("repeated-id-is-ignored",desk.Posts.Count==1);
            channel.Handle("sid1",new(id2,"zweiter"),CancellationToken.None).Wait();
            var busy=desk.Posts.Where(p=>p.Path==P(id2)).Select(p=>p.Body).ToArray();
            Case("second-prompt-while-one-runs-fails-busy",busy.Length==2&&busy[0]=="{\"state\":\"received\"}"&&busy[1].Contains("\"state\":\"failed\"")&&busy[1].Contains("\"code\":\"busy\"")&&busy[1].Contains("\"landed\":null")&&fallbacks.SequenceEqual([PromptCodes.Busy]),new{busy,fallbacks});
            release.SetResult(true);first.Wait(5000);
            var done=desk.Posts.Where(p=>p.Path==P(id1)).Select(p=>p.Body).ToArray();
            Case("first-prompt-reports-done-with-code-landed-and-chars",done.Length==2&&done[1]==$"{{\"state\":\"done\",\"code\":\"typed\",\"landed\":true,\"chars\":{secret.Length}}}",new{done});
            sessionOk=false;channel.Handle("sid1",new(id3,"x"),CancellationToken.None).Wait();sessionOk=true;
            var gone=desk.Posts.Where(p=>p.Path==P(id3)).Select(p=>p.Body).ToArray();
            Case("no-session-fails-closed",gone.Length==2&&gone[1].Contains("\"code\":\"no-session\"")&&fallbacks.Contains(PromptCodes.NoSession),new{gone});
            crash=true;channel.Handle("sid1",new(id4,"x"),CancellationToken.None).Wait();
            var boom=desk.Posts.Where(p=>p.Path==P(id4)).Select(p=>p.Body).ToArray();
            Case("a-crash-reports-interrupted-and-frees-the-gate",boom.Length==2&&boom[1].Contains("\"code\":\"interrupted\""),new{boom});
            crash=false;
            string id5="p3333333333333333",id6="p4444444444444444";
            logBroken=true;channel.Handle("sid1",new(id5,"x"),CancellationToken.None).Wait(10000);logBroken=false;
            channel.Handle("sid1",new(id6,"y"),CancellationToken.None).Wait(10000);
            var afterLog=desk.Posts.Where(p=>p.Path==P(id6)).Select(p=>p.Body).ToArray();
            Case("a-throwing-log-never-blocks-later-prompts",afterLog.Length==2&&afterLog[1].Contains("\"state\":\"done\""),new{afterLog});
            Case("the-prompt-text-is-in-no-report",!desk.Posts.Any(p=>p.Body.Contains("GEHEIMER")||p.Body.Contains("abc123")||p.Path.Contains("GEHEIMER")));
            Case("telemetry-events-carry-no-text",events.All(e=>e.StartsWith("main.prompt.")));
            Directory.Delete(temp,true);
        }
        catch(Exception e){Case("selftest-crashed",false,e.ToString());}
        Console.WriteLine(JsonSerializer.Serialize(new{test="prompt-channel",status=failed?"failed":"passed",results}));
        return failed?1:0;
        static void await_(Task t)=>t.Wait();
        static async Task Until(Func<bool> c){for(int i=0;i<100&&!c();i++)await Task.Delay(50);}
    }
}
