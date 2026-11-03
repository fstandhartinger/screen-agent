using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ScreenAgent.Core;
// Start briefing (v0.1.7): what the app tells the Desk about the desktop when a session starts or the agent asks "what is going on".
// Windows, Chrome tabs, the last OCR text and the tail of the main Claude Code session. Everything here is pure logic; the Windows side is BriefingCollector.
public sealed record BriefingWindow(string Process,string Title);
public sealed record TabInfo(string Window,string Title,bool Active,string? Url);
public sealed record BriefingMessage(string Role,string? At,string Text);
public sealed record MainSession(string Source,string? UpdatedAt,IReadOnlyList<BriefingMessage> Messages);
// One source's answer: a value, or the reason it has nothing to say ("main-session:path-missing").
public sealed record Part<T>(T? Value,string? Missing=null);
public sealed record TailStats(long Length,long BytesRead,int Lines,int Parsed,int Skipped,bool Aborted);

// Patterns of things that must not leave the laptop even inside Florian's own systems; the gateway redacts once more.
public static class SecretFilter
{
    const string Mark="[redacted]";
    static readonly TimeSpan Limit=TimeSpan.FromMilliseconds(100);
    static readonly Regex[] Patterns=
    [
        new(@"\bsk-[A-Za-z0-9_\-]{16,}",RegexOptions.None,Limit),
        new(@"\bgh[pousr]_[A-Za-z0-9]{20,}|\bgithub_pat_[A-Za-z0-9_]{20,}",RegexOptions.None,Limit),
        new(@"\bxox[abprs]-[A-Za-z0-9\-]{10,}|\bAKIA[0-9A-Z]{16}\b",RegexOptions.None,Limit),
        new(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=\-]{12,}",RegexOptions.None,Limit),
        new(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]*",RegexOptions.None,Limit),
        new(@"(?i)\b(api[_\-]?key|apikey|token|secret|password|passwd|authorization)(\s*[:=]\s*)(""?)[^\s""']{6,}",RegexOptions.None,Limit),
        new(@"\b[0-9a-fA-F]{32,}\b",RegexOptions.None,Limit),
    ];
    // Long base64-like runs, but only when they mix upper case, lower case and digits: plain paths and long words stay.
    static readonly Regex Blob=new(@"[A-Za-z0-9+/_\-]{40,}={0,2}",RegexOptions.None,Limit);
    public static string Redact(string? text)
    {
        if(string.IsNullOrEmpty(text))return "";
        try
        {
            string s=text;
            for(int i=0;i<Patterns.Length;i++)
            {
                if(i==5)s=Patterns[i].Replace(s,m=>m.Groups[1].Value+m.Groups[2].Value+Mark);
                else s=Patterns[i].Replace(s,Mark);
            }
            return Blob.Replace(s,m=>m.Value.Any(char.IsUpper)&&m.Value.Any(char.IsLower)&&m.Value.Any(char.IsDigit)?Mark:m.Value);
        }
        catch(RegexMatchTimeoutException){return "";}   // fail closed: a text the filter cannot finish is not sent
    }
    // One line of printable text, capped.
    public static string Clip(string? text,int max)
    {
        if(string.IsNullOrEmpty(text))return "";
        var sb=new StringBuilder(Math.Min(text.Length,max+1));bool space=false;
        foreach(char c in text)
        {
            if(char.IsControl(c)||char.IsWhiteSpace(c)){space=sb.Length>0;continue;}
            if(space){sb.Append(' ');space=false;}
            sb.Append(c);
            if(sb.Length>=max)break;
        }
        string r=sb.ToString();
        return text.Length>max&&r.Length>=max?r[..Math.Max(0,max-1)]+"…":r;
    }
}

