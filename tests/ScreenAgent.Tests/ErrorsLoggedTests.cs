using System.Text;
using System.Text.RegularExpressions;
using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
// A source scan, not a behaviour test: it keeps the rule "nothing the user sees and no caught exception goes unlogged" from eroding.
// What it cannot see: a log call that never runs, a log call in another method of the block's caller, text built at run time (status line, dictation
// messages from DictationManager are covered only because they all pass the one AppController.Notice), and whether a comment really gives the right reason.
public class ErrorsLoggedTests
{
    static readonly string Root=FindRoot();
    static string FindRoot()
    {
        for(var d=new DirectoryInfo(AppContext.BaseDirectory);d is not null;d=d.Parent)if(Directory.Exists(Path.Combine(d.FullName,"src","ScreenAgent")))return d.FullName;
        throw new DirectoryNotFoundException("repo root not found");
    }
    // Shipped code only: the selftests are CI tools that print to the console.
    static IEnumerable<string> Files()=>new[]{"src/ScreenAgent","src/ScreenAgent.Core"}
        .SelectMany(d=>Directory.EnumerateFiles(Path.Combine(Root,d),"*.cs",SearchOption.AllDirectories))
        .Where(f=>!Path.GetFileName(f).Contains("Selftest",StringComparison.OrdinalIgnoreCase)&&!f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
    static string Rel(string f)=>Path.GetRelativePath(Root,f).Replace('\\','/');

    // Scans from `open` (an opening bracket) to its match, skipping string and char literals and comments. Returns the end index (of the closing bracket) and whether a comment was inside.
    static int Match(string s,int open,out bool comment,out string code)
    {
        char o=s[open],c=o=='{'?'}':o=='('?')':']';int depth=0;comment=false;var sb=new StringBuilder();
        for(int i=open;i<s.Length;i++)
        {
            char ch=s[i];
            if(ch=='/'&&i+1<s.Length&&s[i+1]=='/'){comment=true;while(i<s.Length&&s[i]!='\n')i++;sb.Append('\n');continue;}
            if(ch=='/'&&i+1<s.Length&&s[i+1]=='*'){comment=true;int e=s.IndexOf("*/",i+2,StringComparison.Ordinal);i=e<0?s.Length:e+1;continue;}
            if(ch=='"'){bool verbatim=i>0&&s[i-1]=='@'||i>1&&s[i-1]=='$'&&s[i-2]=='@';sb.Append("\"\"");i++;
                while(i<s.Length){if(verbatim){if(s[i]=='"'){if(i+1<s.Length&&s[i+1]=='"'){i+=2;continue;}break;}}else{if(s[i]=='\\'){i+=2;continue;}if(s[i]=='"')break;}i++;}
                continue;}
            if(ch=='\''&&i+2<s.Length&&(s[i+2]=='\''||s[i+1]=='\\')){i++;while(i<s.Length&&s[i]!='\'')i+=s[i]=='\\'?2:1;sb.Append("''");continue;}
            sb.Append(ch);
            if(ch==o)depth++;
            else if(ch==c&&--depth==0){code=sb.ToString();return i;}
        }
        code=sb.ToString();return -1;
    }
    static readonly string[] LogTokens=["Diag.","Event(","Log(","log(","Debug?.Invoke","throw","Fail(","Trace(","Console.","Redact.","PairingStatus(","Notice(","FailDisk(","Warning?.Invoke","Abort(","FlushFailed("];
    static IEnumerable<(string File,int Line,string Why)> BadCatches()
    {
        foreach(var f in Files())
        {
            string s=File.ReadAllText(f);
            foreach(Match m in Regex.Matches(s,@"\bcatch\b"))
            {
                int i=m.Index+m.Length;
                while(i<s.Length&&char.IsWhiteSpace(s[i]))i++;
                bool commentBefore=false;
                if(i<s.Length&&s[i]=='('){int e=Match(s,i,out _,out _);i=e+1;}
                while(i<s.Length&&char.IsWhiteSpace(s[i]))i++;
                if(string.CompareOrdinal(s,i,"when",0,4)==0){int p=s.IndexOf('(',i);int e=Match(s,p,out _,out _);i=e+1;}
                while(i<s.Length&&char.IsWhiteSpace(s[i]))i++;
                if(i>=s.Length||s[i]!='{')continue;   // "catch" in a comment or a name
                int end=Match(s,i,out bool comment,out string code);
                if(end<0)continue;
                // a comment right after the block ("}   // why") counts: the explanation sits behind the closing brace in this code base
                int eol=s.IndexOf('\n',end);string rest=s[(end+1)..(eol<0?s.Length:eol)].TrimStart();
                bool trailing=rest.Contains("//")||rest.Contains("/*");
                if(comment||trailing||commentBefore||LogTokens.Any(code.Contains))continue;
                yield return (Rel(f),s[..m.Index].Count(c=>c=='\n')+1,"catch block without a log call or a comment");
            }
        }
    }
    [Fact]public void EveryCatchLogsOrSaysWhySilenceIsRight()
    {
        var bad=BadCatches().ToList();
        Assert.True(bad.Count==0,"Silent catch blocks:\n"+string.Join("\n",bad.Select(b=>$"{b.File}:{b.Line}")));
    }
    [Fact]public void TheScannerSeesCatchBlocks()
    {
        // guards the guard: the scan must find the known blocks and must flag a silent one
        Assert.True(Files().Sum(f=>Regex.Matches(File.ReadAllText(f),@"\bcatch\b").Count)>100);
        string silent="try{x();}catch(Exception){}";int open=silent.IndexOf('{',silent.IndexOf("catch",StringComparison.Ordinal));
        int end=Match(silent,open,out bool c,out string code);
        Assert.True(end>0&&!c&&!LogTokens.Any(code.Contains));
    }

    static string Source(string rel)=>File.ReadAllText(Path.Combine(Root,rel.Replace('/',Path.DirectorySeparatorChar)));
    // Top-level argument count of a call: the number of commas outside nested brackets and literals, plus one.
    static IEnumerable<(int Line,string Args,int Count)> Calls(string s,string name)
    {
        foreach(Match m in Regex.Matches(s,@"(?<![\w.])"+Regex.Escape(name)+@"\("))
        {
            int open=m.Index+m.Length-1;
            if(Regex.IsMatch(s[Math.Max(0,m.Index-8)..m.Index],@"(void|Task|bool|string)\s*$"))continue;   // the definition
            int end=Match(s,open,out _,out string code);if(end<0)continue;
            int depth=0,commas=0;
            foreach(char ch in code[1..^1]){if(ch is '(' or '{' or '[')depth++;else if(ch is ')' or '}' or ']')depth--;else if(ch==','&&depth==0)commas++;}
            yield return (s[..m.Index].Count(c=>c=='\n')+1,s[(open+1)..end],commas+1);
        }
    }
    [Fact]public void EveryWarningNoticeNamesItsReason()
    {
        string s=Source("src/ScreenAgent/AppController.cs");
        var calls=Calls(s,"Notice").ToList();
        Assert.True(calls.Count>=30);
        // Notice(text, level, why[, exception]): a Warn, or a level that is computed, must come with a reason (third argument).
        var bad=calls.Where(c=>c.Count<3&&(c.Args.Contains("Level.Warn")||c.Count==2&&!Regex.IsMatch(c.Args,@",\s*Level\.(Active|Idle|Muted|Paused|Busy)\s*$"))).ToList();
        Assert.True(bad.Count==0,"Notice without a reason: line "+string.Join(", ",bad.Select(b=>b.Line)));
        Assert.Contains("\"ui.notice\"",s);
        Assert.Contains("\"unspecified\"",s);   // the run-time net under the compile-time-ish rule above
    }
    [Fact]public void EveryBalloonNamesItsReasonAndIsLogged()
    {
        string s=Source("src/ScreenAgent/AppController.cs");
        var calls=Calls(s,"Balloon").ToList();
        Assert.True(calls.Count>=3);
        Assert.True(calls.All(c=>c.Count>=2),"Balloon without a reason");
        Assert.Contains("\"ui.balloon\"",s);
        Assert.Single(Regex.Matches(s,@"ShowBalloonTip\("));   // only inside Balloon
        // the dictation manager shows through the two callbacks it is given, nothing else
        Assert.DoesNotContain("ShowBalloonTip",Source("src/ScreenAgent/DictationManager.cs"));
    }
    [Fact]public void EveryWindowAndMessageBoxIsLogged()
    {
        foreach(var f in Files().Where(f=>Rel(f).StartsWith("src/ScreenAgent/")))
        {
            string s=File.ReadAllText(f);string rel=Rel(f);
            if(rel.EndsWith("Native.cs"))continue;   // the P/Invoke declaration
            if(Regex.IsMatch(s,@"\bMessageBox\("))Assert.True(s.Contains("\"ui.window\""),rel+": MessageBox without ui.window");
            if(rel.EndsWith("DictationWindow.cs"))Assert.Equal(2,Regex.Matches(s,@"""ui\.window""").Count);   // ShowFailure, ShowResult
            // text shown in the Desk window instead of the page
            if(Regex.IsMatch(s,@"Content=new System\.Windows\.Controls\.TextBlock"))Assert.True(Regex.Matches(s,@"""desk-text""").Count>=Regex.Matches(s,@"Content=new System\.Windows\.Controls\.TextBlock").Count,rel+": desk text without log");
        }
        string app=Source("src/ScreenAgent/AppController.cs");
        Assert.Matches(@"""ui\.window""[^;]*""prompt-fallback""[\s\S]{0,200}PromptFallbackWindow\.Show\(",app);
        // the toast is fed only by Notice (ui.notice) and the status line (ui.status)
        var presents=Regex.Matches(app,@"toast\.Present\(").Count;Assert.Equal(3,presents);   // Notice, the dictation live row, the status line
        Assert.Contains("\"ui.status\"",app);
    }
    [Fact]public void UnhandledExceptionsAreLoggedBeforeTheProcessDies()
    {
        string p=Source("src/ScreenAgent/Program.cs");
        Assert.Contains("AppDomain.CurrentDomain.UnhandledException",p);
        Assert.Contains("TaskScheduler.UnobservedTaskException",p);
        Assert.Matches(@"DispatcherUnhandledException\+=[^;]*Diag\.Crash",p);
        Assert.Matches(@"Diag\.Crash\(""startup""",p);
    }
    [Fact]public void ClientErrorsGoThroughTheDeviceClientNote()
    {
        string d=Source("src/ScreenAgent/DeviceClient.cs");
        Assert.Equal(2,Regex.Matches(d,@"http\.SendAsync\(").Count);   // Send and Exec: both noted
        Assert.True(Regex.Matches(d,@"Note\(request").Count>=4);
    }

    [Theory]
    [InlineData(@"Could not find file 'C:\Users\Jane Doe\AppData\x.json'",@"C:\Users\Jane")]
    [InlineData("Bearer abcdefghijklmnopqrstuvwxyz0123456789ABCDEF failed","abcdefghijklmnopqrstuvwxyz")]
    [InlineData("token=sekrit123 refused","sekrit123")]
    public void RedactRemovesProfilesAndSecrets(string text,string gone)=>Assert.DoesNotContain(gone,Redact.Message(text));
    [Fact]public void RedactCapsAndFlattens()
    {
        var m=Redact.Message("line one\r\nline two "+new string('x',500)+" end");
        Assert.True(m.Length<=120);Assert.DoesNotContain('\n',m);Assert.Equal("",Redact.Message(null));
    }
    [Fact]public void ExceptionRecordKeepsTypeMessageAndSurvivesTheSanitizer()
    {
        var d=Redact.Of(new InvalidOperationException("boom at C:\\Users\\Ann\\x"));
        var clean=TraceSanitizer.Clean(d)!;
        Assert.Equal("InvalidOperationException",clean["exType"]);
        Assert.Contains("boom",(string)clean["msg"]!);Assert.DoesNotContain("Ann",(string)clean["msg"]!);
        // the keys the log uses for visible text must not be dropped by the sanitizer
        Assert.True(TraceSanitizer.Clean(new(){["msg"]="Diktat",["why"]="x",["level"]="Warn"})!.Count==3);
    }
}
