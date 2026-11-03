using System.Text.Json;
using System.Text.Json.Serialization;
namespace ScreenAgent.Core;
// The sidecar next to the wav: only what is needed to offer a retry after a restart. No window title, no screen text; PartialText is the live caption
// text of a failed dictation and never leaves the machine.
public sealed class DictationMeta
{
    public string Id {get;set;}="";
    public string Stamp {get;set;}="";   // yyyyMMdd-HHmmss in local time, the file name prefix
    // recording | processing | done | failed | discarded
    public string State {get;set;}="recording";
    public string? Reason {get;set;}
    public string? Stage {get;set;}
    public string Process {get;set;}="";
    public DateTimeOffset Started {get;set;}
    public DateTimeOffset Ended {get;set;}
    public double Seconds {get;set;}
    public List<string> Files {get;set;}=[];
    public long Bytes {get;set;}
    public bool DiskOk {get;set;}=true;
    public string? PartialText {get;set;}
    public int Attempts {get;set;}
    [JsonIgnore] public string Folder {get;set;}="";
    [JsonIgnore] public string MetaPath=>Path.Combine(Folder,$"{Stamp}-{Id}.json");
    [JsonIgnore] public IEnumerable<string> Paths=>Files.Select(f=>Path.Combine(Folder,f));
    [JsonIgnore] public bool Pending=>State=="failed";
    public string Label=>$"Diktat vom {Started.ToLocalTime():HH:mm} ({(int)(Seconds/60)}:{(int)Seconds%60:00})";
}
public sealed record RetentionOptions(TimeSpan Success,TimeSpan Failed,long CapBytes)
{
    public static RetentionOptions Default=>new(TimeSpan.FromHours(24),TimeSpan.FromDays(14),2L<<30);
}
public static class DictationRetention
{
    public sealed record Entry(string Id,string State,DateTimeOffset At,long Bytes);
    // Which recordings to delete. A failed one younger than Failed is never touched (not even over the cap); one still recording or processing never either.
    // Over the cap the oldest successful (or discarded) go first.
    public static List<string> Plan(IEnumerable<Entry> entries,DateTimeOffset now,RetentionOptions o)
    {
        var drop=new List<string>();var keep=new List<Entry>();
        foreach(var e in entries)
        {
            var age=now-e.At;
            bool old=e.State switch{"done" or "discarded"=>age>o.Success,"failed"=>age>o.Failed,_=>false};
            if(old)drop.Add(e.Id);else keep.Add(e);
        }
        long total=keep.Sum(e=>e.Bytes);
        foreach(var e in keep.Where(e=>e.State is "done" or "discarded").OrderBy(e=>e.At))
        {
            if(total<=o.CapBytes)break;
            drop.Add(e.Id);total-=e.Bytes;
        }
        return drop;
    }
}
public sealed record RecoverReport(int Repaired,int Failed,int Orphans,int Dropped);
// %LOCALAPPDATA%\ScreenAgent\dictation: <stamp>-<id>.wav (…-p2.wav for the next file of a very long one) and the sidecar <stamp>-<id>.json.
public sealed class DictationStore(string folder,Func<DateTimeOffset>? now=null)
{
    readonly Func<DateTimeOffset> clock=now??(()=>DateTimeOffset.Now);
    static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    public string Folder=>folder;
    public DictationMeta Begin(string process)
    {
        var t=clock();
        var m=new DictationMeta{Id=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(),Stamp=t.ToString("yyyyMMdd-HHmmss"),Process=process,Started=t.ToUniversalTime(),Folder=folder};
        return m;
    }
    public string NextFile(DictationMeta m)
    {
        string name=m.Files.Count==0?$"{m.Stamp}-{m.Id}.wav":$"{m.Stamp}-{m.Id}-p{m.Files.Count+1}.wav";
        m.Files.Add(name);return Path.Combine(folder,name);
    }
    public void Save(DictationMeta m)
    {
        m.Folder=folder;Directory.CreateDirectory(folder);
        string tmp=m.MetaPath+".tmp";File.WriteAllText(tmp,JsonSerializer.Serialize(m,Json));File.Move(tmp,m.MetaPath,true);
    }
    public DictationMeta? Load(string path)
    {
        try{var m=JsonSerializer.Deserialize<DictationMeta>(File.ReadAllText(path));if(m is null||m.Id.Length==0)return null;m.Folder=folder;return m;}
        catch(Exception){return null;}   /* unreadable sidecar: not listed (the wav stays; Recover finds it) */
    }
    public List<DictationMeta> All()
    {
        if(!Directory.Exists(folder))return [];
        return Directory.EnumerateFiles(folder,"*.json").Select(Load).Where(m=>m is not null).Select(m=>m!).OrderBy(m=>m.Started).ToList();
    }
    public List<DictationMeta> Pending()=>All().Where(m=>m.Pending&&m.Paths.Any(File.Exists)).ToList();
    public long BytesOf(DictationMeta m)=>m.Paths.Where(File.Exists).Sum(p=>new FileInfo(p).Length);
    public void Delete(DictationMeta m)
    {
        foreach(var p in m.Paths){try{File.Delete(p);}catch{ /* a locked file goes with the next sweep */ }}
        try{File.Delete(m.MetaPath);}catch(Exception){ /* swept next time */ }
    }
    // At start: whatever a crash or power loss left behind. A recording that was still running becomes a failed one with a repaired wav; a wav without sidecar gets one.
    public RecoverReport Recover()
    {
        int repaired=0,failed=0,orphans=0,dropped=0;
        if(!Directory.Exists(folder))return new(0,0,0,0);
        var known=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var m in All())
        {
            foreach(var f in m.Files)known.Add(f);
            if(m.State is not ("recording" or "processing"))continue;
            long pcm=0;
            foreach(var p in m.Paths.Where(File.Exists)){var c=Wav.Repair(p);if(c.Repaired)repaired++;pcm+=c.Pcm;}
            if(pcm<DictationGate.MinSeconds*Wav.BytesPerSecond){Delete(m);dropped++;continue;}
            m.State="failed";m.Stage="recover";m.Reason="Die App wurde beendet, bevor das Diktat verarbeitet war";m.Seconds=Wav.Seconds(pcm);m.Bytes=BytesOf(m);
            if(m.Ended==default)m.Ended=File.GetLastWriteTimeUtc(m.Paths.Last());
            Save(m);failed++;
        }
        foreach(var p in Directory.EnumerateFiles(folder,"*.wav"))
        {
            string name=Path.GetFileName(p);if(known.Contains(name))continue;
            var c=Wav.Repair(p);if(c.Repaired)repaired++;
            if(!c.Readable||c.Pcm<DictationGate.MinSeconds*Wav.BytesPerSecond){dropped++;try{File.Delete(p);}catch(Exception){ /* swept next time */ }continue;}
            // <stamp>-<id>.wav (id = 8 hex); anything else keeps its whole name as id
            var stem=Path.GetFileNameWithoutExtension(name);var parts=stem.Split('-');
            var m=new DictationMeta{Id=parts.Length>=3?parts[2]:stem,Stamp=parts.Length>=2?parts[0]+"-"+parts[1]:"",State="failed",Stage="recover",Reason="Aufnahme ohne Protokoll gefunden (Absturz oder Stromausfall)",
                Files=[name],Seconds=Wav.Seconds(c.Pcm),Bytes=new FileInfo(p).Length,Started=File.GetCreationTimeUtc(p),Ended=File.GetLastWriteTimeUtc(p),Folder=folder};
            Save(m);orphans++;failed++;
        }
        return new(repaired,failed,orphans,dropped);
    }
    public List<string> Sweep(RetentionOptions o)
    {
        var all=All();var gone=new List<string>();
        var plan=DictationRetention.Plan(all.Select(m=>new DictationRetention.Entry(m.Id,m.State,m.Ended==default?m.Started:m.Ended,BytesOf(m))),clock(),o);
        foreach(var m in all.Where(m=>plan.Contains(m.Id))){Delete(m);gone.Add(m.Id);}
        return gone;
    }
}
