using System.Diagnostics;
using System.Drawing;
using System.Windows.Automation;
using ScreenAgent.Core;
namespace ScreenAgent;
internal sealed record ScreenTextResult(string Process,string Title,string Text,string UiaText,string OcrText,long UiaMs,long OcrMs,long Ms,IntPtr Window);
// Capture only on worker threads. UIA providers can hang inside COM: one bounded, disposable background MTA lane,
// with a wall-clock deadline for the caller and no new lane until the old call has returned.
internal sealed class ScreenTextSource
{
    readonly PrivacyPolicy policy;readonly Func<bool> allowed;
    static int uiaBusy;
    public ScreenTextSource(PrivacyPolicy policy,Func<bool> allowed){this.policy=policy;this.allowed=allowed;}
    public bool Current(ScreenTextResult result)=>new PrivacyCapture(policy,allowed,captureAddress:false).Allowed(result.Window);
    public Task<ScreenTextResult?> Capture(string reason,CancellationToken ct)=>Capture(reason,ct,null,null);
    public Task<ScreenTextResult?> Capture(string reason,CancellationToken ct,string? reusedOcr,Func<ScreenTextResult,Task>? early=null)=>Task.Run(async ()=>
    {
        if(!allowed()||ct.IsCancellationRequested)return null;
        var clock=Stopwatch.StartNew();var fg=Native.GetForegroundWindow();
        if(fg==IntPtr.Zero)return null;
        var monitor=System.Windows.Forms.Screen.FromHandle(fg).Bounds;
        var privacy=new PrivacyCapture(policy,()=>allowed()&&!ct.IsCancellationRequested&&Native.GetForegroundWindow()==fg,captureAddress:false);
        using var image=privacy.Capture(monitor,true,false,out var info);
        if(image is null||info is null)return null;
        var window=info.Foreground;
        bool Valid()=>allowed()&&!ct.IsCancellationRequested&&Native.GetForegroundWindow()==fg;
        ScreenTextResult Result(string uia,string ocr,long um,long om)=>new(window.Process,ScreenTextLogic.Tidy(window.Title) is {} title?title[..Math.Min(200,title.Length)]:"",ScreenTextLogic.Merge(uia,ocr),uia,ocr,um,om,clock.ElapsedMilliseconds,fg);
        var ocrClock=Stopwatch.StartNew();
        var ocrTask=reusedOcr is not null?Task.FromResult((ScreenTextLogic.Tidy(reusedOcr),0L)):ReadOcr(PrivacyCapture.Png(image),ocrClock,ct);
        var uiaClock=Stopwatch.StartNew();string uia=await ReadUia(fg,info,ct);long uiaMs=uiaClock.ElapsedMilliseconds;
        if(!Valid())return null;
        if(early is not null&&uia.Split('\n',StringSplitOptions.RemoveEmptyEntries).Length>=10&&!ocrTask.IsCompleted)await early(Result(uia,"",uiaMs,0));
        var (ocr,ocrMs)=await ocrTask;
        return Valid()?Result(uia,ocr,uiaMs,ocrMs):null;
    },ct);
    static async Task<(string,long)> ReadOcr(byte[] png,Stopwatch clock,CancellationToken ct)
    {
        try{var result=await Task.Run(()=>Ocr.ReadMemory(png),ct).WaitAsync(TimeSpan.FromSeconds(4),ct);return(ScreenTextLogic.Tidy(result.Text),clock.ElapsedMilliseconds);}
        catch{ /* OCR is optional; UIA still supplies speech context. */ return("",clock.ElapsedMilliseconds); } // OCR is optional; do not turn a speech hint into a user-visible failure
    }
    internal static async Task<string> ReadUia(IntPtr fg,CaptureInfo info,CancellationToken ct)
    {
        if(Interlocked.CompareExchange(ref uiaBusy,1,0)!=0)return "";
        var lines=new List<string>();var sync=new object();var clock=Stopwatch.StartNew();
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            try
            {
                var cache=new CacheRequest{TreeScope=TreeScope.Element};
                foreach(var property in new[]{AutomationElement.NameProperty,AutomationElement.ControlTypeProperty,AutomationElement.IsOffscreenProperty,AutomationElement.IsPasswordProperty,AutomationElement.BoundingRectangleProperty})cache.Add(property);
                var queue=new Queue<AutomationElement>();queue.Enqueue(AutomationElement.FromHandle(fg).GetUpdatedCache(cache));int nodes=0;
                bool More()=>nodes<400&&clock.ElapsedMilliseconds<250&&!ct.IsCancellationRequested;
                var walker=TreeWalker.ControlViewWalker;
                while(queue.Count>0&&More())
                {
                    var e=queue.Dequeue();nodes++;
                    try
                    {
                        var c=e.Cached;if(c.IsOffscreen||c.IsPassword)continue;
                        var r=c.BoundingRectangle;
                        if(info.Windows.Any(w=>w.Blocked&&w.Rect.IntersectsWith(new Rectangle((int)r.X,(int)r.Y,(int)r.Width,(int)r.Height))))continue;
                        string? value=null;var t=c.ControlType;
                        if(t==ControlType.Edit){if(e.TryGetCurrentPattern(ValuePattern.Pattern,out var p))value=((ValuePattern)p).Current.Value;}
                        else if(t==ControlType.Text||t==ControlType.Hyperlink||t==ControlType.Button||t==ControlType.ListItem||t==ControlType.TabItem||t==ControlType.MenuItem||t==ControlType.Document)value=c.Name;
                        if(!string.IsNullOrWhiteSpace(value))lock(sync)lines.Add(value);
                        var child=walker.GetFirstChild(e,cache);int children=0;
                        while(child is not null&&More()&&children++<400&&queue.Count<400){queue.Enqueue(child);child=walker.GetNextSibling(child,cache);}
                    }
                    catch(ElementNotAvailableException){ /* The element disappeared between enumeration and reading. */ }
                }
            }
            catch{ } // A failed provider contributes no text; the masked image is still available to OCR.
            finally{Interlocked.Exchange(ref uiaBusy,0);done.TrySetResult();}
        }){IsBackground=true,Name="Screen text UIA"};thread.SetApartmentState(ApartmentState.MTA);thread.Start();
        await Task.WhenAny(done.Task,Task.Delay(250,ct));
        lock(sync)return ScreenTextLogic.Tidy(string.Join("\n",lines));
    }
}
