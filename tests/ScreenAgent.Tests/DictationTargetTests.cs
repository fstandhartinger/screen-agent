using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
// v0.1.15: the first Alt+Win press must not say "kein Textfeld gewählt" while the foreground or the focus query is still settling.
public class DictationTargetTests
{
    sealed class Field{}
    static readonly IntPtr W=new(0x1234);
    static TargetProbe Real(string process="Code - Insiders",string? why=null)=>new(TargetWindow.Real,process,why);
    static TargetProbe Kind(TargetWindow w)=>new(w,"",null);
    // Plays the given looks in order (the last one repeats); time moves only by the waits, plus msPerLook per look.
    static async Task<(TargetOutcome<Field> Outcome,List<int> Waits)> Run(int msPerLook,params TargetProbe[] looks)
    {
        long now=0;int i=0;var waits=new List<int>();
        var outcome=await DictationTarget.Resolve(()=>{var p=looks[Math.Min(i++,looks.Length-1)];now+=msPerLook;return (p,DictationTarget.Usable(p)?new Field():null);},
            ms=>{waits.Add(ms);now+=ms;return Task.CompletedTask;},()=>now);
        return (outcome,waits);
    }
    [Fact] public async Task FirstLookUsableBindsAtOnce()
    {
        var (o,waits)=await Run(0,Real());
        Assert.True(o.Bound);Assert.Equal(1,o.Attempts);Assert.Empty(waits);Assert.Empty(o.Misses);Assert.Null(o.Refused);
    }
    [Fact] public async Task ElectronFocusSettlingIsAskedAgainNotRefused()
    {
        // 14 Nov 13:45:06: the first read of VS Code's focus did not match the second; 3.5 s later the same chord worked.
        var (o,_)=await Run(5,Real(why:"focus-changed"),Real(why:"focus-changed"),Real());
        Assert.True(o.Bound);Assert.Equal(3,o.Attempts);Assert.Equal(["focus-changed","focus-changed"],o.Misses);Assert.Equal("Code - Insiders",o.Last.Process);
    }
    [Fact] public async Task EmptyProcessIsAskedAgainNeverNoField()
    {
        var (o,_)=await Run(1,Real(process:""),Real());
        Assert.True(o.Bound);Assert.Equal(["no-process"],o.Misses);
    }
    [Theory]
    [InlineData(TargetWindow.None)]
    [InlineData(TargetWindow.Shell)]
    [InlineData(TargetWindow.Ours)]
    public async Task ForegroundNotSettledWaitsForTheRealWindow(TargetWindow first)
    {
        var (o,_)=await Run(2,Kind(first),Kind(first),Real("notepad"));
        Assert.True(o.Bound);Assert.Equal("notepad",o.Last.Process);Assert.Equal(3,o.Attempts);
    }
    [Fact] public async Task GivesUpWithinAbout300Ms()
    {
        var (o,waits)=await Run(0,Kind(TargetWindow.Shell));
        Assert.False(o.Bound);Assert.Equal("no-target",o.Refused);
        Assert.Equal(DictationTarget.Waits.Sum(),waits.Sum());Assert.InRange(waits.Sum(),250,DictationTarget.BudgetMs);
        Assert.Equal(DictationTarget.Waits.Length+1,o.Attempts);
    }
    [Fact] public async Task SlowFocusQueriesStopAtTheBudget()
    {
        // A program whose accessibility answers in 120 ms per look: no more than about 300 ms of asking.
        var (o,_)=await Run(120,Real(why:"error:ElementNotAvailableException"));
        Assert.False(o.Bound);Assert.Equal("no-field",o.Refused);Assert.True(o.Attempts<=3,$"attempts {o.Attempts}");
    }
    [Theory]
    [InlineData("password")]
    [InlineData("elevated")]
    [InlineData("blocked-window")]
    [InlineData("locked-host")]
    public async Task FinalReasonsAreNotRetried(string why)
    {
        var (o,waits)=await Run(0,Real(why:why),Real());
        Assert.False(o.Bound);Assert.Equal(1,o.Attempts);Assert.Empty(waits);Assert.Equal("unsafe-target:"+why,o.Refused);
    }
    [Fact] public void NoticesSayWhatToDo()
    {
        Assert.Contains("ins Textfeld klicken",DictationTarget.Notice("no-target"));
        Assert.Contains("hineinklicken",DictationTarget.Notice("no-field"));
        Assert.Contains("Administrator",DictationTarget.Notice("unsafe-target:elevated"));
        Assert.Contains("Passwort",DictationTarget.Notice("unsafe-target:password"));
        Assert.DoesNotContain("kein Textfeld gewählt",DictationTarget.Notice("no-field"));
    }
    [Fact] public void ClassifiesShellOursAndReal()
    {
        Assert.Equal(TargetWindow.None,DictationTarget.Classify(IntPtr.Zero,"","",false,false));
        Assert.Equal(TargetWindow.Shell,DictationTarget.Classify(W,"Shell_TrayWnd","explorer",false,false));
        Assert.Equal(TargetWindow.Shell,DictationTarget.Classify(W,"WorkerW","explorer",false,false));
        Assert.Equal(TargetWindow.Shell,DictationTarget.Classify(W,"ForegroundStaging","explorer",false,false));
        Assert.Equal(TargetWindow.Shell,DictationTarget.Classify(W,"Windows.UI.Core.CoreWindow","StartMenuExperienceHost",false,false));
        Assert.Equal(TargetWindow.Shell,DictationTarget.Classify(W,"Windows.UI.Core.CoreWindow","SearchHost",false,false));
        Assert.Equal(TargetWindow.Ours,DictationTarget.Classify(W,"HwndWrapper[ScreenAgent;;1]","ScreenAgent",true,true));
        // Our own normal windows (dictionary, history) keep their fields as targets, as before.
        Assert.Equal(TargetWindow.Real,DictationTarget.Classify(W,"HwndWrapper[ScreenAgent;;2]","ScreenAgent",true,false));
        // Explorer's file windows are real programs; only its shell surfaces are not.
        Assert.Equal(TargetWindow.Real,DictationTarget.Classify(W,"CabinetWClass","explorer",false,false));
        Assert.Equal(TargetWindow.Real,DictationTarget.Classify(W,"Chrome_WidgetWin_1","Code - Insiders",false,false));
        Assert.Equal(TargetWindow.Real,DictationTarget.Classify(W,"Notepad","notepad",false,false));
    }
    // The elevation fence is only ever switched off by the selftest on an elevated CI runner, never by the app.
    [Fact] public void ElevationFenceSwitchIsSelftestOnly()
    {
        string root=AppContext.BaseDirectory;while(!File.Exists(Path.Combine(root,"README.md")))root=Path.GetDirectoryName(root)!;
        var setters=Directory.GetFiles(Path.Combine(root,"src"),"*.cs",SearchOption.AllDirectories).Where(f=>System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f),@"ElevatedPeersForTests\s*=[^=]")).Select(Path.GetFileName).ToArray();
        Assert.Equal(["DictationTargetSelftest.cs"],setters);
    }
}
