using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
// The wire between the Desk gateway and this app: testdata/computer-contract.json is the same file the gateway's node tests read (agent-desk gateway/test/fixtures/computer-contract.json).
// Commands are parsed here, results are built here and must equal the fixture byte for byte (as JSON), so neither side can drift alone.
public class ComputerContractTests
{
    static readonly string Root=FindRoot();
    static string FindRoot()
    {
        for(var d=new DirectoryInfo(AppContext.BaseDirectory);d is not null;d=d.Parent)if(File.Exists(Path.Combine(d.FullName,"testdata","computer-contract.json")))return d.FullName;
        throw new DirectoryNotFoundException("testdata/computer-contract.json not found");
    }
    static readonly JsonNode Fx=JsonNode.Parse(File.ReadAllText(Path.Combine(Root,"testdata","computer-contract.json")))!;
    static ComputerCommand? Parse(JsonNode wire,out string why){using var d=JsonDocument.Parse(new JsonObject{["computer"]=wire.DeepClone()}.ToJsonString());return ComputerWire.Parse(d.RootElement,out why);}
    static JsonNode Wire(string name)=>Fx["commands"]!.AsArray().First(c=>c!["name"]!.GetValue<string>()==name)!["wire"]!;

    [Fact] public void StartAndExtend()
    {
        var s=Parse(Wire("start"),out _)!;
        Assert.Equal("start",s.Op);Assert.False(s.Extend);Assert.Equal("Übernimm den Computer und öffne mail.google.com.",s.Goal);Assert.Equal(["mail.google.com"],s.ScopeWords);
        var e=Parse(Wire("extend"),out _)!;
        Assert.Equal("start",e.Op);Assert.True(e.Extend);Assert.Equal(s.Run,e.Run);Assert.Contains("github.com",e.Goal);
    }
    [Fact] public void StopCarriesItsReason(){var c=Parse(Wire("stop"),out _)!;Assert.Equal("stop",c.Op);Assert.Equal("voice-stop",c.Reason);}
    [Fact] public void BeatAndConfirm()
    {
        Assert.Equal("beat",Parse(Wire("beat"),out _)!.Op);
        var c=Parse(Wire("confirm"),out _)!;Assert.Equal("confirm",c.Op);Assert.Equal("a1b2c3d4e5f60718",c.Hash);Assert.Equal("47",c.Digits);
    }
    [Fact] public void EveryActionOfTheFixtureParsesWithItsExactArguments()
    {
        var acts=Fx["actions"]!.AsArray();Assert.True(acts.Count>=14);
        foreach(var a in acts)
        {
            var w=a!["wire"]!;var c=Parse(w,out string why);
            Assert.True(c is not null,a["name"]+": "+why);
            Assert.Equal("action",c!.Op);Assert.Equal(w["tool"]!.GetValue<string>(),c.Tool);
            Assert.Contains(c.Tool,ComputerWire.ToolArgs.Keys);
            var names=w["args"]!.AsObject().Select(p=>p.Key).ToList();
            Assert.All(names,n=>Assert.Contains(n,ComputerWire.ToolArgs[c.Tool]));
            Assert.Equal(names.Count,c.Args.ValueKind==JsonValueKind.Object?c.Args.EnumerateObject().Count():0);
        }
        // every tool of the app is covered by the fixture
        var covered=acts.Select(a=>a!["wire"]!["tool"]!.GetValue<string>()).ToHashSet();
        Assert.All(ComputerWire.ToolArgs.Keys,t=>Assert.Contains(t,covered));
    }
    [Fact] public void BadCommandsAreNeverExecuted()
    {
        foreach(var b in Fx["badCommands"]!.AsArray())
        {
            Assert.Null(Parse(b!["wire"]!,out string why));
            Assert.Equal(b["why"]!.GetValue<string>(),why);
        }
    }
    [Fact] public void ResultsBuiltByTheAppEqualTheFixture()
    {
        foreach(var r in Fx["results"]!.AsArray())
        {
            var b=r!["build"]!;
            string? S(string k)=>b[k] is JsonNode n?n.GetValue<string>():null;
            var nc=b["needsConfirm"] is JsonNode n2?new ComputerWire.NeedsConfirm(n2["hash"]!.GetValue<string>(),n2["spoken"]!.GetValue<string>()):null;
            var meta=new ComputerWire.ResultMeta(S("target"),S("domain"),S("process"),S("jpeg"));
            var body=ComputerWire.Result(b["ok"]!.GetValue<bool>(),S("code")!,S("class"),S("rule")??"",S("text"),nc,S("runState"),meta);
            var got=JsonNode.Parse(JsonSerializer.Serialize(body))!;
            Assert.True(JsonNode.DeepEquals(r["body"],got),r["name"]+": expected "+r["body"]!.ToJsonString()+" got "+got.ToJsonString());
        }
    }
    [Fact] public void ResultVocabularyMatchesTheGateway()
    {
        // codes and run states the gateway's validateResult accepts
        string[] codes=["done","refused","needs-confirm","stale-ref","killed","handson","unclear","no-run","denied"];
        Assert.Equal(codes.OrderBy(x=>x),ComputerCodes.All.OrderBy(x=>x));
        string[] states=["running","handson","paused","ended","killed"];
        foreach(var r in Fx["results"]!.AsArray())if(r!["body"]!["run_state"] is JsonNode rs)Assert.Contains(rs.GetValue<string>(),states);
        foreach(var b in Fx["beats"]!.AsArray())Assert.Contains(b!["body"]!["state"]!.GetValue<string>(),states);
        // the rule names the policy uses fit the gateway's pattern
        var rx=new Regex("^[A-Za-z0-9_.:-]{0,60}$");
        foreach(var r in Fx["results"]!.AsArray())if(r!["body"]!["rule"] is JsonNode rule)Assert.Matches(rx,rule.GetValue<string>());
    }
    [Fact] public void BeatAndGatewayTimingAreFiveSeconds()
    {
        Assert.Equal(5000,Fx["timing"]!["beatMs"]!.GetValue<int>());
        Assert.Equal(15000,Fx["timing"]!["deadAfterMs"]!.GetValue<int>());
        Assert.Equal(15_000,new RunCaps().GatewayQuietMs);   // the app closes after the same three missed beats
        Assert.Equal(120_000,Fx["timing"]!["hotkeySecondResultMs"]!.GetValue<int>());
    }
    [Fact] public void StateNameIsEndedNotClosed()
    {
        var r=new ComputerRun("r0123456789abcdef","öffne gmail",ComputerScope.From("öffne gmail",null,"board.example"));
        r.Close("test");
        Assert.Equal("ended",r.StateName);
    }
}
