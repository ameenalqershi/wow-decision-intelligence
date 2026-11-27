namespace WowDi.Studio.DesignSystem.Localization;

// OS-native physical pixels. A per-monitor logical frame has its origin at
// the monitor's work-area origin, not the virtual desktop origin. A global
// division by the current DPI is invalid when adjacent monitors differ.
public readonly record struct PhysicalRect(int X,int Y,int Width,int Height)
{
    public bool Valid => Width>0&&Height>0&&(long)X+Width<=int.MaxValue&&(long)Y+Height<=int.MaxValue;
    public long Right => (long)X+Width;
    public long Bottom => (long)Y+Height;
}
public sealed record NativeDisplayFrame(string Id,PhysicalRect WorkArea,double Scale,bool Primary=false);
public sealed record NativePlacement(string DisplayId,LogicalRect LocalBounds);
public sealed record NativePlacementResult(string DisplayId,LogicalRect LocalBounds,PhysicalRect PixelBounds,double Scale,bool Recovered);

public static class DisplayCoordinateFrame
{
    public static NativePlacement Capture(NativeDisplayFrame display,PhysicalRect bounds)
    {
        Validate(display);
        if(!bounds.Valid)throw new ArgumentException("Invalid native window rectangle.",nameof(bounds));
        var local=new LogicalRect(((long)bounds.X-display.WorkArea.X)/display.Scale,
            ((long)bounds.Y-display.WorkArea.Y)/display.Scale,bounds.Width/display.Scale,bounds.Height/display.Scale);
        if(!local.Valid)throw new ArgumentException("Native window cannot be represented in logical coordinates.",nameof(bounds));
        return new(display.Id,local);
    }

    // The saved value here is explicitly monitor-local. Do not deserialize
    // older absolute ShellLayout coordinates as this representation.
    public static NativePlacementResult Restore(NativePlacement saved,IReadOnlyList<NativeDisplayFrame> displays)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(displays);
        foreach(var d in displays)Validate(d);
        var localAreas=displays.Select(d=>new DisplayArea(d.Id,
            new LogicalRect(0,0,d.WorkArea.Width/d.Scale,d.WorkArea.Height/d.Scale),d.Scale,d.Primary)).ToArray();
        var recovered=DisplayRecovery.Restore(new SavedPlacement(saved.DisplayId,saved.LocalBounds),localAreas);
        var target=displays.Single(d=>d.Id==recovered.DisplayId);
        var b=recovered.Bounds;
        // Quantize both edges then clamp to the actual pixel work area.
        // Rounding a logical width and position independently can overrun
        // a fractional-scale monitor by one pixel.
        long left=Edge(b.X,target.Scale,target.WorkArea.Width);
        long top=Edge(b.Y,target.Scale,target.WorkArea.Height);
        long right=Edge(b.X+b.Width,target.Scale,target.WorkArea.Width);
        long bottom=Edge(b.Y+b.Height,target.Scale,target.WorkArea.Height);
        left=Math.Min(left,target.WorkArea.Width-1L);top=Math.Min(top,target.WorkArea.Height-1L);
        right=Math.Clamp(right,left+1,target.WorkArea.Width);bottom=Math.Clamp(bottom,top+1,target.WorkArea.Height);
        var pixels=new PhysicalRect(checked((int)(target.WorkArea.X+left)),checked((int)(target.WorkArea.Y+top)),
            checked((int)(right-left)),checked((int)(bottom-top)));
        return new(target.Id,b,pixels,target.Scale,recovered.Recovered);
    }
    private static long Edge(double logical,double scale,int extent)
    {
        double value=logical*scale;
        if(!double.IsFinite(value))throw new InvalidOperationException("Unrepresentable native edge.");
        return (long)Math.Clamp(Math.Round(value,MidpointRounding.AwayFromZero),0,extent);
    }
    private static void Validate(NativeDisplayFrame display)
    {
        if(display is null||string.IsNullOrWhiteSpace(display.Id)||!display.WorkArea.Valid||
            !double.IsFinite(display.Scale)||display.Scale<=0||
            !double.IsFinite(display.WorkArea.Width/display.Scale)||
            !double.IsFinite(display.WorkArea.Height/display.Scale))
            throw new ArgumentException("Valid physical work area, stable host identity and scale required.");
    }
}
