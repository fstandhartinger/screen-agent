using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenAgent.Core;
using Xunit;
using Xunit.Abstractions;
namespace ScreenAgent.Tests;
// The free Chrome toolset against a REAL Chrome, on any OS. Runs only when SA_CDP_WS holds a browser websocket URL (ws://127.0.0.1:<port>/devtools/browser/<id>); without it the test
// returns at once (CI's unit run has no browser: the same steps are proven on Windows by --selftest-chrome). Every tab it opens is closed again.
public class ChromeFreeLiveTests(ITestOutputHelper output)
{
    static JsonElement A(object o){using var d=JsonDocument.Parse(JsonSerializer.Serialize(o));return d.RootElement.Clone();}
    static string Ref(string outline,string role,string name,string? after=null)
    {
        int from=after is null?0:outline.IndexOf(after,StringComparison.Ordinal);
        if(from<0)return "";
        var m=Regex.Match(outline[from..],@"\[(r\d+)\] "+Regex.Escape(role)+" \""+Regex.Escape(name));
        return m.Success?m.Groups[1].Value:"";
    }
    [Fact] public async Task EveryToolOnRealPages()
    {
        string? ws=Environment.GetEnvironmentVariable("SA_CDP_WS");
        if(string.IsNullOrEmpty(ws))return;
        using var pages=new ChromeTestPages();
        var state=new ChromeRunState();var logs=new List<string>();
        var socket=await CdpSocket.Connect(new Uri(ws),()=>false,(t,d)=>{lock(logs)logs.Add(t+" "+JsonSerializer.Serialize(d));},new CancellationTokenSource(8000).Token);
        using var c=new ChromeFree(socket,state,()=>false,(t,d)=>{lock(logs)logs.Add(t+" "+JsonSerializer.Serialize(d));});
        Assert.Contains("Chrome",await c.Version(default));
        var times=new Dictionary<string,long>();
        async Task<ChromeResult> Do(string tool,object args,string? label=null)
        {
            var sw=System.Diagnostics.Stopwatch.StartNew();var r=await c.Run(tool,A(args));sw.Stop();
            times[label??tool]=sw.ElapsedMilliseconds;
            output.WriteLine("== "+tool+" "+JsonSerializer.Serialize(args)+" -> "+(r.Ok?"ok":"FAIL "+r.Rule)+" "+sw.ElapsedMilliseconds+" ms\n"+(r.Text.Length>1800?r.Text[..1800]+"…":r.Text));
            return r;
        }
        try
        {
            // ---- own tab, the three cards
            var open=await Do("chrome_open",new{url=pages.Url("/cards")});
            Assert.True(open.Ok,open.Text);Assert.Single(state.Own);string tab=state.Alias(state.Own.First());
            var tabs=await Do("chrome_tabs",new{});
            Assert.Matches(@"\["+tab+@"\] aktiv eigen ""Agent notifications""",tabs.Text);
            var snap=await Do("chrome_snapshot",new{tab},"snapshot-cards");
            Assert.StartsWith("tab "+tab+" · Agent notifications · "+pages.Url("/cards")+" · viewport ",snap.Text);
            string send=Ref(snap.Text,"button","Senden (empfohlen)","Opnar AI");
            Assert.NotEqual("",send);
            Assert.NotEqual(send,Ref(snap.Text,"button","Senden (empfohlen)","Nordgatan"));
            Assert.Contains("[r",snap.Text);Assert.Contains("clickable \"Anna",snap.Text);Assert.Contains("textbox \"Message\"",snap.Text);
            var click=await Do("chrome_click",new{@ref=send});
            Assert.True(click.Ok,click.Text);await Task.Delay(200);
            Assert.Equal(1,pages.Count("opnar:senden"));Assert.Equal(0,pages.Count("nordgatan:senden"));Assert.Equal(0,pages.Count("outwing:senden"));
            var filtered=await Do("chrome_snapshot",new{tab,filter="Outwing"});
            Assert.Contains("Outwing",filtered.Text);Assert.DoesNotContain("Nordgatan (Lund)",filtered.Text);
            // a ref of the snapshot before the last one is stale
            var stale=await Do("chrome_click",new{@ref=send});
            Assert.False(stale.Ok);Assert.True(stale.Stale);Assert.Equal("stale-ref",stale.Rule);Assert.Contains("chrome_snapshot",stale.Text);
            // contenteditable composer
            string comp=Ref(filtered.Text.Length>0?(await Do("chrome_snapshot",new{tab})).Text:"","textbox","Message");
            var typed=await Do("chrome_fill",new{@ref=comp,text="Hallo Opnar"});
            Assert.True(typed.Ok,typed.Text);Assert.Contains("eingetragen (11 Zeichen)",typed.Text);
            var ev=await Do("chrome_eval",new{tab,js="document.getElementById('composer').innerText"});
            Assert.Equal("Hallo Opnar",ev.Text.Trim());
            // screenshot: a tab frame, long edge <= 1280
            var shot=await Do("chrome_screenshot",new{tab});
            Assert.True(shot.Ok,shot.Text);Assert.NotNull(shot.Shot);Assert.InRange(Math.Max(shot.Shot!.Width,shot.Shot.Height),200,1300);Assert.StartsWith("frame: tab "+shot.Shot.Width+"x"+shot.Shot.Height,shot.Text);
            // the desktop mouse tools on the tab frame: a click at the picture position of "Ändern" of the first card
            var pos=await Do("chrome_eval",new{tab,js="const b=[...document.querySelectorAll('button')].find(b=>b.textContent=='Ändern').getBoundingClientRect();return [b.left+b.width/2,b.top+b.height/2]"});
            var xy=JsonSerializer.Deserialize<double[]>(pos.Text)!;
            Assert.Equal("geklickt",await c.Mouse(shot.Shot.Tab,"click",xy[0],xy[1],0,0,"left",1,0,0,600,""));
            await Task.Delay(200);Assert.Equal(1,pages.Count("nordgatan:aendern"));
            // ---- eval forms
            Assert.Equal("3",(await Do("chrome_eval",new{tab,js="1+2"})).Text);
            Assert.Equal("[2,4]",(await Do("chrome_eval",new{tab,js="[1,2].map(x=>{return x*2})"})).Text);
            Assert.Equal("7",(await Do("chrome_eval",new{tab,js="const r=await new Promise(r=>setTimeout(()=>r(7),50)); return r"})).Text);
            Assert.Equal("Agent notifications",(await Do("chrome_eval",new{tab,js="await Promise.resolve(document.title)"})).Text);
            Assert.Equal("undefined",(await Do("chrome_eval",new{tab,js="void 0"})).Text);
            var err=await Do("chrome_eval",new{tab,js="nichtDa.x"});Assert.False(err.Ok);Assert.Equal("js-error",err.Rule);Assert.Contains("nichtDa",err.Text);
            var slow=await Do("chrome_eval",new{tab,js="await new Promise(r=>setTimeout(r,5000))",timeout_s=1});Assert.False(slow.Ok);Assert.Equal("timeout",slow.Rule);
            // ---- controlled input (React style)
            await Do("chrome_navigate",new{tab,url=pages.Url("/react")});
            var rs=await Do("chrome_snapshot",new{tab});string name=Ref(rs.Text,"textbox","Name");Assert.NotEqual("",name);
            var fill=await Do("chrome_fill",new{@ref=name,text="Florian"},"fill");
            Assert.Contains("eingetragen (7 Zeichen)",fill.Text);
            await Task.Delay(150);   // the page puts its own state back every 30 ms: the text must survive that
            Assert.Equal("Florian",(await Do("chrome_eval",new{tab,js="readState()"})).Text);
            Assert.Equal("Florian",(await Do("chrome_eval",new{tab,js="document.getElementById('name').value"})).Text);
            var more=await Do("chrome_fill",new{@ref=name,text=" S",clear=false});Assert.True(more.Ok,more.Text);
            Assert.Equal("Florian S",(await Do("chrome_eval",new{tab,js="readState()"})).Text);
            var none=await Do("chrome_fill",new{@ref=name,text=""});Assert.True(none.Ok,none.Text);
            Assert.Equal("",(await Do("chrome_eval",new{tab,js="readState()"})).Text);
            // ---- form: select, hidden checkbox behind a label, date, textarea, password, Enter
            await Do("chrome_navigate",new{tab,url=pages.Url("/form")});
            var fs=(await Do("chrome_snapshot",new{tab})).Text;
            Assert.Contains("textbox \"Suche\" wert=\"alt\"",fs);Assert.Contains("combobox \"Land\" wert=\"Deutschland\"",fs);Assert.Contains("checkbox \"AGB annehmen\" [aus]",fs);
            Assert.Contains("eingetragen",(await Do("chrome_fill",new{@ref=Ref(fs,"textbox","Notiz"),text="Zeile 1\nZeile 2"})).Text);
            Assert.Equal("Zeile 1\nZeile 2",(await Do("chrome_eval",new{tab,js="document.getElementById('note').value"})).Text);
            Assert.Contains("Schweden",(await Do("chrome_fill",new{@ref=Ref(fs,"combobox","Land"),text="Schweden"})).Text);await Task.Delay(200);Assert.Equal(1,pages.Count("land:se"));
            var nosuch=await Do("chrome_fill",new{@ref=Ref(fs,"combobox","Land"),text="Norwegen"});Assert.False(nosuch.Ok);Assert.Contains("Österreich",nosuch.Text);
            Assert.True((await Do("chrome_click",new{@ref=Ref(fs,"checkbox","AGB annehmen")})).Ok);await Task.Delay(150);Assert.Equal(1,pages.Count("agb:true"));
            Assert.Contains("eingetragen",(await Do("chrome_fill",new{@ref=Ref(fs,"textbox","Tag"),text="2026-11-09"})).Text);
            Assert.Equal("2026-11-09",(await Do("chrome_eval",new{tab,js="document.getElementById('tag').value"})).Text);
            Assert.Contains("eingetragen (6 Zeichen)",(await Do("chrome_fill",new{@ref=Ref(fs,"textbox","Passwort"),text="geheim"})).Text);
            Assert.Contains("wert=(verdeckt, 6 Zeichen)",(await Do("chrome_snapshot",new{tab,filter="Passwort"})).Text);
            fs=(await Do("chrome_snapshot",new{tab})).Text;
            Assert.Contains("vorher",fs);
            var q=await Do("chrome_fill",new{@ref=Ref(fs,"textbox","Suche"),text="neu",enter=true});
            Assert.Contains("eingetragen (3 Zeichen) · Enter gedrückt",q.Text);await Task.Delay(200);Assert.Equal(1,pages.Count("submit:neu"));
            // keys: select all + type over through the page's focused field
            var key=await Do("chrome_key",new{tab,key="ctrl+a x y"});Assert.True(key.Ok,key.Text);
            Assert.Equal("xy",(await Do("chrome_eval",new{tab,js="document.getElementById('q').value"})).Text);
            Assert.False((await Do("chrome_key",new{tab,key="quatsch"})).Ok);
            // ---- iframe (same origin walked, foreign one named) and shadow root
            await Do("chrome_navigate",new{tab,url=pages.Url("/frame")});await Task.Delay(300);
            var fr=(await Do("chrome_snapshot",new{tab})).Text;
            Assert.Contains("Im Rahmen",fr);Assert.Contains("[iframe fremder Herkunft other.localhost",fr);
            Assert.True((await Do("chrome_click",new{@ref=Ref(fr,"button","Rahmenknopf")})).Ok);await Task.Delay(200);Assert.Equal(1,pages.Count("frame:knopf"));
            Assert.Contains("eingetragen",(await Do("chrome_fill",new{@ref=Ref(fr,"textbox","Rahmenfeld"),text="im Rahmen"})).Text);
            Assert.Equal("im Rahmen",(await Do("chrome_eval",new{tab,js="document.querySelector('iframe').contentDocument.getElementById('fi').value"})).Text);
            await Do("chrome_navigate",new{tab,url=pages.Url("/shadow")});
            var sh=(await Do("chrome_snapshot",new{tab})).Text;
            Assert.Contains("Im Schatten",sh);
            Assert.True((await Do("chrome_click",new{@ref=Ref(sh,"button","Schattenknopf")})).Ok);await Task.Delay(200);Assert.Equal(1,pages.Count("shadow:knopf"));
            Assert.Contains("eingetragen",(await Do("chrome_fill",new{@ref=Ref(sh,"textbox","Schattenfeld"),text="tief"})).Text);
            Assert.Equal("tief",(await Do("chrome_eval",new{tab,js="document.querySelector('x-box').shadowRoot.querySelector('input').value"})).Text);
            // ---- navigation: back, forward, reload, a ref dies with the page
            await Do("chrome_navigate",new{tab,url=pages.Url("/nav1")});
            var n1=(await Do("chrome_snapshot",new{tab})).Text;string weiter=Ref(n1,"link","weiter");
            var nav=await Do("chrome_navigate",new{tab,url=pages.Url("/nav2")});Assert.Contains("Geladen",nav.Text);Assert.Contains("\"Zwei\"",nav.Text);
            var gone=await Do("chrome_click",new{@ref=weiter});Assert.True(gone.Stale,gone.Text);
            Assert.Contains("\"Eins\"",(await Do("chrome_navigate",new{tab,url="back"})).Text);
            Assert.Contains("\"Zwei\"",(await Do("chrome_navigate",new{tab,url="forward"})).Text);
            Assert.Contains("\"Zwei\"",(await Do("chrome_navigate",new{tab,url="reload"})).Text);
            Assert.Equal("no-history",(await Do("chrome_navigate",new{tab,url="forward"})).Rule);
            var bad=await Do("chrome_navigate",new{tab,url="http://127.0.0.1:1/"});Assert.False(bad.Ok);Assert.Equal("nav-error",bad.Rule);
            var read=await Do("chrome_read",new{tab=tab});Assert.True(read.Ok);
            // ---- _blank link: a new tab appears, it is the run's own (it came from an own tab)
            await Do("chrome_navigate",new{tab,url=pages.Url("/blank")});
            var bl=(await Do("chrome_snapshot",new{tab})).Text;
            var pop=await Do("chrome_click",new{@ref=Ref(bl,"link","Neues Fenster")});
            Assert.Contains("neuer Tab [t",pop.Text);Assert.Equal(2,state.Own.Count);
            string popTab=Regex.Match(pop.Text,@"neuer Tab \[(t\d+)\]").Groups[1].Value;
            await Task.Delay(400);
            Assert.Contains("Seite zwei",(await Do("chrome_read",new{tab=popTab})).Text);
            Assert.Contains("geschlossen",(await Do("chrome_close",new{tab=popTab})).Text);Assert.Single(state.Own);
            // ---- alert: dismissed, the call does not hang, the model is told
            await Do("chrome_navigate",new{tab,url=pages.Url("/alert")});
            var al=(await Do("chrome_snapshot",new{tab})).Text;
            var sw=System.Diagnostics.Stopwatch.StartNew();
            var melden=await Do("chrome_click",new{@ref=Ref(al,"button","Melden")});
            Assert.True(melden.Ok,melden.Text);Assert.InRange(sw.ElapsedMilliseconds,0,4000);
            await Task.Delay(300);
            var afterAlert=await Do("chrome_eval",new{tab,js="document.title"});
            Assert.Equal("nach-alert",afterAlert.Text.Split('\n')[0]);
            Assert.Contains("Dialog (alert)",melden.Text+afterAlert.Text);Assert.Contains("Hallo aus der Seite",melden.Text+afterAlert.Text);
            // ---- long page: viewport scope, all, cut, scroll, full-page picture
            await Do("chrome_navigate",new{tab,url=pages.Url("/long")});
            var top=(await Do("chrome_snapshot",new{tab})).Text;
            Assert.Contains("# Lange Seite",top);Assert.Contains("Absatz 1 ",top);Assert.DoesNotContain("Absatz 250 ",top);Assert.Contains("all:true",top);
            var whole=(await Do("chrome_snapshot",new{tab,all=true})).Text;
            Assert.Contains("Absatz 300 ",whole);Assert.Contains("button \"Ganz unten\"",whole);
            var scr=await Do("scroll",new{tab,dy=3000});Assert.True(scr.Ok,scr.Text);await Task.Delay(400);
            Assert.True(double.Parse((await Do("chrome_eval",new{tab,js="scrollY"})).Text,System.Globalization.CultureInfo.InvariantCulture)>1000);
            var mid=(await Do("chrome_snapshot",new{tab})).Text;Assert.DoesNotContain("Absatz 1 ",mid);Assert.Matches(@"scrollY [1-9]\d+/",mid);
            var deep=await Do("chrome_click",new{@ref=Ref(whole=(await Do("chrome_snapshot",new{tab,all=true})).Text,"button","Ganz unten")});
            Assert.True(deep.Ok,deep.Text);await Task.Delay(200);Assert.Equal(1,pages.Count("long:unten"));Assert.DoesNotContain("element.click()",deep.Text);
            var visible=await Do("chrome_screenshot",new{tab});
            var full=await Do("chrome_screenshot",new{tab,full=true});
            Assert.True(full.Shot!.Full);Assert.True(full.Shot.Height>visible.Shot!.Height*2,full.Shot.Height+" vs "+visible.Shot.Height);Assert.InRange(full.Shot.Height,1000,4001);Assert.InRange(full.Shot.Jpeg.Length,1000,1_400_000);
            // a click on the full-page picture near its top: the page scrolls there first
            await c.Mouse(full.Shot.Tab+"#full","click",20,20,0,0,"left",1,0,0,600,"");
            Assert.Equal("0",(await Do("chrome_eval",new{tab,js="scrollY"})).Text);
            // the scrolled viewport picture differs from the one at the top (the clip follows the scroll position)
            var atTop=await Do("chrome_screenshot",new{tab});
            await Do("scroll",new{tab,dy=2000});await Task.Delay(300);
            var lower=await Do("chrome_screenshot",new{tab});
            Assert.NotEqual(atTop.Shot!.Jpeg,lower.Shot!.Jpeg);
            // ---- his tabs: a tab the run did not open is not closed without force
            var theirs=await socket.Call("Target.createTarget",new{url=pages.Url("/nav1"),background=true});
            string theirId=theirs.GetProperty("targetId").GetString()!;
            try
            {
                await Task.Delay(600);   // the background tab loads
                var list=await Do("chrome_tabs",new{});
                string theirName=state.Alias(theirId);
                Assert.Matches(@"\["+theirName+@"\] ""Eins""",list.Text);
                var no=await Do("chrome_close",new{tab=theirName});Assert.False(no.Ok);Assert.Equal("not-own",no.Rule);
                // input into a background tab: it is shown in its window first, and the click lands
                var bs=(await Do("chrome_snapshot",new{tab=theirName})).Text;
                var bc=await Do("chrome_click",new{@ref=Ref(bs,"link","weiter")});
                Assert.True(bc.Ok,bc.Text);await Task.Delay(400);
                Assert.Contains("Seite zwei",(await Do("chrome_read",new{tab=theirName})).Text);
                var yes=await Do("chrome_close",new{tab=theirName,force=true});Assert.True(yes.Ok,yes.Text);theirId="";
            }
            finally{if(theirId.Length>0)await socket.Call("Target.closeTarget",new{targetId=theirId});}
            Assert.Equal("not-found",(await Do("chrome_read",new{tab="t999"})).Rule);
            Assert.Equal(1,await c.OwnOpen());
            output.WriteLine("ms: "+JsonSerializer.Serialize(times));
            Assert.InRange(times["snapshot-cards"],0,400);
        }
        finally
        {
            foreach(string id in state.Own.ToList()){try{await socket.Call("Target.closeTarget",new{targetId=id});}catch(Exception){ }}
            lock(logs)foreach(string l in logs)output.WriteLine("log "+l);
        }
    }
}
