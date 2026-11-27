using WowDi.Studio.DesignSystem.Localization;
using Xunit;
namespace WowDi.Studio.Localization.Tests;
public sealed class DisplayCoordinateTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void PixelRoundtripOnNegativeOrigin(double scale)
    {
        var monitor=new NativeDisplayFrame("left",new(-1920,-100,1920,1080),scale);
        var bounds=new PhysicalRect(-1817,-37,901,607);
        var saved=DisplayCoordinateFrame.Capture(monitor,bounds);
        var result=DisplayCoordinateFrame.Restore(saved,[monitor]);
        Assert.Equal(bounds,result.PixelBounds);
        Assert.False(result.Recovered);
    }
    [Fact]
    public void ActualScaleChangePreservesMonitorLocalLogicalExtent()
    {
        var before=new NativeDisplayFrame("same",new(-1600,0,1600,1000),1);
        var after=new NativeDisplayFrame("same",new(-1920,-50,1920,1200),1.5);
        var saved=DisplayCoordinateFrame.Capture(before,new(-1500,50,800,600));
        var result=DisplayCoordinateFrame.Restore(saved,[after]);
        Assert.Equal(new PhysicalRect(-1770,25,1200,900),result.PixelBounds);
        Assert.Equal(saved.LocalBounds,result.LocalBounds);
    }
    [Fact]
    public void DisconnectedDisplayUsesActualPrimaryOriginAndScale()
    {
        var primary=new NativeDisplayFrame("primary",new(50,-20,1250,800),1.25,true);
        var result=DisplayCoordinateFrame.Restore(new("missing",new(200,100,900,700)),[primary]);
        Assert.Equal("primary",result.DisplayId);Assert.True(result.Recovered);
        Assert.Equal(new PhysicalRect(75,-20,1200,800),result.PixelBounds);
    }
    [Fact]
    public void FractionalScaleQuantizationCannotCrossPixelWorkArea()
    {
        var monitor=new NativeDisplayFrame("fractional",new(-1001,-301,1001,701),1.25);
        for(int i=0;i<1000;i++)
        {
            var value=DisplayCoordinateFrame.Restore(new("fractional",new(i*.071,i*.037,1000.1-i*.003,1000.7)),[monitor]);
            Assert.True(value.PixelBounds.Valid);
            Assert.True(value.PixelBounds.X>=monitor.WorkArea.X&&value.PixelBounds.Y>=monitor.WorkArea.Y);
            Assert.True(value.PixelBounds.Right<=monitor.WorkArea.Right&&value.PixelBounds.Bottom<=monitor.WorkArea.Bottom);
        }
    }
    [Fact]
    public void InvalidOrAmbiguousNativeTopologyRejected()
    {
        var saved=new NativePlacement("a",new(0,0,200,100));
        var good=new NativeDisplayFrame("a",new(0,0,1000,700),1);
        Assert.Throws<ArgumentException>(()=>DisplayCoordinateFrame.Restore(saved,[good,good]));
        Assert.Throws<ArgumentException>(()=>DisplayCoordinateFrame.Restore(saved,[good with{Scale=double.NaN}]));
        Assert.Throws<ArgumentException>(()=>DisplayCoordinateFrame.Restore(saved,[good with{WorkArea=new(int.MaxValue,0,2,2)}]));
        Assert.Throws<ArgumentException>(()=>DisplayCoordinateFrame.Restore(saved,[]));
    }
}
