namespace ScreenAgent.Core;
// Foreign = a modifier that is not part of our chord (for example Ctrl when the chord is Alt+Win): somebody else's shortcut.
public enum Key { Ctrl, Win, Shift, Caps, P, Space, Other, Foreign }
public enum ActionKind { SessionToggle, PauseToggle, DictationStart, DictationStop, DictationDiscard, DrawStart, DrawStop, CapsTap, MaskWin, DictationBind, DrawBind }
public readonly record struct KeyEvent(Key Key, bool Down, long Ms);
public sealed class Hotkeys
{
    readonly HashSet<Key> down = [];
    long? dictSince, capsSince;
    bool cleanChord, dirtyChord, dictating, drawing, capsOwned, handsFree;
    public bool Session { get; private set; }
    public bool Paused { get; private set; }
    public bool Dictating => dictating;
    public bool Drawing => drawing;
    public bool SwallowCaps => capsOwned || Session;
    public bool Masking => down.Contains(Key.Win) && down.Contains(Key.Ctrl);
    // Tray menu and tests flip the session without a chord; same effects as the chord's SessionToggle.
    public IReadOnlyList<ActionKind> SetSession(bool on)
    {
        if(Session==on)return [];
        List<ActionKind> result=[];Session=on;Paused=false;result.Add(ActionKind.SessionToggle);
        if(drawing){drawing=false;result.Add(ActionKind.DrawStop);}
        return result;
    }
    // Tray menu: pause or resume without the chord, same effects as the chord's PauseToggle.
    public IReadOnlyList<ActionKind> SetPaused(bool on)
    {
        if(Paused==on)return [];
        List<ActionKind> result=[ActionKind.PauseToggle];Paused=on;cleanChord=false;
        if(on)
        {
            if(dictating){dictating=false;handsFree=false;result.Add(ActionKind.DictationDiscard);}
            if(drawing){drawing=false;result.Add(ActionKind.DrawStop);}
        }
        return result;
    }
    public IReadOnlyList<ActionKind> Tick(long ms)
    {
        List<ActionKind> result = [];
        if (dictSince is long d && ms-d >= 250 && !dictating && !down.Contains(Key.Shift) && !dirtyChord)
        { dictating = true; result.Add(ActionKind.DictationStart); }
        if (capsSince is long c && ms-c >= 200 && !drawing && Session && !Paused)
        { drawing = true; result.Add(ActionKind.DrawStart); }
        return result;
    }
    public IReadOnlyList<ActionKind> Feed(KeyEvent e)
    {
        // Resolve thresholds before key-up so 200/250 ms boundaries are exact.
        List<ActionKind> result = [..Tick(e.Ms)];
        if (e.Down && !down.Add(e.Key)) return result; // auto repeat
        if (!e.Down) down.Remove(e.Key);
        bool ctrlWin = down.Contains(Key.Ctrl) && down.Contains(Key.Win);
        bool three = ctrlWin && down.Contains(Key.Shift);
        if(e.Down && (e.Key is Key.Other or Key.P or Key.Space or Key.Caps or Key.Foreign)) dirtyChord = true;
        if (e.Down && e.Key == Key.P && three)
        {
            Paused = !Paused; cleanChord = false; result.Add(ActionKind.PauseToggle);
            if(dictating) { dictating=false;handsFree=false; result.Add(ActionKind.DictationDiscard); }
            if(drawing) { drawing=false; result.Add(ActionKind.DrawStop); }
        }
        if (e.Down && ctrlWin && (e.Key is Key.Ctrl or Key.Win)) result.Add(ActionKind.MaskWin);
        if(e.Down&&e.Key==Key.Space&&ctrlWin&&!down.Contains(Key.Shift)&&!Paused)
        {
            dictSince=null;handsFree=!handsFree;
            if(handsFree&&!dictating){dictating=true;result.Add(ActionKind.DictationBind);result.Add(ActionKind.DictationStart);}
            else if(!handsFree&&dictating){dictating=false;result.Add(ActionKind.DictationStop);}
        }
        if (three)
        {
            if (!dirtyChord) cleanChord = true;
            dictSince = null;
            if(dictating) { dictating=false;handsFree=false; result.Add(ActionKind.DictationDiscard); }
        }
        else if(ctrlWin && !down.Contains(Key.Shift) && dictSince is null && !cleanChord && !dirtyChord && !Paused)
            {dictSince = e.Ms;result.Add(ActionKind.DictationBind);}
        if(!ctrlWin)
        {
            dictSince=null;
            if(dictating&&!handsFree) { dictating=false; result.Add(ActionKind.DictationStop); }
        }
        if (!down.Contains(Key.Ctrl) && !down.Contains(Key.Win) && !down.Contains(Key.Shift))
        {
            if(cleanChord && !dirtyChord)
            {
                Session=!Session; Paused=false; result.Add(ActionKind.SessionToggle);
                if(drawing) { drawing=false; result.Add(ActionKind.DrawStop); }
            }
            cleanChord=false; dirtyChord=false;
        }
        // Another tool's modifier (Ctrl while our chord is Alt+Win, e.g. Wispr's Ctrl+Win+Alt) held: the chord is not ours.
        if(down.Contains(Key.Foreign)) { dirtyChord = true; dictSince = null; }
        if(e.Key==Key.Caps)
        {
            if(e.Down && Session) { capsSince=e.Ms; capsOwned=true;result.Add(ActionKind.DrawBind); }
            if(!e.Down && capsOwned)
            {
                if(drawing) { drawing=false; result.Add(ActionKind.DrawStop); }
                else if(capsSince is long t && e.Ms-t <200) result.Add(ActionKind.CapsTap);
                capsSince=null; capsOwned=false;
            }
        }
        return result;
    }
}