// The command channel's request: `{briefing:{id}}` next to (or instead of) prompt and fresh.
public static class BriefingCommand
{
    public static string? Parse(JsonElement root)
    {
        if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("briefing",out var b)||b.ValueKind!=JsonValueKind.Object)return null;
        if(!b.TryGetProperty("id",out var id)||id.ValueKind!=JsonValueKind.String)return null;
        string? v=id.GetString();
        return v is {Length:>=1 and <=32}&&Regex.IsMatch(v,"^[A-Za-z0-9_-]+$")?v:null;
    }
}

// Runs a section on its own background thread so a stalled network path or a slow UI Automation call can never hold anything else.
public static class Timeboxed
{
    public static Task<T> Start<T>(Func<CancellationToken,T> work,CancellationToken ct,string name)
    {
        var tcs=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(()=>{try{tcs.TrySetResult(work(ct));}catch(Exception e){tcs.TrySetException(e);}}){IsBackground=true,Name="ScreenAgent briefing "+name}.Start();
        return tcs.Task;
    }
}

public sealed record Gathered(List<BriefingWindow> Windows,List<TabInfo> Tabs,MainSession? Main,List<string> Missing,List<(string Stage,string Reason)> Failures,long Ms);
public static class BriefingGather
{
    // The three sources run in parallel inside one budget (1.5 s). They get a cancel signal shortly before it ends so they can hand over what they have;
    // a source that is still stuck at the end (a UNC path that stalls) is abandoned and named as missing.
    public static Gathered Run(Func<CancellationToken,Part<List<BriefingWindow>>> windows,Func<CancellationToken,Part<List<TabInfo>>> tabs,Func<CancellationToken,Part<MainSession>> main,int budgetMs=1500)
    {
        var clock=System.Diagnostics.Stopwatch.StartNew();
        using var cts=new CancellationTokenSource();
        var w=Timeboxed.Start(windows,cts.Token,"windows");var t=Timeboxed.Start(tabs,cts.Token,"tabs");var m=Timeboxed.Start(main,cts.Token,"main");
        cts.CancelAfter(Math.Max(1,budgetMs-200));
        var all=new Task[]{w,t,m};
        try{Task.WaitAll(all,Math.Max(1,budgetMs-(int)clock.ElapsedMilliseconds));}catch(AggregateException){ /* read per task below */ }
        cts.Cancel();   // a source still stuck stops at its next check; it only ever reads the token
        if(all.Any(x=>!x.IsCompleted))try{Task.WaitAll(all,100);}catch(AggregateException){}   // a short grace for sources that were about to hand over
        var missing=new List<string>();var failures=new List<(string,string)>();
        T? Take<T>(Task<T> task,string name,string stage) where T:class
        {
            if(!task.IsCompleted){missing.Add(name+":timeout");failures.Add((stage,"timeout"));return null;}
            if(task.IsFaulted){var e=task.Exception!.GetBaseException();bool late=e is OperationCanceledException;missing.Add(name+(late?":timeout":":error"));failures.Add((stage,late?"timeout":e.GetType().Name));return null;}
            return task.Result;
        }
        var wp=Take(w,"windows","windows");var tp=Take(t,"tabs","tabs");var mp=Take(m,"main-session","main-session");
        if(wp?.Missing is {} wm)missing.Add(wm);
        if(tp?.Missing is {} tm)missing.Add(tm);
        if(mp?.Missing is {} mm)missing.Add(mm);
        return new(wp?.Value??[],tp?.Value??[],mp?.Value,missing,failures,clock.ElapsedMilliseconds);
    }
}

