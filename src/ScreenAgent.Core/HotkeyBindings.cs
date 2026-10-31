namespace ScreenAgent.Core;
// The state machine uses logical roles. Configuration maps two physical modifiers,
// Shift and three distinct non-modifier keys to those roles.
public sealed class HotkeyBindings
{
    public string Primary {get;}
    public string Secondary {get;}
    public int DrawVk {get;}
    readonly int pauseVk,spaceVk;
    readonly HashSet<int> held=[];
    public HotkeyBindings(Dictionary<string,string> keys)
    {
        static string[] Parts(string value)=>value.Split('+',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        if(keys.Count!=5||new[]{"dictation","session","pause","handsFree","draw"}.Any(k=>!keys.ContainsKey(k)))throw new InvalidDataException("Five hotkeys required");
        var dictate=Parts(keys["dictation"]);
        if(dictate.Length!=2||dictate.Distinct().Count()!=2||dictate.Any(k=>k is not ("Ctrl" or "Alt" or "Win")))throw new InvalidDataException("Dictation needs two distinct modifiers: Ctrl, Alt, Win");
        Primary=dictate[0];Secondary=dictate[1];
        bool Chord(string[] actual,params string[] extra)=>actual.Order().SequenceEqual(dictate.Concat(extra).Order());
        if(!Chord(Parts(keys["session"]),"Shift"))throw new InvalidDataException("Session must add Shift to dictation");
        var pause=Parts(keys["pause"]);var hands=Parts(keys["handsFree"]);
        string? pauseKey=pause.SingleOrDefault(k=>k!="Shift"&&!dictate.Contains(k));
        string? handsKey=hands.SingleOrDefault(k=>!dictate.Contains(k));
        if(pauseKey is null||handsKey is null||!Chord(pause,"Shift",pauseKey)||!Chord(hands,handsKey))throw new InvalidDataException("Pause/hands-free must extend dictation chord");
        pauseVk=VirtualKey(pauseKey);spaceVk=VirtualKey(handsKey);DrawVk=VirtualKey(keys["draw"]);
        if(new[]{pauseVk,spaceVk,DrawVk}.Distinct().Count()!=3)throw new InvalidDataException("Draw, pause and hands-free keys must differ");
    }
    static int VirtualKey(string key)=>key switch
    {
        "Caps"=>0x14,"Space"=>0x20,
        _ when key.Length==1&&key[0]>='A'&&key[0]<='Z'=>key[0],
        _ when key.StartsWith('F')&&int.TryParse(key[1..],out int n)&&n is >=1 and <=12=>0x6f+n,
        _=>throw new InvalidDataException("Use Caps, Space, A-Z or F1-F12 for action keys")
    };
    static string? Modifier(int vk)=>vk switch{0x11 or 0xa2 or 0xa3=>"Ctrl",0x12 or 0xa4 or 0xa5=>"Alt",0x5b or 0x5c=>"Win",0x10 or 0xa0 or 0xa1=>"Shift",_=>null};
    public Key Role(int vk)
    {
        string? mod=Modifier(vk);
        return mod==Primary?Key.Ctrl:mod==Secondary?Key.Win:mod=="Shift"?Key.Shift:vk==DrawVk?Key.Caps:vk==pauseVk?Key.P:vk==spaceVk?Key.Space:Key.Other;
    }
    public bool CtrlWin=>held.Any(k=>Role(k)==Key.Ctrl)&&held.Any(k=>Role(k)==Key.Win);
    public bool PhysicalWin=>held.Any(k=>k is 0x5b or 0x5c);
    public bool Feed(int vk,bool down,out Key key)
    {
        key=Role(vk);bool was=held.Any(k=>Role(k)==key);
        if(down)held.Add(vk);else held.Remove(vk);
        if(key is not (Key.Ctrl or Key.Win or Key.Shift))return true;
        return was!=held.Any(k=>Role(k)==key); // left+right copies remain logically held
    }
}
