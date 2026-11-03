using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ScreenAgent.Core;
// Pure parts of the Main Agent prompt feature (typing a prompt into the Claude Code box in VS Code Insiders). Everything that touches Windows lives in MainAgentPrompt.cs.
public static class PromptCodes
{
    public const string Typed="typed",TypedUnverified="typed-unverified",NoWindow="no-window",NoForeground="no-foreground",NoField="no-field",WrongFocus="wrong-focus",Interrupted="interrupted",Busy="busy",NoSession="no-session";
    public static bool Ok(string code)=>code is Typed or TypedUnverified;
    // Heading of the fallback window ("Nicht eingetippt: <reason>").
    public static string Reason(string code)=>code switch
    {
        NoWindow=>"kein VS-Code-Insiders-Fenster gefunden",
        NoForeground=>"VS Code ließ sich nicht in den Vordergrund holen",
        NoField=>"Claude-Eingabefeld nicht gefunden",
        WrongFocus=>"der Fokus war nicht im Claude-Eingabefeld",
        Interrupted=>"die Eingabe wurde unterbrochen",
        Busy=>"eine Taste war gedrückt oder ein anderer Auftrag läuft",
        NoSession=>"keine laufende Sitzung",
        _=>"unbekannter Grund"
    };
    // "Nicht eingetippt: <reason>"; after an interruption the characters already typed are still in the box, and the text shown here would repeat them.
    public static string Heading(string code,int charsAlreadyInBox=0)=>
        "Nicht eingetippt: "+Reason(code)+(code==Interrupted&&charsAlreadyInBox>0?$" · {charsAlreadyInBox} Zeichen stehen schon im Feld":"");
}
// One hard deadline for a whole prompt run, counted from the moment the command was received (the Desk gives up after 40 s).
public readonly record struct PromptDeadline(long StartTick,long TotalMs=PromptDeadline.DefaultMs)
{
    public const long DefaultMs=30000,MinTypingMs=8000;
    public long Left(long now)=>StartTick+TotalMs-now;
    public bool Expired(long now)=>Left(now)<=0;
    // Typing (up to 8000 characters plus the read-back) never starts with less than this left.
    public bool MayStartTyping(long now)=>Left(now)>=MinTypingMs;
    public static PromptDeadline FromNow()=>new(Environment.TickCount64);
}
// One gate for everything that puts keys into another app: a prompt being typed and a dictation being injected exclude each other; the drawing panel waits for the prompt.
public sealed class InputGate
{
    readonly SemaphoreSlim one=new(1,1);
    volatile bool prompt;
    public bool PromptActive=>prompt;
    // Raised (on the thread that ended the prompt) after a prompt is over.
    public event System.Action? PromptEnded;
    public async Task<bool> BeginPrompt(TimeSpan wait)
    {
        if(!await one.WaitAsync(wait).ConfigureAwait(false))return false;
        prompt=true;return true;
    }
    public void EndPrompt()
    {
        prompt=false;one.Release();
        try{PromptEnded?.Invoke();}catch{ /* a listener must not keep the gate shut */ }
    }
    // Disposable slot for a dictation injection; null when the prompt did not finish within `wait`.
    public async Task<IDisposable?> BeginInject(TimeSpan wait)
    {
        if(!await one.WaitAsync(wait).ConfigureAwait(false))return null;
        return new Slot(one);
    }
    sealed class Slot(SemaphoreSlim sem):IDisposable
    {
        int released;
        public void Dispose(){if(Interlocked.Exchange(ref released,1)==0)sem.Release();}
    }
}
// The palette entry that may be run with Enter.
public static class PaletteRules
{
    public const string Label="Claude Code: Focus input";
    public static bool IsFocusInput(string? highlightedName)=>highlightedName is not null&&highlightedName.Contains(Label,StringComparison.OrdinalIgnoreCase);
}
public sealed record PromptRequest(string Id,string Text);
public static class PromptCommand
{
    static readonly Regex IdShape=new("^p[0-9a-f]{16}$",RegexOptions.Compiled);
    // Answer of the commands long poll: {fresh:false, prompt:{id:"p<16 hex>", text:"..."}}. Anything else is not a prompt.
    public static PromptRequest? Parse(JsonElement root)
    {
        if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("prompt",out var p)||p.ValueKind!=JsonValueKind.Object)return null;
        if(!p.TryGetProperty("id",out var id)||id.ValueKind!=JsonValueKind.String||!p.TryGetProperty("text",out var text)||text.ValueKind!=JsonValueKind.String)return null;
        string i=id.GetString()!;
        return IdShape.IsMatch(i)?new(i,text.GetString()!):null;
    }
}
public enum PromptStart { Accepted, Repeat, Busy }
// One prompt at a time; a repeated id (the long poll can deliver the same command twice) is ignored, a second one while typing is answered "busy".
public sealed class PromptGate(int memory=64)
{
    readonly object gate=new();
    readonly HashSet<string> seen=[];
    readonly Queue<string> order=new();
    bool running;
    public PromptStart Begin(string id)
    {
        lock(gate)
        {
            if(seen.Contains(id))return PromptStart.Repeat;
            seen.Add(id);order.Enqueue(id);
            while(order.Count>memory)seen.Remove(order.Dequeue());
            if(running)return PromptStart.Busy;
            running=true;return PromptStart.Accepted;
        }
    }
    public void End(){lock(gate)running=false;}
}
public enum StepKind { Chars, Newline }
public readonly record struct TypeStep(StepKind Kind,string Chars);
public static class PromptText
{
    public const int MaxChars=8000,TailChars=40,BatchChars=24;
    // \r\n to \n, tabs to two spaces, other control characters dropped, 3+ newlines to 2, capped, trimmed. "/" and "@" at the start would open menus of the extension: a space goes first.
    public static string Normalize(string text)
    {
        var sb=new StringBuilder();
        for(int i=0;i<text.Length;i++)
        {
            char c=text[i];
            if(c=='\r'){if(i+1<text.Length&&text[i+1]=='\n')i++;sb.Append('\n');}
            else if(c=='\n')sb.Append('\n');
            else if(c=='\t')sb.Append("  ");
            else if(char.IsControl(c)||c is '\u2028' or '\u2029')continue;
            else sb.Append(c);
        }
        string s=Regex.Replace(sb.ToString(),"\n{3,}","\n\n").Trim();
        // "/" or "@" first on any line opens a menu of the extension (after Shift+Enter too): a space goes in front. A mid-line "@" stays as it is.
        s=Regex.Replace(s,"(^|\n)(?=[/@])","$1 ");
        if(s.Length>MaxChars){s=s[..MaxChars];if(char.IsHighSurrogate(s[^1]))s=s[..^1];s=s.TrimEnd();}
        return s;
    }
    public static string Squash(string s)=>Regex.Replace(s,@"\s+"," ").Trim();
    // The last 40 whitespace-normalized characters (never starting inside a surrogate pair).
    public static string Tail(string typed)
    {
        string n=Squash(typed);
        if(n.Length<=TailChars)return n;
        string t=n[^TailChars..];
        return char.IsLowSurrogate(t[0])?t[1..]:t;
    }
    // True when what the box shows contains the end of what was typed. Whitespace is compared normalized (the box turns newlines into other whitespace).
    public static bool Landed(string read,string typed)
    {
        string tail=Tail(typed);
        return tail.Length>0&&Squash(read).Contains(tail,StringComparison.Ordinal);
    }
    // Batches of at most ~24 UTF-16 units (a surrogate pair stays together); every "\n" is its own step.
    public static List<TypeStep> Steps(string text,int batch=BatchChars)
    {
        List<TypeStep> steps=[];var sb=new StringBuilder();
        void Flush(){if(sb.Length>0){steps.Add(new(StepKind.Chars,sb.ToString()));sb.Clear();}}
        for(int i=0;i<text.Length;i++)
        {
            char c=text[i];
            if(c=='\n'){Flush();steps.Add(new(StepKind.Newline,"\n"));continue;}
            bool pair=char.IsHighSurrogate(c)&&i+1<text.Length&&char.IsLowSurrogate(text[i+1]);
            int need=pair?2:1;
            if(sb.Length+need>batch)Flush();
            sb.Append(c);if(pair)sb.Append(text[++i]);
        }
        Flush();return steps;
    }
}
public readonly record struct CodeWindow(long Handle,string Process,string Class,string Title,bool Minimized,bool Cloaked=false);
public static class CandidateOrder
{
    public const string InsidersProcess="Code - Insiders",StableProcess="Code",WindowClass="Chrome_WidgetWin_1";
    static readonly string[] AuxiliaryTitles=["Developer Tools","Issue Reporter","Process Explorer"];
    public static bool Auxiliary(CodeWindow w)=>AuxiliaryTitles.Any(t=>w.Title.StartsWith(t,StringComparison.Ordinal));
    // windows: top-level visible-or-minimized windows in EnumWindows order (most recently used first). Insiders only; plain "Code" only with allowStable (config), and then only when there is no Insiders window.
    // Windows on another virtual desktop (cloaked) and the auxiliary windows of the same process (Developer Tools, Issue Reporter, Process Explorer) never qualify.
    public static List<CodeWindow> Pick(IEnumerable<CodeWindow> windows,bool allowStable=false)
    {
        var all=windows.Where(w=>w.Class==WindowClass&&w.Title.Length>0&&!w.Cloaked&&!Auxiliary(w)).ToList();
        var chosen=all.Where(w=>w.Process==InsidersProcess).ToList();
        if(chosen.Count==0&&allowStable)chosen=all.Where(w=>w.Process==StableProcess).ToList();
        return chosen.Where(Wsl).Concat(chosen.Where(w=>!Wsl(w))).ToList();
    }
    public static bool Wsl(CodeWindow w)=>w.Title.Contains("[WSL",StringComparison.Ordinal);
}
// Decisions about the element that has keyboard focus, from names and the class names of its ancestors (UIA ClassName = the HTML class in Chromium: language independent).
public static class FocusRules
{
    static readonly string[] Forbidden=["monaco-editor","xterm","terminal"];
    public static bool InputName(string? name,IEnumerable<string> accepted)=>!string.IsNullOrEmpty(name)&&accepted.Contains(name,StringComparer.Ordinal);
    public static bool InEditorOrTerminal(IEnumerable<string> ancestorClasses)=>ancestorClasses.Any(c=>Forbidden.Any(f=>c.Contains(f,StringComparison.OrdinalIgnoreCase)));
    public static bool InQuickInput(IEnumerable<string> ancestorClasses)=>ancestorClasses.Any(c=>c.Contains("quick-input",StringComparison.OrdinalIgnoreCase));
    // The one check that lets typing start.
    public static bool MayType(string? name,IEnumerable<string> accepted,IEnumerable<string> ancestorClasses)=>InputName(name,accepted)&&!InEditorOrTerminal(ancestorClasses);
}
