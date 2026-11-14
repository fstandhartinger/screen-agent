using System.Text;
using System.Text.Json;
namespace ScreenAgent.Core;
public sealed record TraceEvent(long ts,string type,Dictionary<string,object?>? data=null);
// What may enter the trace and the local log: counts, ids, short labels. Never images, OCR text, transcripts or long strings.
public static class TraceSanitizer
{
    static readonly string[] Banned=["text","ocr","transcript","image","jpeg","png","base64","audio","title","url"];
    public static Dictionary<string,object?>? Clean(Dictionary<string,object?>? data)
    {
        if(data is null)return null;
        Dictionary<string,object?> result=[];
        foreach(var (key,value) in data)
        {
            if(result.Count>=16||Banned.Any(b=>key.Contains(b,StringComparison.OrdinalIgnoreCase))&&!(key=="ocrMs"&&value is long or int))continue;
            result[key]=value switch{null=>null,bool or int or long or double or float or uint or ulong=>value,string s=>Short(s),string[] a=>a.Take(20).Select(Short).ToArray(),_=>Short(value.ToString()??"")};
        }
        return result;
    }
    static string Short(string s)=>s.Length<=120?s:s[..120];
    // v0.1.16 caption log (config CaptionLog, Florian 14 Nov 2026): the subtitle's own text for the refinement rounds. Only caption.* events, only to the
    // Desk's trace (which keeps text under "text"/"transcript" up to 4096 characters and redacts it), never to the local rolling log. Bounded here too.
    public static Dictionary<string,object?> CleanText(Dictionary<string,object?> data)
    {
        Dictionary<string,object?> result=[];
        foreach(var (key,value) in data)
        {
            if(result.Count>=40)break;
            result[key]=value switch{null=>null,bool or int or long or double or float=>value,string s=>Cut(s,key is "text" or "transcript"?4000:990),
                IEnumerable<string> a=>a.Take(50).Select(x=>Cut(x,990)).ToArray(),_=>Cut(value.ToString()??"",990)};
        }
        return result;
    }
    static string Cut(string s,int n)=>s.Length<=n?s:s[..n];
}
// Batches client events for the Desk trace route. Best effort: a bounded buffer, oldest dropped first.
public sealed class TraceBuffer(int capacity=500)
{
    readonly List<TraceEvent> events=[];
    readonly object gate=new();
    public int Dropped {get;private set;}
    public int Count{get{lock(gate)return events.Count;}}
    public void Add(string type,Dictionary<string,object?>? data=null,long? ts=null)
    {
        lock(gate)
        {
            if(events.Count>=capacity){events.RemoveAt(0);Dropped++;}
            events.Add(new(ts??DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),type,TraceSanitizer.Clean(data)));
        }
    }
    public List<TraceEvent> Take(int max=100){lock(gate){var batch=events.Take(max).ToList();events.RemoveRange(0,batch.Count);return batch;}}
    public void Requeue(List<TraceEvent> batch){lock(gate){events.InsertRange(0,batch);while(events.Count>capacity){events.RemoveAt(0);Dropped++;}}}
}
// Small local log: two files of at most MaxBytes each (screenagent.log and screenagent.1.log), oldest content falls off.
public sealed class RollingLog(string folder,long maxBytes=2*1024*1024)
{
    readonly object gate=new();
    public string Current=>Path.Combine(folder,"screenagent.log");
    public string Previous=>Path.Combine(folder,"screenagent.1.log");
    public void Write(TraceEvent e)
    {
        string line=JsonSerializer.Serialize(new{t=DateTimeOffset.FromUnixTimeMilliseconds(e.ts).ToString("o"),e.type,e.data})+"\n";
        lock(gate)
        {
            try
            {
                Directory.CreateDirectory(folder);
                if(File.Exists(Current)&&new FileInfo(Current).Length+Encoding.UTF8.GetByteCount(line)>maxBytes){File.Move(Current,Previous,true);}
                File.AppendAllText(Current,line);
            }
            catch{ /* logging must never break the app */ }
        }
    }
}
