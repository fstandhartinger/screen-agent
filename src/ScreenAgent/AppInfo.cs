using System.Reflection;
namespace ScreenAgent;
internal static class AppInfo
{
    static readonly string[] Parts=(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion??"0.0.0").Split('+');
    public static string Version=>Parts[0];
    // CI passes -p:SourceRevisionId=<sha>; a local build has none.
    public static string Commit=>Parts.Length>1?Parts[1][..Math.Min(12,Parts[1].Length)]:"dev";
}
