namespace ScreenAgent.Core;
public enum PanelState { Hidden, Open, Minimized }
public enum PanelAct { None, Show, ShowFront, Restore, Hide }
public readonly record struct PanelDecision(PanelAct Act,string Why);
// When the drawing window may appear on its own. It comes to the front only the first time a diagram shows up by itself in this app run; a minimised window
// stays minimised (new diagrams update it silently) until the user clicks its taskbar button; closing keeps it closed until the next session.
// State is what the window reports (Opened/Hidden/Minimized/Restored), the decisions only read it.
public sealed class CanvasPolicy(bool manual=false)
{
    public PanelState State {get;private set;}
    public bool ClosedByUser {get;private set;}
    public bool FrontDone {get;private set;}
    // The agent has a diagram ("canvas-pending").
    public PanelDecision Pending()
    {
        if(manual)return new(PanelAct.None,"manual");
        if(ClosedByUser)return new(PanelAct.None,"closed");
        return State switch
        {
            PanelState.Minimized=>new(PanelAct.None,"minimized"),
            PanelState.Open=>new(PanelAct.None,"open"),
            _=>FrontDone?new(PanelAct.Show,"again"):new(PanelAct.ShowFront,"first")
        };
    }
    // The user asks (tray, shortcut, page button): always honoured, a minimised window comes back.
    public PanelDecision UserShow()
    {
        ClosedByUser=false;
        return State switch{PanelState.Open=>new(PanelAct.None,"open"),PanelState.Minimized=>new(PanelAct.Restore,"user"),_=>new(PanelAct.ShowFront,"user")};
    }
    public PanelDecision UserToggle()=>State==PanelState.Open?new(PanelAct.Hide,"user"):UserShow();
    public void Opened(string why){State=PanelState.Open;ClosedByUser=false;if(why=="first")FrontDone=true;}   // visible again: the earlier close no longer matters
    public void Minimized(){if(State==PanelState.Open)State=PanelState.Minimized;}
    public void Restored(){if(State==PanelState.Minimized)State=PanelState.Open;}
    public void Hidden(bool byUser){State=PanelState.Hidden;if(byUser)ClosedByUser=true;}
    // The voice session ended: an open panel goes away, a minimised one stays in the taskbar, minimised, until he restores it (also across sessions).
    public bool HideAtSessionEnd=>State!=PanelState.Minimized;
    // The window could not be brought up (no browser): back to what was before, so the next diagram tries again instead of finding "open" and "front done".
    public void OpenFailed(PanelState before,bool frontBefore){State=before;FrontDone=frontBefore;}
    public void SessionStarted()=>ClosedByUser=false;
}
