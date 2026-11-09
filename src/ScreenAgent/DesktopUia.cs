using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
namespace ScreenAgent;
internal sealed class DesktopUia(FreeHost host,DesktopInput input)
{
    sealed record Held(string Ref,IntPtr Window,AutomationElement Element,string Runtime);
    readonly ConcurrentDictionary<string,Held> refs=new();
    Task? inFlight;
    long next;int generation;readonly AsyncLocal<Action?> lease=new();
    void Check(){lease.Value?.Invoke();host.ThrowIfStopped();}
    public void Reset(){Interlocked.Increment(ref generation);refs.Clear();}
    T Bounded<T>(Func<T> action,int ms=4000)
    {
        if(inFlight is {IsCompleted:false})throw new TimeoutException("UI Automation antwortet noch; anderes Fenster versuchen.");
        using var cancelled=new CancellationTokenSource(ms);var token=cancelled.Token;int epoch=generation;
        var task=Task.Run(()=>{lease.Value=()=>{token.ThrowIfCancellationRequested();if(epoch!=generation)throw new OperationCanceledException();};try{Check();return action();}finally{lease.Value=null;}});inFlight=task;var sw=Stopwatch.StartNew();
        try{while(!task.IsCompleted){host.ThrowIfStopped();if(sw.ElapsedMilliseconds>=ms)throw new TimeoutException("UI Automation antwortet nicht.");Thread.Sleep(20);}
        return task.GetAwaiter().GetResult();}catch(OperationCanceledException) when(!host.Stopped()){throw new TimeoutException("UI Automation hat das Zeitlimit überschritten.");}finally{cancelled.Cancel();}
    }
    static string Clean(string s,int max=80)=>new string(s.Take(max).Select(c=>char.IsControl(c)?' ':c).ToArray());
    string Line(Held held)
    {
        var c=held.Element.Current;string role=c.ControlType.ProgrammaticName.Replace("ControlType.","");string line=$"{held.Ref} {role} \"{Clean(c.Name??"")}\"";
        var f=host.Frame;var b=c.BoundingRectangle;
        if(f.Kind is FrameKind.Screen or FrameKind.Window or FrameKind.Region&&!b.IsEmpty){var p=f.FromTarget(b.X+b.Width/2,b.Y+b.Height/2);if(f.Contains(p.X,p.Y))line+=$" [{Math.Round(p.X)},{Math.Round(p.Y)}]";}
        if(!c.IsEnabled)line+=" (aus)";if(c.IsOffscreen)line+=" (offscreen)";
        if(held.Element.TryGetCurrentPattern(ValuePattern.Pattern,out var value))line+=" = \""+Clean(((ValuePattern)value).Current.Value)+"\"";
        return line;
    }
    public FreeResult Tree(JsonElement args,bool find)
    {
        if(inFlight is {IsCompleted:false})return FreeResult.Fail("timeout","UI Automation antwortet noch; anderes Fenster versuchen.");
        string? window=FreeArgs.Str(args,"window");var windows=window=="all"?DesktopWindows.List(true):[DesktopWindows.Resolve(window)];
        foreach(var key in refs.Where(p=>windows.Contains(p.Value.Window)).Select(p=>p.Key).ToArray())refs.TryRemove(key,out _);
        int depth=Math.Clamp(FreeArgs.Int(args,"depth")??(find?40:12),1,40),max=find?30:Math.Clamp(FreeArgs.Int(args,"max")??400,1,2000);
        string filter=FreeArgs.Str(args,find?"name":"filter")??"",role=FreeArgs.Str(args,"role")??"";
        var found=new ConcurrentQueue<(Held Held,string Line)>();using var stop=new CancellationTokenSource();var token=stop.Token;bool cut=false;
        var task=Task.Run(()=>
        {
            var sw=Stopwatch.StartNew();int chars=0,count=0;var queue=new Queue<(IntPtr Window,AutomationElement Element,int Depth)>();
            foreach(var h in windows)queue.Enqueue((h,AutomationElement.FromHandle(h),0));
            while(queue.Count>0)
            {
                if(token.IsCancellationRequested||sw.ElapsedMilliseconds>=3850){cut=true;break;}host.ThrowIfStopped();
                var (h,e,d)=queue.Dequeue();
                try
                {
                    var c=e.Current;string name=c.Name??"",type=c.ControlType.ProgrammaticName.Replace("ControlType.","");
                    if(name.Contains(filter,StringComparison.OrdinalIgnoreCase)&&(role.Length==0||type.Equals(role,StringComparison.OrdinalIgnoreCase)))
                    {
                        var held=new Held("r"+Interlocked.Increment(ref next),h,e,string.Join(".",e.GetRuntimeId()));string line=Line(held);
                        if(chars+line.Length>11800||count>=max){cut=true;break;}found.Enqueue((held,line));chars+=line.Length+1;count++;
                    }
                    if(d>=depth)continue;
                    var child=TreeWalker.ControlViewWalker.GetFirstChild(e);
                    while(child is not null)
                    {
                        if(token.IsCancellationRequested||sw.ElapsedMilliseconds>=3850||queue.Count>=4000){cut=true;break;}
                        queue.Enqueue((h,child,d+1));child=TreeWalker.ControlViewWalker.GetNextSibling(child);
                    }
                }
                catch(ElementNotAvailableException){ /* The provider removed this element during the snapshot; continue with its siblings. */ }
                catch(System.Runtime.InteropServices.COMException){ /* A failed provider contributes no nodes; other windows remain readable. */ }
            }
        });inFlight=task;var clock=Stopwatch.StartNew();
        try{while(!task.IsCompleted&&clock.ElapsedMilliseconds<4000){host.ThrowIfStopped();Thread.Sleep(20);}if(task.IsCompleted)task.GetAwaiter().GetResult();else cut=true;}
        finally{stop.Cancel();}
        // Publish only what was completed by the deadline. A stalled provider never writes into the live ref store.
        var rows=found.ToArray();foreach(var row in rows)refs[row.Held.Ref]=row.Held;
        return new(true,string.Join("\n",rows.Select(r=>r.Line))+(cut?"\nabgeschnitten — mit filter, window oder depth eingrenzen.":rows.Length==0?"Keine Elemente gefunden.":""));
    }
    Held Get(string reference)
    {
        if(!refs.TryGetValue(reference,out var held))throw new UiaStaleException("Referenz abgelaufen; ui_tree oder ui_find erneut aufrufen.");
        if(!DesktopNative.IsWindow(held.Window)||string.Join(".",held.Element.GetRuntimeId())!=held.Runtime)throw new UiaStaleException("Element nicht mehr vorhanden; ui_tree erneut aufrufen.");return held;
    }
    public FreeResult Read(string reference)=>Bounded(()=>
    {
        var h=Get(reference);var text=new StringBuilder(Line(h));
        if(h.Element.TryGetCurrentPattern(TextPattern.Pattern,out var p))text.Append('\n').Append(((TextPattern)p).DocumentRange.GetText(20000));
        else if(h.Element.TryGetCurrentPattern(ValuePattern.Pattern,out p))text.Append('\n').Append(((ValuePattern)p).Current.Value[..Math.Min(20000,((ValuePattern)p).Current.Value.Length)]);
        return new FreeResult(true,text.ToString());
    });
    public FreeResult Click(string reference,string button,bool twice)=>Bounded(()=>
    {
        var h=Get(reference);Check();if(!h.Element.Current.IsEnabled)return FreeResult.Fail("disabled","Element ist deaktiviert.");
        object? p;
        if(button=="left"&&!twice)
        {
            if(h.Element.TryGetCurrentPattern(InvokePattern.Pattern,out p)){Check();((InvokePattern)p).Invoke();return new FreeResult(true,"angeklickt",Target:reference);}
            if(h.Element.TryGetCurrentPattern(TogglePattern.Pattern,out p)){Check();((TogglePattern)p).Toggle();return new FreeResult(true,"umgeschaltet",Target:reference);}
            if(h.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern,out p)){Check();((SelectionItemPattern)p).Select();return new FreeResult(true,"ausgewählt",Target:reference);}
            if(h.Element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern,out p)){Check();((ExpandCollapsePattern)p).Expand();return new FreeResult(true,"aufgeklappt",Target:reference);}
        }
        Check();DesktopWindows.Focus(h.Window);var r=h.Element.Current.BoundingRectangle;if(r.IsEmpty||h.Element.Current.IsOffscreen)return FreeResult.Fail("offscreen","Element ist nicht sichtbar; zuerst in den sichtbaren Bereich scrollen.");
        Check();input.Click((int)(r.X+r.Width/2),(int)(r.Y+r.Height/2),button,twice?2:1);return new FreeResult(true,"geklickt",Target:reference);
    });
    public FreeResult Type(string reference,string text,bool clear,bool enter)
    {
        // UIA lookup is bounded; potentially long unicode input remains on the cancellable agent worker.
        var h=Bounded(()=>Get(reference));bool set=Bounded(()=>
        {
            Check();DesktopWindows.Focus(h.Window);Check();h.Element.SetFocus();
            if(clear&&h.Element.TryGetCurrentPattern(ValuePattern.Pattern,out var p)&&!((ValuePattern)p).Current.IsReadOnly){Check();((ValuePattern)p).SetValue(text);return true;}return false;
        });
        if(set){if(enter)input.Keys("enter");}
        else{if(clear)input.Keys("ctrl+a");input.Type(text,text.Length>200||text.Contains('\n'),enter);}
        return new(true,"Text eingefügt",Target:reference);
    }
}
