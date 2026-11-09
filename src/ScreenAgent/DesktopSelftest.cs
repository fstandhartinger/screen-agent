using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Point=System.Drawing.Point;
using Size=System.Drawing.Size;
using Forms=System.Windows.Forms;
namespace ScreenAgent;
internal static class DesktopSelftest
{
    sealed class TestWindow:Forms.Form
    {
        public readonly Forms.TextBox Edit=new(){Location=new(20,210),Width=300,Height=65,Multiline=true,AccessibleName="Testtext"};
        public readonly Forms.ListBox List=new(){Location=new(400,40),Width=180,Height=220,AccessibleName="Scrollliste"};
        public Point LastDown,LastUp;public int Moves,Clicks,ClickCount,Choice;public Forms.MouseButtons Button;
        bool dragging;
        public TestWindow()
        {
            Text="Desktop-Control-Fixture";StartPosition=Forms.FormStartPosition.Manual;Location=new(100,100);Size=new(650,420);BackColor=Color.White;AutoScaleMode=Forms.AutoScaleMode.None;
            Controls.Add(Edit);Controls.Add(List);List.Items.AddRange(Enumerable.Range(0,100).Select(i=>(object)("Zeile "+i)).ToArray());
            var menu=new Forms.ContextMenuStrip();menu.Items.Add("Auswahl treffen",null,(_,_)=>Choice++);ContextMenuStrip=menu;
            MouseDown+=(_,e)=>{LastDown=PointToScreen(e.Location);Button=e.Button;ClickCount=e.Clicks;Clicks++;Moves=0;dragging=true;Capture=true;};
            MouseMove+=(_,e)=>{if(dragging)Moves++;};MouseUp+=(_,e)=>{LastUp=PointToScreen(e.Location);dragging=false;Capture=false;};
            Paint+=(_,e)=>{e.Graphics.FillRectangle(Brushes.Red,40,50,80,80);e.Graphics.FillRectangle(Brushes.Blue,220,50,80,80);};
        }
    }
    sealed record Check(string name,bool ok,long ms,string why,string status="passed");
    static readonly List<Check> checks=[];
    static Application app=null!;static DesktopSession session=null!;static TestWindow fixture=null!;
    static string root="";static int step;
    static T Ui<T>(Func<T> f)=>app.Dispatcher.Invoke(f);
    static void Ui(Action f)=>app.Dispatcher.Invoke(f);
    static void Require(bool condition,string why){if(!condition)throw new InvalidOperationException(why);}
    static string Text(JsonElement r)=>r.TryGetProperty("result",out var v)&&v.TryGetProperty("text",out var t)?t.GetString()??"":"";
    static bool Good(JsonElement r)=>r.TryGetProperty("ok",out var v)&&v.ValueKind==JsonValueKind.True;
    static void Save(string name)
    {
        var b=DesktopScreens.VirtualBounds();using var bitmap=new Bitmap(b.Width,b.Height);using(var g=Graphics.FromImage(bitmap))g.CopyFromScreen(b.X,b.Y,0,0,b.Size);bitmap.Save(Path.Combine(root,$"{Interlocked.Increment(ref step):000}-{name}.png"),ImageFormat.Png);
    }
    static async Task<JsonElement> Act(string tool,object? args=null,bool require=true)
    {
        var r=await session.Act(tool,args);Save(tool);if(require)Require(Good(r),tool+": "+r.GetRawText()[..Math.Min(700,r.GetRawText().Length)]);return r;
    }
    static async Task CheckIt(string name,Func<Task> test)
    {
        var sw=Stopwatch.StartNew();try{await test();checks.Add(new(name,true,sw.ElapsedMilliseconds,""));}catch(Exception e){checks.Add(new(name,false,sw.ElapsedMilliseconds,e.Message,"failed"));}Save(name);
    }
    static string Reference(string text,string role,string? name=null)
    {
        foreach(Match m in Regex.Matches(text,"^(r[0-9]+) (\\w+) \"([^\"]*)\"",RegexOptions.Multiline))if(m.Groups[2].Value.Equals(role,StringComparison.OrdinalIgnoreCase)&&(name is null||m.Groups[3].Value.Contains(name)))return m.Groups[1].Value;
        throw new InvalidOperationException("Element fehlt: "+role+" / "+name+"; "+text[..Math.Min(1000,text.Length)]);
    }
    static PixelFrame Current(JsonElement image)
    {
        var m=Regex.Match(Text(image),@"^frame: \w+ (\d+)x(\d+)");Require(m.Success,"Frame-Zeile fehlt");
        var bounds=DesktopScreens.Monitors()[0].Bounds;return new(int.Parse(m.Groups[1].Value),int.Parse(m.Groups[2].Value),bounds.X,bounds.Y,bounds.Width,bounds.Height);
    }
    static async Task FocusFixture(){await Act("window_focus",new{window=DesktopWindows.Ref(Ui(()=>fixture.Handle)),observe=false});}
    public static int Run()
    {
        app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};int exit=1;
        app.Startup+=async (_,_)=>{try{exit=await Task.Run(All);}catch(Exception e){Console.WriteLine(JsonSerializer.Serialize(new{error=e.ToString()}));}finally{app.Shutdown(exit);}};app.Run();return exit;
    }
    static async Task<int> All()
    {
        root=Path.GetFullPath("artifacts/desktop");Directory.CreateDirectory(root);using var s=new DesktopSession(app,Path.Combine(root,"computer.log"));session=s;
        fixture=Ui(()=>{var form=new TestWindow();form.Show();return form;});string fw=DesktopWindows.Ref(Ui(()=>fixture.Handle));
        Process? browser=null;IntPtr notepad=IntPtr.Zero;string temp=Path.Combine(root,"roundtrip.txt");
        try
        {
            Require(Good(await session.Start("Teste den Desktop")),"Start fehlgeschlagen");
            await CheckIt("a-frame-region",async()=>
            {
                await FocusFixture();var image=await Act("screenshot",new{monitor=0,grid=true});var f=Current(image);
                using var memory=new MemoryStream(Convert.FromBase64String(image.GetProperty("result").GetProperty("jpeg").GetString()!));using var bitmap=new Bitmap(memory);Require(bitmap.Width==f.Width&&bitmap.Height==f.Height,"Bildgröße stimmt nicht");
                var cursor=Regex.Match(Text(image),@"cursor: (-?\d+),(-?\d+)");Require(cursor.Success,"Cursor fehlt");int cx=int.Parse(cursor.Groups[1].Value),cy=int.Parse(cursor.Groups[2].Value);
                if(cx>=8&&cy>=8&&cx<bitmap.Width-8&&cy<bitmap.Height-8){bool mark=false;for(int x=cx-3;x<=cx+3;x++)for(int y=cy-3;y<=cy+3;y++){var c=bitmap.GetPixel(x,y);mark|=c.R>150&&c.B>100&&c.G<130;}Require(mark,"Cursor-Kreuz fehlt");}
                var square=Ui(()=>fixture.PointToScreen(new Point(80,90)));var q=f.Image(square.X,square.Y);double rx=Math.Floor(q.X-30),ry=Math.Floor(q.Y-30);var crop=f.Crop(rx,ry,60,60,1280);
                var zoom=await Act("screenshot",new{region=new[]{rx,ry,60.0,60.0}});Require(Text(zoom).StartsWith("frame: region"),"Region fehlt");var z=crop.Image(square.X,square.Y);
                await Act("click",new{x=z.X,y=z.Y,observe=false});await Task.Delay(50);var hit=Ui(()=>fixture.LastDown);Require(Math.Abs(hit.X-square.X)<=1&&Math.Abs(hit.Y-square.Y)<=1,$"Klick {hit} != {square}");
                // Pure math still maps under an alternate thread context; capture/input themselves remain PerMonitorV2.
                var old=DesktopNative.SetThreadDpiAwarenessContext((IntPtr)(-1));try{Require(crop.Physical(z.X,z.Y)==(square.X,square.Y),"DPI-Kontext beeinflusst Frame-Mathe");}finally{DesktopNative.SetThreadDpiAwarenessContext(old);}
            });
            await CheckIt("b-notepad-unicode",async()=>
            {
                await Act("open",new{target="notepad.exe",settle_ms=700});
                for(int i=0;i<30;i++){notepad=DesktopWindows.List().FirstOrDefault(h=>UiaDriver.ProcessOf(h).Equals("notepad",StringComparison.OrdinalIgnoreCase));if(notepad!=IntPtr.Zero)break;await Task.Delay(100);}
                Require(notepad!=IntPtr.Zero,"Notepad fehlt");await Act("window_focus",new{window=DesktopWindows.Ref(notepad),observe=false});
                const string value="Grüße äöü ß 😀\nZweite Zeile";await Act("type",new{text=value,paste=false,observe=false});await Act("key",new{key="ctrl+a ctrl+c",observe=false});await Task.Delay(150);
                var clip=await Act("clipboard_read");Require(Text(clip).Replace("\r\n","\n")==value,"Unicode-Readback: "+Text(clip));
                var tree=await Act("ui_tree",new{window=DesktopWindows.Ref(notepad)});var edit=Regex.Match(Text(tree),@"^(r\d+) (Document|Edit) ",RegexOptions.Multiline);Require(edit.Success,"Notepad-Editor fehlt");Require(Text(await Act("ui_read",new{@ref=edit.Groups[1].Value})).Contains("Grüße"),"UIA-Readback fehlt");
            });
            await CheckIt("c-context-menu",async()=>
            {
                await FocusFixture();var shot=await Act("screenshot",new{monitor=0});var f=Current(shot);var p=Ui(()=>fixture.PointToScreen(new Point(80,90)));var q=f.Image(p.X,p.Y);await Act("click",new{x=q.X,y=q.Y,button="right",settle_ms=100});
                var found=await Act("ui_find",new{name="Auswahl treffen",window="all"});await Act("ui_click",new{@ref=Reference(Text(found),"MenuItem"),observe=false});await Task.Delay(100);Require(Ui(()=>fixture.Choice)==1,"Menüauswahl fehlt");
            });
            await CheckIt("d-double-middle",async()=>
            {
                await Act("key",new{key="esc",observe=false});await FocusFixture();var f=Current(await Act("screenshot",new{monitor=0}));var p=Ui(()=>fixture.PointToScreen(new Point(80,90)));var q=f.Image(p.X,p.Y);
                await Act("click",new{x=q.X,y=q.Y,count=2,observe=false});await Task.Delay(50);Require(Ui(()=>fixture.ClickCount)==2,"Doppelklick fehlt");await Task.Delay(500);await Act("click",new{x=q.X,y=q.Y,button="middle",observe=false});await Task.Delay(50);Require(Ui(()=>fixture.Button)==Forms.MouseButtons.Middle,"Mittelklick fehlt");
            });
            await CheckIt("e-drag",async()=>
            {
                await FocusFixture();var f=Current(await Act("screenshot",new{monitor=0}));var p=Ui(()=>fixture.PointToScreen(new Point(80,90)));var end=Ui(()=>fixture.PointToScreen(new Point(260,90)));var a=f.Image(p.X,p.Y);var b=f.Image(end.X,end.Y);
                await Act("drag",new{x=a.X,y=a.Y,x2=b.X,y2=b.Y,observe=false});await Task.Delay(100);Require(Ui(()=>fixture.Moves)>=10,"Zu wenige Drag-Schritte");var up=Ui(()=>fixture.LastUp);Require(Math.Abs(up.X-end.X)<=1&&Math.Abs(up.Y-end.Y)<=1,"Drag-Ziel falsch");
                Native.GetWindowRect(Ui(()=>fixture.Handle),out var before);var title=f.Image(before.Left+150,before.Top+12);var dest=f.Image(before.Left+230,before.Top+42);
                await Act("drag",new{x=title.X,y=title.Y,x2=dest.X,y2=dest.Y,observe=false});await Task.Delay(100);Native.GetWindowRect(Ui(()=>fixture.Handle),out var after);Require(after.Left!=before.Left||after.Top!=before.Top,"Titelleisten-Drag fehlt");
            });
            await CheckIt("f-wheel",async()=>
            {
                await FocusFixture();var f=Current(await Act("screenshot",new{monitor=0}));var p=Ui(()=>fixture.List.PointToScreen(new Point(80,80)));var q=f.Image(p.X,p.Y);await Act("click",new{x=q.X,y=q.Y,observe=false});int before=Ui(()=>fixture.List.TopIndex);
                await Act("wheel",new{x=q.X,y=q.Y,dy=4,observe=false});await Task.Delay(100);int down=Ui(()=>fixture.List.TopIndex);Require(down>before,"Positives dy scrollt nicht abwärts");await Act("wheel",new{dy=-4,observe=false});await Task.Delay(100);Require(Ui(()=>fixture.List.TopIndex)<down,"Negatives dy scrollt nicht aufwärts");
            });
            await CheckIt("h-windows",async()=>
            {
                Require(Text(await Act("ui_windows")).Contains(fw+" "),"Fensterliste fehlt");await Act("window_state",new{window=fw,state="maximize",observe=false});await Task.Delay(100);Require(DesktopNative.IsZoomed(Ui(()=>fixture.Handle)),"Maximieren fehlgeschlagen");
                await Act("window_state",new{window=fw,state="restore",observe=false});await Act("window_state",new{window=fw,state="minimize",observe=false});Require(Native.IsIconic(Ui(()=>fixture.Handle)),"Minimieren fehlgeschlagen");await FocusFixture();Require(!Native.IsIconic(Ui(()=>fixture.Handle)),"Focus stellt nicht wieder her");
            });
            await CheckIt("i-shell-files",async()=>
            {
                var ps=Text(await Act("run_powershell",new{command="$PSVersionTable.PSVersion.Major; Write-Error x"}));Require(ps.Contains("5")&&ps.Contains("x"),"PowerShell stdout/stderr fehlen");
                string pidfile=Path.Combine(root,"child.pid");string script="$p=Start-Process powershell.exe -ArgumentList '-NoProfile -Command Start-Sleep 30' -PassThru; $p.Id | Set-Content '"+pidfile.Replace("'","''")+"'; Start-Sleep 30";
                var timer=Stopwatch.StartNew();var timeout=await Act("run_powershell",new{command=script,timeout_s=2},false);Require(!Good(timeout)&&timeout.GetProperty("rule").GetString()=="timeout"&&timer.ElapsedMilliseconds<5000,"Timeout fehlt");
                if(File.Exists(pidfile)){int pid=int.Parse(File.ReadAllText(pidfile).Trim());bool alive;try{using var child=Process.GetProcessById(pid);alive=!child.HasExited;}catch(ArgumentException){alive=false;}Require(!alive,"Kindprozess lebt nach Timeout");}
                Require(Text(await Act("run_cmd",new{command="echo %USERNAME%"})).Contains(Environment.UserName,StringComparison.OrdinalIgnoreCase),"cmd fehlt");Require(Text(await Act("run_python",new{code="print(6*7)"})).Contains("42"),"Python fehlt");
                var wsl=await Act("run_wsl",new{command="printf wsl",timeout_s=3},false);if(!Good(wsl)&&wsl.GetProperty("rule").GetString()=="no-wsl")checks.Add(new("i-wsl",true,0,"Keine WSL-Distribution auf Runner installiert","skipped"));else Require(Good(wsl)&&Text(wsl).Contains("wsl"),"WSL unerwarteter Fehler");
                await Act("file_write",new{path=temp,content="Grüße 😀"});Require(Text(await Act("file_read",new{path=temp}))=="Grüße 😀","Datei-Roundtrip fehlgeschlagen");
            });
            await CheckIt("j-kill",async()=>
            {
                await FocusFixture();Ui(()=>fixture.Edit.Focus());var typing=session.Act("type",new{text=new string('x',5000),paste=false,observe=false});await Task.Delay(120);var sw=Stopwatch.StartNew();session.Kill();var result=await typing;Require(sw.ElapsedMilliseconds<300,"Kill zu langsam");Require(result.GetProperty("code").GetString()=="killed","Kill-Code fehlt");Require(Good(await session.Start("Desktop weiter testen")),"Neustart fehlgeschlagen");
            });
            await CheckIt("k-latency",async()=>
            {
                await FocusFixture();var f=Current(await Act("screenshot",new{monitor=0}));var p=Ui(()=>fixture.PointToScreen(new Point(80,90)));var q=f.Image(p.X,p.Y);var entries=new Dictionary<string,long>();
                async Task Measure(string name,string tool,object args){var times=new List<long>();for(int i=0;i<5;i++){var clock=Stopwatch.StartNew();var r=await session.Act(tool,args);times.Add(clock.ElapsedMilliseconds);Require(Good(r),name+" failed: "+Text(r));}times.Sort();entries[name]=times[2];}
                await Measure("screenshot","screenshot",new{monitor=0});await Measure("click_observe","click",new{x=q.X,y=q.Y});await Measure("click_no_observe","click",new{x=q.X,y=q.Y,observe=false});Ui(()=>fixture.Edit.Focus());
                await Measure("type100","type",new{text=new string('x',100),paste=false,observe=false});await Measure("key","key",new{key="ctrl+a",observe=false});await Measure("ui_tree_notepad","ui_tree",new{window=DesktopWindows.Ref(notepad)});await Measure("cmd_echo","run_cmd",new{command="echo ok"});
                File.WriteAllText(Path.Combine(root,"latency.json"),JsonSerializer.Serialize(entries,new JsonSerializerOptions{WriteIndented=true}));Require(entries["screenshot"]<=250&&entries["click_observe"]<=700&&entries["key"]<=150,"Latenzziele überschritten: "+JsonSerializer.Serialize(entries));
            });
            await CheckIt("l-browser-no-cdp",async()=>
            {
                using var pages=new Fixtures();string? exe=new[]{Environment.GetEnvironmentVariable("ProgramFiles")+@"\Google\Chrome\Application\chrome.exe",Environment.GetEnvironmentVariable("ProgramFiles(x86)")+@"\Microsoft\Edge\Application\msedge.exe"}.FirstOrDefault(File.Exists);Require(exe is not null,"Kein Browser");
                var start=new ProcessStartInfo(exe!){UseShellExecute=false};foreach(var arg in new[]{"--user-data-dir="+Path.Combine(root,"browser-profile"),"--no-first-run","--no-default-browser-check","--force-renderer-accessibility","--window-size=1000,700","http://site.localhost:"+pages.Port+"/inject"})start.ArgumentList.Add(arg);browser=Process.Start(start);IntPtr h=IntPtr.Zero;
                for(int i=0;i<150;i++){h=DesktopWindows.List().FirstOrDefault(h=>UiaDriver.TitleOf(h).Contains("Inject"));if(h!=IntPtr.Zero)break;await Task.Delay(100);}
                Require(h!=IntPtr.Zero,"Browserfenster fehlt; "+(browser is {HasExited:true}?"exit="+browser.ExitCode:"noch gestartet")+"; "+DesktopWindows.Listing());await Act("window_focus",new{window=DesktopWindows.Ref(h),observe=false});var shot=await Act("screenshot",new{monitor=0});Require(shot.GetProperty("result").TryGetProperty("jpeg",out _),"Browserbild ausgeblendet");
                var found=await Act("ui_find",new{name="Senden",window=DesktopWindows.Ref(h)});await Act("ui_click",new{@ref=Reference(Text(found),"Button","Senden"),observe=false});await Task.Delay(250);Require(pages.Count("site.localhost/send")>0,"Browser-UIA-Klick fehlt");
                found=await Act("ui_find",new{name="Ansehen",window=DesktopWindows.Ref(h)});var xy=Regex.Match(Text(found),@"\[(-?\d+),(-?\d+)\]");Require(xy.Success,"Browser-Koordinaten fehlen");await Act("click",new{x=int.Parse(xy.Groups[1].Value),y=int.Parse(xy.Groups[2].Value),observe=false});await Task.Delay(250);Require(UiaDriver.TitleOf(h).Contains("clicked-ok"),"Browser-Koordinatenklick fehlt");
            });
            await CheckIt("g-chords",async()=>
            {
                await FocusFixture();Ui(()=>{fixture.Edit.Text="abcdef";fixture.Edit.Focus();fixture.Edit.SelectionStart=0;});await Act("key",new{key="shift+end",observe=false});Require(Ui(()=>fixture.Edit.SelectionLength)==6,"shift+end fehlgeschlagen");await Act("key",new{key="ctrl+a",observe=false});Require(Ui(()=>fixture.Edit.SelectionLength)==6,"ctrl+a fehlgeschlagen");
                await Act("key",new{key="win+r",observe=false});await Task.Delay(300);Require(UiaDriver.ClassOf(Native.GetForegroundWindow())=="#32770","Run-Dialog fehlt");await Act("key",new{key="esc",observe=false});await FocusFixture();await Act("key",new{key="alt+f4",observe=false});await Task.Delay(200);Require(Ui(()=>fixture.IsDisposed),"alt+f4 schließt nicht");
            });
        }
        finally
        {
            try{if(browser is {HasExited:false})browser.Kill(true);browser?.Dispose();}catch{ }
            if(notepad!=IntPtr.Zero)try{Native.GetWindowThreadProcessId(notepad,out uint pid);using var p=Process.GetProcessById((int)pid);p.Kill(true);}catch{ }
            Ui(()=>{if(!fixture.IsDisposed)fixture.Close();});
            try{Directory.Delete(Path.Combine(root,"browser-profile"),true);}catch{ /* Owned browser profile may still be closing; it is runner-local. */ }
        }
        var output=new{test="desktop",ok=checks.All(c=>c.ok),scenarios=checks,unproven=new[]{"physical kill hook (run.Kill used)","multi-monitor laptop at 125–150%", "real Chrome profile"}};string json=JsonSerializer.Serialize(output,new JsonSerializerOptions{WriteIndented=true});File.WriteAllText(Path.Combine(root,"results.json"),json);Console.WriteLine(json);return checks.All(c=>c.ok)?0:1;
    }
}