public static class BriefingPayload
{
    public const int MaxWindows=30,MaxTabs=40,MaxScreenText=1500,MaxBytes=60000;
    public static string? Url(string? url)
    {
        if(string.IsNullOrWhiteSpace(url))return null;
        string u=url.Trim();
        int cut=u.IndexOfAny(['?','#']);   // queries and fragments carry tokens and ids; the page is what matters
        if(cut>=0)u=u[..cut];
        u=SecretFilter.Redact(u);
        return SecretFilter.Clip(u,150) is {Length:>0} r?r:null;
    }
    // The Desk body. Missing parts are left out (every part is optional on the route); order and caps follow the route's contract.
    public static Dictionary<string,object?> Build(IReadOnlyList<BriefingWindow> windows,IReadOnlyList<TabInfo> tabs,MainSession? main,string? screenText,string? commandId)
    {
        var body=new Dictionary<string,object?>();
        if(windows.Count>0)body["windows"]=windows.Take(MaxWindows).Select(w=>new{process=SecretFilter.Clip(w.Process,60),title=SecretFilter.Clip(SecretFilter.Redact(w.Title),120)}).ToArray();
        var tabList=tabs.Take(MaxTabs).Select(t=>
        {
            var d=new Dictionary<string,object?>{["window"]=SecretFilter.Clip(t.Window,40),["title"]=SecretFilter.Clip(SecretFilter.Redact(t.Title),120),["active"]=t.Active};
            if(t.Active&&Url(t.Url) is {} u)d["url"]=u;
            return d;
        }).ToArray();
        if(tabList.Length>0)body["tabs"]=tabList;
        if(main is {Messages.Count:>0})
            body["mainSession"]=new Dictionary<string,object?>{["source"]=main.Source,["updatedAt"]=main.UpdatedAt,["messages"]=main.Messages.Select(m=>new{role=m.Role,at=m.At,text=m.Text}).ToArray()};
        if(!string.IsNullOrWhiteSpace(screenText)&&ScreenText(screenText) is {Length:>0} st)body["screenText"]=st;
        if(commandId is not null)body["commandId"]=commandId;
        // Far below the route's 64 kB in practice; if it ever is not, the least valuable parts go first.
        foreach(var drop in new[]{"screenText","tabs","windows"})
            if(JsonSerializer.SerializeToUtf8Bytes(body).Length>MaxBytes)body.Remove(drop);
        return body;
    }
    // OCR text: redacted, runs of blanks collapsed, line breaks kept, capped.
    public static string ScreenText(string text)
    {
        string s=Regex.Replace(SecretFilter.Redact(text),@"[^\P{C}\n]","");   // control characters out, line breaks stay
        s=Regex.Replace(s,@"[ \t\u00a0]+"," ");
        s=Regex.Replace(s,@" ?\n ?","\n");
        s=Regex.Replace(s,@"\n{3,}","\n\n").Trim();
        return s.Length>MaxScreenText?s[..MaxScreenText]:s;
    }
}

