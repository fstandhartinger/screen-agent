using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
namespace ScreenAgent;
// Start briefing (v0.1.7): at session start, and when the voice agent asks for a refresh through the command channel, the app tells the Desk what is on the laptop:
// visible windows, Chrome tabs (UI Automation only, never the debugging port: that would pop up Chrome's remote-debugging dialog), the last OCR text and the tail of the main Claude Code session.
// Everything runs on background threads inside a 1.5 s budget and never touches the UI thread or the session. The log gets counts and reasons, never the contents.
internal sealed class BriefingCollector
{
    internal sealed record Win(IntPtr Handle,string Process,string Title);
    readonly AppConfig config;
    readonly DeviceClient client;
    readonly Action<string,Dictionary<string,object?>> log;
    readonly Func<string?> currentSid;
    readonly Func<string,bool?> distroRunning;
    readonly PrivacyPolicy policy;
    readonly object gate=new();
    readonly Timeboxed.SingleFlight flight=new();   // a source whose thread is stuck is not started again until it returns
    string? screenText;
    int inFlight;
    long tailBytes=-1;
    internal TimeSpan[] RetryDelays={TimeSpan.FromSeconds(1.5),TimeSpan.FromSeconds(3),TimeSpan.FromSeconds(6)};   // the gateway answers 409 until its session exists
    internal bool IncludeOwnWindows;   // selftests only
    public BriefingCollector(AppConfig config,DeviceClient client,Action<string,Dictionary<string,object?>> log,Func<string?> currentSid,Func<string,bool?>? distroRunning=null)
    {
        this.config=config;this.client=client;this.log=log;this.currentSid=currentSid;this.distroRunning=distroRunning??WslRunning;policy=config.Policy();
    }
    // What OCR read in the last frame; gone as soon as the picture is forgotten (pause, lock, excluded window, end of session).
    public void NoteScreenText(string? text){lock(gate)screenText=string.IsNullOrWhiteSpace(text)?null:text;}
    string? ScreenTextNow(){lock(gate)return screenText;}
    public Task Start(string sid,CancellationToken ct)=>config.Briefing?Run(sid,null,ct):Task.CompletedTask;
    public Task Request(string sid,string commandId,CancellationToken ct)=>config.Briefing?Run(sid,commandId,ct):Task.CompletedTask;
    async Task Run(string sid,string? commandId,CancellationToken ct)
    {
        if(Interlocked.Increment(ref inFlight)>2)
        {
            Interlocked.Decrement(ref inFlight);log("briefing.fail",new(){["stage"]="start",["reason"]="busy"});
            // The Desk's tool call waits for an answer to its command id: an empty one beats a timeout.
            if(commandId is not null){try{await Post(sid,BriefingPayload.Build([],[],null,null,commandId),0,Stopwatch.StartNew(),ct);}catch(OperationCanceledException){ /* cancelled: the session or call ended */ }}
            return;
        }
        try
        {
            var clock=Stopwatch.StartNew();
            var collecting=Task.Factory.StartNew(Collect,ct,TaskCreationOptions.LongRunning,TaskScheduler.Default);   // blocks up to the budget: its own thread, not the pool
            // The start briefing is sent right after the session starts, before the first picture was read: give OCR the time the sources need anyway.
            if(commandId is null&&ScreenTextNow() is null){for(int waited=0;waited<1500&&ScreenTextNow() is null&&!collecting.IsCompleted;waited+=50)await Task.Delay(50,ct);}
            var got=await collecting;
            string? text=ScreenTextNow();
            foreach(var (stage,reason) in got.Failures)log("briefing.fail",new(){["stage"]=stage,["reason"]=reason});
            var body=BriefingPayload.Build(got.Windows,got.Tabs,got.Main,text,commandId);
            int bytes=JsonSerializer.SerializeToUtf8Bytes(body).Length;
            var missing=got.Missing.ToList();if(text is null)missing.Add("screen-text");
            log("briefing.collect",new(){["windows"]=Math.Min(got.Windows.Count,BriefingPayload.MaxWindows),["tabs"]=Math.Min(got.Tabs.Count,BriefingPayload.MaxTabs),["messages"]=got.Main?.Messages.Count??0,["bytes"]=bytes,["ms"]=got.Ms,["tailBytes"]=Interlocked.Read(ref tailBytes),["missing"]=missing.ToArray(),["asked"]=commandId is not null});
            if(body.Count==(commandId is null?0:1))return;   // nothing to say (a requested refresh is still answered, empty)
            await Post(sid,body,bytes,clock,ct);
        }
        catch(OperationCanceledException){ /* cancelled: the session or call ended */ }
        catch(Exception e){log("briefing.fail",Redact.Of(e,new(){["stage"]="collect",["reason"]=e.GetType().Name}));}
        finally{Interlocked.Decrement(ref inFlight);}
    }
    async Task Post(string sid,Dictionary<string,object?> body,int bytes,Stopwatch clock,CancellationToken ct)
    {
        string path="api/device/session/"+Uri.EscapeDataString(sid)+"/briefing";
        for(int attempt=1;;attempt++)
        {
            string reason;int status=0;
            if(currentSid()!=sid)reason="session-ended";
            else
            {
                try
                {
                    using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    using var reply=await client.Raw(HttpMethod.Post,path,body,timeout.Token);
                    status=reply.Status;
                    if(reply.Ok){log("briefing.sent",new(){["status"]=status,["attempts"]=attempt,["bytes"]=bytes,["ms"]=clock.ElapsedMilliseconds});return;}
                    reason="http-"+status;
                }
                catch(OperationCanceledException) when(ct.IsCancellationRequested){return;}   /* cancelled */
                catch(Exception e){reason=e is OperationCanceledException?"timeout":e.GetType().Name;Diag.Fail("briefing-source",e);}
            }
            bool retry=reason!="session-ended"&&(status==0||status is 404 or 409 or 429 or >=500)&&attempt<=RetryDelays.Length;
            if(!retry){log("briefing.fail",new(){["stage"]="post",["reason"]=reason,["status"]=status,["attempts"]=attempt,["bytes"]=bytes});return;}
            await Task.Delay(RetryDelays[attempt-1],ct);
        }
    }
    internal Gathered Collect()
    {
        Interlocked.Exchange(ref tailBytes,-1);
        return BriefingGather.Run(Windows,Tabs,ct=>MainSessionSource.Read(config.MainSessionPath,distroRunning,ct,s=>Interlocked.Exchange(ref tailBytes,s.BytesRead)),flight:flight);
    }
    Part<List<BriefingWindow>> Windows(CancellationToken ct)
    {
        var list=new List<BriefingWindow>();
        foreach(var w in VisibleWindows(IncludeOwnWindows,ct))
        {
            if(policy.Blocked(w.Process,w.Title))continue;
            list.Add(new(w.Process,w.Title));
            if(list.Count>=BriefingPayload.MaxWindows)break;
        }
        return new(list);
    }
    Part<List<TabInfo>> Tabs(CancellationToken ct)
    {
        var tabs=new List<TabInfo>();int n=0;
        foreach(var w in VisibleWindows(IncludeOwnWindows,ct).Where(w=>w.Process.Equals("chrome",StringComparison.OrdinalIgnoreCase)))
        {
            if(ct.IsCancellationRequested||tabs.Count>=BriefingPayload.MaxTabs)break;
            if(policy.Blocked(w.Process,w.Title))continue;
            try
            {
                var found=TabsOf(AutomationElement.FromHandle(w.Handle),"Chrome "+(++n));
                tabs.AddRange(found.Where(t=>!policy.Blocked(w.Process,t.Title,t.Url is {} u&&!u.Contains("://")?"https://"+u:t.Url)));   // the omnibox shows the address without its scheme
            }
            catch(Exception e) when(e is ElementNotAvailableException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException){ /* the window closed while we looked */ }
        }
        return new(tabs,tabs.Count==0?"tabs:none":null);
    }
    // The tab strip is the first Tab control of a Chrome window (it comes before the page in the UIA tree, so the search ends early). The address of the selected tab is the omnibox value.
    internal static List<TabInfo> TabsOf(AutomationElement root,string label)
    {
        var result=new List<TabInfo>();
        var strip=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Tab));
        if(strip is null)return result;
        string? url=null;
        foreach(AutomationElement item in strip.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.TabItem)))
        {
            string name=item.Current.Name??"";
            bool selected=item.TryGetCurrentPattern(SelectionItemPattern.Pattern,out var p)&&((SelectionItemPattern)p).Current.IsSelected;
            if(selected&&url is null)
            {
                var omnibox=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.ClassNameProperty,"OmniboxViewViews"));
                if(omnibox is not null&&omnibox.TryGetCurrentPattern(ValuePattern.Pattern,out var v))url=((ValuePattern)v).Current.Value;
            }
            if(name.Length>0)result.Add(new(label,name,selected,selected?url:null));
        }
        return result;
    }
    // Visible top-level windows in z-order (topmost first). Not the desktop, tool windows, windows on other virtual desktops (cloaked) or the app's own windows.
    internal static List<Win> VisibleWindows(bool includeOwn,CancellationToken ct)
    {
        var list=new List<Win>();var cache=new Dictionary<uint,string>();var sb=new StringBuilder(512);uint own=(uint)Environment.ProcessId;
        Native.EnumWindows((h,_)=>
        {
            try
            {
                if(ct.IsCancellationRequested||list.Count>=80)return false;
                if(!Native.IsWindowVisible(h)||Native.GetWindow(h,4)!=IntPtr.Zero)return true;   // visible or minimized, top level, not owned
                sb.Clear();Native.GetWindowText(h,sb,512);string title=sb.ToString();
                if(title.Length==0)return true;
                sb.Clear();Native.GetClassName(h,sb,512);if(sb.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd")return true;
                long ex=Native.GetWindowLongPtr(h,-20).ToInt64();
                if((ex&0x80)!=0&&(ex&0x40000)==0)return true;   // tool window without an app window style
                if(Native.DwmGetWindowAttribute(h,14,out int cloaked,4)==0&&cloaked!=0)return true;
                Native.GetWindowThreadProcessId(h,out uint pid);
                if(pid==own&&!includeOwn)return true;
                if(!cache.TryGetValue(pid,out var name)){try{using var p=Process.GetProcessById((int)pid);name=p.ProcessName;}catch(Exception){name="";}   /* the process is gone */cache[pid]=name;}
                list.Add(new(h,name,title));
            }
            catch(Exception ex){Diag.Fail("briefing-window",ex);}   // one odd window never ends the list
            return true;
        },IntPtr.Zero);
        return list;
    }
    // wsl.exe -l --running does not start a distro; opening \\wsl.localhost\<distro> does. null = could not be asked (then the share is read under the section timeout).
    internal static bool? WslRunning(string distro)
    {
        try
        {
            using var p=Process.Start(new ProcessStartInfo("wsl.exe","-l --running"){RedirectStandardOutput=true,UseShellExecute=false,CreateNoWindow=true});
            if(p is null)return null;
            using var output=new MemoryStream();
            var copy=p.StandardOutput.BaseStream.CopyToAsync(output);
            if(!p.WaitForExit(700)){try{p.Kill();}catch(Exception){ /* already exited */ }Diag.Event("briefing.git.timeout",new(){["ms"]=700});return null;}
            copy.Wait(200);
            return p.ExitCode==0?WslProbe.Running(output.ToArray(),distro):null;
        }
        catch(Exception ex){Diag.Fail("briefing-git",ex);return null;}
    }
}
