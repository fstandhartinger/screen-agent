using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
namespace ScreenAgent;
internal sealed record DeviceToken(string token,string deviceId,DateTimeOffset expiresAt);
internal sealed class DeviceClient : IDisposable
{
    readonly HttpClient http;
    DeviceToken? device;
    static string TokenPath=>Path.Combine(ScreenConfig.Folder,"device.bin");
    public bool Paired=>device is not null&&device.expiresAt>DateTimeOffset.UtcNow;
    public DeviceClient(AppConfig config)
    {
        http=new(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=new Uri(config.DeskUrl.TrimEnd('/')+"/"),Timeout=TimeSpan.FromSeconds(30)};
        try{device=JsonSerializer.Deserialize<DeviceToken>(ProtectedData.Unprotect(File.ReadAllBytes(TokenPath),null,DataProtectionScope.CurrentUser));}catch{device=null;}
    }
    public void Pair(DeviceToken value)
    {
        if(string.IsNullOrWhiteSpace(value.token)||string.IsNullOrWhiteSpace(value.deviceId)||value.expiresAt<=DateTimeOffset.UtcNow)throw new InvalidDataException("Invalid device token");
        var bytes=ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(value),null,DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(ScreenConfig.Folder);File.WriteAllBytes(TokenPath+".tmp",bytes);File.Move(TokenPath+".tmp",TokenPath,true);device=value;
    }
    public void Disconnect(){device=null;File.Delete(TokenPath);}
    async Task<HttpResponseMessage> Send(HttpMethod method,string path,HttpContent? body,CancellationToken ct)
    {
        if(!Paired)throw new InvalidOperationException("Pair this device first");
        using var request=new HttpRequestMessage(method,path){Content=body};request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",device!.token);
        var response=await http.SendAsync(request,ct);
        if(!response.IsSuccessStatusCode){response.Dispose();throw new HttpRequestException("Desk refused device request");}return response;
    }
    public async Task<string[]> Exists(string[] ids,CancellationToken ct)
    {
        if(ids.Length==0)return [];
        using var r=await Send(HttpMethod.Get,"api/device/exists?ids="+Uri.EscapeDataString(string.Join(',',DevicePayloads.Ids(ids))),null,ct);
        return DeviceProtocol.Known(await r.Content.ReadAsStringAsync(ct),ids);
    }
    public async Task Post(string path,object body,CancellationToken ct){using var r=await Send(HttpMethod.Post,path,JsonContent.Create(body),ct);}
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
