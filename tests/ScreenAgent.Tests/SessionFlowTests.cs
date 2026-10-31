using ScreenAgent.Core;
using System.Text.Json;
using Xunit;
namespace ScreenAgent.Tests;
public class SessionFlowTests
{
    [Fact] public void FrameVersionsSurviveClockRollbackAndRapidFrames()
    {
        var c=new FrameClock();Assert.Equal(1800000000000,c.Next(1800000000000));
        Assert.Equal(1800000000001,c.Next(1800000000000));Assert.Equal(1800000000002,c.Next(1700000000000));
        Assert.True(new FrameClock().Next(1800000000100)>1800000000002);
    }
    [Fact] public void StaticScreenRenewsAtThirtySeconds()
    {
        var s=new ScreenSchedule();s.Success(100);Assert.False(s.RenewalDue(30099));Assert.True(s.RenewalDue(30100));
        s.Success(30100);Assert.False(s.RenewalDue(60099));Assert.True(s.RenewalDue(60100));
    }
    [Fact] public void OutagesBackOffAndRecover()
    {
        var s=new ScreenSchedule();long now=0;
        foreach(long delay in new long[]{1000,2000,4000,8000,16000,30000,30000})
        {s.Failure(now);Assert.Equal(now+delay,s.RetryAt);Assert.False(s.Ready(now));now=s.RetryAt;Assert.True(s.Ready(now));}
        s.Success(now);s.Failure(now);Assert.Equal(now+1000,s.RetryAt);
    }
    [Theory]
    [InlineData("https://bittensor-earnings.lovable.app/reax",PrivacyClass.IdsOnly)]
    [InlineData("https://bittensor-earnings.lovable.app.evil.test",PrivacyClass.Allowed)]
    [InlineData("https://kraken.com",PrivacyClass.Blocked)]
    [InlineData(null,PrivacyClass.Blocked)]
    public void PrivacyModes(string? url,PrivacyClass mode)=>Assert.Equal(mode,new PrivacyPolicy().Classify("chrome","Earnings",url));
    [Fact] public void FullBlocksCannotBeDowngraded()
    {
        var p=new PrivacyPolicy{IdsOnlyDomains=["kraken.com"]};
        Assert.Equal(PrivacyClass.Blocked,p.Classify("chrome","Earnings","https://kraken.com"));
        Assert.Equal(PrivacyClass.Blocked,p.Classify("","notes"));
        Assert.Equal(PrivacyClass.Blocked,p.Classify("chrome","password","https://bittensor-earnings.lovable.app"));
    }
    [Fact] public void NoImageHighlightHasNoImageFields()
    {
        var json=JsonSerializer.Serialize(new IdsHighlightPayload(["332179"],new(1,2,3,4)));
        Assert.Contains("\"noImage\":true",json);Assert.DoesNotContain("crop",json);Assert.DoesNotContain("context",json);
    }
    [Fact] public async Task SlowFirstDictationPreservesOrderWithoutBlockingCapture()
    {
        var q=new OrderedWork();var release=new TaskCompletionSource();var inserted=new List<int>();
        var first=q.Enqueue(async ()=>{await release.Task;inserted.Add(1);});
        // The caller can capture and enqueue the second clip before the first completes.
        var second=q.Enqueue(()=>{inserted.Add(2);return Task.CompletedTask;});
        Assert.Empty(inserted);Assert.False(second.IsCompleted);release.SetResult();await second;await first;
        Assert.Equal(new[]{1,2},inserted);
    }
    [Fact] public async Task FailedWorkDoesNotWedgeQueue()
    {
        var q=new OrderedWork();var failed=q.Enqueue(()=>Task.FromException(new Exception()));
        await Assert.ThrowsAsync<Exception>(()=>failed);bool ran=false;
        await q.Enqueue(()=>{ran=true;return Task.CompletedTask;});Assert.True(ran);
    }
}
