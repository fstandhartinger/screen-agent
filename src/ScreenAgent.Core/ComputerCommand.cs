using System.Text.Json;
using System.Text.RegularExpressions;
namespace ScreenAgent.Core;
// One command of the poll: {computer:{id:"c<16 hex>",run:"r<16 hex>",seq,op:"start|stop|action|confirm|beat",...}}.
public sealed record ComputerCommand(string Id,string Run,int Seq,string Op,string? Sid,string Goal,string[] ScopeWords,string Tool,JsonElement Args,string Hash,string Digits,string State);
public static class ComputerWire
{
    public static readonly string[] Ops=["start","stop","action","confirm","beat"];
    public const int MaxResultBytes=512*1024;
    static readonly Regex CommandId=new("^c[0-9a-f]{16}$",RegexOptions.Compiled),RunId=new("^r[0-9a-f]{16}$",RegexOptions.Compiled),HashRx=new("^[0-9a-f]{8,64}$",RegexOptions.Compiled);
    static string Str(JsonElement m,string key,int max)
    {
        if(!m.TryGetProperty(key,out var v)||v.ValueKind!=JsonValueKind.String)return "";
        var s=v.GetString()??"";
        return s.Length<=max?s:s[..max];
    }
    // null when the reply carries no computer command; a command that does not fit the contract is reported as such through `why` (and never executed).
    public static ComputerCommand? Parse(JsonElement reply,out string why)
    {
        why="";
        if(reply.ValueKind!=JsonValueKind.Object||!reply.TryGetProperty("computer",out var m)||m.ValueKind!=JsonValueKind.Object)return null;
        string id=Str(m,"id",40),run=Str(m,"run",40),op=Str(m,"op",16);
        if(!CommandId.IsMatch(id)){why="id";return null;}
        if(!RunId.IsMatch(run)){why="run";return null;}
        if(Array.IndexOf(Ops,op)<0){why="op";return null;}
        int seq=m.TryGetProperty("seq",out var s)&&s.ValueKind==JsonValueKind.Number&&s.TryGetInt32(out var sv)&&sv>=0?sv:-1;
        if(seq<0){why="seq";return null;}
        string[] words=m.TryGetProperty("scopeWords",out var sw)&&sw.ValueKind==JsonValueKind.Array?sw.EnumerateArray().Where(x=>x.ValueKind==JsonValueKind.String).Take(20).Select(x=>(x.GetString()??"")[..Math.Min(40,(x.GetString()??"").Length)]).ToArray():[];
        JsonElement args=default;
        if(m.TryGetProperty("args",out var a)&&a.ValueKind==JsonValueKind.Object)args=a.Clone();
        string hash=Str(m,"hash",80);
        if(op=="confirm"&&!HashRx.IsMatch(hash)){why="hash";return null;}
        string tool=Str(m,"tool",32);
        if(op=="action"&&(tool.Length==0||!tool.All(c=>char.IsAsciiLetterOrDigit(c)||c=='_'))){why="tool";return null;}
        return new(id,run,seq,op,m.TryGetProperty("sid",out _)?Str(m,"sid",80):null,Str(m,"goal",2000),words,tool,args,hash,Str(m,"digits",80),Str(m,"state",16));
    }
    public static string? ArgString(JsonElement args,string name,int max=2000)
    {
        if(args.ValueKind!=JsonValueKind.Object||!args.TryGetProperty(name,out var v))return null;
        if(v.ValueKind==JsonValueKind.String){var s=v.GetString()??"";return s.Length<=max?s:s[..max];}
        if(v.ValueKind==JsonValueKind.Number)return v.GetRawText();
        return null;
    }
    public static int? ArgInt(JsonElement args,string name)=>args.ValueKind==JsonValueKind.Object&&args.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetInt32(out var i)?i:null;
    public static bool ArgBool(JsonElement args,string name)=>args.ValueKind==JsonValueKind.Object&&args.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.True;
    public sealed record NeedsConfirm(string hash,string spoken);
    // {ok,code,class,rule,result?,needs_confirm?:{hash,spoken},run_state?}. result is cut so the whole JSON stays within 512 kB.
    public static Dictionary<string,object?> Result(bool ok,string code,string? cls,string rule,string? result=null,NeedsConfirm? ask=null,string? runState=null)
    {
        var d=new Dictionary<string,object?>{["ok"]=ok,["code"]=code,["class"]=cls,["rule"]=rule};
        if(result is not null)d["result"]=Fit(result);
        if(ask is not null)d["needs_confirm"]=new{hash=ask.hash,spoken=ask.spoken};
        if(runState is not null)d["run_state"]=runState;
        return d;
    }
    public static string Fit(string result)
    {
        const int budget=MaxResultBytes-2048;
        if(System.Text.Encoding.UTF8.GetByteCount(result)<=budget)return result;
        // JSON escaping can double the size: cut by characters until the escaped form fits.
        int n=Math.Min(result.Length,budget/2);
        while(n>0&&JsonSerializer.SerializeToUtf8Bytes(result[..n]).Length>budget)n/=2;
        if(n>0&&char.IsHighSurrogate(result[n-1]))n--;
        return result[..n];
    }
}
// The CDP methods this app can call. A compiled list: the gateway never names a method and no model text reaches Send.
public static class CdpMethods
{
    public static readonly HashSet<string> Allowed=new(StringComparer.Ordinal)
    {
        "Browser.getVersion","Target.getTargets","Target.createTarget","Target.attachToTarget","Target.detachFromTarget","Target.activateTarget","Target.closeTarget","Target.setDiscoverTargets",
        "Page.enable","Page.navigate","Page.getFrameTree","Page.createIsolatedWorld","Page.captureScreenshot","Page.handleJavaScriptDialog","Page.bringToFront",
        "DOM.enable","DOM.getDocument","DOM.getNodeForLocation","DOM.describeNode","DOM.resolveNode","DOM.getBoxModel","DOM.scrollIntoViewIfNeeded","DOM.focus",
        "Accessibility.enable","Accessibility.getFullAXTree",
        "Input.dispatchMouseEvent","Input.dispatchKeyEvent","Input.insertText",
        "Runtime.callFunctionOn","Runtime.releaseObject"
    };
    // Named so the tests (and a reader) see what is deliberately not there. Runtime.evaluate is absent: page reads run through callFunctionOn in an isolated world with compiled functions.
    public static readonly string[] Never=
    [
        "Runtime.evaluate","Network.getCookies","Network.getAllCookies","Network.setCookie","Storage.getCookies","Storage.clearDataForOrigin","Browser.grantPermissions","Browser.setDownloadBehavior","Page.setDownloadBehavior",
        "Target.exposeDevToolsProtocol","DOM.setFileInputFiles","Page.setInterceptFileChooserDialog","Fetch.fulfillRequest","Debugger.enable","Browser.close","Target.setAutoAttach","Emulation.setUserAgentOverride","IndexedDB.requestData","DOMStorage.getDOMStorageItems"
    ];
    public static bool Ok(string method)=>Allowed.Contains(method);
}
// One element of the UI tree as the dialog matcher sees it (UIA or a fake tree in the tests).
public sealed record DialogNode(string Role,string Name,bool InDocument,string Process,string ImagePath,int Depth,int Parent);
public sealed record DialogMatch(bool Ok,string Reason,int ButtonIndex);
// Chrome asks "Remote-Fehlerbehebung zulassen?" for each new debugging connection. We click Zulassen only when it is exactly that dialog and nothing else:
// exactly one heading with exactly that text, in native browser UI (no Document ancestor: page content lives in chrome.exe too), an image path equal to the owner of the debug port.
// UNPROVEN on his Chrome: the real dialog's UIA shape is assumed (a group with a heading and buttons Abbrechen/Zulassen); the matcher is tested against fake trees.
public static class AllowDialog
{
    public static readonly string[] Headings=["Remote-Fehlerbehebung zulassen?","Allow remote debugging?"];
    public static readonly string[] Allow=["Zulassen","Allow"],Cancel=["Abbrechen","Cancel"];
    public static List<int> Headers(IReadOnlyList<DialogNode> tree)
    {
        var hits=new List<int>();
        for(int i=0;i<tree.Count;i++)if(tree[i].Role is "Text" or "Heading" or "Group" or "Pane"&&Array.IndexOf(Headings,tree[i].Name)>=0)hits.Add(i);
        return hits;
    }
    public static DialogMatch Find(IReadOnlyList<DialogNode> tree,string portOwnerImage,string[]? buttonNames=null)
    {
        buttonNames??=Allow;
        var heads=Headers(tree);
        if(heads.Count==0)return new(false,"no-dialog",-1);
        if(heads.Count>1)return new(false,"several",-1);
        var h=tree[heads[0]];
        if(h.InDocument)return new(false,"in-page",-1);
        if(!string.Equals(h.ImagePath[(h.ImagePath.LastIndexOfAny(['\\','/'])+1)..],"chrome.exe",StringComparison.OrdinalIgnoreCase)||!string.Equals(h.ImagePath,portOwnerImage,StringComparison.OrdinalIgnoreCase)||portOwnerImage.Length==0)return new(false,"wrong-process",-1);
        // The button must sit under the same dialog container as the heading, in native UI as well.
        int root=tree[heads[0]].Parent>=0?tree[heads[0]].Parent:heads[0];
        var buttons=new List<int>();
        for(int i=0;i<tree.Count;i++)if(tree[i].Role=="Button"&&Array.IndexOf(buttonNames,tree[i].Name)>=0&&!tree[i].InDocument&&Under(tree,i,root)&&tree[i].ImagePath==h.ImagePath)buttons.Add(i);
        return buttons.Count==1?new(true,"ok",buttons[0]):new(false,buttons.Count==0?"no-button":"several-buttons",-1);
    }
    static bool Under(IReadOnlyList<DialogNode> tree,int i,int ancestor)
    {
        for(int g=0;g<64&&i>=0&&i<tree.Count;g++){if(i==ancestor)return true;i=tree[i].Parent;}
        return false;
    }
}
