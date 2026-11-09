using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
public class ChromeUiLaneTests
{
    [Fact] public async Task ExpiredProviderCannotActLaterOrStartAReplacementLane()
    {
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();using var ended=new ManualResetEventSlim();int actions=0;
        var call=Task.Run(()=>ChromeUiLane.Run(valid=>{entered.Set();release.Wait();try{if(valid())Interlocked.Increment(ref actions);return true;}finally{ended.Set();}},()=>false,500));
        try
        {
            for(int i=0;i<200&&!entered.IsSet;i++)await Task.Delay(5);
            Assert.True(entered.IsSet);await Assert.ThrowsAsync<TimeoutException>(()=>call);
            Assert.Throws<TimeoutException>(()=>ChromeUiLane.Run(_=>true,()=>false,100));
        }
        finally{release.Set();}
        Assert.True(ended.Wait(1000));Assert.Equal(0,actions);
    }
}
