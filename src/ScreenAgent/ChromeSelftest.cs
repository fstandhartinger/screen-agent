using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using ScreenAgent.Core;
namespace ScreenAgent;
// --selftest-chrome: the free Chrome toolset through a REAL ComputerAgent in unrestricted mode, against a real Chrome on this machine.
//  A  Chrome on a debug port + local test pages: connect, own tabs, the three-cards task, fill, eval, navigate, iframe, shadow root, _blank, alert, tab frame + mouse, hostile fake prompt, dropped socket.
//  D  CDP away: Chrome gone -> every chrome_* answers with the fallback text at once; Chrome back + 20 s later (injected clock) -> works again.
//  B  the REAL flow: Chrome started normally (fresh profile, no debug port), remote debugging switched on through chrome://inspect by our own automation, the real allow prompt clicked by the
//     allow clicker, Browser.getVersion answers. The prompt's UIA tree goes to artifacts/chrome/allow-dialog-tree.json (a Core test fixture afterwards). C = the same with a German Chrome.
// One JSON object on stdout: per scenario {name, ok, ms, why}; exit code 1 when one failed. Pictures of every step and latency.json land in artifacts/chrome.
internal static class ChromeSelftest
{
    sealed record Row(string name,bool ok,long ms,string why,string status);
    static readonly List<Row> Rows=[];
    static readonly Dictionary<string,long> Latency=[];
    static readonly Dictionary<string,object?> Facts=[];
    static readonly ConcurrentDictionary<string,TaskCompletionSource<JsonElement>> Replies=new();
    static readonly ConcurrentQueue<(string Type,string Data)> Logs=new();
    static ComputerAgent? agent;
    static ComputerOverlay overlay=null!;
    static KeyboardHook hook=null!;
    static Application app=null!;
    static ChromeTestPages pages=null!;
    static string art="",tmp="",chromeExe="";
    static int shotNo;
    const string Sid="sid-chrome-selftest";
    static void Say(string s){Console.Error.WriteLine(DateTime.UtcNow.ToString("HH:mm:ss.fff")+" "+s);Console.Error.Flush();try{File.AppendAllText(Path.Combine(art,"selftest.log"),DateTime.UtcNow.ToString("HH:mm:ss.fff")+" "+s+"\n");}catch(IOException){ }}
    sealed class Skip(string why):Exception(why);
    static void Must(bool ok,string what){if(!ok)throw new InvalidOperationException(what);}
    public static int Run()
    {
        app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};int exit=1;
        app.Startup+=async (_,_)=>
        {
            try{exit=await Task.Run(All);}
            catch(Exception e){Console.WriteLine(JsonSerializer.Serialize(new{test="chrome",status="failed",why="crashed: "+e}));exit=1;}
            finally{app.Shutdown(exit);}
        };
        app.Run();return exit;
    }
    // ---- plumbing: commands in, results out, like the gateway does
    static string Hex()=>Guid.NewGuid().ToString("N")[..16];
    static void NewAgent(ChromeCdp.ConnectOptions options)
    {
        try{agent?.Dispose();}catch(Exception e){Say("dispose "+e.GetType().Name);}
        var policy=new ComputerPolicy(new PrivacyPolicy()){Unrestricted=true};
        agent=new ComputerAgent((url,body)=>
        {
            string json=JsonSerializer.Serialize(body);
            using var doc=JsonDocument.Parse(json);
            if(url.EndsWith("/result",StringComparison.Ordinal)){string cid=url.Split('/')[^2];if(Replies.TryRemove(cid,out var tcs))tcs.TrySetResult(doc.RootElement.Clone());}
            return Task.FromResult(true);
        },(t,d)=>Logs.Enqueue((t,JsonSerializer.Serialize(TraceSanitizer.Clean(d)))),new InputGate(),policy,overlay,hook,()=>Sid,()=>true,()=>true,()=>false,null,options,Path.Combine(tmp,"logs","computer.log"),app.Dispatcher);
    }
    static async Task<JsonElement> Do(string run,string op,string? tool=null,object? args=null,string? goal=null,int waitS=60)
    {
        string id="c"+Hex();var tcs=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);Replies[id]=tcs;
        var m=new Dictionary<string,object?>{["id"]=id,["run"]=run,["seq"]=0,["op"]=op};
        if(tool is not null)m["tool"]=tool;if(args is not null)m["args"]=args;if(goal is not null)m["goal"]=goal;
        using var doc=JsonDocument.Parse(JsonSerializer.Serialize(new{computer=m}));
        var cmd=ComputerWire.Parse(doc.RootElement,out string why)??throw new InvalidOperationException("selftest command invalid: "+why);
        agent!.Handle(Sid,cmd);
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(waitS));
    }
    static bool Ok(JsonElement e)=>e.TryGetProperty("ok",out var v)&&v.ValueKind==JsonValueKind.True;
    static string Rule(JsonElement e)=>e.TryGetProperty("rule",out var v)?v.GetString()??"":"";
    static string Code(JsonElement e)=>e.TryGetProperty("code",out var v)?v.GetString()??"":"";
    static string Text(JsonElement e)=>e.TryGetProperty("result",out var r)&&r.ValueKind==JsonValueKind.Object&&r.TryGetProperty("text",out var t)?t.GetString()??"":"";
    static string Jpeg(JsonElement e)=>e.TryGetProperty("result",out var r)&&r.ValueKind==JsonValueKind.Object&&r.TryGetProperty("jpeg",out var t)?t.GetString()??"":"";
    static string Brief(JsonElement e)=>Code(e)+"/"+Rule(e)+" "+(Text(e).Length>300?Text(e)[..300]:Text(e)).Replace('\n',' ');
    static async Task<string> Start(string goal)
    {
        string run="r"+Hex();var r=await Do(run,"start",goal:goal);
        Must(Code(r)==ComputerCodes.Done,"start failed: "+Brief(r));return run;
    }
    static async Task End(string run){try{await Do(run,"stop",waitS:10);}catch(Exception e) when(e is TimeoutException or InvalidOperationException){Say("stop "+e.GetType().Name);}await Task.Delay(300);}
    // One tool call: timed, its picture saved, the reply returned.
    static async Task<JsonElement> Tool(string run,string tool,object args,string? time=null,int waitS=60)
    {
        var sw=Stopwatch.StartNew();var r=await Do(run,"action",tool,args,waitS:waitS);sw.Stop();
        if(time is not null)Latency[time]=sw.ElapsedMilliseconds;
        string jpeg=Jpeg(r);
        if(jpeg.Length>0){try{File.WriteAllBytes(Path.Combine(art,$"{++shotNo:00}-{tool}{(time is null?"":"-"+time)}.jpg"),Convert.FromBase64String(jpeg));}catch(Exception e) when(e is IOException or FormatException){Say("picture not saved "+e.GetType().Name);}}
        Say(tool+" "+JsonSerializer.Serialize(args)+" -> "+(Ok(r)?"ok":"FAIL")+" "+sw.ElapsedMilliseconds+" ms · "+Brief(r));
        return r;
    }
    static string Ref(string outline,string role,string name,string? after=null)
    {
        int from=after is null?0:outline.IndexOf(after,StringComparison.Ordinal);
        if(from<0)return "";
        var m=Regex.Match(outline[from..],@"\[(r\d+)\] "+Regex.Escape(role)+" \""+Regex.Escape(name));
        return m.Success?m.Groups[1].Value:"";
    }
    static void Desktop(string name)
    {
        try
        {
            var b=System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            using var bmp=new Bitmap(b.Width,b.Height);using(var g=Graphics.FromImage(bmp))g.CopyFromScreen(b.Left,b.Top,0,0,b.Size);
            bmp.Save(Path.Combine(art,$"{++shotNo:00}-desktop-{name}.png"),ImageFormat.Png);
        }
        catch(Exception e){Say("desktop picture failed: "+e.GetType().Name);}
    }
    static async Task Step(string name,Func<Task> body)
    {
        var sw=Stopwatch.StartNew();string why="",status="passed";
        try{await body();}
        catch(Skip s){status="skipped";why=s.Message;}
        catch(Exception e){status="failed";why=e.GetType().Name+": "+e.Message;Desktop("failed-"+Regex.Replace(name,"[^a-z0-9]+","-")[..Math.Min(30,Regex.Replace(name,"[^a-z0-9]+","-").Length)]);}
        Rows.Add(new(name,status!="failed",sw.ElapsedMilliseconds,why,status));
        Say((status=="passed"?"PASS ":status=="skipped"?"SKIP ":"FAIL ")+name+(why.Length>0?" · "+why:""));
    }
    static Process StartChrome(string profile,string extra)
    {
        var p=Process.Start(new ProcessStartInfo(chromeExe,$"{extra} --user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check --disable-sync --window-size=1200,900 --window-position=40,40 about:blank"){UseShellExecute=false})!;
        return p;
    }
    static async Task<bool> WaitFor(Func<bool> what,int ms){long end=Environment.TickCount64+ms;while(Environment.TickCount64<end){if(what())return true;await Task.Delay(150);}return what();}
    static void Kill(Process? p){try{if(p is {HasExited:false})p.Kill(true);}catch(Exception e) when(e is InvalidOperationException or System.ComponentModel.Win32Exception){Say("kill "+e.GetType().Name);}}
    static int LogCount(string type)=>Logs.Count(l=>l.Type==type);
    // ---- the run
    static async Task<int> All()
    {
        art=Path.Combine(Directory.GetCurrentDirectory(),"artifacts","chrome");Directory.CreateDirectory(art);
        tmp=Path.Combine(Path.GetTempPath(),"sa-chrome-"+Hex());Directory.CreateDirectory(tmp);
        Process? chromeA=null,chromeB=null;
        pages=new ChromeTestPages();
        try
        {
            string? exe=new[]{Environment.GetEnvironmentVariable("ProgramFiles")+@"\Google\Chrome\Application\chrome.exe",Environment.GetEnvironmentVariable("ProgramFiles(x86)")+@"\Google\Chrome\Application\chrome.exe",Environment.GetEnvironmentVariable("LOCALAPPDATA")+@"\Google\Chrome\Application\chrome.exe"}.FirstOrDefault(File.Exists);
            if(exe is null){Console.WriteLine(JsonSerializer.Serialize(new{test="chrome",status="failed",why="Chrome not found on this machine"}));return 1;}
            chromeExe=exe;Facts["chrome"]=FileVersionInfo.GetVersionInfo(exe).FileVersion;
            overlay=app.Dispatcher.Invoke(()=>new ComputerOverlay());
            hook=new KeyboardHook(new HotkeyBindings(new AppConfig().Hotkeys));
            // ===== A: Chrome on a debug port =====
            string profileA=Path.Combine(tmp,"profile-a");
            chromeA=StartChrome(profileA,"--remote-debugging-port=0 --disable-extensions");
            Must(await WaitFor(()=>File.Exists(Path.Combine(profileA,"DevToolsActivePort")),30000),"Chrome gave no DevToolsActivePort");
            await Task.Delay(800);
            NewAgent(new ChromeCdp.ConnectOptions(profileA,true));   // the allow clicker runs here too (no prompt can come with a debug port): the hostile page is tested against it
            string run=await Start("Chrome-Selbsttest A");
            string tab="",cards="";
            await Step("A1 connect to a running Chrome (no prompt) and list the tabs",async()=>
            {
                var r=await Tool(run,"chrome_tabs",new{},"connect_no_prompt");
                Must(Ok(r)&&Text(r).Contains("about:blank"),Brief(r));
                Latency["connect_no_prompt_socket"]=ChromeTools.Current!.LastConnectMs;
                var again=await Tool(run,"chrome_tabs",new{},"tabs");Must(Ok(again),Brief(again));
                Must(Regex.IsMatch(Text(again),@"\[t\d+\] aktiv ""about:blank"""),"his tab is not marked aktiv: "+Text(again));
            });
            await Step("A2 chrome_open makes an own tab and pictures it (auto-observe, tab frame)",async()=>
            {
                var r=await Tool(run,"chrome_open",new{url=pages.Url("/cards")},"open_observe");
                Must(Ok(r)&&Jpeg(r).Length>1000&&Text(r).Contains("frame: tab "),Brief(r));
                tab=Regex.Match(Text(r),@"Eigener Tab \[(t\d+)\]").Groups[1].Value;Must(tab.Length>0,"no tab name in "+Text(r));
                var f=ChromeTools.Current!.Host.Frame;Must(f.Kind==FrameKind.Tab&&f.Width>200&&Math.Max(f.Width,f.Height)<=1300,"frame "+f);
                var list=await Tool(run,"chrome_tabs",new{});
                Must(Regex.IsMatch(Text(list),@"\["+tab+@"\] aktiv eigen ""Agent notifications"""),Text(list));
            });
            await Step("A3 the 9 Nov task: three cards, click the Opnar AI card's 'Senden' by reading the outline",async()=>
            {
                var s=await Tool(run,"chrome_snapshot",new{tab},"snapshot_cards");cards=Text(s);
                Must(Ok(s)&&cards.StartsWith("tab "+tab+" · Agent notifications · ",StringComparison.Ordinal),Brief(s));
                int n=cards.IndexOf("Nordgatan",StringComparison.Ordinal),o=cards.IndexOf("Opnar AI",StringComparison.Ordinal),u=cards.IndexOf("Outwing",StringComparison.Ordinal);
                Must(n>0&&o>n&&u>o,"cards not in order in the outline");
                string send=Ref(cards,"button","Senden (empfohlen)","Opnar AI");
                Must(send.Length>0&&send!=Ref(cards,"button","Senden (empfohlen)","Nordgatan")&&cards.IndexOf("["+send+"]",StringComparison.Ordinal)<u,"the button after 'Opnar AI' is not told apart: "+send);
                File.WriteAllText(Path.Combine(art,"outline-cards.txt"),cards);
                var c=await Tool(run,"chrome_click",new{@ref=send},"click_observe");
                Must(Ok(c)&&Jpeg(c).Length>1000,Brief(c));
                await Task.Delay(300);
                Must(pages.Count("opnar:senden")==1&&pages.Count("nordgatan:senden")==0&&pages.Count("outwing:senden")==0,"the page recorded "+JsonSerializer.Serialize(pages.Hits));
                Must(Latency["snapshot_cards"]<=400,"snapshot took "+Latency["snapshot_cards"]+" ms (target 400)");
                Must(Latency["click_observe"]<=800,"click+observe took "+Latency["click_observe"]+" ms (target 800)");
                var stale=await Tool(run,"chrome_snapshot",new{tab,filter="Outwing"});Must(Text(stale).Contains("Outwing")&&!Text(stale).Contains("Nordgatan (Lund)"),"filter: "+Text(stale));
                var old=await Tool(run,"chrome_click",new{@ref=send});
                Must(!Ok(old)&&Code(old)==ComputerCodes.StaleRef&&Text(old).Contains("chrome_snapshot"),"an old ref must be stale-ref: "+Brief(old));
                var quiet=await Tool(run,"chrome_click",new{@ref=Ref(Text(await Tool(run,"chrome_snapshot",new{tab})),"button","Nicht jetzt","Outwing"),observe=false});
                Must(Ok(quiet)&&Jpeg(quiet).Length==0,"observe:false must bring no picture");
            });
            await Step("A4 chrome_fill: contenteditable, a React-style controlled input (Input.insertText fires input), append, clear, Enter",async()=>
            {
                string comp=Ref(Text(await Tool(run,"chrome_snapshot",new{tab})),"textbox","Message");
                var t=await Tool(run,"chrome_fill",new{@ref=comp,text="Hallo Opnar"});Must(Ok(t)&&Text(t).Contains("eingetragen (11 Zeichen)"),Brief(t));
                Must(Text(await Tool(run,"chrome_eval",new{tab,js="document.getElementById('composer').innerText"})).Trim()=="Hallo Opnar","composer text");
                await Tool(run,"chrome_navigate",new{tab,url=pages.Url("/react")},"navigate_observe");
                string name=Ref(Text(await Tool(run,"chrome_snapshot",new{tab})),"textbox","Name");
                var f=await Tool(run,"chrome_fill",new{@ref=name,text="Florian"},"fill");Must(Ok(f)&&Text(f).Contains("eingetragen (7 Zeichen)"),Brief(f));
                await Task.Delay(150);
                Must(Text(await Tool(run,"chrome_eval",new{tab,js="readState()+'|'+document.getElementById('name').value+'|'+(Number(document.getElementById('events').textContent)>0)"},"eval"))=="Florian|Florian|true","the page's own state did not take the text");
                await Tool(run,"chrome_fill",new{@ref=name,text=" S",clear=false,observe=false});
                Must(Text(await Tool(run,"chrome_eval",new{tab,js="readState()"}))=="Florian S","append");
                await Tool(run,"chrome_navigate",new{tab,url=pages.Url("/form"),observe=false});
                string form=Text(await Tool(run,"chrome_snapshot",new{tab}));
                Must(form.Contains("combobox \"Land\" wert=\"Deutschland\"")&&form.Contains("checkbox \"AGB annehmen\" [aus]"),"form outline: "+form);
                Must(Text(await Tool(run,"chrome_fill",new{@ref=Ref(form,"combobox","Land"),text="Schweden",observe=false})).Contains("Schweden"),"select");
                Must(Ok(await Tool(run,"chrome_click",new{@ref=Ref(form,"checkbox","AGB annehmen"),observe=false})),"checkbox behind its label");
                Must(Text(await Tool(run,"chrome_fill",new{@ref=Ref(form,"textbox","Tag"),text="2026-11-09",observe=false})).Contains("eingetragen"),"date field");
                var q=await Tool(run,"chrome_fill",new{@ref=Ref(form,"textbox","Suche"),text="neu",enter=true});
                Must(Text(q).Contains("eingetragen (3 Zeichen) · Enter gedrückt"),Brief(q));
                await Task.Delay(300);
                Must(pages.Count("submit:neu")==1&&pages.Count("land:se")==1&&pages.Count("agb:true")==1,"the page recorded "+JsonSerializer.Serialize(pages.Hits));
                var k=await Tool(run,"chrome_key",new{tab,key="ctrl+a x y"},"key_observe");Must(Ok(k),Brief(k));
                Must(Text(await Tool(run,"chrome_eval",new{tab,js="document.getElementById('q').value"}))=="xy","ctrl+a then typing");
            });
            await Step("A5 chrome_eval: expression, function body with return/await, error as text, timeout",async()=>
            {
                Must(Text(await Tool(run,"chrome_eval",new{tab,js="1+2"}))=="3","expression");
                Must(Text(await Tool(run,"chrome_eval",new{tab,js="[1,2].map(x=>{return x*2})"}))=="[2,4]","return inside an expression");
                Must(Text(await Tool(run,"chrome_eval",new{tab,js="const r=await new Promise(r=>setTimeout(()=>r(7),50)); return r"}))=="7","async body");
                var e=await Tool(run,"chrome_eval",new{tab,js="nichtDa.x"});Must(!Ok(e)&&Rule(e)=="js-error"&&Text(e).Contains("nichtDa"),Brief(e));
                var sw=Stopwatch.StartNew();var slow=await Tool(run,"chrome_eval",new{tab,js="await new Promise(r=>setTimeout(r,20000))",timeout_s=1});
                Must(!Ok(slow)&&Rule(slow)=="timeout"&&sw.ElapsedMilliseconds<6000,Brief(slow)+" after "+sw.ElapsedMilliseconds+" ms");
            });
            await Step("A6 chrome_navigate: url, back, forward, reload; a ref dies with the page; a dead address says so",async()=>
            {
                await Tool(run,"chrome_navigate",new{tab,url=pages.Url("/nav1"),observe=false});
                string weiter=Ref(Text(await Tool(run,"chrome_snapshot",new{tab})),"link","weiter");
                var nav=await Tool(run,"chrome_navigate",new{tab,url=pages.Url("/nav2")});Must(Ok(nav)&&Text(nav).Contains("Geladen")&&Text(nav).Contains("\"Zwei\""),Brief(nav));
                var gone=await Tool(run,"chrome_click",new{@ref=weiter});Must(Code(gone)==ComputerCodes.StaleRef,Brief(gone));
                Must(Text(await Tool(run,"chrome_navigate",new{tab,url="back",observe=false})).Contains("\"Eins\""),"back");
                Must(Text(await Tool(run,"chrome_navigate",new{tab,url="forward",observe=false})).Contains("\"Zwei\""),"forward");
                Must(Text(await Tool(run,"chrome_navigate",new{tab,url="reload",observe=false})).Contains("\"Zwei\""),"reload");
                var bad=await Tool(run,"chrome_navigate",new{tab,url="http://127.0.0.1:1/",observe=false});Must(!Ok(bad)&&Rule(bad)=="nav-error",Brief(bad));
                var read=await Tool(run,"chrome_read",new{tab});Must(Ok(read),Brief(read));
            });
            await Step("A7 iframe (same origin walked, foreign one named) and open shadow root: outline, click, fill",async()=>
            {
                await Tool(run,"chrome_navigate",new{tab,url=pages.Url("/frame"),observe=false});await Task.Delay(400);
                string fr=Text(await Tool(run,"chrome_snapshot",new{tab}));
                Must(fr.Contains("Im Rahmen")&&fr.Contains("[iframe fremder Herkunft other.localhost"),"frame outline: "+fr);
                Must(Ok(await Tool(run,"chrome_click",new{@ref=Ref(fr,"button","Rahmenknopf")})),"click in the iframe");
                Must(Text(await Tool(run,"chrome_fill",new{@ref=Ref(fr,"textbox","Rahmenfeld"),text="im Rahmen",observe=false})).Contains("eingetragen"),"fill in the iframe");
                await Tool(run,"chrome_navigate",new{tab,url=pages.Url("/shadow"),observe=false});await Task.Delay(300);
                string sh=Text(await Tool(run,"chrome_snapshot",new{tab}));
                Must(sh.Contains("Im Schatten"),"shadow outline: "+sh);
                Must(Ok(await Tool(run,"chrome_click",new{@ref=Ref(sh,"button","Schattenknopf")})),"click in the shadow root");
                Must(Text(await Tool(run,"chrome_fill",new{@ref=Ref(sh,"textbox","Schattenfeld"),text="tief",observe=false})).Contains("eingetragen"),"fill in the shadow root");
                await Task.Delay(300);
                Must(pages.Count("frame:knopf")==1&&pages.Count("shadow:knopf")==1,"the page recorded "+JsonSerializer.Serialize(pages.Hits));
                Must(Text(await Tool(run,"chrome_eval",new{tab,js="document.querySelector('x-box').shadowRoot.querySelector('input').value"}))=="tief","shadow field value");
            });
            await Step("A8 a _blank link opens an own tab; own tabs close, his tab only with force",async()=>
            {
                await Tool(run,"chrome_navigate",new{tab,url=pages.Url("/blank"),observe=false});
                var pop=await Tool(run,"chrome_click",new{@ref=Ref(Text(await Tool(run,"chrome_snapshot",new{tab})),"link","Neues Fenster")});
                string popTab=Regex.Match(Text(pop),@"neuer Tab \[(t\d+)\]").Groups[1].Value;Must(popTab.Length>0,Brief(pop));
                await Task.Delay(500);
                Must(Text(await Tool(run,"chrome_read",new{tab=popTab})).Contains("Seite zwei"),"the new tab's text");
                Must(Regex.IsMatch(Text(await Tool(run,"chrome_tabs",new{})),@"\["+popTab+@"\] (aktiv )?eigen "),"the new tab is not own");
                Must(Ok(await Tool(run,"chrome_close",new{tab=popTab})),"closing an own tab");
                string his=Regex.Match(Text(await Tool(run,"chrome_tabs",new{})),@"\[(t\d+)\] (?:aktiv )?""about:blank""").Groups[1].Value;Must(his.Length>0,"his about:blank tab is not listed");
                var no=await Tool(run,"chrome_close",new{tab=his});Must(!Ok(no)&&Rule(no)=="not-own",Brief(no));
                Must(Regex.IsMatch(Text(await Tool(run,"chrome_tabs",new{})),@"\["+his+@"\] "),"his tab is gone");
                await Tool(run,"chrome_activate",new{tab});
            });
            await Step("A9 a JavaScript alert is closed, the call does not hang, the model is told",async()=>
            {
                await Tool(run,"chrome_navigate",new{tab,url=pages.Url("/alert"),observe=false});
                var sw=Stopwatch.StartNew();
                var r=await Tool(run,"chrome_click",new{@ref=Ref(Text(await Tool(run,"chrome_snapshot",new{tab})),"button","Melden")});
                Must(Ok(r)&&sw.ElapsedMilliseconds<6000,Brief(r)+" after "+sw.ElapsedMilliseconds+" ms");
                await Task.Delay(300);
                var t=await Tool(run,"chrome_eval",new{tab,js="document.title"});
                Must(Text(t).StartsWith("nach-alert",StringComparison.Ordinal),"the page did not go on after the alert: "+Text(t));
                Must((Text(r)+Text(t)).Contains("Dialog (alert)")&&(Text(r)+Text(t)).Contains("Hallo aus der Seite"),"the model was not told about the dialog");
            });
            await Step("A10 chrome_screenshot sets a tab frame; the desktop mouse tools work on it through CDP; full page; long page scope",async()=>
            {
                await Tool(run,"chrome_navigate",new{tab,url=pages.Url("/cards"),observe=false});
                var s=await Tool(run,"chrome_screenshot",new{tab},"screenshot");
                var host=ChromeTools.Current!.Host;var f=host.Frame;
                Must(Ok(s)&&Jpeg(s).Length>1000&&f.Kind==FrameKind.Tab&&Math.Max(f.Width,f.Height)<=1300&&Text(s).StartsWith("frame: tab "+f.Width+"x"+f.Height,StringComparison.Ordinal),Brief(s)+" frame "+f);
                Facts["tab_frame"]=f.Width+"x"+f.Height+" scale "+Math.Round(f.Scale,3);
                // where "Ändern" of the first card is in CSS pixels -> in picture pixels -> back through the frame, like the desktop click tool does
                var xy=JsonSerializer.Deserialize<double[]>(Text(await Tool(run,"chrome_eval",new{tab,js="const b=[...document.querySelectorAll('button')].find(b=>b.textContent=='Ändern').getBoundingClientRect();return [b.left+b.width/2,b.top+b.height/2]"})))!;
                var (ix,iy)=f.FromTarget(xy[0],xy[1]);var (cx,cy)=f.ToTarget(ix,iy);
                string said=await Task.Run(()=>host.TabMouseHandler!(new TabMouse(f.Tab!,"click",cx,cy)));
                await Task.Delay(300);
                Must(said=="geklickt"&&pages.Count("nordgatan:aendern")==1,"mouse on the tab frame: "+said+" "+JsonSerializer.Serialize(pages.Hits));
                var again=await Task.Run(()=>host.TabShot!(f.Tab!));Must(again is {Jpeg.Length:>1000},"TabShot gave no picture");
                await Tool(run,"chrome_navigate",new{tab,url=pages.Url("/long"),observe=false});
                string top=Text(await Tool(run,"chrome_snapshot",new{tab}));
                Must(top.Contains("# Lange Seite")&&top.Contains("Absatz 1 ")&&!top.Contains("Absatz 250 ")&&top.Contains("all:true"),"viewport scope");
                string whole=Text(await Tool(run,"chrome_snapshot",new{tab,all=true}));
                Must(whole.Contains("Absatz 300 ")&&whole.Contains("button \"Ganz unten\""),"all:true");
                var full=await Tool(run,"chrome_screenshot",new{tab,full=true},"screenshot_full");
                var ff=host.Frame;Must(Ok(full)&&ff.Height>f.Height*2&&ff.Height<=4001&&ff.Tab!.EndsWith("#full",StringComparison.Ordinal)&&Jpeg(full).Length<=ComputerWire.MaxJpegChars,"full page frame "+ff+" chars "+Jpeg(full).Length);
                var sc=await Tool(run,"scroll",new{tab,dy=3000},"scroll_observe");Must(Ok(sc),Brief(sc));
                await Task.Delay(300);
                Must(double.Parse(Text(await Tool(run,"chrome_eval",new{tab,js="scrollY"})),System.Globalization.CultureInfo.InvariantCulture)>1000,"the page did not scroll");
                var deep=await Tool(run,"chrome_click",new{@ref=Ref(Text(await Tool(run,"chrome_snapshot",new{tab,all=true})),"button","Ganz unten")});
                await Task.Delay(300);
                Must(Ok(deep)&&pages.Count("long:unten")==1&&!Text(deep).Contains("element.click()"),"a button far below is scrolled to and clicked by the mouse: "+Brief(deep));
            });
            await Step("A11 hostile page that paints the allow prompt: the running allow clicker never presses it",async()=>
            {
                var ck=ChromeTools.Current!.Clicker;Must(ck is not null,"no allow clicker is running");
                await Tool(run,"chrome_navigate",new{tab,url=pages.Url("/fake")});
                int before=ck!.Scans;ck.Hot(4000);
                await Task.Delay(3000);
                var scan=ChromeUi.Scan();
                File.WriteAllText(Path.Combine(art,"fake-page-native-tree.json"),JsonSerializer.Serialize(ChromeUi.Dump(scan,""),new JsonSerializerOptions{WriteIndented=true}));
                Facts["clicker_scan_ms_max"]=ck.MaxScanMs;Facts["clicker_nodes"]=ck.LastNodes;Facts["clicker_windows"]=ck.LastWindows;
                Must(ck.Scans-before>=5,"the clicker scanned only "+(ck.Scans-before)+" times in 3 s");
                Must(ck.LastWindows>=1&&ck.LastNodes>=3,"the clicker saw no browser window ("+ck.LastWindows+" windows, "+ck.LastNodes+" nodes)");
                Must(scan.Nodes.Any(n=>n.Role=="Document"),"the scan did not reach the page's Document node");
                Must(!scan.Nodes.Any(n=>n.Name is "Zulassen" or "Allow"),"page content leaked into the native scan");
                Must(AllowPrompt.Find(scan.Nodes,"").Count==0&&ck.Clicks==0&&pages.Count("fake-clicked")==0,"the fake prompt was matched or pressed: clicks "+ck.Clicks+", page "+pages.Count("fake-clicked"));
                Must(Text(await Tool(run,"chrome_snapshot",new{tab})).Contains("button \"Zulassen\""),"the fake button must be there (in the page)");
            });
            await Step("A12 a dropped socket reconnects by itself on the next call",async()=>
            {
                int connects=LogCount("computer.chrome.connected");
                ChromeTools.Current!.DropSocketForTest();await Task.Delay(300);
                var r=await Tool(run,"chrome_tabs",new{},"reconnect_tabs");
                Must(Ok(r)&&LogCount("computer.chrome.connected")==connects+1&&LogCount("computer.chrome.dropped")>=1,Brief(r)+" connects "+LogCount("computer.chrome.connected"));
                Must(Regex.IsMatch(Text(r),@"\["+tab+@"\] (aktiv )?eigen "),"the own tab is still known as own after the reconnect: "+Text(r));
            });
            await End(run);
            await Step("A13 at the end of the run own tabs stay open and are counted in the log",async()=>
            {
                await Task.Delay(300);
                var last=Logs.LastOrDefault(l=>l.Type=="computer.chrome.own_tabs");
                Must(last.Data is not null&&last.Data.Contains("\"open\":1"),"log computer.chrome.own_tabs: "+last.Data);
            });
            // ===== D: CDP away =====
            string runD="";
            await Step("D1 Chrome gone: every chrome_* answers with the reason and the desktop fallback, within 1 s after the first attempt",async()=>
            {
                runD=await Start("Chrome-Selbsttest D");
                Must(Ok(await Tool(runD,"chrome_tabs",new{})),"connected before Chrome is closed");
                Kill(chromeA);
                Must(await WaitFor(()=>!ChromeUi.Running(),10000),"chrome.exe did not exit");
                var first=await Tool(runD,"chrome_tabs",new{},"away_first");
                Must(!Ok(first)&&Rule(first) is "chrome-not-running" or "debugging-off"&&Text(first).Contains("Desktop-Werkzeuge (screenshot, click, type, key, ui_tree)"),Brief(first));
                long worst=0;
                foreach(var (tool,args) in new (string,object)[]{("chrome_tabs",new{}),("chrome_open",new{url="https://example.org"}),("chrome_navigate",new{tab="t1",url="back"}),("chrome_activate",new{tab="t1"}),("chrome_close",new{tab="t1"}),
                    ("chrome_snapshot",new{tab="t1"}),("chrome_click",new{@ref="r1"}),("chrome_fill",new{@ref="r1",text="x"}),("chrome_key",new{tab="t1",key="enter"}),("chrome_read",new{tab="t1"}),("chrome_eval",new{tab="t1",js="1"}),("chrome_screenshot",new{tab="t1"})})
                {
                    var sw=Stopwatch.StartNew();var r=await Tool(runD,tool,args);worst=Math.Max(worst,sw.ElapsedMilliseconds);
                    Must(!Ok(r)&&Rule(r)==Rule(first)&&Text(r).Contains("Desktop-Werkzeuge"),tool+": "+Brief(r));
                }
                Latency["away_worst"]=worst;
                Must(worst<1000,"a call took "+worst+" ms while Chrome was away");
            });
            await Step("D2 Chrome back: still the remembered answer, and after 20 s (injected clock) it works again",async()=>
            {
                string before=File.Exists(Path.Combine(profileA,"DevToolsActivePort"))?File.ReadAllText(Path.Combine(profileA,"DevToolsActivePort")):"";
                chromeA=StartChrome(profileA,"--remote-debugging-port=0 --disable-extensions");
                Must(await WaitFor(()=>File.Exists(Path.Combine(profileA,"DevToolsActivePort"))&&File.ReadAllText(Path.Combine(profileA,"DevToolsActivePort"))!=before,30000),"Chrome gave no new DevToolsActivePort");
                await Task.Delay(800);
                var still=await Tool(runD,"chrome_tabs",new{});Must(!Ok(still),"within the 20 s the failure is remembered: "+Brief(still));
                ChromeTools.Now=()=>Environment.TickCount64+21000;
                try{var back=await Tool(runD,"chrome_tabs",new{},"away_back");Must(Ok(back),Brief(back));}
                finally{ChromeTools.Now=()=>Environment.TickCount64;}
            });
            if(runD.Length>0)await End(runD);
            Kill(chromeA);chromeA=null;
            await WaitFor(()=>!ChromeUi.Running(),10000);
            // ===== B, C: the real flow =====
            foreach(var (tag,lang) in new[]{("en","--lang=en-US"),("de","--lang=de")})
            {
                string profile=Path.Combine(tmp,"profile-"+tag);string treeFile=Path.Combine(art,tag=="en"?"allow-dialog-tree.json":"allow-dialog-tree-"+tag+".json");
                string runB="";bool connected=false;
                await Step((tag=="en"?"B1":"C1")+" REAL flow ("+tag+"): Chrome started normally, remote debugging switched on through chrome://inspect, the real allow prompt clicked, Browser.getVersion answers",async()=>
                {
                    chromeB=StartChrome(profile,lang);
                    Must(await WaitFor(()=>ChromeUi.Windows().Any(w=>w.Title.Contains("Chrome",StringComparison.Ordinal)),30000),"no Chrome window appeared");
                    await Task.Delay(2500);
                    Must(!File.Exists(Path.Combine(profile,"DevToolsActivePort")),"this Chrome must start without a debugging port");
                    Desktop(tag+"-chrome-started");
                    ChromeAllowClicker.Record=true;ChromeAllowClicker.LastTree=null;
                    ChromeTools.EnableSnap=n=>Desktop(tag+"-"+n);
                    NewAgent(new ChromeCdp.ConnectOptions(profile,true));
                    runB=await Start("Chrome-Selbsttest echt "+tag);
                    int allowBefore=LogCount("computer.chrome.allow");
                    // a watcher takes a picture of the desktop as soon as the prompt is on the screen (before the clicker has pressed it, when it is quick enough)
                    using var watch=new CancellationTokenSource();
                    var seen=Task.Run(async()=>{while(!watch.IsCancellationRequested){try{if(ChromeUi.Windows().Any(w=>Array.IndexOf(AllowPrompt.Headings,w.Title)>=0)){Desktop(tag+"-prompt");return true;}}catch(Exception){ }await Task.Delay(40);}return false;});   // a window that closes while it is listed: look again
                    var r=await Tool(runB,"chrome_tabs",new{},"connect_with_enable_"+tag,waitS:90);
                    watch.Cancel();bool promptPictured=await seen;
                    var tools=ChromeTools.Current!;
                    Facts[tag+"_enable"]=tools.LastEnable;Facts[tag+"_prompt_clicks"]=tools.LastConnectClicks;Facts[tag+"_prompt_window_pictured"]=promptPictured;
                    Latency["connect_with_prompt_"+tag]=tools.LastConnectMs;
                    Desktop(tag+"-after-connect");
                    if(!Ok(r))
                    {
                        File.WriteAllText(Path.Combine(art,"real-"+tag+"-native-tree.json"),JsonSerializer.Serialize(ChromeUi.Dump(ChromeUi.Scan(),""),new JsonSerializerOptions{WriteIndented=true}));
                        throw new InvalidOperationException("not connected: "+Brief(r)+" · enable step: "+tools.LastEnable+" · logs: "+string.Join(" ; ",Logs.Where(l=>l.Type.StartsWith("computer.chrome",StringComparison.Ordinal)).TakeLast(8).Select(l=>l.Type+" "+l.Data)));
                    }
                    connected=true;
                    Must(tools.LastEnable=="done","debugging was not switched on by our automation: "+tools.LastEnable);
                    Must(LogCount("computer.chrome.allow")>allowBefore&&tools.LastConnectClicks>=1,"connected, but no allow prompt was clicked (clicks "+tools.LastConnectClicks+")");
                    var version=Logs.LastOrDefault(l=>l.Type=="computer.chrome.connected").Data??"";
                    Must(version.Contains("Chrome/"),"Browser.getVersion: "+version);Facts[tag+"_connected"]=version;
                    Must(ChromeAllowClicker.LastTree is not null,"the prompt's tree was not recorded");
                    File.WriteAllText(treeFile,JsonSerializer.Serialize(ChromeAllowClicker.LastTree,new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
                    Must(!Regex.IsMatch(Text(r),"Inspect with Chrome"),"our chrome://inspect tab was left open: "+Text(r));
                });
                if(connected)
                {
                    await Step((tag=="en"?"B2":"C2")+" on the really attached Chrome ("+tag+"): own tab, outline, click; a dropped socket reconnects and the prompt is clicked again",async()=>
                    {
                        var o=await Tool(runB,"chrome_open",new{url=pages.Url("/cards")});Must(Ok(o)&&Jpeg(o).Length>1000,Brief(o));
                        string t=Regex.Match(Text(o),@"Eigener Tab \[(t\d+)\]").Groups[1].Value;
                        string outline=Text(await Tool(runB,"chrome_snapshot",new{tab=t}));
                        int before=pages.Count("outwing:senden");
                        Must(Ok(await Tool(runB,"chrome_click",new{@ref=Ref(outline,"button","Senden (empfohlen)","Outwing")})),"click");
                        await Task.Delay(300);Must(pages.Count("outwing:senden")==before+1,"the click did not reach the page");
                        int allow=LogCount("computer.chrome.allow");
                        ChromeTools.Current!.DropSocketForTest();await Task.Delay(300);
                        var again=await Tool(runB,"chrome_tabs",new{},"reconnect_with_prompt_"+tag,waitS:30);
                        Must(Ok(again),"reconnect: "+Brief(again));
                        Facts[tag+"_reconnect_prompt_clicked"]=LogCount("computer.chrome.allow")>allow;
                        Latency["reconnect_socket_"+tag]=ChromeTools.Current.LastConnectMs;
                        Must(Ok(await Tool(runB,"chrome_close",new{tab=t})),"close the own tab");
                    });
                    if(tag=="en")await Step("B3 a stale prompt of a dead connection is cancelled before our own connect",async()=>
                    {
                        await End(runB);runB="";await Task.Delay(500);
                        var port=File.ReadAllLines(Path.Combine(profile,"DevToolsActivePort"));
                        using var dead=new CancellationTokenSource(20000);
                        var orphan=CdpSocket.Connect(new Uri("ws://127.0.0.1:"+port[0]+port[1]),()=>false,(_,_)=>{},dead.Token);   // nobody clicks for this one
                        bool shown=await WaitFor(()=>ChromeUi.Windows().Any(w=>Array.IndexOf(AllowPrompt.Headings,w.Title)>=0)||AllowPrompt.Windows(ChromeUi.Scan().Nodes,"").Count>0,5000);
                        Facts["stale_prompt_shown"]=shown;Desktop("stale-prompt");
                        if(!shown)throw new Skip("Chrome showed no prompt for the second connection attempt, so there was nothing stale to clear");
                        int stale=LogCount("computer.chrome.stale");
                        runB=await Start("Chrome-Selbsttest echt stale");
                        var r=await Tool(runB,"chrome_tabs",new{},"connect_after_stale",waitS:30);
                        Must(Ok(r),"connect with a stale prompt open: "+Brief(r));
                        Must(LogCount("computer.chrome.stale")==stale+1,"the stale prompt was not handled (no computer.chrome.stale)");
                        Facts["stale_orphan_state"]=orphan.Status.ToString();
                        dead.Cancel();
                        try{(await orphan).Dispose();}catch(Exception e) when(e is OperationCanceledException or System.Net.WebSockets.WebSocketException){ }   // the orphan was refused or cancelled: that is the point
                    });
                }
                if(runB.Length>0)await End(runB);
                ChromeAllowClicker.Record=false;ChromeTools.EnableSnap=null;
                Kill(chromeB);chromeB=null;
                await WaitFor(()=>!ChromeUi.Running(),10000);
            }
        }
        catch(Exception e){Rows.Add(new("selftest ran to the end",false,0,e.GetType().Name+": "+e.Message,"failed"));Say(e.ToString());}
        finally
        {
            try{agent?.Dispose();}catch(Exception e){Say("dispose "+e.GetType().Name);}
            try{hook?.Dispose();}catch(Exception e){Say("hook "+e.GetType().Name);}
            Kill(chromeA);Kill(chromeB);pages.Dispose();
            await Task.Delay(500);
            try{Directory.Delete(tmp,true);}catch(Exception e) when(e is IOException or UnauthorizedAccessException){Say("temp profile not deleted");}
        }
        bool pass=Rows.Count>0&&Rows.All(r=>r.ok);
        var options=new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
        File.WriteAllText(Path.Combine(art,"latency.json"),JsonSerializer.Serialize(new{unit="ms",targets=new{snapshot_cards=400,click_observe=800},measured=Latency,facts=Facts},options));
        File.WriteAllText(Path.Combine(art,"chrome-logs.json"),JsonSerializer.Serialize(Logs.Where(l=>l.Type.StartsWith("computer.chrome",StringComparison.Ordinal)).Select(l=>l.Type+" "+l.Data),options));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            test="chrome",status=pass?"passed":"failed",passed=Rows.Count(r=>r.status=="passed"),failed=Rows.Count(r=>r.status=="failed"),skipped=Rows.Count(r=>r.status=="skipped"),
            scenarios=Rows,latency=Latency,facts=Facts
        },options));
        return pass?0:1;
    }
}
