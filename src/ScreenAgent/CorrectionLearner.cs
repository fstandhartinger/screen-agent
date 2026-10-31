using System.Text.Json;
using System.Windows.Automation;
namespace ScreenAgent;
internal sealed class CorrectionLearner
{
    readonly DeviceClient client;
    readonly PrivacyPolicy policy;
    readonly List<PendingCorrection> pending;
    readonly string path=Path.Combine(ScreenConfig.Folder,"learn-pending.json");
    int generation;
    public CorrectionLearner(DeviceClient client,PrivacyPolicy policy)
    {
        this.client=client;this.policy=policy;
        try{pending=JsonSerializer.Deserialize<List<PendingCorrection>>(File.ReadAllText(path))??[];}catch{pending=[];}
    }
    void Save(){File.WriteAllText(path+".tmp",JsonSerializer.Serialize(pending));File.Move(path+".tmp",path,true);}
    public void Cancel()=>generation++;
    public async Task Watch(TextInjector.Target target,string before,string inserted,CancellationToken ct)
    {
        int own=++generation;
        try
        {
            if(!TextInjector.Safe(target,policy)||TextInjector.Terminal(target.Window))return;
            string? initial=TextInjector.Read(target.Element);
            if(initial is null||!Corrections.InsertedSpan(before,initial,inserted,out int offset))return;
            string prefix=initial[..offset],suffix=initial[(offset+inserted.Length)..];
            Correction? last=null;int stable=0;
            for(int i=0;i<45&&own==generation;i++)
            {
                await Task.Delay(1000,ct);
                if(own!=generation||!TextInjector.Safe(target,policy))return;
                string? value=TextInjector.Read(target.Element);
                if(value is null||!value.StartsWith(prefix,StringComparison.Ordinal)||!value.EndsWith(suffix,StringComparison.Ordinal)||value.Length<prefix.Length+suffix.Length)return;
                string span=value.Substring(prefix.Length,value.Length-prefix.Length-suffix.Length);
                var pair=Corrections.Diff(inserted,span);
                if(pair is null){last=null;stable=0;continue;}
                stable=pair==last?stable+1:1;last=pair;
                if(stable<2)continue;
                bool promote=Corrections.Observe(pending,pair);Save();
                if(promote)
                {
                    await client.Post("api/device/dictionary/learn",new{from=pair.From,to=pair.To},ct);
                    pending.RemoveAll(p=>p.From==pair.From&&p.To==pair.To);Save();
                }
                return; // One independent observation per injection, never one per poll.
            }
        }
        catch(OperationCanceledException){}
        catch{ /* Learning is optional. Keep pending pairs for the next independent observation. */ }
    }
}
