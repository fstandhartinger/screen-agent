using System.Text;
using System.Text.Json;
using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
public class BriefingTests
{
    sealed class Counting(Stream inner) : Stream
    {
        public long Bytes;
        public override int Read(byte[] buffer,int offset,int count){int n=inner.Read(buffer,offset,count);Bytes+=n;return n;}
        public override int Read(Span<byte> buffer){int n=inner.Read(buffer);Bytes+=n;return n;}
        public override bool CanRead=>true;public override bool CanSeek=>true;public override bool CanWrite=>false;
        public override long Length=>inner.Length;
        public override long Position{get=>inner.Position;set=>inner.Position=value;}
        public override long Seek(long offset,SeekOrigin origin)=>inner.Seek(offset,origin);
        public override void Flush(){}public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
    }
    static string Line(string type,string text,string ts="2026-11-03T10:00:00.000Z")=>
        JsonSerializer.Serialize(new{type,message=new{role=type,content=type=="user"?(object)text:new object[]{new{type="thinking",thinking="denke"},new{type="text",text},new{type="tool_use",id="t1",name="Bash",input=new{command="ls"}}}},timestamp=ts});
    static string Temp()=>Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"sa-briefing-"+Guid.NewGuid().ToString("N"))).FullName;

    [Fact]public void BigTranscriptIsReadFromTheTailOnly()
    {
        string dir=Temp();string file=Path.Combine(dir,"big.jsonl");
        try
        {
            string filler=JsonSerializer.Serialize(new{type="user",message=new{role="user",content=new object[]{new{type="tool_result",tool_use_id="t1",content=new string('x',30000)}}}});
            using(var w=new StreamWriter(file,false,new UTF8Encoding(false)))
                for(int i=0;i<1700;i++){w.Write("{\"type\":\"summary\"}\n");w.Write(Line("user","frage "+i)+"\n");w.Write(filler+"\n");w.Write(Line("assistant","antwort "+i)+"\n");}
            Assert.True(new FileInfo(file).Length>50_000_000);
            using var counting=new Counting(new FileStream(file,FileMode.Open,FileAccess.Read));
            var messages=TranscriptTail.Read(counting,out var stats);
            Assert.True(counting.Bytes<=512*1024,"read "+counting.Bytes);
            Assert.True(stats.BytesRead<=512*1024);Assert.Equal(counting.Bytes,stats.BytesRead);
            Assert.False(stats.Aborted);
            Assert.Equal(12,messages.Count);
            Assert.Equal("antwort 1699",messages[^1].Text);Assert.Equal("assistant",messages[^1].Role);
            Assert.Equal("frage 1694",messages[0].Text);   // 6 pairs, oldest first
            Assert.DoesNotContain(messages,m=>m.Text.Contains("xxxx")||m.Text.Contains("denke")||m.Text.Contains("Bash"));
        }
        finally{Directory.Delete(dir,true);}
    }
    [Fact]public void FirstPartialLineIsSkippedAndSmallFilesAreWhole()
    {
        string text=Line("user","eins")+"\n"+Line("assistant","zwei")+"\n"+Line("user","drei")+"\n";
        var bytes=Encoding.UTF8.GetBytes(text);
        var whole=TranscriptTail.Read(new MemoryStream(bytes),out var s0);
        Assert.Equal(["eins","zwei","drei"],whole.Select(m=>m.Text));Assert.Equal(bytes.Length,s0.BytesRead);
        int cut=Encoding.UTF8.GetByteCount(Line("user","eins"))/2;   // the window starts in the middle of line one
        var part=TranscriptTail.Read(new MemoryStream(bytes),out var s1,maxTail:bytes.Length-cut);
        Assert.Equal(["zwei","drei"],part.Select(m=>m.Text));Assert.Equal(bytes.Length-cut,s1.BytesRead);
    }
    [Fact]public void LastLineWithoutNewlineCountsHalfWrittenOneIsDropped()
    {
        var ok=TranscriptTail.Read(new MemoryStream(Encoding.UTF8.GetBytes(Line("user","eins")+"\n"+Line("assistant","zwei"))),out _);
        Assert.Equal(["eins","zwei"],ok.Select(m=>m.Text));
        string half=Line("assistant","zwei");
        var cut=TranscriptTail.Read(new MemoryStream(Encoding.UTF8.GetBytes(Line("user","eins")+"\n"+half[..(half.Length/2)])),out var st);
        Assert.Equal(["eins"],cut.Select(m=>m.Text));Assert.Equal(1,st.Skipped);
    }
    [Fact]public void BudgetStopsTheRead()
    {
        var sb=new StringBuilder();for(int i=0;i<500;i++)sb.Append(Line("user","nachricht "+i)+"\n");
        var all=TranscriptTail.Read(new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString())),out var stats,budget:4000);
        Assert.True(stats.Aborted);Assert.True(all.Count<12||all.Count==12);Assert.True(stats.BytesRead<new UTF8Encoding().GetByteCount(sb.ToString()));
    }
    [Fact]public void OnlyTextOfUserAndAssistantCapsAndFilters()
    {
        string secret="sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789";
        var lines=new List<string>
        {
            Line("user","Hallo "+secret+" ende"),
            Line("assistant",string.Concat(Enumerable.Repeat("wort ",200))),
            "{kaputt",
            JsonSerializer.Serialize(new{type="user",isSidechain=true,message=new{role="user",content="agent intern"}}),
            JsonSerializer.Serialize(new{type="user",isMeta=true,message=new{role="user",content="meta"}}),
            Line("user","<command-name>/clear</command-name>"),
            Line("user","   \n  "),
            JsonSerializer.Serialize(new{type="assistant",message=new{role="assistant",content=new object[]{new{type="tool_use",name="Read"}}}}),
        };
        var messages=TranscriptTail.Read(new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n",lines)+"\n")),out var stats);
        Assert.Equal(2,messages.Count);
        Assert.DoesNotContain("sk-ant",messages[0].Text);Assert.Contains("[redacted]",messages[0].Text);
        Assert.True(messages[1].Text.Length<=TranscriptTail.MaxChars);Assert.EndsWith("…",messages[1].Text);
        Assert.Equal("2026-11-03T10:00:00.000Z",messages[0].At);
        Assert.True(stats.Skipped>=1);
    }
    [Fact]public void OnlyTheLastTwelveMessagesStay()
    {
        var sb=new StringBuilder();for(int i=0;i<40;i++)sb.Append(Line(i%2==0?"user":"assistant","m"+i)+"\n");
        var messages=TranscriptTail.Read(new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString())),out _);
        Assert.Equal(Enumerable.Range(28,12).Select(i=>"m"+i),messages.Select(m=>m.Text));
    }
    [Fact]public void AnOverlongLineIsSkippedNotBuffered()
    {
        string huge=Line("assistant",new string('z',TranscriptTail.MaxLine+10));
        var messages=TranscriptTail.Read(new MemoryStream(Encoding.UTF8.GetBytes(Line("user","vorher")+"\n"+huge+"\n"+Line("user","nachher")+"\n")),out var stats);
        Assert.Equal(["vorher","nachher"],messages.Select(m=>m.Text));Assert.Equal(1,stats.Skipped);
    }
    [Theory]
    [InlineData("key sk-proj-AbCdEf0123456789AbCdEf0123456789")]
    [InlineData("token ghp_0123456789abcdefghijABCDEFGHIJ012345")]
    [InlineData("Authorization: Bearer abcdef0123456789.abcdef-xyz")]
    [InlineData("hash 0123456789abcdef0123456789abcdef01234567 end")]
    [InlineData("blob QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVowMTIzNDU2Nzg5YWJj end")]
    [InlineData("api_key=hunter2hunter2")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abcdefghijk")]
    public void SecretsAreRedacted(string input)
    {
        string r=SecretFilter.Redact(input);
        Assert.Contains("[redacted]",r);
        foreach(var bad in new[]{"sk-proj-AbCd","ghp_0123","abcdef0123456789.abc","0123456789abcdef0123456789abcdef","QUJDREVG","hunter2","eyJzdWIi"})Assert.DoesNotContain(bad,r);
    }
    [Fact]public void OrdinaryTextAndPathsStay()
    {
        string text="Bitte pruefe /home/flori/jobs/screen-agent-v017-20261103/workers/g-app/src und a#123abc fuer https://desk.app.mintapis.com/device";
        Assert.Equal(text,SecretFilter.Redact(text));
    }
    [Fact]public void NewestFileAcrossAccountsListsDirectoriesOnly()
    {
        string home=Temp();
        try
        {
            void Put(string rel,DateTime when,string content="{}\n"){string p=Path.Combine(home,rel);Directory.CreateDirectory(Path.GetDirectoryName(p)!);File.WriteAllText(p,content);File.SetLastWriteTimeUtc(p,when);}
            var now=DateTime.UtcNow;
            Put(".claude/projects/p1/old.jsonl",now.AddHours(-5));
            Put(".claude-A/projects/p2/b.jsonl",now.AddHours(-1),Line("user","aus a")+"\n");
            Put(".claude-B/projects/p3/c.jsonl",now.AddHours(-3));
            Put(".claude-A/projects/p2/sess/subagents/deep.jsonl",now);   // below a project folder: not looked at
            Put("notes/other.jsonl",now);
            var hit=MainSessionSource.Newest(home,CancellationToken.None);
            Assert.NotNull(hit);Assert.EndsWith("b.jsonl",hit!.Value.File);Assert.Equal(".claude-A",hit.Value.Source);
            var part=MainSessionSource.Read(home,_=>null,CancellationToken.None);
            Assert.Equal(["aus a"],part.Value!.Messages.Select(m=>m.Text));Assert.Equal(".claude-A",part.Value.Source);
            Assert.EndsWith("b.jsonl",MainSessionSource.Newest(Path.Combine(home,".claude-A","projects","p2","b.jsonl"),CancellationToken.None)!.Value.File);   // explicit file
            Assert.Equal("main-session:path-missing",MainSessionSource.Read(Path.Combine(home,"gibtsnicht"),_=>null,CancellationToken.None).Missing);
        }
        finally{Directory.Delete(home,true);}
    }
    [Fact]public void StoppedWslIsNeverOpened()
    {
        string asked="";
        var part=MainSessionSource.Read(@"\\wsl.localhost\Ubuntu\home\flori",d=>{asked=d;return false;},CancellationToken.None);
        Assert.Equal("Ubuntu",asked);Assert.Equal("main-session:wsl-not-running",part.Missing);Assert.Null(part.Value);
        Assert.Null(WslProbe.DistroOf(@"C:\Users\x"));Assert.Equal("Debian",WslProbe.DistroOf(@"\\wsl$\Debian\home"));
    }
    [Fact]public void WslListIsReadAsUtf16()
    {
        var output=Encoding.Unicode.GetBytes("Ubuntu (Default)\r\nDebian\r\n");
        Assert.True(WslProbe.Running(output,"Ubuntu"));Assert.True(WslProbe.Running(output,"debian"));Assert.False(WslProbe.Running(output,"Alpine"));
        Assert.False(WslProbe.Running(Encoding.Unicode.GetBytes("Es gibt keine laufenden Distributionen.\r\n"),"Ubuntu"));
    }
    [Fact]public void ASlowOrBrokenSourceNeverBlocksTheOthers()
    {
        var gate=new ManualResetEventSlim();
        var clock=System.Diagnostics.Stopwatch.StartNew();
        var g=BriefingGather.Run(
            _=>new(new List<BriefingWindow>{new("chrome","Titel")}),
            ct=>{gate.Wait(10_000);return new(new List<TabInfo>());},   // a stalled path: does not look at the token
            _=>throw new InvalidOperationException("boom"),budgetMs:500);
        gate.Set();
        Assert.True(clock.ElapsedMilliseconds<1500,"took "+clock.ElapsedMilliseconds);
        Assert.Single(g.Windows);Assert.Empty(g.Tabs);Assert.Null(g.Main);
        Assert.Contains("tabs:timeout",g.Missing);Assert.Contains("main-session:error",g.Missing);
        Assert.Contains(("tabs","timeout"),g.Failures);Assert.Contains(("main-session","InvalidOperationException"),g.Failures);
    }
    [Fact]public void ASourceThatHonoursTheTokenHandsOverWhatItHas()
    {
        var g=BriefingGather.Run(_=>new(new List<BriefingWindow>()),ct=>{var list=new List<TabInfo>{new("Chrome 1","A",true,null)};while(!ct.IsCancellationRequested)Thread.Sleep(10);return new(list);},_=>new(null,"main-session:path-missing"),budgetMs:600);
        Assert.Single(g.Tabs);Assert.Equal(["main-session:path-missing"],g.Missing);
    }
    [Fact]public void CommandRequestIsParsed()
    {
        string? Id(string json){using var d=JsonDocument.Parse(json);return BriefingCommand.Parse(d.RootElement);}
        Assert.Equal("a1b2c3d4",Id("{\"fresh\":false,\"briefing\":{\"id\":\"a1b2c3d4\"}}"));
        Assert.Null(Id("{\"fresh\":true,\"id\":\"x\"}"));Assert.Null(Id("{\"briefing\":{}}"));Assert.Null(Id("{\"briefing\":{\"id\":\"../x\"}}"));Assert.Null(Id("{\"briefing\":{\"id\":5}}"));
    }
    [Fact]public void PayloadFollowsTheDeskContract()
    {
        var windows=Enumerable.Range(0,50).Select(i=>new BriefingWindow("proc"+i,"Fenster "+i)).ToList();
        var tabs=Enumerable.Range(0,60).Select(i=>new TabInfo("Chrome 1","Tab "+i,i==3,"https://example.com/seite?token=geheim123&x=1#frag")).ToList();
        var main=new MainSession(".claude-A","2026-11-03T10:00:00Z",[new("user","2026-11-03T09:59:00Z","hallo")]);
        string json=JsonSerializer.Serialize(MakeBody(windows,tabs,main,"Zeile eins\n\n\n\nZeile   zwei sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789","c0ffee12"));
        using var d=JsonDocument.Parse(json);var r=d.RootElement;
        Assert.Equal(30,r.GetProperty("windows").GetArrayLength());Assert.Equal(40,r.GetProperty("tabs").GetArrayLength());
        var active=r.GetProperty("tabs").EnumerateArray().Single(t=>t.GetProperty("active").GetBoolean());
        Assert.Equal("https://example.com/seite",active.GetProperty("url").GetString());
        Assert.Equal(1,r.GetProperty("tabs").EnumerateArray().Count(t=>t.TryGetProperty("url",out _)));
        Assert.Equal(".claude-A",r.GetProperty("mainSession").GetProperty("source").GetString());
        Assert.Equal("hallo",r.GetProperty("mainSession").GetProperty("messages")[0].GetProperty("text").GetString());
        string st=r.GetProperty("screenText").GetString()!;
        Assert.Equal("Zeile eins\n\nZeile zwei [redacted]",st);
        Assert.Equal("c0ffee12",r.GetProperty("commandId").GetString());
        Assert.True(json.Length<64000);
    }
    static Dictionary<string,object?> MakeBody(List<BriefingWindow> w,List<TabInfo> t,MainSession? m,string? s,string? id)=>BriefingPayload.Build(w,t,m,s,id);
    [Fact]public void EmptyPartsAreLeftOutAndTheBodyStaysSmall()
    {
        var body=BriefingPayload.Build([],[],null,"  ",null);
        Assert.Empty(body);
        var big=BriefingPayload.Build(Enumerable.Range(0,30).Select(i=>new BriefingWindow(new string('p',200),new string('t',500))).ToList(),
            Enumerable.Range(0,40).Select(i=>new TabInfo("Chrome 1",new string('t',500),i==0,"https://e.com/"+new string('u',500))).ToList(),
            new MainSession(".claude",null,Enumerable.Range(0,12).Select(i=>new BriefingMessage("user",null,new string('m',300))).ToList()),new string('s',9000),"abc12345");
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(big).Length<BriefingPayload.MaxBytes);
        Assert.Equal(1500,((string)big["screenText"]!).Length);
    }
    [Fact]public void ConfigDefaultsAndMigrationKeepTheBriefingSettings()
    {
        var def=AppConfig.Parse("{}");Assert.True(def.Briefing);Assert.Null(def.MainSessionPath);
        string legacy=JsonSerializer.Serialize(new{Briefing=false,MainSessionPath=@"C:\x\main.jsonl",Hotkeys=AppConfig.V012Hotkeys()});
        var c=AppConfig.Parse(legacy);   // goes through WithHotkeys
        Assert.True(c.HotkeysMigrated);Assert.False(c.Briefing);Assert.Equal(@"C:\x\main.jsonl",c.MainSessionPath);
        Assert.Throws<InvalidDataException>(()=>AppConfig.Parse("{\"MainSessionPath\":\"a\\nb\"}"));
    }
}
