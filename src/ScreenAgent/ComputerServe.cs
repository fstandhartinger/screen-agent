using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
namespace ScreenAgent;
internal static class ComputerServe
{
    public static int Run(int port)
    {
        if(port is <1 or >65535)throw new ArgumentException("Ungültiger Port.");
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};int exit=0;
        app.Startup+=async (_,_)=>
        {
            try{await Serve(app,port);}catch(Exception e){Console.WriteLine(JsonSerializer.Serialize(new{error=e.Message}));exit=1;}finally{app.Shutdown(exit);}
        };app.Run();return exit;
    }
    static async Task Serve(Application app,int port)
    {
        using var session=new DesktopSession(app,Path.Combine(Path.GetTempPath(),"sa-serve-"+Guid.NewGuid().ToString("N"),"computer.log"));
        using var listener=new HttpListener();listener.Prefixes.Add($"http://127.0.0.1:{port}/");listener.Start();
        using var lifetime=new CancellationTokenSource(TimeSpan.FromMinutes(30));using var cancel=lifetime.Token.Register(()=>listener.Stop());
        var requests=new List<Task>();var serial=new SemaphoreSlim(1);string? token=Environment.GetEnvironmentVariable("SA_SERVE_TOKEN");
        async Task Handle(HttpListenerContext context)
        {
            bool shutdown=false;object response;int status=200;
            try
            {
                if(!string.IsNullOrEmpty(token)&&context.Request.Headers["Authorization"]!="Bearer "+token){context.Response.StatusCode=401;context.Response.Close();return;}
                var path=context.Request.Url!.AbsolutePath;
                if(path=="/health"&&context.Request.HttpMethod=="GET")response=new{ok=true,mode="computer-serve",run=session.RunId};
                else if(context.Request.HttpMethod!="POST"){status=405;response=new{ok=false,error="POST required"};}
                else if(path=="/stop"){session.Kill();response=new{ok=true};shutdown=true;}
                else
                {
                    using var reader=new StreamReader(context.Request.InputStream,Encoding.UTF8);var chars=new char[65537];int count=0,n;
                    while(count<chars.Length&&(n=await reader.ReadAsync(chars.AsMemory(count)))>0)count+=n;
                    if(count>65536)throw new ArgumentException("Anfrage zu groß.");using var doc=JsonDocument.Parse(new string(chars,0,count));var a=doc.RootElement;
                    await serial.WaitAsync(lifetime.Token);
                    try
                    {
                        if(path=="/start"){var result=await session.Start(FreeArgs.Str(a,"goal")??"Desktop steuern");response=new{run=session.RunId,result};}
                        else if(path=="/act")response=await session.Act(FreeArgs.Str(a,"tool")??throw new ArgumentException("tool fehlt"),a.TryGetProperty("args",out var args)?args.Clone():new{});
                        else{status=404;response=new{ok=false,error="unknown endpoint"};}
                    }
                    finally{serial.Release();}
                }
            }
            catch(Exception e){ /* Debug protocol reports the failure to its caller. */ status=400;response=new{ok=false,error=e.Message};}
            try{byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(response);context.Response.StatusCode=status;context.Response.ContentType="application/json; charset=utf-8";context.Response.ContentLength64=bytes.Length;await context.Response.OutputStream.WriteAsync(bytes);context.Response.Close();}finally{if(shutdown)lifetime.Cancel();}
        }
        Console.WriteLine($"computer-serve: http://127.0.0.1:{port}");
        try{while(!lifetime.IsCancellationRequested){var c=await listener.GetContextAsync();requests.RemoveAll(t=>t.IsCompleted);requests.Add(Task.Run(()=>Handle(c)));}}
        catch(HttpListenerException) when(lifetime.IsCancellationRequested){ /* Stop or the 30 minute deadline closed the listener. */ }
        finally{session.Kill();await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));}
    }
}
