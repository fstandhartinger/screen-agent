using System.Text.Json;
namespace ScreenAgent.Core;
// Events that still have to reach the gateway, kept in a file so a restart, a lost connection or the end of a session loses nothing:
// at most Capacity events (oldest dropped), none older than MaxAge. The file is rewritten when something changed (Save), not on every event.
public sealed class TraceQueue(string path,int capacity=1000,TimeSpan? maxAge=null,Func<long>? clock=null)
{
    readonly List<TraceEvent> events=[];
    readonly object gate=new();
    readonly TimeSpan age=maxAge??TimeSpan.FromHours(24);
    readonly Func<long> now=clock??(()=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    bool dirty;
    public int Dropped {get;private set;}
    public int Count{get{lock(gate)return events.Count;}}
    public void Load()
    {
        lock(gate)
        {
            try
            {
                if(!File.Exists(path))return;
                foreach(var line in File.ReadLines(path))
                {
                    try{var e=JsonSerializer.Deserialize<TraceEvent>(line);if(e is not null&&e.type is {Length:>0})events.Add(e with{data=Normalize(e.data)});}catch(JsonException){ /* a torn last line */ }
                }
                Trim();
            }
            catch(IOException){ }
        }
    }
    // After JSON, values are JsonElements; the sanitizer's shapes are what the upload needs.
    static Dictionary<string,object?>? Normalize(Dictionary<string,object?>? d)=>d?.ToDictionary(x=>x.Key,x=>x.Value is JsonElement j?FromJson(j):x.Value);
    static object? FromJson(JsonElement j)=>j.ValueKind switch
    {
        JsonValueKind.String=>j.GetString(),JsonValueKind.True=>true,JsonValueKind.False=>false,JsonValueKind.Number=>j.TryGetInt64(out var l)?l:j.GetDouble(),
        JsonValueKind.Array=>j.EnumerateArray().Select(x=>x.ToString()).ToArray(),_=>null
    };
    void Trim()
    {
        long cutoff=now()-(long)age.TotalMilliseconds;
        int before=events.Count;events.RemoveAll(e=>e.ts<cutoff);
        while(events.Count>capacity){events.RemoveAt(0);Dropped++;}
        if(events.Count!=before)dirty=true;
    }
    public void Add(string type,Dictionary<string,object?>? data,long ts)
    {
        lock(gate){events.Add(new(ts,type,TraceSanitizer.Clean(data)));dirty=true;if(events.Count>capacity){events.RemoveAt(0);Dropped++;}}
    }
    // The next batch, still in the queue: Remove(n) only after the gateway took it.
    public List<TraceEvent> Peek(int max=100){lock(gate){Trim();return events.Take(max).ToList();}}
    public void Remove(int n){lock(gate){events.RemoveRange(0,Math.Min(n,events.Count));dirty=true;}}
    public void Save()
    {
        lock(gate)
        {
            if(!dirty)return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllLines(path+".tmp",events.Select(e=>JsonSerializer.Serialize(e)));File.Move(path+".tmp",path,true);dirty=false;
            }
            catch{ /* the rolling log has the same events */ }
        }
    }
}
