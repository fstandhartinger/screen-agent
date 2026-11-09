using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using ScreenAgent.Core;
namespace ScreenAgent;
internal static class ComputerServe
{
    public static int Run(int port)
    {
        if(port is <1 or >65535)throw new ArgumentException("Ungültiger Port.");
        string? token=Environment.GetEnvironmentVariable("SA_SERVE_TOKEN");
        if(!ServeAuth.ValidToken(token)){Console.Error.WriteLine("--computer-serve benötigt SA_SERVE_TOKEN mit mindestens 16 Zeichen.");return 2;}
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};int exit=0;
        app.Startup+=async (_,_)=>
        {
            try{await Serve(app,port,token!);}catch(Exception e){Console.WriteLine(JsonSerializer.Serialize(new{error=e.Message}));exit=1;}finally{app.Shutdown(exit);}
        };app.Run();return exit;
    }
    // TcpListener avoids HttpListener's administrator-only URL reservation on a normal Windows laptop.
    // This deliberately small debug protocol accepts bounded Content-Length requests and closes each connection.
    sealed record Request(string Method,string Path,Dictionary<string,string> Headers,byte[] Body);
    static async Task<Request> Read(NetworkStream stream,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(5000);
        var header=new List<byte>();var one=new byte[1];
        while(header.Count<16384)
        {
            int n=await stream.ReadAsync(one,deadline.Token);if(n==0)throw new IOException("Anfrage abgebrochen.");header.Add(one[0]);
            if(header.Count>=4&&header[^4]==13&&header[^3]==10&&header[^2]==13&&header[^1]==10)break;
        }
        if(header.Count>=16384)throw new ArgumentException("HTTP-Header zu groß.");var lines=Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");var start=lines[0].Split(' ');
        if(start.Length!=3||!start[2].StartsWith("HTTP/1."))throw new ArgumentException("Ungültige HTTP-Anfrage.");var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(string line in lines.Skip(1)){int colon=line.IndexOf(':');if(colon>0){string name=line[..colon];if(!headers.TryAdd(name,line[(colon+1)..].Trim()))throw new ArgumentException("Doppelter HTTP-Header.");}}
        if(headers.ContainsKey("Transfer-Encoding"))throw new ArgumentException("Content-Length erforderlich.");int length=0;
        if(headers.TryGetValue("Content-Length",out var size)&&(!int.TryParse(size,out length)||length<0||length>65536))throw new ArgumentException("Anfrage zu groß.");
        var body=new byte[length];await stream.ReadExactlyAsync(body,deadline.Token);return new(start[0],start[1],headers,body);
    }
    static async Task Reply(NetworkStream stream,int status,object result)
    {
        byte[] body=JsonSerializer.SerializeToUtf8Bytes(result);string reason=status switch{200=>"OK",401=>"Unauthorized",404=>"Not Found",405=>"Method Not Allowed",_=>"Bad Request"};
        byte[] head=Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");await stream.WriteAsync(head);await stream.WriteAsync(body);
    }
    static async Task Serve(Application app,int port,string token)
    {
        using var session=new DesktopSession(app,Path.Combine(Path.GetTempPath(),"sa-serve-"+Guid.NewGuid().ToString("N"),"computer.log"));
        using var listener=new TcpListener(IPAddress.Loopback,port);listener.Start();using var lifetime=new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var requests=new List<Task>();using var serial=new SemaphoreSlim(1);
        async Task Handle(TcpClient client)
        {
            using(client)
            {
                bool shutdown=false;object response;int status=200;using var stream=client.GetStream();
                try
                {
                    var request=await Read(stream,lifetime.Token);string path=request.Path;
                    if(!ServeAuth.Authorized(token,request.Headers.GetValueOrDefault("Authorization"))){await Reply(stream,401,new{ok=false});return;}
                    if(request.Method=="POST"&&!ServeAuth.JsonContentType(request.Headers.GetValueOrDefault("Content-Type"))){await Reply(stream,415,new{ok=false,error="Content-Type: application/json required"});return;}
                    if(path=="/health"&&request.Method=="GET")response=new{ok=true,mode="computer-serve",run=session.RunId};
                    else if(request.Method!="POST"){status=405;response=new{ok=false,error="POST required"};}
                    else if(path=="/stop"){session.Kill();response=new{ok=true};shutdown=true;}
                    else
                    {
                        using var doc=JsonDocument.Parse(request.Body);var a=doc.RootElement;await serial.WaitAsync(lifetime.Token);
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
                try{await Reply(stream,status,response);}catch(IOException){ /* The debug client disconnected; no action is retried. */ }finally{if(shutdown)lifetime.Cancel();}
            }
        }
        Console.WriteLine($"computer-serve: http://127.0.0.1:{port}");
        try{while(!lifetime.IsCancellationRequested){var c=await listener.AcceptTcpClientAsync(lifetime.Token);requests.RemoveAll(t=>t.IsCompleted);requests.Add(Task.Run(()=>Handle(c)));}}
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){ /* Stop or the 30 minute deadline closed the listener. */ }
        finally{listener.Stop();session.Kill();await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));}
    }
}