// `wsl.exe -l --running` answers in UTF-16; the default distro carries a "(Default)" or "(Standard)" suffix.
public static class WslProbe
{
    public static string? DistroOf(string path)
    {
        foreach(var prefix in new[]{@"\\wsl.localhost\",@"\\wsl$\"})
            if(path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))
            {
                string rest=path[prefix.Length..];int cut=rest.IndexOf('\\');
                string name=cut<0?rest:rest[..cut];
                return name.Length>0?name:null;
            }
        return null;
    }
    public static bool Running(byte[] output,string distro)
    {
        string text=output.Length>=2&&output[1]==0?Encoding.Unicode.GetString(output):Encoding.UTF8.GetString(output);
        text=text.Replace("\0","");
        foreach(var raw in text.Split('\n'))
        {
            string line=raw.Trim().TrimStart('*',' ');
            int paren=line.IndexOf(" (",StringComparison.Ordinal);
            if(paren>0)line=line[..paren];
            if(line.Equals(distro,StringComparison.OrdinalIgnoreCase))return true;
        }
        return false;
    }
}

public static class MainSessionSource
{
    public const string DefaultPath=@"\\wsl.localhost\Ubuntu\home\flori";
    const int MaxDirs=300;
    // Newest *.jsonl by modified time. `path` is a transcript file, a projects folder, an account folder (.claude*) or a home folder holding .claude* accounts.
    // Only directory listings, one level per step; the whole tree is never walked.
    public static (string File,DateTime Utc,string Source)? Newest(string path,CancellationToken ct)
    {
        if(File.Exists(path))return (path,File.GetLastWriteTimeUtc(path),Label(path));
        if(!Directory.Exists(path))return null;
        var roots=new List<string>();
        foreach(var dir in Safe(()=>Directory.EnumerateDirectories(path,".claude*")))
        {
            ct.ThrowIfCancellationRequested();
            string projects=Path.Combine(dir,"projects");
            if(Directory.Exists(projects))roots.Add(projects);
        }
        string own=Path.Combine(path,"projects");
        if(Directory.Exists(own))roots.Add(own);
        if(roots.Count==0)roots.Add(path);
        (string,DateTime,string)? best=null;
        foreach(var root in roots)
        {
            var folders=new List<string>{root};
            folders.AddRange(Safe(()=>Directory.EnumerateDirectories(root)).Take(MaxDirs));
            foreach(var folder in folders)
            {
                ct.ThrowIfCancellationRequested();
                foreach(var f in Safe(()=>new DirectoryInfo(folder).EnumerateFiles("*.jsonl")))
                    if(f.Length>0&&(best is null||f.LastWriteTimeUtc>best.Value.Item2))best=(f.FullName,f.LastWriteTimeUtc,Label(f.FullName));
            }
        }
        return best;
    }
    // The account folder (.claude-A) tells which Claude account the session ran under; never the project or the file name.
    static string Label(string file)
    {
        foreach(var part in file.Split('\\','/'))if(part.StartsWith(".claude",StringComparison.Ordinal))return part;
        return "custom";
    }
    static IEnumerable<T> Safe<T>(Func<IEnumerable<T>> list)
    {
        IEnumerator<T> e;
        try{e=list().GetEnumerator();}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){yield break;}
        using(e)
            while(true)
            {
                try{if(!e.MoveNext())yield break;}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){yield break;}
                yield return e.Current;
            }
    }
    // distroRunning: true/false from `wsl.exe -l --running`, null when it cannot be asked (then the UNC read runs under the section's own timeout).
    public static Part<MainSession> Read(string? configured,Func<string,bool?> distroRunning,CancellationToken ct,Action<TailStats>? stats=null)
    {
        string path=string.IsNullOrWhiteSpace(configured)?DefaultPath:configured.Trim();
        if(WslProbe.DistroOf(path) is {} distro&&distroRunning(distro)==false)return new(null,"main-session:wsl-not-running");   // opening the share would start the distro
        ct.ThrowIfCancellationRequested();
        var newest=Newest(path,ct);
        if(newest is null)return new(null,"main-session:path-missing");
        ct.ThrowIfCancellationRequested();
        List<BriefingMessage> messages;TailStats s;
        try
        {
            using var stream=new FileStream(newest.Value.File,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,4096,FileOptions.SequentialScan);
            messages=TranscriptTail.Read(stream,out s,ct);
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException){return new(null,"main-session:unreadable");}
        stats?.Invoke(s);
        if(messages.Count==0)return new(null,"main-session:no-messages");
        return new(new(newest.Value.Source,newest.Value.Utc.ToString("o"),messages));
    }
}

