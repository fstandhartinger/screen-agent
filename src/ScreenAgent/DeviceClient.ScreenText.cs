using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace ScreenAgent;
internal sealed partial class DeviceClient
{
    // No generic HTTP telemetry here: the caller emits just one counts-only screen.text event.
    public async Task<int> PostScreenText(object body,CancellationToken ct)
    {
        if(!Paired)return 0;
        using var request=new HttpRequestMessage(HttpMethod.Post,"api/device/screen-text"){Content=JsonContent.Create(body)};
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",device!.token);
        using var reply=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        return (int)reply.StatusCode;
    }
}
