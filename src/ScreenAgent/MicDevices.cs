using NAudio.CoreAudioApi;
using ScreenAgent.Core;
namespace ScreenAgent;
// Windows capture devices for the microphone picker. Chosen is the name from config (null = Windows default); dictation and the subtitle
// microphone open their capture through Capture(), so a pick applies to the next recording.
internal static class MicDevices
{
    public static volatile string? Chosen;
    public static List<(string Id,string Name)> List()
    {
        var list=new List<(string Id,string Name)>();
        using var e=new MMDeviceEnumerator();
        foreach(var d in e.EnumerateAudioEndPoints(DataFlow.Capture,DeviceState.Active))list.Add((d.ID,d.FriendlyName));
        return list;
    }
    public static string? DefaultName()
    {
        try{using var e=new MMDeviceEnumerator();return e.GetDefaultAudioEndpoint(DataFlow.Capture,Role.Console).FriendlyName;}
        catch(Exception ex){Diag.Fail("mic-default-name",ex);return null;}   // no microphone at all: the menu says "Windows-Standard" without a name
    }
    // The chosen device, or the default when none is chosen or it is gone (unplugged): recording never fails because of the picker.
    public static WasapiCapture Capture()
    {
        if(Chosen is {} name)
        {
            try
            {
                using var e=new MMDeviceEnumerator();
                var devices=e.EnumerateAudioEndPoints(DataFlow.Capture,DeviceState.Active).ToList();
                if(MicChoice.Pick(devices.Select(d=>(d.ID,d.FriendlyName)).ToList(),name) is {} id)return new WasapiCapture(devices.First(d=>d.ID==id));
                Diag.Event("mic.device.missing",new(){["fallback"]="default"});
            }
            catch(Exception ex){Diag.Fail("mic-device-open",ex);}   // falls back to the default device below
        }
        return new WasapiCapture();
    }
}
