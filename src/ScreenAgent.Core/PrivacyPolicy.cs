using System.Text.RegularExpressions;
namespace ScreenAgent.Core;
public enum PrivacyClass { Allowed, IdsOnly, Blocked }
public sealed partial class PrivacyPolicy
{
    // Additive configurable policy. Empty/unreadable browser addresses fail closed.
    public string[] ExtraProcesses { get; init; } = [];
    public string[] ExtraDomains { get; init; } = [];
    public string[] ExtraTitlePatterns { get; init; } = [];
    public string[] IdsOnlyDomains { get; init; } = ["bittensor-earnings.lovable.app"];
    public PrivacyClass Classify(string process,string title,string? url=null)
    {
        if(string.IsNullOrWhiteSpace(process)||Blocked(process,title,url))return PrivacyClass.Blocked;
        return Browser(process)&&Uri.TryCreate(url,UriKind.Absolute,out var u)&&
            IdsOnlyDomains.Append("bittensor-earnings.lovable.app").Any(d=>u.Host.Equals(d,StringComparison.OrdinalIgnoreCase)||u.Host.EndsWith("."+d,StringComparison.OrdinalIgnoreCase))
            ?PrivacyClass.IdsOnly:PrivacyClass.Allowed;
    }
    public static readonly string[] Domains = ["accounts.google.com", "passwords.google.com", "finanzblick.de", "buhl.de", "kraken.com", "paypal.com", "sparkasse.de", "comdirect.de", "consorsbank.de", "ing.de", "ing-diba.de", "n26.com", "revolut.com", "wise.com", "bunq.com", "vivid.money", "c24.de", "commerzbank.de", "deutsche-bank.de", "postbank.de", "dkb.de", "volksbank.de", "raiffeisenbank.de", "targobank.de", "santander.de", "traderepublic.com", "scalable.capital", "flatex.de", "degiro.de", "interactivebrokers.com", "coinbase.com", "binance.com", "bitpanda.com", "bsdex.de", "bisonapp.com", "bitstamp.net", "1password.com", "bitwarden.com", "lastpass.com", "dashlane.com", "elster.de"];
    [GeneratedRegex("finanzblick|keepass|1password|bitwarden|lastpass|dashlane|talisman",RegexOptions.IgnoreCase)] private static partial Regex ProcessPattern();
    [GeneratedRegex("finanzblick|kraken|talisman|banking|sparkasse|comdirect|consors|ing-diba|n26|paypal|1password|bitwarden|keepass|passwort|password|tresor|seed|wallet|ledger live|metamask|steuer|elster|revolut|trade republic|scalable|coinbase|binance|deutsche bank|commerzbank|volksbank|raiffeisen|postbank|dkb|broker",RegexOptions.IgnoreCase)] private static partial Regex TitlePattern();
    public static bool Browser(string process) => new[]{"chrome","msedge","firefox"}.Contains(process, StringComparer.OrdinalIgnoreCase);
    public bool Blocked(string process,string title,string? url=null)
    {
        if(ProcessPattern().IsMatch(process) || TitlePattern().IsMatch(title) || ExtraProcesses.Any(p=>process.StartsWith(p,StringComparison.OrdinalIgnoreCase)) || ExtraTitlePatterns.Any(p=>Regex.IsMatch(title,p,RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(50)))) return true;
        if(!Browser(process)) return false;
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri) || (uri.Scheme!="https" && uri.Scheme!="http")) return true;
        return Domains.Concat(ExtraDomains).Any(d=>uri.Host.Equals(d,StringComparison.OrdinalIgnoreCase)||uri.Host.EndsWith("."+d,StringComparison.OrdinalIgnoreCase))
            || (uri.Host.EndsWith("mintapis.com",StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(uri.AbsolutePath,"password|passwort|credential",RegexOptions.IgnoreCase));
    }
    public static bool TransientHost(string process,string title) => new[]{"LockApp","LogonUI","consent","ShellExperienceHost","StartMenuExperienceHost"}.Contains(process,StringComparer.OrdinalIgnoreCase) || title is "Task Switching" or "Task View";
}
