namespace ScreenAgent.Core;
public enum DeliveryKind { Typed, Undelivered, Empty, CaptureFailed }
// What becomes of a finished transcription. The text is only ever typed by the first pass, into the field he dictated into, when that is still safe; every other case puts
// it in a window that stays until he closes it (never on the clipboard, never into a field he did not choose) and keeps the dictation "failed / undelivered" until then.
public static class DictationDelivery
{
    public const string UndeliveredStage="undelivered",CaptureStage="capture";
    // injected: null = not tried (a retry never types), otherwise what the injector answered. captureLost: the microphone went away and was not reopened.
    public static DeliveryKind Decide(bool retry,bool hasText,bool? injected,bool captureLost)
    {
        if(!hasText)return captureLost?DeliveryKind.CaptureFailed:DeliveryKind.Empty;
        if(retry||injected!=true)return DeliveryKind.Undelivered;
        return captureLost?DeliveryKind.CaptureFailed:DeliveryKind.Typed;
    }
    public static void MarkUndelivered(DictationMeta m,string text,DateTimeOffset now)
    {m.State="failed";m.Stage=UndeliveredStage;m.Reason="Text noch nicht eingefügt";m.PartialText=text;m.Ended=now;}
    // He closed the window with the text, or it went into a field: delivered.
    public static void MarkDone(DictationMeta m,DateTimeOffset now)
    {m.State="done";m.Reason=null;m.Stage=null;m.PartialText=null;m.Ended=now;}
    public static void MarkCaptureFailed(DictationMeta m,string reason,DateTimeOffset now)
    {m.State="failed";m.Stage=CaptureStage;m.Reason=reason;m.PartialText=null;m.Ended=now;}
    // The tray entry / retry of such a dictation shows the stored text again instead of transcribing again.
    public static bool ShowStoredText(DictationMeta m)=>m.Stage==UndeliveredStage&&!string.IsNullOrEmpty(m.PartialText);
}
