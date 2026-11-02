using System.Net;
using System.Net.Sockets;
using System.Text;
using ScreenAgent;
using Xunit;
namespace ScreenAgent.Tests;
public class PairingApprovalTests
{
    const string Proof="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    static async Task<string> Request(Uri callback,string target,string? host=null,CancellationToken ct=default)
    {
        using var client=new TcpClient();await client.ConnectAsync(IPAddress.Loopback,callback.Port,ct);
        using var stream=client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {target} HTTP/1.1\r\nHost: {host??callback.Authority}\r\nConnection: close\r\n\r\n"),ct);
        using var text=new StreamReader(stream);return await text.ReadToEndAsync(ct);
    }
    [Fact]public async Task OnlyTheExactLocalAddressDeliversProofAndThePageDoesNotReflectIt()
    {
        using var callback=new LoopbackApproval();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var uri=new Uri(callback.CallbackUrl);var proof=callback.WaitForProof(timeout.Token);
        foreach(var (path,host) in new[]{("/wrong/?proof="+Proof,uri.Authority),(uri.AbsolutePath+"?proof="+Proof,"attacker.invalid"),(uri.AbsolutePath+"?proof="+Proof+"&other=1",uri.Authority),(uri.AbsolutePath+"?proof=short",uri.Authority)})
        {
            var invalid=await Request(uri,path,host,timeout.Token);
            Assert.StartsWith("HTTP/1.1 400",invalid);Assert.False(proof.IsCompleted);
        }
        var result=await Request(uri,uri.AbsolutePath+"?proof="+Proof,ct:timeout.Token);
        Assert.Equal(Proof,await proof);Assert.StartsWith("HTTP/1.1 200",result);Assert.DoesNotContain(Proof,result);
        Assert.Contains("Cache-Control: no-store",result);Assert.Contains("Referrer-Policy: no-referrer",result);
        Assert.Contains("history.replaceState(null,'','/ScreenAgent/connected')",result);
        Assert.Contains("script-src 'sha256-",result);Assert.Contains("frame-ancestors 'none'",result);
    }
    [Fact]public async Task CancellationStopsWaitingWithoutApproval()
    {
        using var callback=new LoopbackApproval();using var cancel=new CancellationTokenSource();
        var waiting=callback.WaitForProof(cancel.Token);cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>waiting);
    }
    [Fact]public async Task InvalidLocalTrafficHasAFixedAttemptBudget()
    {
        using var callback=new LoopbackApproval();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var uri=new Uri(callback.CallbackUrl);var waiting=callback.WaitForProof(timeout.Token);
        for(int i=0;i<10;i++)Assert.StartsWith("HTTP/1.1 400",await Request(uri,"/wrong",ct:timeout.Token));
        await Assert.ThrowsAsync<InvalidDataException>(()=>waiting);
    }
    [Fact]public void CallbackAddressesAreRandomAndBoundOnlyToLoopback()
    {
        using var a=new LoopbackApproval();using var b=new LoopbackApproval();var uri=new Uri(a.CallbackUrl);
        Assert.Equal("127.0.0.1",uri.Host);Assert.InRange(uri.Port,1024,65535);Assert.Equal("http",uri.Scheme);
        Assert.NotEqual(a.CallbackUrl,b.CallbackUrl);Assert.Matches("^/ScreenAgent/[A-Za-z0-9_-]{43}/$",uri.AbsolutePath);
    }
}
