using System.Text.Json;
namespace ScreenAgent.Core;
// One line of the live activity overlay, as the Desk page sends it: {"type":"activity","id","phase","label","tool","ms","reason"}.
public sealed record ActivityEvent(string Id,string Phase,string Label,string Tool,long Ms,string Reason);
public enum RowKind { Running, Done, Fail }
public readonly record struct ActivityRowView(string Id,string Text,RowKind Kind,double Opacity);
public static class ActivityParse
{
    public static readonly string[] Phases=["thinking","heard","start","done","fail"];
    static string Str(JsonElement m,string key,int max)
    {
        if(!m.TryGetProperty(key,out var v)||v.ValueKind!=JsonValueKind.String)return "";
        var s=new string((v.GetString()??"").Where(c=>!char.IsControl(c)).ToArray()).Trim();
        return s.Length<=max?s:s[..max];
    }
    // false: the reason says why the message was dropped (logged as activity.invalid; the page's text itself is not).
    public static bool TryParse(JsonElement m,out ActivityEvent? ev,out string reason)
    {
        ev=null;reason="";
        string phase=Str(m,"phase",16).ToLowerInvariant();
        if(!Phases.Contains(phase)){reason="phase";return false;}
        string id=Str(m,"id",64);
        if(id.Length==0&&phase is "start" or "done" or "fail"){reason="id";return false;}
        long ms=m.TryGetProperty("ms",out var n)&&n.ValueKind==JsonValueKind.Number&&n.TryGetInt64(out var v)?Math.Clamp(v,0,3_600_000):0;
        ev=new(id,phase,Str(m,"label",80),Str(m,"tool",64),ms,Str(m,"reason",120));
        return true;
    }
}
// The rows of the overlay as a state machine over (event, time): pure, so the order of events decides what is shown.
// thinking/heard: one transient row, replaced by the first tool. start: spinner row. done: tick for DoneMs, then fades. fail: orange for FailMs with the reason.
public sealed class ActivityBoard(int maxRows=4,int doneMs=2500,int failMs=8000,int fadeMs=400,int transientMs=12000,int staleMs=90000)
{
    public const string TransientId="\u0001transient";
    sealed class Row{public string Id="",Label="",Reason="";public RowKind Kind;public bool Transient;public long Since,Ms;}
    readonly List<Row> rows=[];
    public int MaxRows=>maxRows;
    // true when this event made a row appear that was not on screen before (counted as activity.shown).
    public bool Apply(ActivityEvent e,long now)
    {
        Prune(now);
        if(e.Phase is "thinking" or "heard")
        {
            var t=rows.Find(r=>r.Transient);bool fresh=t is null;
            if(t is null){t=new(){Id=TransientId,Transient=true};rows.Add(t);}
            t.Label=e.Label.Length>0?e.Label:e.Phase=="heard"?"Gehört …":"Denkt nach …";t.Since=now;t.Kind=RowKind.Running;
            Trim();return fresh;
        }
        rows.RemoveAll(r=>r.Transient);
        var row=rows.Find(r=>r.Id==e.Id);bool added=row is null;
        if(row is null){row=new(){Id=e.Id};rows.Add(row);}
        if(e.Label.Length>0)row.Label=e.Label;else if(row.Label.Length==0)row.Label=e.Tool.Length>0?e.Tool:"…";
        row.Since=now;
        switch(e.Phase)
        {
            case "start":row.Kind=RowKind.Running;row.Reason="";row.Ms=0;break;
            case "done":row.Kind=RowKind.Done;row.Ms=e.Ms;break;
            default:row.Kind=RowKind.Fail;row.Reason=e.Reason;row.Ms=e.Ms;break;
        }
        Trim(row);return added;
    }
    // More than maxRows: the transient row goes first, then the oldest finished row, then the oldest running one; never the row just touched.
    void Trim(Row? keep=null)
    {
        while(rows.Count>maxRows)
        {
            var victim=rows.Find(r=>r.Transient&&r!=keep)??rows.Find(r=>r.Kind!=RowKind.Running&&r!=keep)??rows.Find(r=>r!=keep);
            if(victim is null)break;rows.Remove(victim);
        }
    }
    void Prune(long now)=>rows.RemoveAll(r=>now-r.Since>=r.Kind switch{RowKind.Done=>doneMs+fadeMs,RowKind.Fail=>failMs+fadeMs,_=>r.Transient?transientMs:staleMs});
    public bool Any(long now){Prune(now);return rows.Count>0;}
    public IReadOnlyList<ActivityRowView> View(long now)
    {
        Prune(now);
        return rows.Select(r =>
        {
            long age=now-r.Since;int hold=r.Kind==RowKind.Done?doneMs:r.Kind==RowKind.Fail?failMs:int.MaxValue;
            double opacity=age<=hold?1:Math.Max(0,1-(age-hold)/(double)fadeMs);
            string text=r.Kind switch
            {
                RowKind.Done=>r.Label+(r.Ms>0?$" · {r.Ms} ms":""),
                RowKind.Fail=>r.Label+" · "+(r.Reason.Length>0?r.Reason:"fehlgeschlagen"),
                _=>r.Label
            };
            return new ActivityRowView(r.Id,text,r.Kind,opacity);
        }).ToList();
    }
    public void Clear()=>rows.Clear();
}
// The steps the app does itself (typing the Main Agent prompt) become rows from its own trace events, so the seconds before the prompt appears are visibly busy.
public static class ActivityLocal
{
    public const string MainPrompt="local:main-prompt";
    public static ActivityEvent? FromTrace(string type,IReadOnlyDictionary<string,object?>? data)
    {
        string Step(string label)=>"Main Agent · "+label;
        ActivityEvent Start(string label)=>new(MainPrompt,"start",Step(label),"",0,"");
        switch(type)
        {
            case "main.prompt.received":return Start("Prompt kommt an");
            case "main.prompt.start":return Start("suche das VS-Code-Fenster");
            case "main.prompt.route":return Start("Eingabefeld im Fokus");
            case "main.prompt.step":
                return data is not null&&data.TryGetValue("step",out var s)?s?.ToString() switch
                {
                    "window"=>Start("Fenster gefunden"),
                    "focus"=>Start("Fenster im Vordergrund"),
                    "typing"=>Start("tippe den Prompt"),
                    _=>null
                }:null;
            case "main.prompt.done":
                string code=data is not null&&data.TryGetValue("code",out var c)?c?.ToString()??"":"";
                long ms=data is not null&&data.TryGetValue("ms",out var m)&&m is not null&&long.TryParse(Convert.ToString(m,System.Globalization.CultureInfo.InvariantCulture),out var v)?v:0;
                return PromptCodes.Ok(code)?new(MainPrompt,"done",Step("Prompt eingetippt"),"",ms,""):new(MainPrompt,"fail",Step("nicht eingetippt"),"",ms,PromptCodes.Reason(code));
            default:return null;
        }
    }
}
