using ScreenAgent.Core;
using System.Text.Json;
using Xunit;
namespace ScreenAgent.Tests;
public class AppTests
{
    [Theory]
    [InlineData("Jeff","Jev","Jeff","Jev")]
    [InlineData("hello Hugging Phase today","hello Huggingface today","Hugging Phase","Huggingface")]
    [InlineData("one two three four","one new three four","two","new")]
    public void LetterCorrections(string before,string after,string from,string to)=>Assert.Equal(new Correction(from,to),Corrections.Diff(before,after));
    [Theory]
    [InlineData("hello","hello")][InlineData("a","a b")][InlineData("bank DE123","bank DE456")]
    [InlineData("me@here","you@there")][InlineData("one two three four","new text right here")]
    [InlineData("123","Jev")][InlineData("word!","Word")]
    public void RejectUnsafeOrBroadDiff(string a,string b)=>Assert.Null(Corrections.Diff(a,b));
    [Fact]public void PromoteOnlyIndependentSecondObservation()
    {
        List<PendingCorrection> pending=[];var pair=new Correction("Jeff","Jev");
        Assert.False(Corrections.Observe(pending,pair));
        string saved=JsonSerializer.Serialize(pending);pending=JsonSerializer.Deserialize<List<PendingCorrection>>(saved)!;
        Assert.True(Corrections.Observe(pending,pair));Assert.Equal(2,pending[0].Count);
        Assert.False(Corrections.Observe(pending,new("DE123","DE456")));Assert.Single(pending);
    }
    [Fact]public void UniqueInsertionOnly()
    {
        Assert.True(Corrections.InsertedSpan("hello  world","hello Jeff world","Jeff",out int at));Assert.Equal(6,at);
        Assert.False(Corrections.InsertedSpan("Jeff ","Jeff Jeff","Jeff",out _));
        Assert.False(Corrections.InsertedSpan("old","new Jeff","Jeff",out _));
        Assert.False(Corrections.InsertedSpan("same","same","",out _));
    }
    [Fact]public void ScreenHasOnlyConfirmedIdsAndProcessClass()
    {
        var p=DevicePayloads.Screen(["a#123abc","a#123abc","a#abcdef","secret"],"notepad",42);
        using var j=JsonDocument.Parse(JsonSerializer.Serialize(p));var root=j.RootElement;
        Assert.Equal(new[]{"ids","window","frameVersion"},root.EnumerateObject().Select(x=>x.Name));
        Assert.Equal(2,p.ids.Length);Assert.False(p.window.blocked);Assert.Equal(42,p.frameVersion);
        Assert.Equal(new[]{"process","blocked"},root.GetProperty("window").EnumerateObject().Select(x=>x.Name));
    }
    [Fact]public void CapAndValidateIds()=>Assert.Equal(20,DevicePayloads.Ids(Enumerable.Range(0,25).Select(i=>"a#"+i.ToString("x6"))).Length);
    [Fact]public void HighlightSizeLimits()
    {
        Assert.Throws<InvalidDataException>(()=>DevicePayloads.Highlight(new byte[2097153],[],new(0,0,1,1),[]));
        Assert.Throws<InvalidDataException>(()=>DevicePayloads.Highlight([],new byte[1048577],new(0,0,1,1),[]));
        var p=DevicePayloads.Highlight([1,2,3],[4,5],new(-100,20,300,200),["a#123abc"]);
        Assert.Equal(new byte[]{1,2,3},Convert.FromBase64String(p.crop));Assert.Equal(-100,p.bbox.x);
    }
    [Fact]public void ConfigDefaultsAndLocalEmail()
    {
        var c=AppConfig.Parse("{}");Assert.Equal(4000,c.IntervalMs);Assert.False(c.RetainScreenshots);Assert.Null(c.UserEmail);
        c=AppConfig.Parse("{\"userEmail\":\"local-only\",\"blockProcesses\":[\"private\"]}");
        Assert.True(c.Policy().Blocked("private","notes"));Assert.True(c.Policy().Blocked("keepassxc","notes"));
        Assert.DoesNotContain("local-only",JsonSerializer.Serialize(DevicePayloads.Screen([],"notepad",1)));
    }
    [Theory][InlineData("{\"deskUrl\":\"http://example.test\"}")][InlineData("{\"deskUrl\":\"https://example.test/private\"}")]
    [InlineData("{\"intervalMs\":10}")][InlineData("{\"hotkeys\":{}}")]public void BadConfigFailsClosed(string json)=>Assert.Throws<InvalidDataException>(()=>AppConfig.Parse(json));
    [Fact]public void HandsFreeSurvivesModifierReleaseAndStopsAtSecondToggle()
    {
        var h=new Hotkeys();h.Feed(new(Key.Ctrl,true,0));h.Feed(new(Key.Win,true,10));
        Assert.Contains(ActionKind.DictationStart,h.Feed(new(Key.Space,true,20)));
        h.Feed(new(Key.Space,false,30));h.Feed(new(Key.Win,false,40));h.Feed(new(Key.Ctrl,false,50));Assert.True(h.Dictating);
        h.Feed(new(Key.Win,true,100));h.Feed(new(Key.Ctrl,true,110));
        Assert.Contains(ActionKind.DictationStop,h.Feed(new(Key.Space,true,120)));Assert.False(h.Dictating);
    }
    [Fact]public void BindAtChordDownBeforeThreshold()
    {
        var h=new Hotkeys();h.Feed(new(Key.Ctrl,true,0));var actions=h.Feed(new(Key.Win,true,10));
        Assert.Contains(ActionKind.DictationBind,actions);Assert.DoesNotContain(ActionKind.DictationStart,actions);
    }
}
