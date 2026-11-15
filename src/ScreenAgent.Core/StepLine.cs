namespace ScreenAgent.Core;
// A compact view independent of the canvas and the optional multi-row overlay.
public sealed class StepLine
{
    readonly Dictionary<string,string> running=[];
    string label="",last="",run="";
    int step;
    bool computer;
    long changed;
    static string Clean(object? value,int max=180)
    {
        string s=new((value?.ToString()??"").Where(c=>!char.IsControl(c)).ToArray());
        return s.Length<=max?s:s[..max];
    }
    public void Activity(ActivityNote note,long now)
    {
        if(!computer)last="";
        string text=Clean(note.Label);
        if(text.Length==0)text=running.TryGetValue(note.Id,out var saved)?saved:Clean(note.Tool);
        if(note.Phase=="start")running[note.Id]=text;
        else if(note.Phase is "done" or "fail")
        {
            if(text.Length==0&&running.TryGetValue(note.Id,out var prior))text=prior;
            running.Remove(note.Id);
        }
        label=text.Length>0?text:note.Phase=="heard"?"Gehört …":note.Phase=="thinking"?"Denkt nach …":label;
        if(running.Count>0)label=running.Values.Last();
        changed=now;
    }
    public void Computer(string type,IReadOnlyDictionary<string,object?> data,long now)
    {
        string Get(string key)=>data.TryGetValue(key,out var v)?Clean(v):"";
        string id=Get("run");
        if(type=="computer.run.start")
        {computer=true;run=id;step=0;last="Computer-Steuerung startet";}
        else
        {
            if(!computer||id!=run)return;
            switch(type)
            {
                case "computer.action":
                    if(int.TryParse(Get("seq"),out int n))step=Math.Max(step,n);
                    bool done=data.TryGetValue("ok",out var ok)&&ok is bool;
                    string action=Get("tool"),target=Get("target");
                    last=action+(target.Length>0?" · "+target:"")+(done?(ok is true?" · fertig":" · fehlgeschlagen · "+Get("reason")):" · läuft");
                    break;
                case "computer.run.stop":computer=false;last="Computer-Steuerung beendet · "+Get("why");break;
                case "computer.handson":last="Du bist dran · wartet auf Fortsetzen";break;
                case "computer.confirm.ask":last="Wartet auf deine Bestätigung";break;
                case "computer.resume":last="Computer-Steuerung läuft weiter";break;
                case "computer.error":last="Computer-Steuerung · Fehler · "+Get("type");break;
                default:return;
            }
        }
        changed=now;
    }
    public (string Text,double Opacity) View(long now,bool voice)
    {
        if(!voice)return ("",0);
        bool busy=computer||running.Count>0;
        long age=now-changed;
        if(!busy&&age>=4000)return ("",0);
        string text=label;
        if(last.Length>0)text=(text.Length>0?text+" · ":"")+$"Schritt {step} · {last}";
        return (text,busy||age<=3600?1:Math.Max(0,(4000-age)/400.0));
    }
    public void Clear(){running.Clear();label=last=run="";computer=false;step=0;changed=0;}
}
