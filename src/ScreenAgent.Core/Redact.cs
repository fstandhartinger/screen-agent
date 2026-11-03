using System.Text.RegularExpressions;
namespace ScreenAgent.Core;
// Exception messages and other free strings for the log: no user name in a path, no token-like runs, one line, capped.
public static class Redact
{
    static readonly TimeSpan Limit=TimeSpan.FromMilliseconds(50);
    static readonly Regex Profile=new(@"(?i)[a-z]:\\users\\[^\\\s""']+",RegexOptions.Compiled,Limit);
    static readonly Regex Token=new(@"[A-Za-z0-9_\-+/=]{32,}",RegexOptions.Compiled,Limit);
    static readonly Regex Secret=new(@"(?i)\b(bearer|token|cookie|key|proof)\b\s*[=:]?\s*\S+",RegexOptions.Compiled,Limit);
    public static string Message(string? s,int max=120)
    {
        if(string.IsNullOrEmpty(s))return "";
        try{s=Secret.Replace(Token.Replace(Profile.Replace(s,"~"),"<tok>"),"$1 <x>");}
        catch(RegexMatchTimeoutException){return "<unreadable>";}   /* fail closed: nothing of the string is kept */
        s=s.Replace('\r',' ').Replace('\n',' ').Trim();
        return s.Length<=max?s:s[..max];
    }
    // Type, redacted message and HResult of an exception, as log fields.
    public static Dictionary<string,object?> Of(Exception ex,Dictionary<string,object?>? into=null)
    {
        into??=new();into["exType"]=ex.GetType().Name;into["msg"]=Message(ex.Message);if(ex.HResult!=0)into["hr"]="0x"+ex.HResult.ToString("x8");
        return into;
    }
}
