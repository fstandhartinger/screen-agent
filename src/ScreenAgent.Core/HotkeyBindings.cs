namespace ScreenAgent.Core;
// Maps physical keys to roles for the state machine. Configuration names modifiers (Ctrl, Alt, Win, Shift) and
// four distinct action keys (draw, pause, hands-free, mute).
public sealed class HotkeyBindings
{
    public int DrawVk {get;}
    public int MuteVk {get;}
    public int CanvasVk {get;}
    public HotkeyLayout Layout {get;}
    readonly int pauseVk,spaceVk;
    readonly HashSet<int> held=[];
    readonly ChordTracker tracker;
    public static readonly string[] Required=["session","dictation","dictationToggle","mute","pause","handsFree","draw","canvas"];
    public HotkeyBindings(Dictionary<string,string> keys)
    {
        static string[] Parts(string value)=>value.Split('+',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        foreach(var k in Required)if(!keys.ContainsKey(k))throw new InvalidDataException("Hotkey missing: "+k);
        static Key Mod(string s)=>s switch{"Ctrl"=>Key.Ctrl,"Alt"=>Key.Alt,"Win"=>Key.Win,"Shift"=>Key.Shift,_=>throw new InvalidDataException("Unknown modifier: "+s)};
        static Key[] Mods(string name,string value,int count,bool shift=true)
        {
            var parts=Parts(value);
            if(parts.Length!=count||parts.Distinct().Count()!=count||parts.Any(p=>p is not ("Ctrl" or "Alt" or "Win" or "Shift")))throw new InvalidDataException($"{name} needs {count} distinct modifiers from Ctrl, Alt, Win, Shift");
            if(!shift&&parts.Contains("Shift"))throw new InvalidDataException($"{name} cannot use Shift");
            return parts.Select(Mod).ToArray();
        }
        // Action chords: two to four modifiers plus one action key.
        static (Key[] Mods,string Key) WithKey(string name,string value)
        {
            var parts=Parts(value);
            if(parts.Length<3||parts.Length>5||parts[..^1].Any(p=>p is not ("Ctrl" or "Alt" or "Win" or "Shift"))||parts[..^1].Distinct().Count()!=parts.Length-1)throw new InvalidDataException($"{name} needs two to four modifiers plus one key");
            return (parts[..^1].Select(Mod).ToArray(),parts[^1]);
        }
        var session=Mods("Session",keys["session"],3);var dictation=Mods("Dictation",keys["dictation"],2,false);var toggle=Mods("Dictation toggle",keys["dictationToggle"],3);
        if(HotkeyLayout.Same(session,toggle))throw new InvalidDataException("Session and dictation toggle need different modifiers");
        var pause=WithKey("Pause",keys["pause"]);var hands=WithKey("Hands-free",keys["handsFree"]);var mute=WithKey("Mute",keys["mute"]);var canvas=WithKey("Canvas",keys["canvas"]);
        pauseVk=VirtualKey(pause.Key);spaceVk=VirtualKey(hands.Key);MuteVk=VirtualKey(mute.Key);DrawVk=VirtualKey(keys["draw"]);CanvasVk=VirtualKey(canvas.Key);
        if(new[]{pauseVk,spaceVk,MuteVk,DrawVk,CanvasVk}.Distinct().Count()!=5)throw new InvalidDataException("Draw, pause, hands-free, mute and canvas keys must differ");
        Layout=new(session,dictation,toggle,pause.Mods,hands.Mods,mute.Mods,canvas.Mods);tracker=new(Layout);
    }
    static int VirtualKey(string key)=>key switch
    {
        "Caps"=>0x14,"Space"=>0x20,
        _ when key.Length==1&&key[0]>='A'&&key[0]<='Z'=>key[0],
        _ when key.StartsWith('F')&&int.TryParse(key[1..],out int n)&&n is >=1 and <=12=>0x6f+n,
        _=>throw new InvalidDataException("Use Caps, Space, A-Z or F1-F12 for action keys")
    };
    static Key? Modifier(int vk)=>vk switch{0x11 or 0xa2 or 0xa3=>Key.Ctrl,0x12 or 0xa4 or 0xa5=>Key.Alt,0x5b or 0x5c=>Key.Win,0x10 or 0xa0 or 0xa1=>Key.Shift,_=>null};
    public Key Role(int vk)=>Modifier(vk) ?? (vk==DrawVk?Key.Caps:vk==pauseVk?Key.P:vk==spaceVk?Key.Space:vk==MuteVk?Key.Mute:vk==CanvasVk?Key.Canvas:Key.Other);
    // Ctrl+Win is Wispr Flow's dictation chord; binding it as a hold chord here makes both tools fight over the same keys.
    public bool CollidesWithWispr=>HotkeyLayout.Same(Layout.Dictation,[Key.Ctrl,Key.Win]);
    // The mute key is swallowed only while its chord is held, so Ctrl+Shift+M still works for everybody else.
    public bool CanvasChordHeld=>tracker.Is(Layout.Canvas)&&!tracker.Tainted;
    public bool MuteChordHeld=>tracker.Is(Layout.Mute)&&!tracker.Tainted;
    public bool Is(Key[] set)=>tracker.Is(set);
    // True for exactly one Feed call per press: the moment a chord of ours completes while Win or Alt is held.
    // The caller injects the unassigned-key mask then, so releasing Win does not open Start and releasing Alt does not
    // focus the menu bar. Never before the chord is complete: Ctrl+Win alone (Wispr) reaches everybody byte-identical.
    public bool MaskNow {get;private set;}
    // Releases we never saw (Win+L, Ctrl+Alt+Del, a secure desktop swallowing the key-up) must not leave a key "held":
    // the next Alt would then look like a chord. Called on every key-down with the physical state from Windows.
    public List<(int Vk,Key Role)> Resync(Func<int,bool> isDown,int except)
    {
        List<(int,Key)> released=[];
        foreach(int vk in held.Where(v=>v!=except&&!isDown(v)).ToList())
            if(Feed(vk,false,out var key))released.Add((vk,key));
        return released;
    }
    public bool Feed(int vk,bool down,out Key key)
    {
        key=Role(vk);var role=key;bool was=held.Any(k=>Role(k)==role);MaskNow=false;
        if(down)held.Add(vk);else held.Remove(vk);
        if(!HotkeyLayout.IsMod(key)){tracker.Feed(key,down);return true;}
        bool changed=was!=held.Any(k=>Role(k)==role); // left+right copies remain logically held
        if(changed){tracker.Feed(key,down);MaskNow=tracker.MaskNow;}
        return changed;
    }
}
