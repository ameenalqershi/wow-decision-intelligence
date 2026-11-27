namespace WowDi.Studio.DesignSystem.Localization;

// Coordinates are host-provided logical work-area coordinates, never screen pixels.
public readonly record struct LogicalRect(double X,double Y,double Width,double Height)
{
    public bool Valid => double.IsFinite(X)&&double.IsFinite(Y)&&double.IsFinite(Width)&&double.IsFinite(Height)
        &&double.IsFinite(X+Width)&&double.IsFinite(Y+Height)&&Width>0&&Height>0;
}
public sealed record DisplayArea(string Id,LogicalRect WorkArea,double Scale,bool Primary=false);
public sealed record SavedPlacement(string DisplayId,LogicalRect Bounds);
public sealed record RecoveredPlacement(string DisplayId,LogicalRect Bounds,double Scale,bool Recovered);

public static class DisplayRecovery
{
    public static RecoveredPlacement Restore(SavedPlacement saved,IReadOnlyList<DisplayArea> displays)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(displays);
        if(displays.Count==0||displays.Any(d=>string.IsNullOrWhiteSpace(d.Id)||!d.WorkArea.Valid||!double.IsFinite(d.Scale)||d.Scale<=0)
            ||displays.Select(d=>d.Id).Distinct(StringComparer.Ordinal).Count()!=displays.Count)
            throw new ArgumentException("Valid unique display work areas and scales are required.",nameof(displays));
        var selected=displays.FirstOrDefault(d=>d.Id==saved.DisplayId);
        var valid=saved.Bounds.Valid && selected is not null;
        selected ??= displays.FirstOrDefault(d=>d.Primary) ?? displays[0];
        var area=selected.WorkArea;
        var width=Math.Min(valid?saved.Bounds.Width:960,area.Width);
        var height=Math.Min(valid?saved.Bounds.Height:640,area.Height);
        var x=valid?Math.Clamp(saved.Bounds.X,area.X,area.X+area.Width-width):area.X+(area.Width-width)/2;
        var y=valid?Math.Clamp(saved.Bounds.Y,area.Y,area.Y+area.Height-height):area.Y+(area.Height-height)/2;
        var bounds=new LogicalRect(x,y,width,height);
        return new(selected.Id,bounds,selected.Scale,!valid||bounds!=saved.Bounds);
    }
}
