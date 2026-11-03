namespace ScreenAgent.Core;
// What a physical key means for a run (the decision part of the hook callback, pure so it can be tested with the real event order).
// Modifier keys alone are never hands-on: Ctrl, Win, Shift go down BEFORE the Y or X of the chords, and a press of them must not revoke the confirm it belongs to.
public static class ComputerInput
{
    public static bool IsModifier(int vk)=>vk is 0x10 or 0x11 or 0x12 or 0x5b or 0x5c or 0xa0 or 0xa1 or 0xa2 or 0xa3 or 0xa4 or 0xa5 or 0x14 or 0x90 or 0x91;   // shift ctrl alt win caps num scroll
    public static bool IsChordKey(int vk)=>vk is 0x58 or 0x59;
    /// <param name="held">true while that virtual key is down right now (GetAsyncKeyState)</param>
    public static void OnKey(ComputerRun? r,int vk,bool down,bool injected,Func<int,bool> held)
    {
        if(r is null||!down)return;
        bool chord=held(0x11)&&held(0x10)&&(held(0x5b)||held(0x5c));
        if(vk==0x58&&chord){r.Kill("hotkey");return;}
        if(vk==0x59&&chord&&!injected){if(r.Pending is not null)r.PhysicalConfirm();return;}   // the confirm chord is the user's physical hand: never injected, never swallowed
        if(IsModifier(vk))return;
        if(!injected)r.HandsOn();
    }
}
