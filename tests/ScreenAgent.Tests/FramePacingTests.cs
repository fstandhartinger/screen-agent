using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
public class FramePacingTests
{
    [Fact]public void SlowUploadKeepsOnlyNewestWaitingCapture()
    {
        var slot=new LatestFrameSlot<string>();
        Assert.Null(slot.Offer("first"));Assert.True(slot.TryTake(out var first));Assert.Equal("first",first);
        Assert.Null(slot.Offer("second"));Assert.False(slot.TryTake(out _));
        Assert.Equal("second",slot.Offer("third"));Assert.Equal("third",slot.Offer("newest"));
        slot.Complete();Assert.True(slot.TryTake(out var latest));Assert.Equal("newest",latest);
        Assert.False(slot.TryTake(out _));slot.Complete();Assert.False(slot.TryTake(out _));
    }
    [Fact]public void CapturingBeforeFirstAcceptanceDoesNotSpinEveryTick()
    {
        var planner=new FramePlanner();Assert.True(planner.CaptureDue(10000,1,false));planner.Captured(10000,1);
        Assert.False(planner.CaptureDue(10100,1,false));Assert.False(planner.CaptureDue(11999,1,false));
        Assert.True(planner.CaptureDue(14000,1,false));
    }
    [Fact]public void UnchangedPendingCaptureIsStillDedupedAgainstAcceptedFrame()
    {
        var planner=new FramePlanner();var grid=new byte[ChangeDetector.Width*ChangeDetector.Height];
        planner.Captured(0,1);planner.Sent(0,grid,1);
        Assert.Equal(FrameVerdict.Deduped,planner.Decide(5000,(byte[])grid.Clone(),1,false).Verdict);
    }
    [Fact]public void ResetDropsPendingButDoesNotReleaseCancelledUploadEarly()
    {
        var slot=new LatestFrameSlot<string>();slot.Offer("old-active");slot.TryTake(out _);slot.Offer("old-pending");
        Assert.Equal("old-pending",slot.Clear());Assert.True(slot.InFlight);
        slot.Offer("new-session");Assert.False(slot.TryTake(out _));
        slot.Complete();Assert.True(slot.TryTake(out var next));Assert.Equal("new-session",next);
    }
    [Fact]public void ConnectingNeverStartsSessionCallsAndNoSessionBacksOffUntilFirstOk()
    {
        var gate=new DeskSessionGate();
        foreach(var state in new[]{"connecting","idle","error","",null})
        {Assert.False(gate.CanPoll(DeskSessionGate.RegisteredState(state),0));Assert.False(gate.CanSend(DeskSessionGate.RegisteredState(state),0));}
        foreach(var state in new[]{"listening","thinking","speaking"})Assert.True(gate.CanSend(DeskSessionGate.RegisteredState(state),0));
        gate.NoSession(100);Assert.False(gate.CanPoll(true,1599));Assert.False(gate.CanSend(true,1600));
        Assert.True(gate.CanPoll(true,1600));gate.Accepted();Assert.True(gate.CanSend(true,1601));
        gate.NoSession(2000);gate.Reset();Assert.True(gate.CanSend(true,2001));Assert.False(gate.CanSend(false,9999));
    }
    [Fact]public void SkipCountsHaveIndependentThirtySecondWindows()
    {
        var counter=new FrameSkipCounter();Assert.Equal(1,counter.Note("in-flight",0));
        for(int i=1;i<300;i++)Assert.Equal(0,counter.Note("in-flight",i*100));
        Assert.Equal(1,counter.Note("no-session",200));Assert.Equal(300,counter.Note("in-flight",30000));
        Assert.Equal(0,counter.Note("no-session",30000));Assert.Equal(2,counter.Note("no-session",30200));
    }
    [Fact]public async Task OcrWritesCannotOvertakeFrameAndVersionsAreAssignedInWireOrder()
    {
        var serial=new OrderedWork();var clock=new FrameClock();var entered=new TaskCompletionSource();var release=new TaskCompletionSource();
        var wire=new List<(string Kind,long Version)>();
        var frame=serial.Enqueue(async ()=>{wire.Add(("frame",clock.Next(1)));entered.SetResult();await release.Task;});
        await entered.Task;
        var ocr=serial.Enqueue(()=>{wire.Add(("ocr",clock.Next(1)));return Task.CompletedTask;});
        Assert.Single(wire);release.SetResult();await Task.WhenAll(frame,ocr);
        Assert.Equal(new[]{("frame",1L),("ocr",2L)},wire);
    }
}
