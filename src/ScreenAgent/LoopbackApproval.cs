using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
namespace ScreenAgent;
// The signed-in browser must reach this machine before a pairing can deliver a credential. The public browser nonce
// and private polling proof are insufficient on their own: a remote requester cannot receive this loopback approval.
internal sealed class LoopbackApproval : IDisposable
{
    readonly TcpListener listener=new(IPAddress.Loopback,0);
    readonly string path;
    readonly string authority;
    bool disposed;
    public string CallbackUrl {get;}
    public LoopbackApproval()
    {
        path="/ScreenAgent/"+Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_')+"/";
        listener.Server.ExclusiveAddressUse=true;
        listener.Start(4);
        int port=((IPEndPoint)listener.LocalEndpoint).Port;
        authority="127.0.0.1:"+port;
        CallbackUrl="http://"+authority+path;
    }
    public async Task<string> WaitForProof(CancellationToken ct)
    {
        for(int attempt=0;attempt<10;attempt++)
        {
            using var connection=await listener.AcceptTcpClientAsync(ct);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var stream=connection.GetStream();
            try
            {
                var buffer=new byte[8192];int count=0,end=-1;
                while(count<buffer.Length)
                {
                    int n=await stream.ReadAsync(buffer.AsMemory(count,buffer.Length-count),timeout.Token);
                    if(n==0)break;
                    count+=n;
                    end=Encoding.ASCII.GetString(buffer,0,count).IndexOf("\r\n\r\n",StringComparison.Ordinal);
                    if(end>=0)break;
                }
                string? proof=null;
                if(end>=0)
                {
                    var lines=Encoding.ASCII.GetString(buffer,0,end).Split("\r\n");
                    var request=lines[0].Split(' ');
                    var hosts=lines.Skip(1).Where(line=>line.StartsWith("Host:",StringComparison.OrdinalIgnoreCase)).ToArray();
                    var modes=lines.Skip(1).Where(line=>line.StartsWith("Sec-Fetch-Mode:",StringComparison.OrdinalIgnoreCase)).ToArray();
                    bool navigation=modes.Length==0||(modes.Length==1&&modes[0][(modes[0].IndexOf(':')+1)..].Trim()=="navigate");
                    if(request.Length==3&&request[0]=="GET"&&request[2] is "HTTP/1.1" or "HTTP/1.0"&&navigation&&hosts.Length==1&&hosts[0][5..].Trim().Equals(authority,StringComparison.OrdinalIgnoreCase))
                    {
                        string prefix=path+"?proof=";
                        if(request[1].StartsWith(prefix,StringComparison.Ordinal))
                        {
                            string candidate=request[1][prefix.Length..];
                            if(Regex.IsMatch(candidate,"^[A-Za-z0-9_-]{43}$"))proof=candidate;
                        }
                    }
                }
                const string clearLocation="history.replaceState(null,'','/ScreenAgent/connected')";
                string scriptHash=Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(clearLocation)));
                string body=proof is null?"Ungültige Anfrage.":"<!doctype html><html lang='de'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Screen Agent</title><script>"+clearLocation+"</script><style>body{font:20px system-ui;background:#0f1317;color:#eef3ef;padding:40px;max-width:600px}h1{color:#63d297}</style><h1>Verbindung bestätigt</h1><p>Screen Agent übernimmt jetzt die Verbindung. Du kannst dieses Fenster schließen.</p></html>";
                byte[] bytes=Encoding.UTF8.GetBytes(body);
                string headers=$"HTTP/1.1 {(proof is null?"400 Bad Request":"200 OK")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'none'; script-src 'sha256-{scriptHash}'; style-src 'unsafe-inline'; frame-ancestors 'none'\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers),timeout.Token);await stream.WriteAsync(bytes,timeout.Token);
                if(proof is not null){listener.Stop();return proof;}
            }
            catch(OperationCanceledException) when(!ct.IsCancellationRequested){ /* A stalled local connection uses only its three-second slot. */ }
            catch(IOException){ /* invalid local traffic cannot supply approval; the pairing then times out and the caller logs pairing.fail */ }
        }
        throw new InvalidDataException("Local pairing approval unavailable");
    }
    public void Dispose(){if(disposed)return;disposed=true;listener.Stop();}
}
