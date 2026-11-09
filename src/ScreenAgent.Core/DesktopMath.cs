namespace ScreenAgent.Core;
// Coordinates refer to pixel centres; crops use pixel edges. This also works left/above the primary monitor.
public readonly record struct PixelFrame(int Width,int Height,double X,double Y,double SourceWidth,double SourceHeight)
{
    public bool Contains(double x,double y)=>double.IsFinite(x)&&double.IsFinite(y)&&x>=0&&y>=0&&x<Width&&y<Height;
    public (int X,int Y) Physical(double x,double y)
    {
        if(!Contains(x,y))throw new ArgumentOutOfRangeException(nameof(x),"Punkt außerhalb des Bildes.");
        return ((int)Math.Floor(X+(x+.5)*SourceWidth/Width),(int)Math.Floor(Y+(y+.5)*SourceHeight/Height));
    }
    public (double X,double Y) Image(double x,double y)=>((x-X+.5)*Width/SourceWidth-.5,(y-Y+.5)*Height/SourceHeight-.5);
    public PixelFrame Crop(double x,double y,double w,double h,int max)
    {
        if(!Contains(x,y)||!double.IsFinite(w)||!double.IsFinite(h)||w<=0||h<=0||x+w>Width||y+h>Height)throw new ArgumentException("Region außerhalb des Bildes.");
        int left=(int)Math.Floor(X+x*SourceWidth/Width),top=(int)Math.Floor(Y+y*SourceHeight/Height);
        int right=(int)Math.Ceiling(X+(x+w)*SourceWidth/Width),bottom=(int)Math.Ceiling(Y+(y+h)*SourceHeight/Height);
        return Fit(left,top,right-left,bottom-top,max);
    }
    public static PixelFrame Fit(double x,double y,int w,int h,int max)
    {
        if(w<=0||h<=0||max<=0)throw new ArgumentOutOfRangeException(nameof(w));
        double s=Math.Min(1,(double)max/Math.Max(w,h));
        return new(Math.Max(1,(int)Math.Round(w*s)),Math.Max(1,(int)Math.Round(h*s)),x,y,w,h);
    }
    public static int Absolute(int pixel,int origin,int extent)
    {
        if(extent<1||pixel<origin||pixel>=origin+extent)throw new ArgumentOutOfRangeException(nameof(pixel));
        return Math.Clamp((int)Math.Ceiling(((pixel-origin)+.5)*65536/extent),0,65535);
    }
}
public static class DesktopText
{
    public static string TailMarker(long removed)=>removed>0?$"[… gekürzt, {removed} Zeichen davor]\n":"";
    public static string Tail(string s,int max=12000)=>s.Length<=max?s:TailMarker(s.Length-max)+s[^max..];
    public static string Output(int exit,long ms,string stdout,string stderr)=>$"exit={exit} ({ms} ms)\n--- stdout ---\n{Tail(stdout)}\n--- stderr ---\n{Tail(stderr)}";
}
public sealed record DesktopChord(ushort[] Modifiers,ushort Key,bool Extended);
public static class DesktopKeys
{
    static readonly Dictionary<string,ushort> Names=new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"]=13,["return"]=13,["eingabe"]=13,["esc"]=27,["escape"]=27,["tab"]=9,["space"]=32,["leer"]=32,["backspace"]=8,["rücktaste"]=8,
        ["delete"]=46,["entf"]=46,["insert"]=45,["einfg"]=45,["home"]=36,["pos1"]=36,["end"]=35,["ende"]=35,["pageup"]=33,["bildauf"]=33,["pagedown"]=34,["bildab"]=34,
        ["up"]=38,["hoch"]=38,["down"]=40,["runter"]=40,["left"]=37,["links"]=37,["right"]=39,["rechts"]=39,["printscreen"]=44,["druck"]=44,["apps"]=93,["menu"]=93,
        ["plus"]=187,["minus"]=189,["comma"]=188,["komma"]=188,["period"]=190,["punkt"]=190,["numpadadd"]=107,["numpadsubtract"]=109,["numpadmultiply"]=106,["numpaddivide"]=111,["numpaddecimal"]=110,["numpadenter"]=13
    };
    public static ushort Modifier(string s)=>s.ToLowerInvariant() switch {"ctrl" or "strg" or "control"=>17,"shift" or "umschalt"=>16,"alt"=>18,"win" or "windows"=>91,_=>throw new ArgumentException("Unbekannter Modifikator: "+s)};
    public static ushort[] Mods(string s)=>string.IsNullOrWhiteSpace(s)?[]:s.Split('+').Select(Modifier).Distinct().ToArray();
    public static DesktopChord[] Parse(string text)
    {
        var chords=text.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        if(chords.Length==0)throw new ArgumentException("Taste fehlt.");
        return chords.Select(c=>
        {
            var p=c.ToLowerInvariant().Split('+');string k=p[^1];ushort vk;
            if(k.Length==1&&char.IsAsciiLetterOrDigit(k[0]))vk=char.ToUpperInvariant(k[0]);
            else if(k.StartsWith('f')&&int.TryParse(k[1..],out int f)&&f>=1&&f<=24)vk=(ushort)(111+f);
            else if(k.StartsWith("numpad")&&int.TryParse(k[6..],out int n)&&n>=0&&n<=9)vk=(ushort)(96+n);
            else if(!Names.TryGetValue(k,out vk))throw new ArgumentException("Unbekannte Taste: "+k);
            return new DesktopChord(p[..^1].Select(Modifier).Distinct().ToArray(),vk,vk is >=33 and <=46 or 91 or 92 or 93 or 111||k=="numpadenter");
        }).ToArray();
    }
}