// The last user/assistant text of a Claude Code transcript (.jsonl), read from the end of a file that can be gigabytes.
// Hard limits: seek to at most the last MaxTail bytes, stream them in 32 kB chunks, never hold more than one line, stop at Budget.
public static class TranscriptTail
{
    public const int MaxTail=512*1024,Budget=1024*1024,MaxLine=128*1024,Keep=12,MaxChars=300;
    static readonly byte[] UserTag="\"type\":\"user\""u8.ToArray(),AssistantTag="\"type\":\"assistant\""u8.ToArray();
    public static List<BriefingMessage> Read(Stream stream,out TailStats stats,CancellationToken ct=default,int maxTail=MaxTail,int budget=Budget)
    {
        long length=stream.Length,start=Math.Max(0,length-maxTail),read=0,cost=0;
        int lines=0,parsed=0,skipped=0;bool aborted=false;
        stream.Seek(start,SeekOrigin.Begin);
        var ring=new Queue<BriefingMessage>();
        var chunk=new byte[32*1024];var line=new byte[MaxLine];int used=0;bool overlong=false,dropFirst=start>0;
        void End()
        {
            if(dropFirst){dropFirst=false;used=0;overlong=false;return;}   // starts mid-line: unparsable, skipped
            lines++;
            if(overlong){skipped++;used=0;overlong=false;return;}
            var span=new ReadOnlySpan<byte>(line,0,used);
            if(used>0&&(span.IndexOf(UserTag)>=0||span.IndexOf(AssistantTag)>=0))
            {
                cost+=used;
                var m=Parse(line,used);
                if(m is null)skipped++;
                else
                {
                    parsed++;cost+=m.Text.Length*2;ring.Enqueue(m);
                    while(ring.Count>Keep)ring.Dequeue();
                }
            }
            used=0;
        }
        while(read<maxTail&&!aborted)
        {
            ct.ThrowIfCancellationRequested();
            int n=stream.Read(chunk,0,(int)Math.Min(chunk.Length,maxTail-read));
            if(n<=0)break;
            read+=n;
            var data=new ReadOnlySpan<byte>(chunk,0,n);
            while(data.Length>0)
            {
                int nl=data.IndexOf((byte)'\n');
                var part=nl<0?data:data[..nl];
                if(!dropFirst&&!overlong)
                {
                    if(used+part.Length>MaxLine)overlong=true;
                    else{part.CopyTo(new Span<byte>(line,used,part.Length));used+=part.Length;}
                }
                if(nl<0)break;
                End();
                data=data[(nl+1)..];
                if(cost>budget){aborted=true;break;}
            }
        }
        // The last line has no newline yet when the file ends there; a half-written one just fails to parse.
        if(!aborted&&used>0&&read>=Math.Min(length-start,maxTail))End();
        stats=new(length,read,lines,parsed,skipped,aborted);
        return ring.ToList();
    }
    static readonly string[] Noise=["<command-name>","<command-message>","<local-command","<system-reminder>","Caveat: The messages below"];
    static BriefingMessage? Parse(byte[] buffer,int length)
    {
        try
        {
            using var doc=JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer,0,length));
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)return null;
            string? type=Str(root,"type");
            if(type is not ("user" or "assistant"))return null;
            if(Flag(root,"isSidechain")||Flag(root,"isMeta"))return null;
            if(!root.TryGetProperty("message",out var message)||message.ValueKind!=JsonValueKind.Object||!message.TryGetProperty("content",out var content))return null;
            string text;
            if(content.ValueKind==JsonValueKind.String)text=content.GetString()??"";
            else if(content.ValueKind==JsonValueKind.Array)
            {
                var sb=new StringBuilder();
                foreach(var block in content.EnumerateArray())   // text blocks only: tool calls, tool output and thinking are not part of the briefing
                    if(block.ValueKind==JsonValueKind.Object&&Str(block,"type")=="text"&&Str(block,"text") is {} t){if(sb.Length>0)sb.Append(' ');sb.Append(t);if(sb.Length>MaxChars*4)break;}
                text=sb.ToString();
            }
            else return null;
            text=text.TrimStart();
            if(Noise.Any(n=>text.StartsWith(n,StringComparison.Ordinal)))return null;
            // Redact the longer stretch first so a secret cut by the cap cannot survive as a prefix.
            string clean=SecretFilter.Clip(SecretFilter.Redact(text.Length>MaxChars*4?text[..(MaxChars*4)]:text),MaxChars);
            if(clean.Length==0)return null;
            return new(type,Str(root,"timestamp"),clean);
        }
        catch(JsonException){return null;}
    }
    static string? Str(JsonElement e,string name)=>e.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
    static bool Flag(JsonElement e,string name)=>e.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.True;
}
