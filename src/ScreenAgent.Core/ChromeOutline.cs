using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace ScreenAgent.Core;
// chrome_snapshot: the page as an OUTLINE in document order. Text lines and interactive elements with refs, so a button can be told apart by the text around it
// (three cards "Nordgatan", "Opnar AI", "Outwing", each with "Senden (empfohlen)" / "Ändern" / "Nicht jetzt": the card's text line stands right above its buttons).
// The page function (ChromePageJs.Snapshot) walks the DOM and returns raw items; the text is built here, so cutting, filtering and the hints are tested without a browser.
public static class ChromeOutline
{
    public const int MaxLines=500,MaxChars=24000,TextCut=160,NameCut=100,ValueCut=80;
    static string Clean(string? s,int max)
    {
        if(string.IsNullOrEmpty(s))return "";
        var sb=new StringBuilder(Math.Min(s.Length,max+1));bool space=false;
        foreach(char c in s)
        {
            if(char.IsWhiteSpace(c)||char.IsControl(c)){space=sb.Length>0;continue;}
            if(space){sb.Append(' ');space=false;}
            sb.Append(c);
            if(sb.Length>=max*2)break;
        }
        string t=sb.ToString();
        if(t.Length<=max)return t;
        int cut=max-1;if(char.IsHighSurrogate(t[cut-1]))cut--;
        return t[..cut]+"…";
    }
    static string Str(JsonElement o,string n)=>o.TryGetProperty(n,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
    static bool? Flag(JsonElement o,string n)
    {
        if(!o.TryGetProperty(n,out var v))return null;
        return v.ValueKind switch{JsonValueKind.True=>true,JsonValueKind.False=>false,JsonValueKind.Number=>v.GetDouble()!=0,_=>null};
    }
    // One raw item of the page function: a string is a text line; an object is an interactive element
    // {r:ref number, k:role, n:name, v:value, p:length of a hidden (password) value, c:checked, d:disabled, h:host of a link that leaves the page's host, x:expanded, s:selected}.
    public static string Line(JsonElement item)
    {
        if(item.ValueKind==JsonValueKind.String)return Clean(item.GetString(),TextCut);
        if(item.ValueKind!=JsonValueKind.Object||!item.TryGetProperty("r",out var r)||!r.TryGetInt32(out int n))return "";
        var sb=new StringBuilder();
        string role=Clean(Str(item,"k"),24);
        sb.Append("[r").Append(n).Append("] ").Append(role.Length>0?role:"clickable").Append(" \"").Append(Clean(Str(item,"n"),NameCut).Replace('"','\'')).Append('"');
        if(item.TryGetProperty("v",out var v)&&v.ValueKind==JsonValueKind.String)sb.Append(" wert=\"").Append(Clean(v.GetString(),ValueCut).Replace('"','\'')).Append('"');
        if(item.TryGetProperty("p",out var p)&&p.TryGetInt32(out int pl))sb.Append(" wert=(verdeckt, ").Append(pl).Append(" Zeichen)");
        if(Flag(item,"c") is {} c)sb.Append(c?" [an]":" [aus]");
        if(Flag(item,"x") is {} x)sb.Append(x?" [offen]":" [zu]");
        if(Flag(item,"s")==true)sb.Append(" [gewählt]");
        if(Flag(item,"d")==true)sb.Append(" [deaktiviert]");
        string host=Clean(Str(item,"h"),60);
        if(host.Length>0)sb.Append(" -> ").Append(host);
        return sb.ToString();
    }
    public static List<string> Lines(JsonElement items)
    {
        var list=new List<string>();
        if(items.ValueKind!=JsonValueKind.Array)return list;
        foreach(var it in items.EnumerateArray()){string l=Line(it);if(l.Length>0)list.Add(l);}
        return list;
    }
    public static string Header(string tab,string title,string url,int viewW,int viewH,int scrollY,int scrollMax)
        =>"tab "+tab+" · "+Clean(title,80)+" · "+Clean(url,200)+" · viewport "+viewW+"x"+viewH+" · scrollY "+scrollY+"/"+Math.Max(0,scrollMax);
    // filter: lines that contain it (case-insensitive) with 2 lines around; gaps are marked. Then the cap of 500 lines / 24,000 characters; a cut says so and names the ways on.
    public static string Build(string header,IReadOnlyList<string> lines,string? filter=null,bool all=false,bool pageCut=false,bool more=false)
    {
        var sb=new StringBuilder(header).Append('\n');
        IReadOnlyList<string> shown=lines;int matches=-1;
        if(!string.IsNullOrWhiteSpace(filter))
        {
            string f=filter.Trim();var keep=new bool[lines.Count];matches=0;
            for(int i=0;i<lines.Count;i++)
            {
                if(lines[i].IndexOf(f,StringComparison.OrdinalIgnoreCase)<0)continue;
                matches++;
                for(int k=Math.Max(0,i-2);k<=Math.Min(lines.Count-1,i+2);k++)keep[k]=true;
            }
            var pick=new List<string>();int last=-1;
            for(int i=0;i<lines.Count;i++)
            {
                if(!keep[i])continue;
                if(last>=0&&i>last+1)pick.Add("…");
                pick.Add(lines[i]);last=i;
            }
            shown=pick;
            if(matches==0){sb.Append("Kein Treffer für filter \"").Append(Clean(f,60)).Append("\" in ").Append(lines.Count).Append(" Zeilen. Ohne filter, mit all:true oder nach scroll erneut versuchen.\n");return sb.ToString();}
        }
        int used=0,n=0;
        foreach(string l in shown)
        {
            if(n>=MaxLines||used+l.Length+1>MaxChars)break;
            sb.Append(l).Append('\n');used+=l.Length+1;n++;
        }
        if(n<shown.Count||pageCut)
            sb.Append("… gekürzt: ").Append(n).Append(" von ").Append(pageCut?"über ":"").Append(shown.Count).Append(" Zeilen. Eingrenzen mit filter:\"Text\" (Teilwort, 2 Zeilen Umfeld) oder scrollen (scroll) und chrome_snapshot neu holen.\n");
        else if(lines.Count==0)sb.Append(all?"(Die Seite hat keinen sichtbaren Inhalt.)\n":"(Im sichtbaren Bereich ist nichts. all:true zeigt die ganze Seite.)\n");
        else if(!all&&more&&matches<0)sb.Append("(Nur der sichtbare Bereich mit je 50 % darüber und darunter. all:true = ganze Seite, scroll = weiter.)\n");
        return sb.ToString();
    }
}
// One key press for Input.dispatchKeyEvent. Modifiers: Alt 1, Ctrl 2, Meta/Win 4, Shift 8 (the CDP bitmask). Text "" = no character is produced (rawKeyDown).
public sealed record CdpKey(int Modifiers,string Key,string Code,int Vk,string Text);
public static class CdpKeys
{
    public const int Alt=1,Ctrl=2,Meta=4,Shift=8;
    static readonly Dictionary<string,(string Key,string Code,int Vk,string Text)> Named=new(StringComparer.Ordinal)
    {
        ["enter"]=("Enter","Enter",13,"\r"),["tab"]=("Tab","Tab",9,""),["esc"]=("Escape","Escape",27,""),["backspace"]=("Backspace","Backspace",8,""),["delete"]=("Delete","Delete",46,""),["insert"]=("Insert","Insert",45,""),
        ["space"]=(" ","Space",32," "),["up"]=("ArrowUp","ArrowUp",38,""),["down"]=("ArrowDown","ArrowDown",40,""),["left"]=("ArrowLeft","ArrowLeft",37,""),["right"]=("ArrowRight","ArrowRight",39,""),
        ["home"]=("Home","Home",36,""),["end"]=("End","End",35,""),["pageup"]=("PageUp","PageUp",33,""),["pagedown"]=("PageDown","PageDown",34,""),
        ["plus"]=("+","Equal",187,"+"),["minus"]=("-","Minus",189,"-"),
        ["-"]=("-","Minus",189,"-"),["="]=("=","Equal",187,"="),["["]=("[","BracketLeft",219,"["),["]"]=("]","BracketRight",221,"]"),["\\"]=("\\","Backslash",220,"\\"),[";"]=(";","Semicolon",186,";"),
        ["'"]=("'","Quote",222,"'"),[","]=(",","Comma",188,","),["."]=(".","Period",190,"."),["/"]=("/","Slash",191,"/"),["`"]=("`","Backquote",192,"`")
    };
    public static int Mods(string? mods)
    {
        int m=0;
        foreach(string p in KeyChord.Normalize((mods??"")+"+x").Split('+'))m|=p switch{"alt"=>Alt,"ctrl"=>Ctrl,"win"=>Meta,"shift"=>Shift,_=>0};
        return m;
    }
    // "ctrl+a", "Enter", "shift+tab", several chords separated by spaces ("ctrl+a delete"). null + `bad` = the chord that is not understood.
    public static List<CdpKey>? Parse(string? chords,out string bad)
    {
        bad="";var list=new List<CdpKey>();
        foreach(string raw in (chords??"").Split([' ','\t','\n'],StringSplitOptions.RemoveEmptyEntries))
        {
            string chord=raw.Length>1&&raw.EndsWith('+')?raw[..^1]+"plus":raw=="+"?"plus":raw;   // "ctrl++" means Ctrl and the plus key
            string n=KeyChord.Normalize(chord);var parts=n.Split('+');string key=parts[^1];
            int mods=0;
            foreach(string p in parts[..^1])mods|=p switch{"alt"=>Alt,"ctrl"=>Ctrl,"win"=>Meta,"shift"=>Shift,_=>0};
            if(key is "ctrl" or "alt" or "shift" or "win"||key.Length==0){bad=raw;return null;}   // a modifier alone presses nothing
            bool silent=(mods&(Alt|Ctrl|Meta))!=0;   // with Ctrl, Alt or Win a key is a shortcut, it types no character
            if(Named.TryGetValue(key,out var k)){list.Add(new(mods,k.Key,k.Code,k.Vk,silent?"":k.Text));continue;}
            if(key.Length>=2&&key[0]=='f'&&int.TryParse(key[1..],out int fn)&&fn is >=1 and <=24){list.Add(new(mods,"F"+fn,"F"+fn,111+fn,""));continue;}
            if(key.Length==1&&key[0] is >='a' and <='z')
            {
                string ch=(mods&Shift)!=0?key.ToUpperInvariant():key;
                list.Add(new(mods,ch,"Key"+char.ToUpperInvariant(key[0]),char.ToUpperInvariant(key[0]),silent?"":ch));continue;
            }
            if(key.Length==1&&key[0] is >='0' and <='9'){list.Add(new(mods,key,"Digit"+key,key[0],silent?"":key));continue;}
            if(key.Length==1||(key.Length==2&&char.IsSurrogatePair(key,0))){list.Add(new(mods,key,"",0,silent?"":key));continue;}   // any other single character (ä, €): typed as text
            bad=raw;return null;
        }
        if(list.Count==0){bad=chords??"";return null;}
        return list;
    }
}
public static class ChromeJs
{
    static readonly Regex Body=new(@"\b(await|return)\b",RegexOptions.CultureInvariant);
    // chrome_eval takes an expression or an async function body. A body ("return x", "const r=await fetch(..); return r.status") only parses inside a function.
    public static bool NeedsWrap(string js)=>Body.IsMatch(js);
    public static string Wrap(string js)=>"(async()=>{\n"+js+"\n})()";
    // Width and height from the JPEG's frame header (SOF0..SOF15 without DHT, JPG and DAC): the frame of a tab screenshot is measured from the picture itself, not assumed from the request.
    public static (int W,int H)? JpegSize(ReadOnlySpan<byte> b)
    {
        if(b.Length<4||b[0]!=0xff||b[1]!=0xd8)return null;
        int i=2;
        while(i+9<b.Length)
        {
            if(b[i]!=0xff){i++;continue;}
            byte m=b[i+1];
            if(m==0xff){i++;continue;}
            if(m is 0xd8 or 0x01||(m>=0xd0&&m<=0xd7)){i+=2;continue;}
            int len=(b[i+2]<<8)|b[i+3];
            if(m>=0xc0&&m<=0xcf&&m is not (0xc4 or 0xc8 or 0xcc))return ((b[i+7]<<8)|b[i+8],(b[i+5]<<8)|b[i+6]);
            if(len<2)return null;
            i+=2+len;
        }
        return null;
    }
    // Scale for Page.captureScreenshot so the long edge of the picture stays within maxEdge. `factor` = picture pixels per CSS pixel at scale 1 (the device pixel ratio, measured from a first shot).
    public static double ShotScale(double cssW,double cssH,double factor,int maxEdge=1280)=>Math.Min(1.0,maxEdge/Math.Max(1.0,Math.Max(cssW,cssH)*Math.Max(0.1,factor)));
    public static string Cut(string s,int max,out bool cut)
    {
        cut=s.Length>max;
        if(!cut)return s;
        int n=max;if(n>0&&char.IsHighSurrogate(s[n-1]))n--;
        return s[..n];
    }
}
