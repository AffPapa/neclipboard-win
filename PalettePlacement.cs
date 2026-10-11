namespace NeClipboardWin;

internal static class PalettePlacement
{
    internal static double ClampOrigin(double origin, double workAreaOrigin, double workAreaSize, double windowSize)
    {
        var start = double.IsFinite(workAreaOrigin) ? workAreaOrigin : 0;
        var area = double.IsFinite(workAreaSize) ? Math.Max(0, workAreaSize) : 0;
        var size = double.IsFinite(windowSize) ? Math.Max(0, windowSize) : 0;
        var requested = double.IsFinite(origin) ? origin : start;
        var lastFullyVisibleOrigin = Math.Max(start, start + area - size);
        return Math.Clamp(requested, start, lastFullyVisibleOrigin);
    }
}
