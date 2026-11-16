using System.Text.Json;
using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
// The microphone picker on the status note: choice by Windows name, matched to the device list and to the browser's track label.
public class MicChoiceTests
{
    static readonly List<(string Id,string Name)> Devices=[("{a}","Mikrofonarray (Example Audio)"),("{b}","Headset (Example USB)"),("{c}","Headset (Example USB) 2")];
    [Fact] public void PicksTheSameNameFirst()=>Assert.Equal("{b}",MicChoice.Pick(Devices,"headset (example usb)"));
    [Fact] public void PicksANameWithSuffix()=>Assert.Equal("{a}",MicChoice.Pick([("{a}","Studio Mic (Example) (1234:abcd)")],"Studio Mic (Example)"));
    [Fact] public void GoneDeviceOrNoChoiceMeansDefault()
    {
        Assert.Null(MicChoice.Pick(Devices,"Unplugged (Example)"));
        Assert.Null(MicChoice.Pick(Devices,null));
        Assert.Null(MicChoice.Pick(Devices,"  "));
    }
    [Theory]
    [InlineData("Headset (Example USB) (1234:abcd)","Headset (Example USB)",true)]
    [InlineData("Headset (Example USB)","Headset (Example USB)",true)]
    [InlineData("Mikrofonarray (Example Audio)","Headset (Example USB)",false)]
    [InlineData(null,"Headset (Example USB)",false)]
    [InlineData("anything",null,true)]
    public void TrackLabelMatchesTheChoice(string? label,string? name,bool same)=>Assert.Equal(same,MicChoice.Same(label,name));
    [Fact] public void ConfigRoundTripsAndOldConfigUsesDefault()
    {
        Assert.Null(AppConfig.Parse("{}").Microphone);
        var parsed=AppConfig.Parse(JsonSerializer.Serialize(new AppConfig{Microphone="Headset (Example USB)"}));
        Assert.Equal("Headset (Example USB)",parsed.Microphone);
    }
    [Theory][InlineData("bad\nname")][InlineData("")]
    public void InvalidChoiceRejected(string name)=>Assert.Throws<InvalidDataException>(()=>AppConfig.Parse(JsonSerializer.Serialize(new{Microphone=name})));
    [Fact] public void ShortCutsLongNames()
    {
        Assert.Equal("abc",MicChoice.Short("abc"));
        var s=MicChoice.Short(new string('x',60),40);Assert.Equal(40,s.Length);Assert.EndsWith("…",s);
    }
}
public class MicOverlaySourceTests
{
    static readonly string Root=FindRoot();
    static string FindRoot()
    {
        for(var d=new DirectoryInfo(AppContext.BaseDirectory);d is not null;d=d.Parent)if(Directory.Exists(Path.Combine(d.FullName,"src","ScreenAgent")))return d.FullName;
        throw new DirectoryNotFoundException("repo root not found");
    }
    static string Src(string rel)=>File.ReadAllText(Path.Combine(Root,rel.Replace('/',Path.DirectorySeparatorChar)));
    // A source scan: the overlay button uses the hotkey's switch, every capture opens the chosen device, and the page is told.
    [Fact] public void OverlayControlsAreWired()
    {
        var app=Src("src/ScreenAgent/AppController.cs");
        Assert.Contains("toast.MuteClicked+=()=>_=ToggleMute(\"overlay\")",app);
        Assert.Contains("toast.MicPicked+=name=>_=PickMic(name,\"overlay\")",app);
        Assert.Contains("toast.ShowMic(bridge.VoiceOn&&Active,micMuted)",app);
        Assert.Contains("bridge.SetMic(name)",app);
        Assert.Contains("ScreenConfig.SetMicrophone(name)",app);
        Assert.DoesNotContain("new WasapiCapture()",Src("src/ScreenAgent/CaptionHost.cs"));
        Assert.DoesNotContain("new WasapiCapture()",Src("src/ScreenAgent/DictationCapture.cs"));
        Assert.Contains("v.setMicLabel(",Src("src/ScreenAgent/AgentBridge.cs"));
    }
}
