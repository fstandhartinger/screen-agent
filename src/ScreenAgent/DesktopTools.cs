using ScreenAgent.Core;
namespace ScreenAgent;
// Desktop half of the general computer control: screenshots and frames, mouse, keyboard, windows, UI Automation, clipboard, shell, Python, files. Contract: CONTRACT-V2.
internal sealed class DesktopTools:IFreeToolset
{
    readonly FreeHost host;
    public DesktopTools(FreeHost host){this.host=host;}
    public IReadOnlyDictionary<string,bool> Tools {get;}=new Dictionary<string,bool>();
    public FreeResult Run(FreeCall call)=>FreeResult.Fail("unknown-tool","Unbekanntes Werkzeug.");
    public void RunStarted(ComputerRun run){}
    public void RunEnded(ComputerRun run){}
    public void Dispose(){}
}
