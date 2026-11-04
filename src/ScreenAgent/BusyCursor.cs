namespace ScreenAgent;
// While a dictation is processed the system pointer shows the arrow with the little circle (IDC_APPSTARTING), in text fields too. Windows offers this only system-wide, so
// it must always come back: when the work ends, after MaxOnMs at the latest, when the app exits, and at the next start if a crash left it on (the flag file says so).
internal static class BusyCursor
{
    const uint Arrow=32512,TextBeam=32513,SpiSetCursors=0x57;const int AppStarting=32650;
    public const long MaxOnMs=120000;
    static bool on,expired;static long onAt;
    static string Flag=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScreenAgent","busy-cursor.flag");
    public static bool On=>on;
    public static void RestoreIfLeftOn()
    {
        try{if(File.Exists(Flag)){Restore();Diag.Event("cursor.restored",new(){["why"]="left-on"});}}
        catch(Exception ex){Diag.Fail("busy-cursor-restore",ex);}
    }
    // Called from the app tick, and with false at exit.
    public static void Set(bool want)
    {
        try
        {
            if(!want){expired=false;if(on)Restore();return;}
            if(on){if(Environment.TickCount64-onAt>MaxOnMs){expired=true;Restore();Diag.Event("cursor.restored",new(){["why"]="time-limit"});}return;}
            if(expired)return;   // stays the normal pointer until this piece of work is over
            File.WriteAllText(Flag,"");
            var source=Native.LoadCursor(IntPtr.Zero,(IntPtr)AppStarting);
            foreach(uint id in new[]{Arrow,TextBeam}){var copy=Native.CopyIcon(source);if(copy!=IntPtr.Zero)Native.SetSystemCursor(copy,id);}   // SetSystemCursor owns and destroys its copy
            on=true;onAt=Environment.TickCount64;
        }
        catch(Exception ex){Diag.Fail("busy-cursor",ex);}
    }
    static void Restore()
    {
        on=false;
        Native.SystemParametersInfo(SpiSetCursors,0,IntPtr.Zero,0);
        try{File.Delete(Flag);}
        catch(IOException ex){Diag.Fail("busy-cursor-flag",ex);}
    }
}
