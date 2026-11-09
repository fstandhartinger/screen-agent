using ScreenAgent.Core;
using Xunit;
namespace ScreenAgent.Tests;
public class DesktopMathTests
{
    [Fact] public void ExecutingIsNotIdleButGatewayAndTimeCapsRemain()
    {
        long now=0;var run=new ComputerRun("run","goal",new ComputerScope(),()=>now);
        now=61000;run.Seen();Assert.Equal("idle",run.Due());Assert.Null(run.Due(executing:true));
        now+=16000;Assert.Equal("gateway-quiet",run.Due(executing:true));
        now=600001;run.Seen();Assert.Equal("time-cap",run.Due(executing:true));
    }
    [Fact] public void NegativeOriginAndPixelCentres(){var f=PixelFrame.Fit(-2560,-1440,2560,1440,1280);Assert.Equal((-2559,-1439),f.Physical(0,0));Assert.Equal((-1,-1),f.Physical(1279,719));}
    [Fact] public void ZoomUsesEdgesAndMapsToOriginalSpot(){var f=PixelFrame.Fit(-2560,0,2560,1440,1280);var z=f.Crop(100,100,200,100,1280);Assert.Equal(-2360,z.X);Assert.Equal(200,z.Y);Assert.Equal((400,200),(z.Width,z.Height));Assert.Equal((-2160,300),z.Physical(200,100));}
    [Fact] public void RoundTripEveryPixel(){var f=PixelFrame.Fit(-800,50,1920,1080,1280);for(int x=0;x<f.Width;x++){var p=f.Physical(x,50);var q=f.Image(p.X,p.Y);Assert.InRange(Math.Abs(q.X-x),0,.5);}}
    [Fact] public void AbsoluteCoordinatesLandOnPixel(){foreach(int width in new[]{1920,2560,3840,7680})for(int x=-1920;x<width-1920;x++){int a=PixelFrame.Absolute(x,-1920,width);Assert.Equal(x,(int)Math.Floor(a*width/65536.0)-1920);}}
    [Theory][InlineData(-1,0,1,1)][InlineData(0,0,0,1)][InlineData(99,0,2,1)]public void InvalidCrops(double x,double y,double w,double h)=>Assert.Throws<ArgumentException>(()=>PixelFrame.Fit(0,0,100,100,100).Crop(x,y,w,h,100));
    [Fact] public void TailKeepsFailureAtEnd(){string t=DesktopText.Tail(new string('a',13000)+"FAIL");Assert.EndsWith("FAIL",t);Assert.Contains("1004 Zeichen davor",t);}
    [Theory][InlineData("strg+umschalt+a",65,2)][InlineData("win+r",82,1)][InlineData("f24",135,0)][InlineData("numpad9",105,0)][InlineData("alt+bildab",34,1)]public void Chords(string s,int key,int mods){var c=DesktopKeys.Parse(s)[0];Assert.Equal(key,c.Key);Assert.Equal(mods,c.Modifiers.Length);}
    [Fact] public void SequenceAndExtended(){var c=DesktopKeys.Parse("ctrl+a delete win+r");Assert.Equal(3,c.Length);Assert.True(c[1].Extended);}
    [Theory][InlineData("f25")][InlineData("hyper+a")][InlineData("")]public void InvalidKeys(string s)=>Assert.Throws<ArgumentException>(()=>DesktopKeys.Parse(s));
}
