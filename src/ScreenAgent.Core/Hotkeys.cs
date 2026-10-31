namespace ScreenAgent.Core;
public enum Key { Ctrl, Win, Shift, Caps, P, Space, Other }
public enum ActionKind { SessionToggle, PauseToggle, DictationStart, DictationStop, DictationDiscard, DrawStart, DrawStop, CapsTap, MaskWin }
public readonly record struct KeyEvent(Key Key, bool Down, long Ms);
public sealed class Hotkeys
{
    readonly HashSet<Key> down = [];
    long? dictSince, capsSince;
    bool cleanChord, dirtyChord, dictating, drawing, capsOwned;
    public bool Session { get; private set; }
    public bool Paused { get; private set; }
    public bool SwallowCaps => capsOwned || Session;
    public bool Masking => down.Contains(Key.Win) && down.Contains(Key.Ctrl);
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
        if(e.Down && (e.Key is Key.Other or Key.P or Key.Space)) dirtyChord = true;
        if (e.Down && e.Key == Key.P && three)
        {
            Paused = !Paused; cleanChord = false; result.Add(ActionKind.PauseToggle);
            if(dictating) { dictating=false; result.Add(ActionKind.DictationDiscard); }
            if(drawing) { drawing=false; result.Add(ActionKind.DrawStop); }
        }
        if (e.Down && ctrlWin && (e.Key is Key.Ctrl or Key.Win)) result.Add(ActionKind.MaskWin);
        if (three)
        {
            if (!dirtyChord) cleanChord = true;
            dictSince = null;
            if(dictating) { dictating=false; result.Add(ActionKind.DictationDiscard); }
        }
        else if(ctrlWin && !down.Contains(Key.Shift) && dictSince is null && !cleanChord && !dirtyChord && !Paused)
            dictSince = e.Ms;
        if(!ctrlWin)
        {
            dictSince=null;
            if(dictating) { dictating=false; result.Add(ActionKind.DictationStop); }
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
        if(e.Key==Key.Caps)
        {
            if(e.Down && Session) { capsSince=e.Ms; capsOwned=true; }
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
