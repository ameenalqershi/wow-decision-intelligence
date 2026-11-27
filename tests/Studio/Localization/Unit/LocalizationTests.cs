using WowDi.Studio.DesignSystem.Localization;
using Xunit;
namespace WowDi.Studio.Localization.Tests;
public sealed class LocalizationTests
{
    [Fact] public void LanguageAndDirectionAreExplicit()
    {
        Assert.Equal("ar",StudioLanguage.Arabic.Tag());Assert.Equal("rtl",StudioLanguage.Arabic.Direction());
        Assert.Equal("en",StudioLanguage.English.Tag());Assert.Equal("ltr",StudioLanguage.English.Direction());
        Assert.Throws<ArgumentOutOfRangeException>(()=>((StudioLanguage)100).Tag());
    }
    [Fact] public void MissingMonitorFallsBackAndKeepsWindowVisible()
    {
        var result=DisplayRecovery.Restore(new("disconnected",new(4000,100,900,700)),[new("primary",new(-100,20,800,600),2,true)]);
        Assert.Equal("primary",result.DisplayId);Assert.Equal(new LogicalRect(-100,20,800,600),result.Bounds);
        Assert.Equal(2,result.Scale);Assert.True(result.Recovered);
    }
    [Fact] public void ExistingMonitorUsesItsScaleAndClampsOffscreenBounds()
    {
        var result=DisplayRecovery.Restore(new("secondary",new(-2200,100,800,500)),
            [new("primary",new(0,0,1600,900),1,true),new("secondary",new(-1920,0,1920,1080),1.5)]);
        Assert.Equal("secondary",result.DisplayId);Assert.Equal(new LogicalRect(-1920,100,800,500),result.Bounds);
        Assert.Equal(1.5,result.Scale);Assert.True(result.Recovered);
    }
    [Fact] public void ValidPlacementRemainsUnchanged()
    {
        var bounds=new LogicalRect(100,100,900,700);
        var result=DisplayRecovery.Restore(new("a",bounds),[new("a",new(0,0,1600,900),1)]);
        Assert.Equal(bounds,result.Bounds);Assert.False(result.Recovered);
    }
    [Fact] public void InvalidTopologyIsRejected()
    {
        Assert.Throws<ArgumentException>(()=>DisplayRecovery.Restore(new("a",new(0,0,100,100)),[]));
        Assert.Throws<ArgumentException>(()=>DisplayRecovery.Restore(new("a",new(0,0,100,100)),[new("a",new(0,0,100,100),double.NaN)]));
    }
}
