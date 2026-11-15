using System.Text.Json;
using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
public class VoiceChoicesTests
{
    [Fact] public void OldConfigUsesDeskDefaults()=>Assert.Empty(AppConfig.Parse("{}").VoiceNames);
    [Theory]
    [InlineData("xai","eve")][InlineData("openai","alloy")][InlineData("gemini","Kore")]
    [InlineData("gemini-fast","Aoede")][InlineData("default","Eve")]
    public void ChoicesRoundTrip(string provider,string name)
    {
        var original=new AppConfig{VoiceProvider=provider,VoiceNames=new(){[provider]=name,["openai"]="alloy"}};
        var parsed=AppConfig.Parse(JsonSerializer.Serialize(original));
        Assert.Equal(name,parsed.VoiceNames[provider]);
        Assert.Equal("alloy",parsed.VoiceNames["openai"]);
    }
    [Theory]
    [InlineData("other","eve")][InlineData("XAI","eve")][InlineData("xai","")]
    [InlineData("xai","a")][InlineData("xai","abcdefghijklmnopqrstu")][InlineData("xai","voice1")]
    [InlineData("xai","évê")][InlineData("xai","eve&voice=x")][InlineData("xai","eve\n")]
    [InlineData("xai",null)]
    public void InvalidChoicesRejected(string provider,string? id)
    {
        var json=JsonSerializer.Serialize(new{VoiceNames=new Dictionary<string,string?>{[provider]=id}});
        Assert.Throws<InvalidDataException>(()=>AppConfig.Parse(json));
    }
    [Fact] public void NullMapRejected()=>Assert.Throws<InvalidDataException>(()=>AppConfig.Parse("{\"VoiceNames\":null}"));
    [Fact] public void HotkeyMigrationKeepsChoices()
    {
        var json=JsonSerializer.Serialize(new AppConfig{VoiceNames=new(){["xai"]="eve"},Hotkeys=AppConfig.LegacyHotkeys()});
        var parsed=AppConfig.Parse(json);
        Assert.True(parsed.HotkeysMigrated);Assert.Equal("eve",parsed.VoiceNames["xai"]);
    }
    [Fact] public void MissingHotkeyMergeKeepsChoices()
    {
        var keys=AppConfig.DefaultHotkeys();keys.Remove("canvas");
        var parsed=AppConfig.Parse(JsonSerializer.Serialize(new AppConfig{VoiceNames=new(){["gemini"]="Kore"},Hotkeys=keys}));
        Assert.Equal("Kore",parsed.VoiceNames["gemini"]);Assert.Contains("canvas",parsed.Hotkeys.Keys);
    }
    [Fact] public void CompanionIncludesOnlyCurrentProviderAndEscapesMetadata()
    {
        var config=new AppConfig{VoiceProvider="gemini-fast",VoiceNames=new(){["xai"]="eve",["gemini-fast"]="Kore"}};
        var url=VoiceChoices.CompanionUrl(config,"1 & 2","a#b");
        Assert.Equal("https://desk.app.mintapis.com/?companion=1&voice=gemini-fast&voiceName=Kore&client=screen-agent&cv=1%20%26%202&cc=a%23b&device=windows",url.AbsoluteUri);
    }
    [Fact] public void DefaultOmitsNameEvenWhenAnotherProviderHasChoice()
    {
        var config=new AppConfig{VoiceNames=new(){["openai"]="alloy"}};
        Assert.DoesNotContain("voiceName=",VoiceChoices.CompanionUrl(config,"1","abc").Query);
        config.VoiceNames["xai"]="eve";config.VoiceNames.Remove("xai");
        Assert.DoesNotContain("voiceName=",VoiceChoices.CompanionUrl(config,"1","abc").Query);
    }
    [Theory][InlineData("gemini")][InlineData("gemini-fast")]
    public void GeminiCatalogIsShared(string provider)
    {
        using var json=JsonDocument.Parse("""{"providers":{"gemini":{"default":"Kore","voices":[{"id":"Kore","label":"Kore","note":"Calm","sample":"/sample"},{"id":"bad-id","label":"Bad"}]}}}""");
        var voice=Assert.Single(VoiceChoices.Catalog(json.RootElement,provider));
        Assert.Equal("Kore",voice.Id);Assert.Equal("Calm",voice.Note);
    }
}
