using ScreenAgent.Core;
namespace ScreenAgent;
// Chrome half of the general computer control: his running signed-in Chrome over CDP (auto-accepted allow prompt), own tabs by default. Contract: CONTRACT-V2.
internal sealed class ChromeTools:IFreeToolset
{
    readonly FreeHost host;
    public ChromeTools(FreeHost host){this.host=host;}
    public IReadOnlyDictionary<string,bool> Tools {get;}=new Dictionary<string,bool>();
    public FreeResult Run(FreeCall call)=>FreeResult.Fail("unknown-tool","Unbekanntes Werkzeug.");
    public void RunStarted(ComputerRun run){}
    public void RunEnded(ComputerRun run){}
    public void Dispose(){}
}
