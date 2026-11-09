using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
public class ServeAuthTests
{
    [Theory,InlineData(null),InlineData(""),InlineData("short"),InlineData("               ")]
    public void DebugServeRequiresStrongToken(string? token)=>Assert.False(ServeAuth.ValidToken(token));
    [Fact] public void BearerMustMatchExactly()
    {
        const string t="test-token-123456789";
        Assert.True(ServeAuth.Authorized(t,"Bearer "+t));
        foreach(string? bad in new[]{null,"",t,"bearer "+t,"Bearer "+t+"x","Bearer wrong-token-12345"})Assert.False(ServeAuth.Authorized(t,bad));
    }
    [Theory,InlineData("application/json",true),InlineData("application/json; charset=utf-8",true),InlineData("text/plain",false),InlineData("application/x-www-form-urlencoded",false),InlineData(null,false)]
    public void PostsRequireJson(string? type,bool ok)=>Assert.Equal(ok,ServeAuth.JsonContentType(type));
}
