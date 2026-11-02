namespace ScreenAgent.Core;
public enum Chord { None, Session, Dictation, Toggle }
// Modifier sets per chord (physical roles). Session and Toggle are three modifiers, Dictation two; Pause, HandsFree and Mute add one action key.
public sealed record HotkeyLayout(Key[] Session,Key[] Dictation,Key[] Toggle,Key[] Pause,Key[] HandsFree,Key[] Mute)
{
    public static bool IsMod(Key k)=>k is Key.Ctrl or Key.Alt or Key.Win or Key.Shift;
    public static bool Same(IReadOnlyCollection<Key> held,Key[] set)=>held.Count==set.Length&&set.All(held.Contains);
    public Chord Match(IReadOnlyCollection<Key> held)=>Same(held,Session)?Chord.Session:Same(held,Dictation)?Chord.Dictation:Same(held,Toggle)?Chord.Toggle:Chord.None;
    public Key[] Set(Chord chord)=>chord switch{Chord.Session=>Session,Chord.Dictation=>Dictation,Chord.Toggle=>Toggle,_=>[]};
}
// Recognises "the modifiers now held are exactly chord X", at most once per press and only when they were pressed up to it:
// any release or any non-modifier key in between taints the press, so Ctrl+Win (Wispr) followed by Alt, or a typed shortcut, never completes a chord.
public sealed class ChordTracker(HotkeyLayout layout)
{
    readonly HashSet<Key> mods=[];
    bool released,tainted,masked;
    public IReadOnlyCollection<Key> Mods=>mods;
    public bool Tainted=>tainted;
    // True on the Feed call that completed a chord whose release would otherwise open Start (Win) or the menu bar (Alt); once per press.
    public bool MaskNow {get;private set;}
    public bool Is(Key[] set)=>HotkeyLayout.Same(mods,set);
    public Chord Feed(Key key,bool down)
    {
        MaskNow=false;
        if(!HotkeyLayout.IsMod(key)){if(down&&mods.Count>0)tainted=true;return Chord.None;}
        if(down){if(!mods.Add(key))return Chord.None;}
        else
        {
            if(!mods.Remove(key))return Chord.None;
            released=true;
            if(mods.Count==0){released=false;tainted=false;masked=false;}
            return Chord.None;
        }
        if(tainted||released)return Chord.None;
        var chord=layout.Match(mods);
        if(chord!=Chord.None&&!masked&&(mods.Contains(Key.Win)||mods.Contains(Key.Alt))){masked=true;MaskNow=true;}
        return chord;
    }
}
