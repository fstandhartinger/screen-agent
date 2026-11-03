using System.Text.RegularExpressions;
namespace ScreenAgent.Core;
// Secret-looking values in anything a tool returns to the provider (page text, UI values, labels) are replaced by "ausgeblendet": keys, tokens, JWTs, private key blocks, key=value pairs, long hex and long opaque strings.
public static class SecretText
{
    public const string Hidden="ausgeblendet";
    static Regex R(string p)=>new(p,RegexOptions.Compiled|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(250));
    static readonly Regex[] Plain=
    [
        R(@"-----BEGIN [A-Z0-9 ]{0,40}-----[\s\S]{0,6000}?(?:-----END [A-Z0-9 ]{0,40}-----|$)"),
        R(@"\b(?:sk|pk|rk)-[A-Za-z0-9_\-]{16,}"),
        R(@"\bgh[pousr]_[A-Za-z0-9]{20,}|\bgithub_pat_[A-Za-z0-9_]{20,}"),
        R(@"\bxox[abprs]-[A-Za-z0-9\-]{10,}"),
        R(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b"),
        R(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{4,}"),
        R(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=\-]{16,}"),
        R(@"(?i)\b[\w.\-]*(?:api[_\-]?key|secret|token|passwo?r?d|passwort|kennwort|passphrase|private[_\-]?key|coldkey|mnemonic)[\w.\-]*\s*[:=]\s*[""']?[^\s""']{6,}"),
        R(@"\b[A-Fa-f0-9]{40,}\b")
    ];
    static readonly Regex Opaque=R(@"\b[A-Za-z0-9_\-]{32,}={0,2}");
    public static string Redact(string? s)
    {
        if(string.IsNullOrEmpty(s))return s??"";
        try
        {
            foreach(var rx in Plain)s=rx.Replace(s,Hidden);
            return Opaque.Replace(s,m=>m.Value.Any(char.IsDigit)&&m.Value.Any(char.IsLetter)?Hidden:m.Value);
        }
        catch(RegexMatchTimeoutException){return Hidden;}   // a text that defeats the patterns is hidden whole
    }
    public static bool Looks(string? s)=>Redact(s)!=s;
}
