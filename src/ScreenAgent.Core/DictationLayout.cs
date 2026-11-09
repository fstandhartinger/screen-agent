using System.Text;
using System.Text.RegularExpressions;
namespace ScreenAgent.Core;
// v0.1.13 (Florian, 9 Nov 2026): the inserted text may carry light structure: a line break between thoughts, a blank line between topics. Nothing else:
// no markup, no tabs. A line break only survives where the text is pasted; where it is typed key by key (terminals, short texts, a busy clipboard) Enter would
// send a chat message or run a command, so there the text is one line as before.
public static class DictationLayout
{
    // One line: every run of line breaks and tabs, with the spaces around it, becomes one space. Other control characters go.
    public static string Flat(string? text)
    {
        var one=Regex.Replace(text??"",@"[ \t]*[\r\n\t\u2028\u2029]+[ \t]*"," ");
        return new string(one.Where(c=>!char.IsControl(c)).ToArray());
    }
    public static bool HasBreaks(string? text)=>text is not null&&text.IndexOfAny(['\n','\r'])>=0;
    // For the clipboard: lines without spaces at their ends, at most one blank line in a row, none at the start or the end, CRLF (what Windows text boxes expect),
    // no other control characters.
    public static string ForPaste(string? text)
    {
        if(!HasBreaks(text))return Flat(text);   // one line: exactly what was pasted before v0.1.13 (a leading or trailing space included)
        var lines=(text??"").Replace("\r\n","\n").Replace('\r','\n').Replace('\u2028','\n').Replace('\u2029','\n').Split('\n')
            .Select(l=>new string(l.Select(c=>c=='\t'?' ':c).Where(c=>!char.IsControl(c)).ToArray()).Trim()).ToList();
        var sb=new StringBuilder();int blank=0;
        foreach(var line in lines)
        {
            if(line.Length==0){blank++;continue;}
            if(sb.Length>0)sb.Append(blank>0?"\r\n\r\n":"\r\n");
            sb.Append(line);blank=0;
        }
        return sb.ToString();
    }
}
