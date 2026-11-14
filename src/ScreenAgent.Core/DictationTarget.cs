namespace ScreenAgent.Core;
// v0.1.15: which window and field an ad-hoc dictation types into. Until 0.1.14 the field was read ONCE when the chord completed;
// any momentary miss (the foreground still settling, a Chromium/Electron window such as VS Code waking its accessibility tree and
// answering the focus query differently on the next read, an empty process name) became "kein Textfeld gewählt", and the next
// press worked (device trace 14 Nov 13:45:06, process ""). Now a miss is asked again for about 300 ms; only a result that cannot
// change by waiting (password field, elevated window, blocked window) ends early, and a refusal says what to do.
public enum TargetWindow { None, Ours, Shell, Real }
// One look at the foreground: what kind of window is in front, its process, why the field is not usable (null = usable).
public sealed record TargetProbe(TargetWindow Window,string Process,string? Why,string Source="foreground");
public sealed record TargetOutcome<T>(T? Target,TargetProbe Last,int Attempts,long Ms,string? Refused,IReadOnlyList<string> Misses) where T:class
{
    public bool Bound=>Target is not null;
}
public static class DictationTarget
{
    // Waits before each further attempt (the first look is immediate): about 300 ms in total.
    public static readonly int[] Waits=[30,40,50,60,60,60];
    public const int BudgetMs=300;
    // Windows-shell surfaces that briefly hold the foreground (taskbar, desktop, Start/Search, Alt+Tab, the activation staging window).
    static readonly string[] ShellClasses=["Shell_TrayWnd","Shell_SecondaryTrayWnd","Progman","WorkerW","ForegroundStaging","MultitaskingViewFrame","TaskListThumbnailWnd","XamlExplorerHostIslandWindow","NotifyIconOverflowWindow","TopLevelWindowForOverflowXamlIsland"];
    static readonly string[] ShellProcesses=["StartMenuExperienceHost","SearchHost","SearchApp","ShellExperienceHost","ShellHost","LockApp"];
    // own: the window belongs to this process; overlay: it cannot be typed into (no-activate, transparent or tool window) — our
    // captions and notices. Our real windows (dictionary, history) stay Real: a field there is a valid target, as before.
    public static TargetWindow Classify(IntPtr window,string className,string process,bool own,bool overlay)
    {
        if(window==IntPtr.Zero)return TargetWindow.None;
        if(own&&overlay)return TargetWindow.Ours;
        if(ShellClasses.Contains(className,StringComparer.Ordinal))return TargetWindow.Shell;
        if(ShellProcesses.Contains(process,StringComparer.OrdinalIgnoreCase))return TargetWindow.Shell;
        return TargetWindow.Real;
    }
    // Reasons a field is refused that waiting cannot change (TextInjector.SafeWhy names).
    public static bool Final(string? why)=>why is "elevated" or "password" or "blocked-window" or "locked-host";
    public static bool Usable(TargetProbe p)=>p.Window==TargetWindow.Real&&p.Process.Length>0&&p.Why is null;
    // What one miss is called in the log: the window kind when nothing real is in front, else the field's reason.
    public static string Miss(TargetProbe p)=>p.Window!=TargetWindow.Real?p.Window.ToString().ToLowerInvariant():p.Process.Length==0?"no-process":p.Why??"ok";
    // Asks again while the answer can still change; probe returns the look and, when usable, the bound target.
    public static async Task<TargetOutcome<T>> Resolve<T>(Func<(TargetProbe Probe,T? Target)> probe,Func<int,Task> wait,Func<long> clock) where T:class
    {
        long t0=clock();var misses=new List<string>();
        for(int attempt=1;;attempt++)
        {
            (TargetProbe p,T? target)=probe();
            if(Usable(p)&&target is not null)return new(target,p,attempt,clock()-t0,null,misses);
            misses.Add(Miss(p));
            if(p.Window==TargetWindow.Real&&Final(p.Why))return new(null,p,attempt,clock()-t0,"unsafe-target:"+p.Why,misses);
            if(attempt>Waits.Length||clock()-t0>=BudgetMs)return new(null,p,attempt,clock()-t0,RefusedWhy(p),misses);
            await wait(Waits[attempt-1]);
        }
    }
    // After the last attempt: nothing typeable in front (desktop, taskbar, our overlay, no window) or a window whose field did not answer.
    public static string RefusedWhy(TargetProbe p)=>p.Window==TargetWindow.Real&&Final(p.Why)?"unsafe-target:"+p.Why:p.Window==TargetWindow.Real?"no-field":"no-target";
    // The notice for a refused start: what is wrong and what to do.
    public static string Notice(string refused)=>refused switch
    {
        "no-target"=>"Diktat nicht möglich · kein Programm im Vordergrund · ins Textfeld klicken, dann erneut drücken",
        "no-field"=>"Diktat nicht möglich · das Textfeld antwortet nicht · hineinklicken, dann erneut drücken",
        "unsafe-target:password"=>"Diktat nicht möglich · Passwortfeld",
        "unsafe-target:elevated"=>"Diktat nicht möglich · Fenster läuft als Administrator",
        _ when refused.StartsWith("unsafe-target:",StringComparison.Ordinal)=>"Diktat nicht möglich · dieses Fenster ist für Diktat gesperrt",
        _=>"Diktat nicht möglich",
    };
}
