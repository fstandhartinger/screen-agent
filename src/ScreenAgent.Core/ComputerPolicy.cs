using System.Text;
using System.Text.RegularExpressions;
namespace ScreenAgent.Core;
// Computer use (v0.1.7 F): the policy lives here, compiled, never as data from the gateway. Every action is classified before it runs:
// R0 read, R1 act inside the run scope, R2 act only after his confirmation (code on the overlay or physical key), R3 refused, no confirm exists.
public enum ActionClass { R0, R1, R2, R3 }
public sealed record Verdict(ActionClass Class,string Rule)
{
    public bool Refused=>Class==ActionClass.R3;
    public bool Confirm=>Class==ActionClass.R2;
    public static Verdict Of(ActionClass c,string rule)=>new(c,rule);
}
// Hosts and eTLD+1 from a small embedded suffix list (no network, no full public suffix list). Punycode stays raw: no unicode lookalike is ever normalised into a trusted name.
public static class Domains
{
    static readonly HashSet<string> Multi=new(StringComparer.Ordinal)
    {
        "co.uk","org.uk","ac.uk","gov.uk","me.uk","ltd.uk","plc.uk","com.au","net.au","org.au","edu.au","gov.au","co.nz","org.nz","co.jp","ne.jp","or.jp","co.kr","com.br","com.mx","com.ar","com.cn","net.cn","org.cn",
        "com.tr","com.sg","com.hk","co.in","net.in","org.in","co.za","com.ua","com.pl","github.io","gitlab.io","vercel.app","netlify.app","pages.dev","workers.dev","herokuapp.com","appspot.com","web.app","firebaseapp.com",
        "onrender.com","fly.dev","azurewebsites.net","cloudfront.net","blogspot.com","wordpress.com","s3.amazonaws.com","trycloudflare.com"
    };
    static readonly HashSet<string> Tlds=new(StringComparer.Ordinal)
    {
        "com","org","net","de","io","ai","app","dev","co","uk","eu","ch","at","fr","es","it","nl","me","tv","xyz","info","edu","gov","us","ca","au","jp","cn","ru","br","in","se","no","dk","fi","pl","cz","pt","be","ie","gg","sh","so","to","cloud","tech","online","site","localhost"
    };
    public static string Etld1(string host)
    {
        host=host.Trim().TrimEnd('.').ToLowerInvariant();
        if(host.Length==0||System.Net.IPAddress.TryParse(host.Trim('[',']'),out _))return host;
        var labels=host.Split('.');
        if(labels.Length<=2)return host;
        string two=labels[^2]+"."+labels[^1];
        if(Multi.Contains(two)&&labels.Length>=3)return labels[^3]+"."+two;
        if(labels[^1]=="localhost")return two;
        return two;
    }
    // Host of an http(s) URL as punycode (IdnHost), lowercase; null for anything else or an unparsable URL.
    public static string? HostOf(string? url)
    {
        if(url is null||!Uri.TryCreate(url,UriKind.Absolute,out var u)||u.Scheme is not ("http" or "https"))return null;
        return u.IdnHost.ToLowerInvariant().TrimEnd('.');
    }
    public static string? Scheme(string? url)
    {
        if(url is null)return null;
        int i=url.IndexOf(':');
        return i<=0||i>20||!url[..i].All(c=>char.IsLetterOrDigit(c)||c is '+' or '-' or '.')?null:url[..i].ToLowerInvariant();
    }
    // Is `host` the entry itself or a subdomain of it.
    public static bool Within(string host,string entry)=>host==entry||host.EndsWith("."+entry,StringComparison.Ordinal);
    public static bool KnownTld(string tld)=>Tlds.Contains(tld);
    public static string Origin(string url)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var u))return "";
        return u.Scheme+"://"+u.IdnHost.ToLowerInvariant()+(u.IsDefaultPort?"":":"+u.Port);
    }
    public static string OriginPath(string url)=>Uri.TryCreate(url,UriKind.Absolute,out var u)?Origin(url)+u.AbsolutePath:"";
}
// The origins (and apps) his one spoken sentence names. Empty: only read, focus and scroll anywhere; everything else is R2.
public sealed class ComputerScope
{
    public HashSet<string> Hosts {get;}=new(StringComparer.Ordinal);
    public HashSet<string> Apps {get;}=new(StringComparer.Ordinal);
    public bool Empty=>Hosts.Count==0&&Apps.Count==0;
    // Hosts he approved to open by a confirmed R2 (an out-of-scope navigation). They are visible to reads, but NOT in the scope: every click there stays R2.
    public HashSet<string> Opened {get;}=new(StringComparer.Ordinal);
    // Word table: what a spoken name stands for. Only the verbatim goal is looked at, never a list of words the gateway made up.
    static readonly Dictionary<string,string> HostWords=new(StringComparer.Ordinal)
    {
        ["gmail"]="mail.google.com",["googlemail"]="mail.google.com",["telegram"]="web.telegram.org",["github"]="github.com",["youtube"]="youtube.com",["wikipedia"]="wikipedia.org",
        ["google"]="www.google.com",["twitter"]="x.com",["linkedin"]="linkedin.com",["reddit"]="reddit.com",["amazon"]="amazon.de",["claude"]="claude.ai"
    };
    static readonly Dictionary<string,string> AppWords=new(StringComparer.Ordinal)
    {
        ["notepad"]="notepad",["editor"]="notepad",["rechner"]="calculatorapp",["taschenrechner"]="calculatorapp",["calc"]="calculatorapp",["paint"]="mspaint",["wordpad"]="wordpad",["explorer"]="explorer"
    };
    static readonly Regex HereRx=new(@"(?<![\p{L}\p{N}])(?:hier|diese[rn]?\s+(?:seite|tab))(?![\p{L}\p{N}])",RegexOptions.Compiled|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    // "hier" / "diese Seite" as whole words ("Hierarchie" is not "hier").
    public static bool MentionsHere(string? goal){try{return HereRx.IsMatch((goal??"").ToLowerInvariant());}catch(RegexMatchTimeoutException){return false;}}   // a timeout means "not here": the scope then stays smaller (closed)
    static readonly Regex HostRx=new(@"(?<![\w@.-])((?:[a-z0-9-]+\.)+[a-z]{2,})(?![\w-])",RegexOptions.Compiled|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    public static ComputerScope From(string goal,string? hereHost=null,string boardHost="board.app.mintapis.com"){var s=new ComputerScope();s.Extend(goal,hereHost,boardHost);return s;}
    public void Extend(string goal,string? hereHost=null,string boardHost="board.app.mintapis.com")
    {
        string g=(goal??"").ToLowerInvariant();
        g=Regex.Replace(g,@"https?://","");
        try
        {
            foreach(Match m in HostRx.Matches(g))
            {
                string h=m.Groups[1].Value.Trim('.');
                if(Domains.KnownTld(h[(h.LastIndexOf('.')+1)..]))Hosts.Add(h);
            }
        }
        catch(RegexMatchTimeoutException){ }   // timeout means no match; callers treat it conservatively
        var words=Regex.Split(g,@"[^\p{L}\p{N}\-]+").Where(w=>w.Length>0).ToArray();
        foreach(var w in words)
        {
            if(HostWords.TryGetValue(w,out var host))Hosts.Add(host);
            if(AppWords.TryGetValue(w,out var app))Apps.Add(app);
            if(w=="board")Hosts.Add(boardHost);
        }
        if(hereHost is not null&&MentionsHere(g))Hosts.Add(hereHost);
    }
    public bool ContainsHost(string host)=>Hosts.Any(h=>Domains.Within(host,h));
    public bool ContainsApp(string process)=>Apps.Contains(process);
    public bool Reaches(string host)=>ContainsHost(host)||Opened.Any(h=>Domains.Within(host,h));
}
// Everything the policy needs to know about one proposed action. The page controls Label and Title: they can only ever raise a class.
public sealed record ActionContext(string Tool)
{
    public string Process {get;init;}="";
    public string Title {get;init;}="";
    public string? Url {get;init;}            // verified CDP target URL; null = unknown (debugging off, other browser)
    public string? Label {get;init;}
    public string? Role {get;init;}
    public bool Password {get;init;}
    public bool SearchBox {get;init;}
    public string? Key {get;init;}
    public string? Text {get;init;}
    public string? OpenUrl {get;init;}
    public bool UrlInWords {get;init;}
    public bool UrlInSnapshot {get;init;}
    public bool Nameless {get;init;}          // raw mouse on an element with no accessible name
    public bool BrowserChrome {get;init;}     // browser UI (download bubble, permission prompt, toolbar)
    public bool FileDialog {get;init;}        // common file dialog, Explorer double-click, opening a download
    public string? Href {get;init;}           // where a click would go, read live in the page: closest a[href], form action, formaction (null = nothing)
    public bool Tainted {get;init;}           // the run read a sensitive target or a server_read: every outward action asks
    public bool FocusVerified {get;init;}     // key: the focused element of the foreground window/tab was read and bound
}
public static class Labels
{
    static readonly Regex Send=new(@"(?i)\b(send\w*|senden|absenden|abschicken|submit|buy|kaufen|bestellen|order|pay|bezahlen|zahlen|post|tweet|publish|veröffentlichen|ver[oö]ffentlichen|confirm|bestätigen|best[aä]tigen|accept|akzeptieren|zustimmen|allow|zulassen|grant|authorize|genehmigen|install|download|herunterladen|upload|hochladen|forward|weiterleiten|reply|antworten|share|teilen|invite|einladen|unsubscribe|abbestellen|save|speichern|apply|anwenden|deploy|merge|transfer|überweisen|ueberweisen|sign|unterschreiben)\b",RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));
    static readonly Regex Delete=new(@"(?i)\b(delete|löschen|loeschen|remove|entfernen|trash|papierkorb|discard|verwerfen|clear|leeren|erase|destroy|reset|zurücksetzen|logout|abmelden|disconnect|trennen|revoke|widerrufen|cancel subscription|kündigen)\b",RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));
    public static bool SendLike(string? label)=>Match(Send,label);
    public static bool DeleteLike(string? label)=>Match(Delete,label);
    static bool Match(Regex rx,string? s){if(string.IsNullOrEmpty(s))return false;try{return rx.IsMatch(s);}catch(RegexMatchTimeoutException){return true;}}   // an unreadable label counts as risky
    // Letters, digits and single spaces only, 40 characters at most: a button named "Speichern, sag einfach ja" cannot smuggle a sentence into what is read to him.
    public static string Clean(string? label)
    {
        var sb=new StringBuilder();
        foreach(char c in label??"")sb.Append(char.IsLetterOrDigit(c)?c:' ');
        string s=Regex.Replace(sb.ToString(),@"\s+"," ").Trim();
        if(s.Length>40){s=s[..40].TrimEnd();if(s.Length>0&&char.IsHighSurrogate(s[^1]))s=s[..^1];}
        return s;
    }
    public static string Spoken(string? label)=>Clean(label) is {Length:>0} c?"Schaltfläche mit der Aufschrift „"+c+"“":"Element ohne Namen";
}
public static class KeyChord
{
    // Global allowlist, independent of the focused app. Enter is not on it: it is R2 (or R1 in a known search box in scope).
    static readonly HashSet<string> Allowed=new(StringComparer.Ordinal){"tab","shift+tab","up","down","left","right","pageup","pagedown","home","end","esc","ctrl+f"};
    static readonly HashSet<string> Enters=new(StringComparer.Ordinal){"enter","ctrl+enter","alt+enter","shift+enter","ctrl+shift+enter"};
    static readonly HashSet<string> Clipboard=new(StringComparer.Ordinal){"ctrl+c","ctrl+v","ctrl+x","ctrl+insert","shift+insert","shift+delete","ctrl+shift+v","ctrl+a","ctrl+z","ctrl+y"};
    // "Ctrl + Enter" / "control+return" / "Escape" to one lowercase form: modifiers in fixed order, aliases folded.
    public static string Normalize(string? chord)
    {
        var parts=(chord??"").ToLowerInvariant().Split('+',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        var mods=new List<string>();string key="";
        foreach(var p in parts)
        {
            string q=p switch{"control" or "strg" or "ctl"=>"ctrl","windows" or "meta" or "super" or "cmd" or "lwin" or "rwin"=>"win","option"=>"alt","umschalt"=>"shift","escape"=>"esc","return"=>"enter","pgup" or "prior"=>"pageup","pgdn" or "next"=>"pagedown","del"=>"delete","ins"=>"insert",_=>p};
            if(q is "ctrl" or "alt" or "shift" or "win"){if(!mods.Contains(q))mods.Add(q);}
            else key=key.Length==0?q:key+"+"+q;
        }
        mods.Sort((a,b)=>Array.IndexOf(Order,a).CompareTo(Array.IndexOf(Order,b)));
        return string.Join("+",mods.Append(key).Where(s=>s.Length>0));
    }
    static readonly string[] Order=["ctrl","alt","shift","win"];
    public static bool IsWin(string normalized)=>normalized.Split('+').Contains("win");
    public static bool IsClipboard(string normalized)=>Clipboard.Contains(normalized);
    public static bool IsAllowed(string normalized)=>Allowed.Contains(normalized);
    public static bool IsEnter(string normalized)=>Enters.Contains(normalized);
}
public static class TextCheck
{
    public const int MaxChars=2000;
    // null = fine; otherwise the rule that refuses it. No control character at all (a newline would send without any key call), at most 2000 characters.
    public static string? Refusal(string? text)
    {
        if(string.IsNullOrEmpty(text))return "text-empty";
        if(text.Length>MaxChars)return "text-long";
        foreach(char c in text)if(char.IsControl(c)||c is '\u2028' or '\u2029' or '\u0085')return "text-control";
        return null;
    }
}
public sealed class ComputerPolicy
{
    public static readonly string[] Shells=["cmd","powershell","powershell_ise","pwsh","wt","windowsterminal","conhost","openconsole","wsl","wslhost","mintty","bash","sh","putty","kitty","alacritty","wezterm","warp","hyper","tabby","terminus","fluentterminal","cmder","conemu","conemu64","git-bash","zsh","fish","nu","mobaxterm","securecrt","termius"];
    public static readonly string[] SystemTools=["regedit","regedt32","mmc","taskmgr","msconfig"];
    public static readonly string[] PasswordApps=["1password","keepass","keepassxc","bitwarden","lastpass","dashlane","keeper","enpass","nordpass","roboform"];
    public static readonly string[] SecurityApps=["msmpeng","securityhealthsystray","securityhealthui","windowsdefender","kaspersky","avp","avast","avastui","norton","nortonsecurity","mcafee","bdagent","bitdefender","eset","egui","malwarebytes","mbam"];
    static readonly string[] SecureDesktop=["consent","logonui","lockapp","winlogon"];
    static readonly string[] StartSearch=["searchhost","searchapp","searchui","startmenuexperiencehost","shellexperiencehost"];
    public static readonly string[] ComposerApps=["code","code - insiders","code-insiders","cursor","windsurf","claude","zed","kiro","trae","codex"];
    public static readonly string[] SensitiveApps=["outlook","olk","thunderbird","ms-teams","teams","slack","discord","telegram","whatsapp","signal","mailspring","winmail"];
    public static readonly string[] Browsers=["chrome","msedge","firefox","brave","opera","vivaldi","arc"];
    public static readonly string[] PasswordHosts=["bitwarden.com","bitwarden.eu","1password.com","1password.eu","lastpass.com","dashlane.com","keepersecurity.com","nordpass.com","enpass.io","roboform.com","passwords.google.com","passwords.google","keepass.info"];
    public static readonly string[] Money=
    [
        "paypal.com","paypal.me","stripe.com","wise.com","revolut.com","n26.com","finanzblick.de","sparkasse.de","commerzbank.de","deutsche-bank.de","ing.de","dkb.de","comdirect.de","consorsbank.de","postbank.de","hypovereinsbank.de",
        "volksbank.de","raiffeisen.de","targobank.de","santander.de","coinbase.com","binance.com","kraken.com","bitpanda.com","klarna.com","paysafe.com","skrill.com","wallet.google.com","pay.google.com","pay.amazon.com","banking.com"
    ];
    // Pattern rules on the registrable label (the first label of the eTLD+1), any TLD: sparkasse-koelnbonn.de, vr-bank-x.de, volksbank-y.de, dkb.de, ing.com ... and the exchanges and wallets.
    public static readonly string[] MoneyLabels=["paypal","n26","dkb","ing","comdirect","consorsbank","finanzblick","postbank","commerzbank","hypovereinsbank","targobank","santander","revolut","wise","klarna",
        "coinbase","binance","kraken","bitpanda","bitstamp","bitfinex","bybit","kucoin","okx","bitvavo","ledger","trezor","metamask"];
    public static readonly string[] MoneyPrefixes=["sparkasse-","volksbank-","vr"];
    public static bool MoneyHost(string host)
    {
        if(InList(host,Money))return true;
        string label=Domains.Etld1(host).Split('.')[0];
        return label=="sparkasse"||Array.IndexOf(MoneyLabels,label)>=0||MoneyPrefixes.Any(p=>label.StartsWith(p,StringComparison.Ordinal));
    }
    public static readonly string[] Mail=
    [
        "mail.google.com","outlook.live.com","outlook.office.com","outlook.office365.com","mail.yahoo.com","gmx.net","gmx.de","web.de","mail.proton.me","proton.me","protonmail.com","fastmail.com","icloud.com","mail.ru","zoho.com","hey.com"
    ];
    public static readonly string[] Social=
    [
        "x.com","twitter.com","facebook.com","instagram.com","linkedin.com","web.telegram.org","web.whatsapp.com","discord.com","slack.com","reddit.com","tiktok.com","mastodon.social","bsky.app","teams.microsoft.com","messenger.com","threads.net"
    ];
    public static readonly string[] Infra=
    [
        "console.cloud.google.com","console.aws.amazon.com","aws.amazon.com","portal.azure.com","dash.cloudflare.com","cloudflare.com","vercel.com","netlify.com","render.com","railway.app","fly.io","digitalocean.com","cloud.hetzner.com","console.hetzner.cloud",
        "robot.hetzner.com","hetzner.com","hetzner.cloud","coolify.io","dashboard.stripe.com","runpod.io","lambdalabs.com","vast.ai","huggingface.co","portainer.io","grafana.net","sentry.io",
        "github.com","gitlab.com","mintapis.com","neon.tech","neon.com","chutes.ai","tailscale.com","openrouter.ai","supabase.com","console.anthropic.com","platform.openai.com","console.x.ai","npmjs.com","pypi.org","hub.docker.com",
        "accounts.google.com","myaccount.google.com"
    ];
    public static readonly string[] Composers=
    [
        "claude.ai","chatgpt.com","chat.openai.com","gemini.google.com","aistudio.google.com","perplexity.ai","grok.com","copilot.microsoft.com","chat.mistral.ai","chat.deepseek.com","openrouter.ai","poe.com","claude.com","lovable.dev","bolt.new","v0.dev","replit.com"
    ];
    // Files and paths whose NAME says they hold secrets; applies to every window, a browser tab included.
    static readonly Regex ReadDenyRx=new(@"(?i)(dropbox[\\/]dev\b|[\\/]\.ssh\b|(^|[\\/\s])\.env\b|\.env\.\w+|\b\w+\.env\b|id_rsa|id_ed25519|\.pem\b|\.kdbx\b|\.ppk\b|passwords?\.(txt|csv|xlsx?)|kennw(ö|oe)rter)",RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));
    // Titles of NON-browser windows (editors, Notepad, Explorer): key, token, secret, password, wallet words and the vault's ALLCAPS name.txt files. A web page titled "tokens explained" stays readable.
    static readonly Regex TitleSecretRx=new(@"(?i)(api[_\- ]?key|_key\b|\bkey\b|\btoken|secret|passw|credential|private[_\- ]?key|seed ?phrase|mnemonic|coldkey|hotkey|wallet|\.pem\b|id_rsa|\.env\b)|(?-i:^[A-Z][A-Z0-9_]{3,}\.txt\b)",RegexOptions.Compiled|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    static readonly Regex OwnFilesRx=new(@"(?i)(screenagent[\\/](config\.json|device\.bin|logs)|%appdata%[\\/]screenagent)",RegexOptions.Compiled,TimeSpan.FromMilliseconds(100));
    readonly PrivacyPolicy privacy;
    readonly string deskHost;
    readonly string[] extraSensitive,extraComposer;
    public ComputerPolicy(PrivacyPolicy? privacy=null,string deskHost="desk.app.mintapis.com",IEnumerable<string>? extraSensitive=null,IEnumerable<string>? extraComposer=null)
    {
        this.privacy=privacy??new PrivacyPolicy();this.deskHost=deskHost.ToLowerInvariant();
        this.extraSensitive=(extraSensitive??[]).Select(s=>s.ToLowerInvariant()).ToArray();this.extraComposer=(extraComposer??[]).Select(s=>s.ToLowerInvariant()).ToArray();
    }
    public static string ProcessName(string? p){p=(p??"").Trim().ToLowerInvariant();return p.EndsWith(".exe")?p[..^4]:p;}
    public static bool ComposerOrSensitiveProcess(string process){string p=ProcessName(process);return ComposerApps.Contains(p)||SensitiveApps.Contains(p);}
    public static bool IsBrowser(string process)=>Browsers.Contains(ProcessName(process));
    public static readonly string[] ReadTools=["ui_windows","ui_tree","screenshot","chrome_tabs","chrome_snapshot","chrome_read","server_read"];
    public static readonly string[] AllTools=["ui_windows","ui_tree","ui_click","ui_type","key","scroll","window_focus","screenshot","chrome_tabs","chrome_open","chrome_snapshot","chrome_click","chrome_fill","chrome_read","server_read"];
    static bool InList(string host,string[] list)=>list.Any(e=>{int slash=e.IndexOf('/');return slash<0?Domains.Within(host,e):Domains.Within(host,e[..slash]);});
    bool Sensitive(string host)=>MoneyHost(host)||InList(host,Mail)||InList(host,Social)||InList(host,Infra)||extraSensitive.Any(e=>Domains.Within(host,e));
    // Reading this puts the run under taint: from then on every outward action asks (the page content could be sent on).
    public bool SensitiveRead(string process,string? title,string? url)
    {
        string p=ProcessName(process);string? host=Domains.HostOf(url);
        if(host is not null&&(Sensitive(host)||Composer(host)||PasswordHost(host)))return true;
        return SensitiveApps.Contains(p)||ComposerApps.Contains(p)||PasswordApps.Contains(p);
    }
    bool Composer(string host)=>InList(host,Composers)||extraComposer.Any(e=>Domains.Within(host,e));
    bool PasswordHost(string host)=>InList(host,PasswordHosts);
    // Mail and chat sites: their bodies are rich text and their recipients are chips; the question read to him may be incomplete there and says so.
    public bool Messaging(string host)=>InList(host,Mail)||InList(host,Social);
    public bool IsDesk(string host)=>Domains.Within(host,deskHost);
    // Built-in read deny, not configurable, plus his own block lists: password managers, money domains, secret files by title or path. The caller answers "ausgeblendet".
    public bool ReadDenied(string process,string? title,string? url)
    {
        string p=ProcessName(process);
        if(PasswordApps.Contains(p))return true;
        string? host=Domains.HostOf(url);
        if(host is not null&&(MoneyHost(host)||PasswordHost(host)))return true;
        if(Domains.Scheme(url) is "chrome" or "about" or "chrome-extension" or "devtools" or "file" or "view-source")return true;
        if(title is not null)try{if(ReadDenyRx.IsMatch(title)||OwnFilesRx.IsMatch(title)||(!IsBrowser(p)&&TitleSecretRx.IsMatch(title))||title.Contains("Passwort-Manager",StringComparison.OrdinalIgnoreCase)||title.Contains("Password Manager",StringComparison.OrdinalIgnoreCase))return true;}catch(RegexMatchTimeoutException){return true;}   // a timeout counts as denied
        return privacy.Blocked(p,title??"",url);
    }
    // The class of one proposed action in the run's scope. Order matters: refusals first, then the page-independent raisers, then the scope.
    public Verdict Classify(ActionContext c,ComputerScope scope)
    {
        var v=ClassifyCore(c,scope);
        // What he confirms is read to him and shown on the overlay: 200 characters. Longer typing under an ask is refused, not cut (200 harmless characters, then 1800 hostile ones).
        if(v.Class==ActionClass.R2&&c.Tool is "ui_type" or "chrome_fill"&&(c.Text?.Length??0)>MaxConfirmedText)return Verdict.Of(ActionClass.R3,"r2-text-long");
        // A key under an ask needs a verified focused element it is bound to.
        if(v.Class==ActionClass.R2&&c.Tool=="key"&&!c.FocusVerified)return Verdict.Of(ActionClass.R3,"key-no-focus");
        return v;
    }
    public const int MaxConfirmedText=200;
    Verdict ClassifyCore(ActionContext c,ComputerScope scope)
    {
        string tool=c.Tool;string p=ProcessName(c.Process);string? host=Domains.HostOf(c.Url);string? scheme=Domains.Scheme(c.Url);
        if(Array.IndexOf(AllTools,tool)<0)return Verdict.Of(ActionClass.R3,"unknown-tool");
        // ---- R3: no confirm exists
        if(p is "screenagent")return Verdict.Of(ActionClass.R3,"own-process");
        if(Shells.Contains(p))return Verdict.Of(ActionClass.R3,"shell");
        if(SystemTools.Contains(p))return Verdict.Of(ActionClass.R3,"system-tool");
        if(PasswordApps.Contains(p))return Verdict.Of(ActionClass.R3,"password-manager");
        if(SecurityApps.Contains(p))return Verdict.Of(ActionClass.R3,"security-software");
        if(SecureDesktop.Contains(p))return Verdict.Of(ActionClass.R3,"secure-desktop");
        if(StartSearch.Contains(p))return Verdict.Of(ActionClass.R3,"start-search");
        if(p=="explorer"&&Regex.IsMatch(c.Title,@"(?i)^(ausführen|run)$"))return Verdict.Of(ActionClass.R3,"run-dialog");
        if(p=="explorer"&&Regex.IsMatch(c.Label??"",@"(?i)(adressleiste|address bar|^address)"))return Verdict.Of(ActionClass.R3,"address-bar");
        if(c.Title.Contains("Screen Agent",StringComparison.OrdinalIgnoreCase))return Verdict.Of(ActionClass.R3,"own-window");
        try{if(OwnFilesRx.IsMatch(c.Title))return Verdict.Of(ActionClass.R3,"own-files");}catch(RegexMatchTimeoutException){return Verdict.Of(ActionClass.R3,"own-files");}   // a timeout counts as own files (R3)
        if(scheme is not null&&scheme is not ("http" or "https"))return Verdict.Of(ActionClass.R3,"scheme");
        if(host is not null)
        {
            if(IsDesk(host))return Verdict.Of(ActionClass.R3,"desk-origin");
            if(PasswordHost(host))return Verdict.Of(ActionClass.R3,"password-manager");
            if(Sensitive(host)&&Regex.IsMatch((c.Role??"")+" "+(c.Label??""),@"(?i)\b(terminal|xterm|console shell|shell)\b")&&InList(host,Infra))return Verdict.Of(ActionClass.R3,"web-terminal");
        }
        // The Desk in a browser we cannot see into (no CDP url): its window title says so. Only raises (a page may name itself "Agent Desk" to be refused).
        if(IsBrowser(p)&&host is null&&c.Title.Contains("Agent Desk",StringComparison.OrdinalIgnoreCase))return Verdict.Of(ActionClass.R3,"desk-origin");
        // ---- reads and plain navigation
        if(Array.IndexOf(ReadTools,tool)>=0)return Verdict.Of(ActionClass.R0,"read");
        if(tool is "window_focus")return Verdict.Of(ActionClass.R1,"focus");
        if(tool is "scroll")return Verdict.Of(ActionClass.R1,"scroll");
        if(tool is "chrome_open")return Open(c,scope);
        // ---- key
        if(tool is "key")
        {
            string k=KeyChord.Normalize(c.Key);
            if(k.Length==0)return Verdict.Of(ActionClass.R3,"key-empty");
            if(KeyChord.IsWin(k))return Verdict.Of(ActionClass.R3,"win-chord");
            if(KeyChord.IsClipboard(k))return Verdict.Of(ActionClass.R3,"clipboard-chord");
            bool enter=KeyChord.IsEnter(k);
            if(!enter&&!KeyChord.IsAllowed(k))return Verdict.Of(ActionClass.R3,"key-not-allowed");
            if(IsComposerTarget(p,host))return Verdict.Of(ActionClass.R2,"composer-key");
            var ctx=Target(c,p,host,scope);
            if(ctx is not null)return ctx;
            if(enter)return k=="enter"&&c.SearchBox?Verdict.Of(ActionClass.R1,"enter-search"):Verdict.Of(ActionClass.R2,"enter");
            return Verdict.Of(ActionClass.R1,"key");
        }
        // ---- click / type / fill
        if(tool is "ui_type" or "chrome_fill")
        {
            if(TextCheck.Refusal(c.Text) is {} bad)return Verdict.Of(ActionClass.R3,bad);
            if(c.Password)return Verdict.Of(ActionClass.R3,"password-field");
        }
        if(IsComposerTarget(p,host))return Verdict.Of(ActionClass.R2,"composer");
        // Where a click goes (link, form action, formaction), read live in the page: out of the scope it asks, with the domain spoken. Data leaves by a link or a form post as well as by chrome_open.
        if(tool=="chrome_click"&&c.Href is {Length:>0} href&&Link(href,scope) is {} lv)return lv;
        if(c.Tainted&&tool is "ui_click" or "ui_type" or "chrome_click" or "chrome_fill")return Verdict.Of(ActionClass.R2,"tainted");
        if(Target(c,p,host,scope) is {} tv)return tv;
        if(c.FileDialog)return Verdict.Of(ActionClass.R2,"file");
        if(c.BrowserChrome)return Verdict.Of(ActionClass.R2,"browser-chrome");
        if(c.Nameless&&tool is "ui_click")return Verdict.Of(ActionClass.R2,"nameless");
        if(Labels.DeleteLike(c.Label))return Verdict.Of(ActionClass.R2,"label-delete");
        if(Labels.SendLike(c.Label))return Verdict.Of(ActionClass.R2,"label-send");
        return Verdict.Of(ActionClass.R1,"in-scope");
    }
    bool IsComposerTarget(string p,string? host)=>ComposerApps.Contains(p)||(host is not null&&Composer(host));
    // Where the action lands: the raisers that depend on the target (domain list, app list, CDP, scope). null = nothing raises it.
    Verdict? Target(ActionContext c,string p,string? host,ComputerScope scope)
    {
        if(host is not null&&Sensitive(host))return Verdict.Of(ActionClass.R2,"sensitive-domain");
        if(SensitiveApps.Contains(p))return Verdict.Of(ActionClass.R2,"sensitive-app");
        if(IsBrowser(p)&&host is null)return Verdict.Of(ActionClass.R2,"no-cdp");   // debugging off must not silently downgrade the checks
        if(scope.Empty)return Verdict.Of(ActionClass.R2,"no-scope");
        if(host is not null)return scope.ContainsHost(host)?null:Verdict.Of(ActionClass.R2,"out-of-scope");
        return scope.ContainsApp(p)?null:Verdict.Of(ActionClass.R2,"out-of-scope");
    }
    // null = the target is in the scope (or nothing the policy can say); otherwise the verdict for a click that leaves it.
    Verdict? Link(string href,ComputerScope scope)
    {
        string? scheme=Domains.Scheme(href);
        if(scheme is null||scheme=="javascript")return null;   // a relative link or a script handler: the page the click happens on is already classified
        if(scheme is "mailto" or "tel" or "sms" or "ms-settings" or "ms-windows-store")return Verdict.Of(ActionClass.R2,"link-scheme");
        if(scheme is not ("http" or "https"))return Verdict.Of(ActionClass.R3,"scheme");
        string? h=Domains.HostOf(href);
        if(h is null)return Verdict.Of(ActionClass.R3,"url-invalid");
        if(IsDesk(h))return Verdict.Of(ActionClass.R3,"desk-origin");
        if(PasswordHost(h))return Verdict.Of(ActionClass.R3,"password-manager");
        return scope.ContainsHost(h)?null:Verdict.Of(ActionClass.R2,"link-out-of-scope");
    }
    Verdict Open(ActionContext c,ComputerScope scope)
    {
        string raw=c.OpenUrl??"";
        string? scheme=Domains.Scheme(raw);
        if(scheme is not ("http" or "https"))return Verdict.Of(ActionClass.R3,"scheme");
        if(!Uri.TryCreate(raw,UriKind.Absolute,out var u)||u.UserInfo.Length>0||raw.Any(char.IsControl))return Verdict.Of(ActionClass.R3,"url-invalid");
        string host=u.IdnHost.ToLowerInvariant().TrimEnd('.');
        if(IsDesk(host))return Verdict.Of(ActionClass.R3,"desk-origin");
        if(PasswordHost(host))return Verdict.Of(ActionClass.R3,"password-manager");
        // Data leaves in a query or fragment: only a URL he spoke or a link of the current snapshot may carry one.
        if((u.Query.Length>0||u.Fragment.Length>0)&&!c.UrlInWords&&!c.UrlInSnapshot)return Verdict.Of(ActionClass.R3,"url-query");
        // A path carries data just as well (evil.example/<base64 of the mail>): anything beyond the root must be in his words or a link of the snapshot.
        if(u.AbsolutePath.Length>1&&!c.UrlInWords&&!c.UrlInSnapshot)return Verdict.Of(ActionClass.R3,"url-path");
        if(scope.Empty)return Verdict.Of(ActionClass.R2,"no-scope");
        if(!scope.ContainsHost(host))return Verdict.Of(ActionClass.R2,"open-out-of-scope");
        return c.Tainted?Verdict.Of(ActionClass.R2,"tainted"):Verdict.Of(ActionClass.R1,"open-in-scope");
    }
}
