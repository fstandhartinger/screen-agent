using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
namespace ScreenAgent;
internal sealed record DeviceToken(string token,string deviceId,DateTimeOffset expiresAt,string? origin=null);
internal sealed record PairingStart(string pairId,string pollToken,string browserUrl,long expiresAt);
internal sealed record WebSession(string cookie,long expiresAt);
internal sealed record RawReply(int Status,JsonDocument? Json,int RetryAfter) : IDisposable
{
    public bool Ok=>Status is >=200 and <300;
    public string? Code=>Json is {} j&&j.RootElement.ValueKind==JsonValueKind.Object&&j.RootElement.TryGetProperty("code",out var c)&&c.ValueKind==JsonValueKind.String?c.GetString():null;
    public bool Stop=>Json is {} j&&j.RootElement.ValueKind==JsonValueKind.Object&&j.RootElement.TryGetProperty("stop",out var s)&&s.ValueKind==JsonValueKind.True;
    public void Dispose()=>Json?.Dispose();
}
internal sealed class DeviceClient : IDisposable
{
    readonly HttpClient http;
    readonly string origin;
    DeviceToken? device;
    readonly string tokenPath;
    public bool Paired=>device is not null&&device.expiresAt>DateTimeOffset.UtcNow;
    public string? DeviceId=>device?.deviceId;
    public DeviceClient(AppConfig config,HttpMessageHandler? handler=null,string? tokenPath=null)
    {
        origin=new Uri(config.DeskUrl).GetLeftPart(UriPartial.Authority);
        this.tokenPath=tokenPath??Path.Combine(ScreenConfig.Folder,"device.bin");
        http=new(handler??new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=new Uri(config.DeskUrl.TrimEnd('/')+"/"),Timeout=TimeSpan.FromSeconds(40)};
        try{device=JsonSerializer.Deserialize<DeviceToken>(ProtectedData.Unprotect(File.ReadAllBytes(this.tokenPath),null,DataProtectionScope.CurrentUser));if(device?.origin!=origin)device=null;}catch{device=null;}
    }
    async Task<T> Read<T>(HttpResponseMessage response,CancellationToken ct)
    {
        // Responses contain credentials: neither content nor request URLs ever enter an exception or a log.
        if(response.Content.Headers.ContentLength>65536)throw new InvalidDataException("Invalid Desk response");
        using var stream=await response.Content.ReadAsStreamAsync(ct);
        using var bytes=new MemoryStream();var chunk=new byte[4096];int n;
        while((n=await stream.ReadAsync(chunk,ct))>0){if(bytes.Length+n>65536)throw new InvalidDataException("Invalid Desk response");bytes.Write(chunk,0,n);}
        return JsonSerializer.Deserialize<T>(bytes.ToArray())??throw new InvalidDataException("Invalid Desk response");
    }
    static void Success(HttpResponseMessage response)
    {
        if(!response.IsSuccessStatusCode)throw new HttpRequestException("Desk-Anfrage abgelehnt",null,response.StatusCode);
    }
    public async Task<PairingStart> StartPairing(string callbackUrl,CancellationToken ct)
    {
        using var r=await http.PostAsJsonAsync("api/device/pairing/start",new{callbackUrl},ct);Success(r);
        var result=await Read<PairingStart>(r,ct);
        if(!System.Text.RegularExpressions.Regex.IsMatch(result.pairId??"","^[A-Za-z0-9_-]{43}$")||
           !System.Text.RegularExpressions.Regex.IsMatch(result.pollToken??"","^[A-Za-z0-9_-]{43}$")||
           result.expiresAt<=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()||result.expiresAt>DateTimeOffset.UtcNow.AddMinutes(11).ToUnixTimeMilliseconds())
            throw new InvalidDataException("Invalid pairing response");
        return result;
    }
    public async Task<DeviceToken?> PollPairing(string pairId,string pollToken,string approvalProof,CancellationToken ct)
    {
        using var r=await http.PostAsJsonAsync("api/device/pairing/poll",new{pairId,pollToken,approvalProof},ct);
        if(r.StatusCode==System.Net.HttpStatusCode.Accepted)return null;
        Success(r);using var json=await Read<JsonDocument>(r,ct);var m=json.RootElement;
        return new(m.GetProperty("token").GetString()!,m.GetProperty("deviceId").GetString()!,DeviceProtocol.Expiry(m.GetProperty("expiresAt")));
    }
    public async Task<WebSession> WebSession(CancellationToken ct)
    {
        using var r=await Send(HttpMethod.Post,"api/device/web-session",JsonContent.Create(new{}),ct);
        var result=await Read<WebSession>(r,ct);
        if(string.IsNullOrWhiteSpace(result.cookie)||result.cookie.Length>4096||result.cookie.Any(c=>c<'!'||c>'~'||c==';')||
           result.expiresAt<=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())throw new InvalidDataException("Invalid Desk session");
        return result;
    }
    public void Pair(DeviceToken value)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(value.token??"","^[A-Za-z0-9_-]{43}$")||!Guid.TryParse(value.deviceId,out _)||value.expiresAt<=DateTimeOffset.UtcNow)throw new InvalidDataException("Invalid device token");
        value=value with{origin=origin};
        var bytes=ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(value),null,DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(tokenPath)!);File.WriteAllBytes(tokenPath+".tmp",bytes);File.Move(tokenPath+".tmp",tokenPath,true);device=value;
    }
    public void Disconnect(){device=null;File.Delete(tokenPath);}
    async Task<HttpResponseMessage> Send(HttpMethod method,string path,HttpContent? body,CancellationToken ct)
    {
        if(!Paired)throw new InvalidOperationException("Pair this device first");
        using var request=new HttpRequestMessage(method,path){Content=body};request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",device!.token);
        var response=await http.SendAsync(request,ct);
        if(!response.IsSuccessStatusCode){var status=response.StatusCode;response.Dispose();throw new HttpRequestException("Desk-Anfrage abgelehnt",null,status);}return response;
    }
    public async Task<string[]> Exists(string[] ids,CancellationToken ct)
    {
        if(ids.Length==0)return [];
        using var r=await Send(HttpMethod.Get,"api/device/exists?ids="+Uri.EscapeDataString(string.Join(',',DevicePayloads.Ids(ids))),null,ct);
        return DeviceProtocol.Known(await r.Content.ReadAsStringAsync(ct),ids);
    }
    public async Task Post(string path,object body,CancellationToken ct){using var r=await Send(HttpMethod.Post,path,JsonContent.Create(body),ct);}
    // Like Post/Get but hands back status and a (small) JSON body instead of throwing: the frame and commands routes answer 409 with a code that decides what to do next.
    public async Task<RawReply> Raw(HttpMethod method,string path,object? body,CancellationToken ct)
    {
        if(!Paired)throw new InvalidOperationException("Pair this device first");
        using var request=new HttpRequestMessage(method,path){Content=body is null?null:JsonContent.Create(body)};request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",device!.token);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        JsonDocument? json=null;
        try
        {
            using var stream=await response.Content.ReadAsStreamAsync(ct);using var bytes=new MemoryStream();var chunk=new byte[4096];int n;
            while((n=await stream.ReadAsync(chunk,ct))>0){if(bytes.Length+n>65536)break;bytes.Write(chunk,0,n);}
            if(bytes.Length>0&&bytes.Length<=65536)json=JsonDocument.Parse(bytes.ToArray());
        }
        catch(JsonException){}
        int retry=response.Headers.RetryAfter?.Delta is {} d?(int)d.TotalSeconds:0;
        return new((int)response.StatusCode,json,retry);
    }
    public async Task<string[]> Dictionary(CancellationToken ct)
    {
        using var r=await Send(HttpMethod.Get,"api/device/dictionary",null,ct);
        return DeviceProtocol.Terms(await r.Content.ReadAsStringAsync(ct));
    }
    public async Task<string> Dictate(byte[] wav,DictationContext context,CancellationToken ct)
    {
        using var form=new MultipartFormDataContent();var audio=new ByteArrayContent(wav);audio.Headers.ContentType=new("audio/wav");form.Add(audio,"audio","dictation.wav");form.Add(new StringContent(JsonSerializer.Serialize(context)),"context");
        using var r=await Send(HttpMethod.Post,"api/device/dictate",form,ct);using var j=JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));return j.RootElement.GetProperty("text").GetString()??"";
    }
    public void Dispose()=>http.Dispose();
}
