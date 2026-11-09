using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ScreenAgent.Core;
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
internal sealed partial class DeviceClient : IDisposable
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
        http=new(handler??new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=new Uri(config.DeskUrl.TrimEnd('/')+"/"),Timeout=TimeSpan.FromSeconds(75)};
        try{device=JsonSerializer.Deserialize<DeviceToken>(ProtectedData.Unprotect(File.ReadAllBytes(this.tokenPath),null,DataProtectionScope.CurrentUser));if(device?.origin!=origin)device=null;}
        catch(Exception ex){device=null;if(ex is not (FileNotFoundException or DirectoryNotFoundException))Diag.Fail("token-load",ex);}   // no file = not paired yet
    }
    // http.fail {route, method, status, ms, sent, got, exType, msg}: every call that did not come back 2xx, threw, or took over 3 s. No URLs with ids, no bodies. The Desk
    // trace routes are left out (their failure is logged locally by Telemetry; logging it here would feed itself).
    static string Route(string path)
    {
        int q=path.IndexOf('?');if(q>=0)path=path[..q];
        return System.Text.RegularExpressions.Regex.Replace(path,"/[A-Za-z0-9_-]{16,}","/{id}");
    }
    static long Len(HttpContent? c){try{return c?.Headers.ContentLength??-1;}catch(Exception){return -1;}}   // -1: not known before it is sent
    static void Note(HttpRequestMessage request,int status,System.Diagnostics.Stopwatch clock,long got,Exception? ex=null)
    {
        string route=Route(request.RequestUri?.ToString()??"");
        bool ok=status is >=200 and <300;
        if(route.EndsWith("/trace")||route=="api/device/log"||ok&&(clock.ElapsedMilliseconds<3000||route.EndsWith("/commands")))return;   // the commands poll is long by design
        var data=new Dictionary<string,object?>{["route"]=route,["method"]=request.Method.Method,["status"]=status,["ms"]=clock.ElapsedMilliseconds,["sent"]=Len(request.Content),["got"]=got};
        if(ex is not null)Redact.Of(ex,data);
        Diag.Event(ok?"http.slow":"http.fail",data);
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
    static void Success(HttpResponseMessage response,string route)
    {
        if(!response.IsSuccessStatusCode)Diag.Event("http.fail",new(){["route"]="api/device/"+route,["method"]="POST",["status"]=(int)response.StatusCode});
        if(!response.IsSuccessStatusCode)throw new HttpRequestException("Desk-Anfrage abgelehnt",null,response.StatusCode);
    }
    public async Task<PairingStart> StartPairing(string callbackUrl,CancellationToken ct)
    {
        using var r=await http.PostAsJsonAsync("api/device/pairing/start",new{callbackUrl},ct);Success(r,"pairing/start");
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
        Success(r,"pairing/poll");using var json=await Read<JsonDocument>(r,ct);var m=json.RootElement;
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
        var clock=System.Diagnostics.Stopwatch.StartNew();HttpResponseMessage response;
        try{response=await http.SendAsync(request,ct);}
        catch(Exception ex){if(!ct.IsCancellationRequested)Note(request,0,clock,0,ex);throw;}
        Note(request,(int)response.StatusCode,clock,response.Content.Headers.ContentLength??-1);
        if(!response.IsSuccessStatusCode){var status=response.StatusCode;response.Dispose();throw new HttpRequestException("Desk-Anfrage abgelehnt",null,status);}return response;
    }
    public async Task<string[]> Exists(string[] ids,CancellationToken ct)
    {
        var known=new List<string>();
        foreach(var part in ids.Chunk(20)) // the route takes 20 at a time; OCR hands over up to 60 candidates, most of them misreadings the registry rejects
        {
            using var r=await Send(HttpMethod.Get,"api/device/exists?ids="+Uri.EscapeDataString(string.Join(',',DevicePayloads.Ids(part))),null,ct);
            known.AddRange(DeviceProtocol.Known(await r.Content.ReadAsStringAsync(ct),part));
        }
        return known.Distinct().Take(20).ToArray();
    }
    public async Task Post(string path,object body,CancellationToken ct){using var r=await Send(HttpMethod.Post,path,JsonContent.Create(body),ct);}
    // Like Post/Get but hands back status and a (small) JSON body instead of throwing: the frame and commands routes answer 409 with a code that decides what to do next.
    public async Task<RawReply> Raw(HttpMethod method,string path,object? body,CancellationToken ct)
    {
        if(!Paired)throw new InvalidOperationException("Pair this device first");
        using var request=new HttpRequestMessage(method,path){Content=body is null?null:JsonContent.Create(body)};request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",device!.token);
        return await Exec(request,ct);
    }
    // Same for a ready-made body (multipart audio): status and the small JSON answer, never an exception for a refusal.
    public async Task<RawReply> RawContent(HttpMethod method,string path,HttpContent body,CancellationToken ct)
    {
        if(!Paired)throw new InvalidOperationException("Pair this device first");
        using var request=new HttpRequestMessage(method,path){Content=body};request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",device!.token);
        return await Exec(request,ct);
    }
    async Task<RawReply> Exec(HttpRequestMessage request,CancellationToken ct)
    {
        var clock=System.Diagnostics.Stopwatch.StartNew();HttpResponseMessage response;
        try{response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);}
        catch(Exception ex){if(!ct.IsCancellationRequested)Note(request,0,clock,0,ex);throw;}
        using var held=response;
        JsonDocument? json=null;long got=0;
        try
        {
            using var stream=await response.Content.ReadAsStreamAsync(ct);using var bytes=new MemoryStream();var chunk=new byte[4096];int n;
            while((n=await stream.ReadAsync(chunk,ct))>0){if(bytes.Length+n>65536)break;bytes.Write(chunk,0,n);}
            got=bytes.Length;
            if(bytes.Length>0&&bytes.Length<=65536)json=JsonDocument.Parse(bytes.ToArray());
        }
        catch(JsonException){ /* a body that is no JSON: Json stays null, the status tells */ }
        Note(request,(int)response.StatusCode,clock,got);
        int retry=response.Headers.RetryAfter?.Delta is {} d?(int)d.TotalSeconds:0;
        return new((int)response.StatusCode,json,retry);
    }
    public async Task<string[]> Dictionary(CancellationToken ct)
    {
        using var r=await Send(HttpMethod.Get,"api/device/dictionary",null,ct);
        return DeviceProtocol.Terms(await r.Content.ReadAsStringAsync(ct));
    }
    public async Task<DictionaryEntry[]> DictionaryItems(CancellationToken ct)
    {
        using var r=await Send(HttpMethod.Get,"api/device/dictionary",null,ct);
        return DictionaryEntry.Parse(await r.Content.ReadAsStringAsync(ct));
    }
    public Task<RawReply> SaveTerm(string term,string[] from,string? previous,CancellationToken ct)=>Raw(HttpMethod.Post,"api/device/dictionary/term",previous is null?new{term,replace_from=from}:new{term,replace_from=from,previous},ct);
    public Task<RawReply> DeleteTerm(string term,CancellationToken ct)=>Raw(HttpMethod.Delete,"api/device/dictionary/term/"+Uri.EscapeDataString(term),null,ct);
    public async Task<string> Dictate(byte[] wav,DictationContext context,CancellationToken ct)
    {
        using var form=new MultipartFormDataContent();var audio=new ByteArrayContent(wav);audio.Headers.ContentType=new("audio/wav");form.Add(audio,"audio","dictation.wav");form.Add(new StringContent(JsonSerializer.Serialize(context)),"context");
        using var r=await Send(HttpMethod.Post,"api/device/dictate",form,ct);using var j=JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));return j.RootElement.GetProperty("text").GetString()??"";
    }
    // One chunk of a dictation (or the whole of it: Only = no `chunk` field). A refusal is a ChunkReply with the gateway's status and code, a lost connection throws.
    // Providers: {groq:{ok,ms,status,reason},...} becomes "groq:ok", "openai:503".
    public bool SkipSilentChunks {get;set;}=true;   // the selftest's recording holds digital silence between sentences and counts every request
    public async Task<ChunkReply> DictateChunk(ChunkRequest r,DictationContext baseContext,CancellationToken ct)
    {
        // A digitally silent chunk inside a long recording: providers invent text for it (cake recipes in the 3 Nov test), so it is not sent.
        if(SkipSilentChunks&&!r.Only&&AudioLevel.Peak(r.Chunk.Samples)<=DictationGate.SilentPeak){Diag.Event("dictation.chunk.silent",new(){["id"]=r.DictationId,["index"]=r.Chunk.Index,["seconds"]=Math.Round(r.Chunk.Seconds,1)});return new(200,"");}
        if(!Paired)return new(401,Code:"not-paired",Failure:"NotPaired");
        using var form=new MultipartFormDataContent();var audio=new ByteArrayContent(r.Chunk.WavBytes);audio.Headers.ContentType=new("audio/wav");
        form.Add(audio,"audio","dictation.wav");form.Add(new StringContent(JsonSerializer.Serialize(baseContext with{fieldBefore=r.FieldBefore})),"context");
        if(!r.Only)form.Add(new StringContent(JsonSerializer.Serialize(new{dictationId=r.DictationId,index=r.Chunk.Index,final=r.Chunk.Final})),"chunk");
        using var reply=await RawContent(HttpMethod.Post,"api/device/dictate",form,ct);
        return Parse(reply);
    }
    internal static ChunkReply Parse(RawReply reply)=>ChunkReplyParse.From(reply.Status,reply.Json?.RootElement,reply.Code);
    // Live caption of the last seconds of audio (PCM16 16 kHz mono WAV, at most 12 s). Status, text and the cost the gateway measured, if it says.
    public sealed record CaptionReply(int Status,string Text,long Ms,double Seconds,double? Cost,string? Code);
    public async Task<CaptionReply> Caption(byte[] wav,string[] visibleIds,string[] recentTerms,CancellationToken ct)
    {
        using var form=new MultipartFormDataContent();var audio=new ByteArrayContent(wav);audio.Headers.ContentType=new("audio/wav");
        form.Add(audio,"audio","caption.wav");form.Add(new StringContent(JsonSerializer.Serialize(new{visibleIds,recentTerms})),"context");
        using var reply=await RawContent(HttpMethod.Post,"api/device/caption",form,ct);
        var j=reply.Json?.RootElement;bool obj=j is {ValueKind:JsonValueKind.Object};
        double? cost=obj&&j!.Value.TryGetProperty("usage",out var u)&&u.ValueKind==JsonValueKind.Object&&u.TryGetProperty("cost",out var c)&&c.ValueKind==JsonValueKind.Number?c.GetDouble()
            :obj&&j!.Value.TryGetProperty("cost",out var c2)&&c2.ValueKind==JsonValueKind.Number?c2.GetDouble():null;
        return new(reply.Status,obj&&j!.Value.TryGetProperty("text",out var t)&&t.ValueKind==JsonValueKind.String?t.GetString()??"":"",
            obj&&j!.Value.TryGetProperty("ms",out var m)&&m.ValueKind==JsonValueKind.Number?m.GetInt64():0,obj&&j!.Value.TryGetProperty("seconds",out var s)&&s.ValueKind==JsonValueKind.Number?s.GetDouble():0,cost,reply.Code);
    }
    // v0.1.12 text clean-up (gateway /api/device/polish): mode "final" for the whole dictation, "live" for the subtitle. Status and text; a refusal never throws.
    public sealed record PolishReply(int Status,string Text,long Ms,int Changed,string? Code);
    public async Task<PolishReply> Polish(string text,string mode,string process,string fieldBefore,CancellationToken ct)
    {
        using var reply=await Raw(HttpMethod.Post,"api/device/polish",new{mode,text,context=new{process,fieldBefore}},ct);
        var j=reply.Json?.RootElement;bool obj=j is {ValueKind:JsonValueKind.Object};
        string t=obj&&j!.Value.TryGetProperty("text",out var tx)&&tx.ValueKind==JsonValueKind.String?tx.GetString()??"":"";
        long ms=obj&&j!.Value.TryGetProperty("ms",out var m)&&m.ValueKind==JsonValueKind.Number?m.GetInt64():0;
        int changed=obj&&j!.Value.TryGetProperty("changed",out var c)&&c.ValueKind==JsonValueKind.Number?c.GetInt32():0;
        return new(reply.Status,t,ms,changed,reply.Code);
    }
    public void Dispose()=>http.Dispose();
}
