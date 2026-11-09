using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace ScreenAgent;
internal sealed class DesktopShell(FreeHost host)
{
    public static string Expand(string path)
    {
        if(path=="~"||path.StartsWith("~/")||path.StartsWith("~\\"))path=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)+path[1..];
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }
    sealed class TailBuffer
    {
        readonly object sync=new();readonly StringBuilder tail=new();long removed;
        public async Task Read(Stream stream)
        {
            // WSL uses UTF-16 for some launcher diagnostics, UTF-8 for Linux output. Detect its NUL layout as well as BOMs.
            var bytes=new byte[4096];int n=await stream.ReadAsync(bytes);if(n==0)return;
            bool utf16=n>=2&&(bytes[0]==255&&bytes[1]==254||bytes[1]==0);var encoding=utf16?Encoding.Unicode:Encoding.UTF8;var decoder=encoding.GetDecoder();var chars=new char[8192];
            do{int count=decoder.GetChars(bytes,0,n,chars,0,false);Add(new string(chars,0,count).TrimStart('\ufeff'));n=await stream.ReadAsync(bytes);}while(n>0);
            int last=decoder.GetChars([],0,0,chars,0,true);if(last>0)Add(new string(chars,0,last));
        }
        void Add(string s){lock(sync){tail.Append(s);if(tail.Length>12000){int cut=tail.Length-12000;tail.Remove(0,cut);removed+=cut;}}}
        public override string ToString(){lock(sync)return DesktopText.TailMarker(removed)+tail;}
    }
    (int Exit,long Ms,string Out,string Error) Execute(string exe,IEnumerable<string> args,int seconds,string cwd,string? stdin=null)
    {
        host.ThrowIfStopped();var start=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true,StandardInputEncoding=new UTF8Encoding(false),WorkingDirectory=cwd};
        foreach(var arg in args)start.ArgumentList.Add(arg);
        using var process=new Process{StartInfo=start};var sw=Stopwatch.StartNew();process.Start();var stdout=new TailBuffer();var stderr=new TailBuffer();var output=stdout.Read(process.StandardOutput.BaseStream);var error=stderr.Read(process.StandardError.BaseStream);
        var writer=Task.Run(async()=>{try{if(stdin is not null)await process.StandardInput.WriteAsync(stdin);process.StandardInput.Close();}catch(IOException){ /* The child closed stdin early; its exit/stderr are returned. */ }});
        try
        {
            while(!process.WaitForExit(50)){host.ThrowIfStopped();if(sw.ElapsedMilliseconds>=seconds*1000L)throw new TimeoutException("Zeitlimit; Prozessbaum beendet.");}
            host.ThrowIfStopped();var drains=Task.WhenAll(output,error,writer);var drainClock=Stopwatch.StartNew();while(!drains.IsCompleted){host.ThrowIfStopped();if(drainClock.ElapsedMilliseconds>1000)break;Thread.Sleep(20);}
            return(process.ExitCode,sw.ElapsedMilliseconds,stdout.ToString(),stderr.ToString());
        }
        finally{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){ /* The owned process exited between HasExited and Kill. */ } }
    }
    static string? Find(string name)
    {
        foreach(var dir in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator)){string p=Path.Combine(dir.Trim('"'),name);if(File.Exists(p))return p;}return null;
    }
    public FreeResult Run(string tool,JsonElement args)
    {
        int seconds=Math.Clamp(FreeArgs.Int(args,"timeout_s")??60,1,600);string cwd=Expand(FreeArgs.Str(args,"cwd")??"~"),command=FreeArgs.Str(args,"command")??"";
        if(!Directory.Exists(cwd))return FreeResult.Fail("bad-args","Arbeitsverzeichnis fehlt.");
        string? temporary=null;
        try
        {
            if(tool=="run_python")
            {
                string code=FreeArgs.Str(args,"code")??throw new ArgumentException("code fehlt.");temporary=Path.Combine(Path.GetTempPath(),"sa-python-"+Guid.NewGuid().ToString("N")+".py");File.WriteAllText(temporary,code,new UTF8Encoding(false));
            }
            (int Exit,long Ms,string Out,string Error) result;
            // Windows PowerShell 5 decodes redirected stdin in the OEM code page. Keep the transport UTF-8/ASCII and decode the UTF-8 script explicitly.
            if(tool=="run_powershell")result=Execute("powershell.exe",["-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-Command","-"],seconds,cwd,"[Console]::OutputEncoding=[Text.Encoding]::UTF8;\n& ([ScriptBlock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"+Convert.ToBase64String(Encoding.UTF8.GetBytes(command))+"'))))\n");
            else if(tool=="run_cmd")result=Execute("cmd.exe",["/d","/s","/c",command],seconds,cwd);
            else if(tool=="run_wsl"||tool=="run_python"&&(FreeArgs.Str(args,"where")=="wsl"||Find("py.exe") is null&&Find("python.exe") is null))
            {
                string? wsl=Find("wsl.exe");if(wsl is null)return FreeResult.Fail("no-wsl","WSL ist nicht installiert; run_powershell verwenden.");
                var check=Execute(wsl,["--list","--quiet"],Math.Min(seconds,3),cwd);if(check.Exit!=0||string.IsNullOrWhiteSpace(check.Out))return FreeResult.Fail("no-wsl","Keine WSL-Distribution installiert; run_powershell verwenden.");
                var parameters=new List<string>();if(FreeArgs.Str(args,"distro") is {} distro){parameters.Add("-d");parameters.Add(distro);}parameters.AddRange(["--","bash","-lc"]);
                // wslpath respects a distro's configured mount root. The Windows-owned temporary file is deleted even on kill.
                parameters.Add(tool=="run_python"?"python3 \"$(wslpath -a '"+temporary!.Replace("'","'\\''")+"')\"":command);result=Execute(wsl,parameters,seconds,cwd);
            }
            else
            {
                string? py=Find("py.exe");string python=py??Find("python.exe")??throw new ArgumentException("Python fehlt.");result=Execute(python,py is null?[temporary!]:["-3",temporary!],seconds,cwd);
            }
            return new(true,$"exit={result.Exit} ({result.Ms} ms)\n--- stdout ---\n{result.Out}\n--- stderr ---\n{result.Error}");
        }
        finally{if(temporary is not null)File.Delete(temporary);}
    }
    public FreeResult FileTool(string tool,JsonElement args)
    {
        string path=Expand(FreeArgs.Str(args,"path")??throw new ArgumentException("path fehlt."));host.ThrowIfStopped();
        if(tool=="file_write")
        {
            string content=FreeArgs.Str(args,"content",1000000)??throw new ArgumentException("content fehlt.");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if(FreeArgs.Bool(args,"append")==true)File.AppendAllText(path,content,new UTF8Encoding(false));else File.WriteAllText(path,content,new UTF8Encoding(false));return new(true,$"{content.Length} Zeichen geschrieben.");
        }
        int max=Math.Clamp(FreeArgs.Int(args,"max")??20000,1,1000000);using var stream=File.OpenRead(path);var probe=new byte[4096];int n=stream.Read(probe);bool bom=n>=2&&(probe[0]==255&&probe[1]==254||probe[0]==254&&probe[1]==255);
        if(!bom&&probe.Take(n).Any(b=>b==0))return new(true,$"Binärdatei: {stream.Length} Bytes.");stream.Position=0;using var reader=new StreamReader(stream,Encoding.UTF8,true);
        var text=new StringBuilder();var buf=new char[Math.Min(max,4096)];bool tail=FreeArgs.Bool(args,"tail")==true;long skipped=0;
        while((n=reader.Read(buf,0,buf.Length))>0){host.ThrowIfStopped();text.Append(buf,0,n);if(text.Length>max){int cut=text.Length-max;if(tail){text.Remove(0,cut);skipped+=cut;}else{text.Length=max;break;}}if(!tail&&text.Length>=max)break;}
        return new(true,DesktopText.TailMarker(tail?skipped:0)+text);
    }
}
