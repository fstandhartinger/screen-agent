using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
public class ScreenTextTests
{
    public const string Opnar="DU BIST DRAN\nOpnar AI (Lund) ein Angebot schicken?\nSenden (empfohlen)\nNordgatan (Lund) unser Angebot schicken?\nSenden (empfohlen)\nOutwing ...\nSenden (empfohlen)";
    [Fact] public void ConfigSwitchSurvivesHotkeyNormalization(){Assert.True(AppConfig.Parse("{}").ScreenTextForSpeech);Assert.False(AppConfig.Parse("{\"ScreenTextForSpeech\":false}").ScreenTextForSpeech);Assert.False(AppConfig.Parse("{\"ScreenTextForSpeech\":false,\"Hotkeys\":{}}").ScreenTextForSpeech);}
    [Fact] public void OcrLatencySurvivesTelemetry(){var clean=TraceSanitizer.Clean(new(){["ocrMs"]=23L,["ocrText"]="secret",["ocrMsText"]="secret"})!;Assert.Equal(23L,clean["ocrMs"]);Assert.Single(clean);}
    [Fact] public void CardsPreserveNamesAndDeduplicateButtons(){var text=ScreenTextLogic.Tidy(Opnar);Assert.Contains("Opnar AI",text);Assert.Contains("Nordgatan",text);Assert.Contains("Outwing",text);Assert.Equal(1,text.Split('\n').Count(l=>l=="Senden (empfohlen)"));}
    [Fact] public void Secrets(){var text=ScreenTextLogic.Tidy("API sk-1234567890abcdefghijklmnop\nCode: 123456\nfür Grüße ÄÖÜ ß");Assert.DoesNotContain("123456",text);Assert.True(text.Contains(SecretText.Hidden)||text.Contains("[redacted]"));Assert.Contains("für Grüße ÄÖÜ ß",text);}
    [Fact] public void CapAtWholeLine(){var text=ScreenTextLogic.Tidy(string.Join('\n',Enumerable.Range(0,1000).Select(i=>$"Zeile {i} mit einigen Wörtern")));Assert.InRange(text.Length,5900,6000);Assert.EndsWith("Wörtern",text);}
    [Theory] [InlineData(null)] [InlineData("")] [InlineData(" \n12\nOK\nx\n---")] public void Empty(string? raw)=>Assert.Equal("",ScreenTextLogic.Tidy(raw));
    [Fact] public void Whitespace()=>Assert.Equal("für Grüße\nÄÖÜ ß",ScreenTextLogic.Tidy("  für\tGrüße \r\nÄÖÜ   ß\nfür Grüße"));
    [Fact] public void MergeKeepsUiaSpelling()=>Assert.Equal("Opnar AI (Lund)\nNordgatan",ScreenTextLogic.Merge("Opnar AI (Lund)","opnar  ai\nNordgatan"));
    [Fact] public void ChangeThreshold(){var a=string.Join('\n',Enumerable.Range(0,10).Select(i=>"Zeile "+i));Assert.False(ScreenTextLogic.Changed(a,a.Replace("Zeile 9","Andere Zeile")));Assert.True(ScreenTextLogic.Changed(a,a.Replace("Zeile 9","Andere Zeile").Replace("Zeile 8","Neue Zeile")));Assert.True(ScreenTextLogic.Changed(null,a));Assert.False(ScreenTextLogic.Changed(null,""));Assert.True(ScreenTextLogic.Changed(a,""));}
}
