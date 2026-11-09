using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenAgent.Core;
namespace ScreenAgent;
internal static class ScreenTextSelftest
{
    sealed record Scenario(string name,bool ok,long ms,string? why=null,string status="passed");
    static readonly string[] Names=["Opnar AI","Nordgatan","Outwing"];
    public static int Run()
    {
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};int exit=1;
        app.Startup+=async (_,_)=>
        {
            var scenarios=new List<Scenario>();Window? window=null;HttpListener? listener=null;
            string folder=Path.GetFullPath("artifacts");Directory.CreateDirectory(folder);
            string tokenFile=Path.Combine(folder,"screentext-test-token.bin");
            try
            {
                var stack=new StackPanel{Margin=new Thickness(24)};
                stack.Children.Add(new TextBlock{Text="DU BIST DRAN",FontSize=20,Margin=new Thickness(0,0,0,12)});
                foreach(string name in Names)
                {
                    var card=new StackPanel{Margin=new Thickness(0,8,0,16)};
                    card.Children.Add(new TextBlock{Text=$"{name} (Lund) ein Angebot schicken?",Margin=new Thickness(0,0,0,8)});
                    var buttons=new StackPanel{Orientation=Orientation.Horizontal};
                    foreach(string label in new[]{"Senden (empfohlen)","Nein danke","Später entscheiden"})buttons.Children.Add(new Button{Content=label,Padding=new Thickness(12,6,12,6),Margin=new Thickness(0,0,8,0)});
                    card.Children.Add(buttons);stack.Children.Add(card);
                }
                window=new Window{Title="Screen text test cards",Content=stack,Width=1050,Height=650,Left=25,Top=25,FontFamily=new FontFamily("Verdana"),FontSize=16,Background=Brushes.White,Topmost=true};
                window.Show();var hwnd=new WindowInteropHelper(window).Handle;Native.SetForegroundWindow(hwnd);window.Activate();await Task.Delay(350);
                var source=new ScreenTextSource(new PrivacyPolicy(),()=>true);
                var result=await source.Capture("dictation",CancellationToken.None)??throw new InvalidOperationException("Test window capture returned null");
                using(var image=new PrivacyCapture(new PrivacyPolicy()).Capture(System.Windows.Forms.Screen.FromHandle(hwnd).Bounds,true,false))
                    if(image is not null)image.Save(Path.Combine(folder,"screentext-window.png"),System.Drawing.Imaging.ImageFormat.Png);
                File.WriteAllText(Path.Combine(folder,"screentext-sources.json"),JsonSerializer.Serialize(new{result.UiaText,result.OcrText,result.UiaMs,result.OcrMs,result.Ms}));
                scenarios.Add(new("uia",Names.All(n=>result.UiaText.Contains(n,StringComparison.Ordinal)),result.UiaMs));
                bool noOcrLanguage=Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Count==0;
                bool ocrOk=Names.All(n=>result.OcrText.Contains(n,StringComparison.Ordinal));
                scenarios.Add(new("ocr",ocrOk||noOcrLanguage,result.OcrMs,noOcrLanguage&&!ocrOk?"No Windows OCR language pack on runner; bundled fallback did not recognize all fixture names":ocrOk?null:"OCR missed at least one fixture name",noOcrLanguage&&!ocrOk?"skipped":ocrOk?"passed":"failed"));
                scenarios.Add(new("capture",Names.All(n=>result.Text.Contains(n,StringComparison.Ordinal)),result.Ms));
                // Bind one ephemeral loopback port; never contact the live gateway.
                var portProbe=new TcpListener(IPAddress.Loopback,0);portProbe.Start();int port=((IPEndPoint)portProbe.LocalEndpoint).Port;portProbe.Stop();
                string origin=$"http://127.0.0.1:{port}";listener=new HttpListener();listener.Prefixes.Add(origin+"/");listener.Start();
                var config=new AppConfig{DeskUrl=origin};using var client=new DeviceClient(config,tokenPath:tokenFile);
                string token=new('a',43);client.Pair(new(token,Guid.NewGuid().ToString(),DateTimeOffset.UtcNow.AddMinutes(10)));
                long now=1000000;var sender=new ScreenTextSender(source,client,()=>true,(_,_)=>{},()=>now);
                var pending=listener.GetContextAsync();var watch=Stopwatch.StartNew();
                var send=sender.Send(result,"session",CancellationToken.None,123456L);
                var request=await pending.WaitAsync(TimeSpan.FromSeconds(5));
                string json=await new StreamReader(request.Request.InputStream,Encoding.UTF8).ReadToEndAsync();
                File.WriteAllText(Path.Combine(folder,"screentext-payload.json"),json);
                using var body=JsonDocument.Parse(json);var root=body.RootElement;
                bool shape=root.GetProperty("reason").GetString()=="session"&&root.GetProperty("frameVersion").GetInt64()==123456&&root.GetProperty("process").GetString()?.Length>0&&root.GetProperty("title").GetString()?.Length<=200&&root.GetProperty("text").GetString()?.Length<=6000&&Names.All(n=>root.GetProperty("text").GetString()!.Contains(n));
                bool auth=request.Request.Headers["Authorization"]=="Bearer "+token&&request.Request.HttpMethod=="POST"&&request.Request.Url?.AbsolutePath=="/api/device/screen-text";
                byte[] reply=Encoding.UTF8.GetBytes("{\"ok\":true,\"terms\":3}");request.Response.StatusCode=200;await request.Response.OutputStream.WriteAsync(reply);request.Response.Close();await send;
                scenarios.Add(new("wire-json-bearer",shape&&auth,watch.ElapsedMilliseconds));
                pending=listener.GetContextAsync();
                watch.Restart();await sender.Send(result,"session",CancellationToken.None,123457L);await Task.Delay(120);
                scenarios.Add(new("unchanged-no-post",!pending.IsCompleted,watch.ElapsedMilliseconds));
                async Task Accept(Task sending,Task<HttpListenerContext> next,int status=200)
                {
                    var received=await next.WaitAsync(TimeSpan.FromSeconds(5));
                    await new StreamReader(received.Request.InputStream).ReadToEndAsync();
                    received.Response.StatusCode=status;received.Response.Close();await sending;
                }
                watch.Restart();now+=19999;await sender.Send(result,"dictation",CancellationToken.None);
                scenarios.Add(new("dictation-under-20s-no-post",!pending.IsCompleted,watch.ElapsedMilliseconds));
                watch.Restart();now++;await Accept(sender.Send(result,"dictation",CancellationToken.None),pending);
                scenarios.Add(new("dictation-20s-refresh",true,watch.ElapsedMilliseconds));pending=listener.GetContextAsync();
                watch.Restart();now+=59999;await sender.Send(result,"session",CancellationToken.None);
                scenarios.Add(new("session-under-60s-no-post",!pending.IsCompleted,watch.ElapsedMilliseconds));
                watch.Restart();now++;await Accept(sender.Send(result,"session",CancellationToken.None),pending);
                scenarios.Add(new("session-60s-refresh",true,watch.ElapsedMilliseconds));pending=listener.GetContextAsync();
                watch.Restart();var enriched=result with{Text=result.Text+"\nZusätzlicher sichtbarer Name"};
                await Accept(sender.Send(enriched,"dictation",CancellationToken.None,enrichment:true),pending);
                scenarios.Add(new("ocr-small-enrichment",true,watch.ElapsedMilliseconds));pending=listener.GetContextAsync();
                int failures=0;var oldGatewaySender=new ScreenTextSender(source,client,()=>true,(_,_)=>failures++);
                watch.Restart();await Accept(oldGatewaySender.Send(result,"dictation",CancellationToken.None),pending,404);pending=listener.GetContextAsync();
                await oldGatewaySender.Send(result,"dictation",CancellationToken.None);await Task.Delay(120);
                scenarios.Add(new("old-gateway-no-retry",!pending.IsCompleted&&failures==1,watch.ElapsedMilliseconds));
                var blockedSource=new ScreenTextSource(new PrivacyPolicy{ExtraTitlePatterns=["Screen text test cards"]},()=>true);
                var blockedSender=new ScreenTextSender(blockedSource,client,()=>true,(_,_)=>{});
                watch.Restart();var blocked=await blockedSource.Capture("dictation",CancellationToken.None);await blockedSender.Run("dictation",CancellationToken.None);await blockedSender.Send(result,"dictation",CancellationToken.None);await Task.Delay(120);
                scenarios.Add(new("blocked-no-post",blocked is null&&!pending.IsCompleted,watch.ElapsedMilliseconds));
                watch.Restart();var paused=new ScreenTextSource(new PrivacyPolicy(),()=>false);await new ScreenTextSender(paused,client,()=>true,(_,_)=>{}).Run("dictation",CancellationToken.None);
                scenarios.Add(new("paused-no-post",!pending.IsCompleted&&await paused.Capture("dictation",CancellationToken.None) is null,watch.ElapsedMilliseconds));
                watch.Restart();await new ScreenTextSender(source,client,()=>false,(_,_)=>{}).Run("dictation",CancellationToken.None);
                scenarios.Add(new("disabled-no-post",!pending.IsCompleted,watch.ElapsedMilliseconds));
                watch.Restart();var reused=await source.Capture("session",CancellationToken.None,"Wiederverwendeter OCR Kontext");
                scenarios.Add(new("session-reuses-ocr",reused?.OcrText=="Wiederverwendeter OCR Kontext"&&reused.OcrMs==0,watch.ElapsedMilliseconds));
                var liveSender=new ScreenTextSender(source,client,()=>true,(_,_)=>{});
                watch.Restart();var running=liveSender.Start("dictation",CancellationToken.None);long queuedMs=watch.ElapsedMilliseconds;
                var firstPost=await pending.WaitAsync(TimeSpan.FromSeconds(6));long firstMs=watch.ElapsedMilliseconds;
                string firstJson=await new StreamReader(firstPost.Request.InputStream).ReadToEndAsync();
                File.WriteAllText(Path.Combine(folder,"screentext-first-post.json"),firstJson);
                using var firstBody=JsonDocument.Parse(firstJson);
                bool firstNames=Names.All(n=>firstBody.RootElement.GetProperty("text").GetString()!.Contains(n));
                firstPost.Response.StatusCode=200;firstPost.Response.Close();
                var nextPost=listener.GetContextAsync();
                if(await Task.WhenAny(running,nextPost)==nextPost)await Accept(running,nextPost);
                else await running;
                scenarios.Add(new("hotkey-queue",queuedMs<5,queuedMs));
                scenarios.Add(new("dictation-first-post",firstNames&&firstMs<=800,firstMs,firstMs>800?"First POST exceeded the 800 ms target":null));
                scenarios.Add(new("dictation-whole-capture",true,watch.ElapsedMilliseconds));
                exit=scenarios.All(s=>s.ok)?0:1;
            }
            catch(Exception ex){scenarios.Add(new("exception",false,0,ex.ToString()));}
            finally
            {
                listener?.Close();window?.Close();if(File.Exists(tokenFile))File.Delete(tokenFile);
                var output=JsonSerializer.Serialize(new{test="screentext",scenarios=scenarios.Select(s=>s with{status=s.status=="skipped"?"skipped":s.ok?"passed":"failed"})},new JsonSerializerOptions{WriteIndented=false});
                File.WriteAllText(Path.Combine(folder,"screentext.json"),output);Console.WriteLine(output);app.Shutdown(exit);
            }
        };
        app.Run();return exit;
    }
}
