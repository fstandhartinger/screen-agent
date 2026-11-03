using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
// v0.1.7 f-fix3: one test per finding of CRITIC-F3 (S1, S3, S4, the label NIT) that fails if the fix is taken out.
public class ComputerFix3Tests
{
    static readonly ComputerPolicy P=new(deskHost:"desk.localhost");
    static string R(ActionContext c,ComputerScope s){var v=P.Classify(c,s);return v.Class+"/"+v.Rule;}
    static readonly string H=ComputerSpeech.HiddenMark;
    [Fact] public void S1_HiddenValuesAreNotSpokenUnlessAnAddressOrShortAndPlain()
    {
        string line=ComputerSpeech.FormLine([(H+"authenticity_token","aB3dE6gH9jK2mN5pQ8sT1vW4"),(H+"sessionid","x7k2mq9zt4v8w1p6r3n5"),(H+"VIEWSTATE","/wEPDwUKLTQ5MzY4+NjE0D2QWAmYPZBYCAgMP/ZB"),(H+"to","chef@x.org"),(H+"mode","Entwurf"),("subject","Rechnung")]);
        foreach(var secret in new[]{"aB3dE6gH9jK2mN5pQ8sT1vW4","x7k2mq9zt4v8w1p6r3n5","wEPDw"})Assert.DoesNotContain(secret,line);
        Assert.Contains("verstecktes Feld (authenticity token)",line);Assert.Contains("verstecktes Feld (sessionid)",line);
        Assert.Contains("„chef@x.org“",line);Assert.Contains("„Entwurf“",line);Assert.Contains("„Rechnung“",line);
    }
    [Fact] public void S1_VisibleFieldsComeFirstSoHiddenOnesCannotDisplaceTheBody()
    {
        var f=new List<(string,string)>();for(int i=0;i<6;i++)f.Add((H+"h"+i,"wert"+i));f.Add(("body","Hallo Anna"));
        string line=ComputerSpeech.FormLine(f);
        Assert.Contains("„Hallo Anna“",line);Assert.Contains("Und 1 weitere Felder",line);
    }
    [Fact] public void NIT_ALabelIsRedactedBeforeItIsCleaned()
    {
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz0123456789",Labels.Spoken("ghp_abcdefghijklmnopqrstuvwxyz0123456789"));
        Assert.Contains("ausgeblendet",Labels.Spoken("ghp_abcdefghijklmnopqrstuvwxyz0123456789"));
    }
    // ---- S3: a read of another origin makes open, scroll and key ask
    [Fact] public void S3_OpenScrollAndKeyAskAfterAReadOfAnotherOrigin()
    {
        var sc=ComputerScope.From("öffne intranet.firma.de und evil-blog.com");
        string read=ComputerPolicy.ReadOrigin("chrome","https://intranet.firma.de/inbox");
        Assert.Equal("R1/open-in-scope",R(new("chrome_open"){Process="chrome",OpenUrl="https://evil-blog.com/"},sc));
        Assert.Equal("R2/cross-origin",R(new("chrome_open"){Process="chrome",OpenUrl="https://evil-blog.com/",ReadOrigins=[read]},sc));
        Assert.Equal("R1/scroll",R(new("scroll"){Process="chrome",Url="https://evil-blog.com/"},sc));
        Assert.Equal("R2/cross-origin",R(new("scroll"){Process="chrome",Url="https://evil-blog.com/",ReadOrigins=[read]},sc));
        Assert.Equal("R1/key",R(new("key"){Process="chrome",Url="https://evil-blog.com/",Key="down",FocusVerified=true},sc));
        Assert.Equal("R2/cross-origin",R(new("key"){Process="chrome",Url="https://evil-blog.com/",Key="down",FocusVerified=true,ReadOrigins=[read]},sc));
        // the same origin stays free
        Assert.Equal("R1/scroll",R(new("scroll"){Process="chrome",Url="https://intranet.firma.de/inbox",ReadOrigins=[read]},sc));
        Assert.Equal("R1/open-in-scope",R(new("chrome_open"){Process="chrome",OpenUrl="https://intranet.firma.de/",UrlInSnapshot=true,ReadOrigins=[read]},sc));
    }
    // ---- S4: the tab looked up after a screenshot must be the one before
    [Fact] public void S4_AScreenshotIsKeptOnlyWhenTheTabIsTheSameAfterwards()
    {
        Assert.True(ComputerPolicy.SameTab("t1","https://a.example/",("t1","https://a.example/")));
        Assert.False(ComputerPolicy.SameTab("t1","https://a.example/",("t1","https://mail.example/")));
        Assert.False(ComputerPolicy.SameTab("t1","https://a.example/",("t2","https://a.example/")));
        Assert.False(ComputerPolicy.SameTab("t1","https://a.example/",(null,null)));
    }
}
