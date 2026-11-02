namespace ScreenAgent.Core;
public enum Key { Ctrl, Alt, Win, Shift, Caps, P, Space, Mute, Other }
public enum ActionKind { SessionToggle, PauseToggle, MuteToggle, DictationStart, DictationStop, DictationDiscard, DrawStart, DrawStop, CapsTap, DictationBind, DrawBind }
public readonly record struct KeyEvent(Key Key, bool Down, long Ms);
// Default layout: Ctrl+Win+Shift session (any order, +P pause, +M mute), Alt+Win hold = dictate, Alt+Win+Shift = dictation on/off, Alt+Win+Y hands-free.
public sealed class Hotkeys
{
    readonly HashSet<Key> down = [];
    readonly HotkeyLayout layout;
    readonly ChordTracker chords;
    long? dictSince, capsSince;
    Chord armed;
    bool dictating, drawing, capsOwned, handsFree, bound;
    public Hotkeys(HotkeyLayout? layout=null){this.layout=layout??Default;chords=new(this.layout);}
    public static readonly HotkeyLayout Default=new([Key.Ctrl,Key.Win,Key.Shift],[Key.Alt,Key.Win],[Key.Alt,Key.Win,Key.Shift],[Key.Ctrl,Key.Win,Key.Shift],[Key.Alt,Key.Win],[Key.Ctrl,Key.Win,Key.Shift]);
    public bool Session { get; private set; }
    public bool Paused { get; private set; }
    public bool Dictating => dictating;
    public bool Drawing => drawing;
    public bool SwallowCaps => capsOwned || Session;
    // The mute key is eaten (down and up) only while it would act: a session is running and its chord is held.
    public bool MuteLive => Session && !Paused;
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
        List<ActionKind> result=[ActionKind.PauseToggle];Paused=on;armed=Chord.None;
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
        if (dictSince is long d && ms-d >= 250 && !dictating && chords.Is(layout.Dictation) && !chords.Tainted && !Paused)
        { dictating = true; result.Add(ActionKind.DictationStart); }
        if (capsSince is long c && ms-c >= 200 && !drawing && Session && !Paused)
        { drawing = true; result.Add(ActionKind.DrawStart); }
        return result;
    }
    public IReadOnlyList<ActionKind> Feed(KeyEvent e)
    {
        // Resolve thresholds before key-up so 200/250 ms boundaries are exact.
        List<ActionKind> result = [..Tick(e.Ms)];
        if (e.Key != Key.Other) // typed keys are never collapsed: each one must taint a chord in progress
        {
            if (e.Down && !down.Add(e.Key)) return result; // auto repeat
            if (!e.Down) down.Remove(e.Key);
        }
        var completed = chords.Feed(e.Key, e.Down);
        if (e.Down && e.Key == Key.P && chords.Is(layout.Pause))
        {
            Paused = !Paused; armed = Chord.None; result.Add(ActionKind.PauseToggle);
            if(dictating) { dictating=false;handsFree=false; result.Add(ActionKind.DictationDiscard); }
            if(drawing) { drawing=false; result.Add(ActionKind.DrawStop); }
        }
        if (e.Down && e.Key == Key.Mute && chords.Is(layout.Mute) && MuteLive) result.Add(ActionKind.MuteToggle);
        if(e.Down&&e.Key==Key.Space&&chords.Is(layout.HandsFree)&&!Paused)
        {
            dictSince=null;handsFree=!handsFree;
            if(handsFree&&!dictating){dictating=true;result.Add(ActionKind.DictationBind);result.Add(ActionKind.DictationStart);}
            else if(!handsFree&&dictating){dictating=false;result.Add(ActionKind.DictationStop);}
        }
        switch (completed)
        {
            case Chord.Dictation when !Paused && !chords.Tainted: dictSince = e.Ms; bound = true; result.Add(ActionKind.DictationBind); break;
            case Chord.Session: dictSince = null; armed = Chord.Session; break;
            case Chord.Toggle:
                dictSince = null;
                // Shift added to a running hold-dictation latches it instead of stopping it; the next press ends it.
                if (dictating && !handsFree) handsFree = true; else if (!Paused) armed = Chord.Toggle;
                break;
        }
        if (!e.Down && HotkeyLayout.IsMod(e.Key))
        {
            // The chord fires on its first release, so a fourth key (P, M) pressed while it is held can claim it instead.
            if (armed != Chord.None && !chords.Tainted) result.AddRange(Fire(armed));
            armed = Chord.None;
        }
        if (chords.Tainted) armed = Chord.None;
        if (chords.Mods.Count == 0) bound = false;
        if (!chords.Is(layout.Dictation)) dictSince = null;
        if (dictating && !handsFree && !chords.Is(layout.Dictation))
        {
            // Released a dictation key: transcribe. Another modifier joined (not the latch chord): throw it away.
            dictating = false;
            result.Add(chords.Mods.Count > layout.Dictation.Length && layout.Dictation.All(chords.Mods.Contains) ? ActionKind.DictationDiscard : ActionKind.DictationStop);
        }
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
    IEnumerable<ActionKind> Fire(Chord chord)
    {
        if (chord == Chord.Session)
        {
            Session=!Session; Paused=false; yield return ActionKind.SessionToggle;
            if(drawing) { drawing=false; yield return ActionKind.DrawStop; }
        }
        else if (chord == Chord.Toggle && !Paused)
        {
            handsFree = !dictating;
            if (!dictating) { dictating = true; if (!bound) yield return ActionKind.DictationBind; bound = true; yield return ActionKind.DictationStart; }
            else { dictating = false; yield return ActionKind.DictationStop; }
        }
    }
}
