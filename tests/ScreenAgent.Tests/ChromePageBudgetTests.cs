using System.Diagnostics;
using System.Text.Json;
using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
public class ChromePageBudgetTests
{
    static JsonElement Run(string setup,string function,string arguments)
    {
        string file=Path.Combine(Path.GetTempPath(),"sa-js-test-"+Guid.NewGuid().ToString("N")+".js");
        try
        {
            File.WriteAllText(file,setup+"\nconsole.log(JSON.stringify(("+function+")("+arguments+")));\n");
            var start=new ProcessStartInfo("node"){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};start.ArgumentList.Add(file);
            using var p=Process.Start(start)!;var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();
            if(!p.WaitForExit(10000)){p.Kill(true);throw new TimeoutException("DOM mock exceeded 10 seconds");}
            Assert.True(p.ExitCode==0,error.GetAwaiter().GetResult());return JsonDocument.Parse(output.GetAwaiter().GetResult()).RootElement.Clone();
        }
        finally{File.Delete(file);}
    }
    const string Mock="""
        global.innerWidth=800;global.innerHeight=600;global.scrollY=0;global.location={href:'https://example.test',host:'example.test'};
        global.getComputedStyle=()=>({display:'block',visibility:'visible',opacity:'1',cursor:'auto'});
        function node(tag='div'){return {localName:tag,nodeType:1,childNodes:[],scrollHeight:600,getAttribute:()=>null,hasAttribute:()=>false,getBoundingClientRect:()=>({left:0,top:0,right:10,bottom:10,width:10,height:10})};}
        const body=node('body');global.document={body,documentElement:body,title:'Test',createRange:()=>({})};
        """;
    [Fact] public void FiftyThousandEmptyNodesHitVisitedBudget()
    {
        var r=Run(Mock+"for(let i=0;i<50000;i++)body.childNodes.push(node());",ChromePageJs.Snapshot,"1,1,true");
        Assert.True(r.GetProperty("cut").GetBoolean());Assert.Equal("nodes",r.GetProperty("reason").GetString());
        Assert.InRange(r.GetProperty("visited").GetInt32(),20000,20001);Assert.Empty(r.GetProperty("items").EnumerateArray());
    }
    [Fact] public void DeepTreeEndsAtDepthBudgetWithoutRecursiveOverflow()
    {
        var r=Run(Mock+"let last=body;for(let i=0;i<50000;i++){const next=node();last.childNodes.push(next);last=next;}",ChromePageJs.Snapshot,"1,1,true");
        Assert.True(r.GetProperty("cut").GetBoolean());Assert.Equal("depth",r.GetProperty("reason").GetString());
        Assert.InRange(r.GetProperty("visited").GetInt32(),1,200);
    }
    [Fact] public void ElapsedBudgetStopsBeforeNodeBudget()
    {
        var r=Run(Mock+"let clock=0;global.performance={now:()=>clock+=100};for(let i=0;i<1000;i++)body.childNodes.push(node());",ChromePageJs.Snapshot,"1,1,true");
        Assert.True(r.GetProperty("cut").GetBoolean());Assert.Equal("time",r.GetProperty("reason").GetString());Assert.True(r.GetProperty("visited").GetInt32()<1000);
    }
    [Fact] public void ParentOverlayPreventsIframeMouseHit()
    {
        const string setup="""
        const overlay={localName:'div'},parent={innerWidth:800,innerHeight:600,getComputedStyle:()=>({paddingLeft:'0',paddingTop:'0'})};
        const doc={defaultView:parent,elementFromPoint:()=>overlay};
        const f={ownerDocument:doc,clientLeft:0,clientTop:0,contains:()=>false,getBoundingClientRect:()=>({left:0,top:0})};
        const win={innerWidth:800,innerHeight:600,frameElement:f};
        const r={left:0,top:0,width:100,height:50};let clicks=0;
        const el={localName:'button',isConnected:true,innerText:'Button',ownerDocument:{defaultView:win,elementFromPoint:()=>el},getAttribute:()=>null,getRootNode(){return this.ownerDocument;},getBoundingClientRect:()=>r,getClientRects:()=>[r],scrollIntoView:()=>{},click:()=>clicks++};
        global.__sa={g:1,m:new Map([[1,el]])};
        """;
        var r=Run(setup,ChromePageJs.Locate,"1,1,'click'");Assert.Equal("js",r.GetProperty("how").GetString());
    }
}
