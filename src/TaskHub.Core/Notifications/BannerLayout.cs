namespace TaskHub.Core.Notifications;

/// <summary>Placement maths for stacked banners, in physical pixels of one monitor's work area.</summary>
public static class BannerLayout
{
    /// <summary>Top edge for each banner, stacking downward from the top of the work area.</summary>
    public static IReadOnlyList<double> StackTops(double workTop, IReadOnlyList<double> heights, double margin, double gap)
    {
        var tops = new double[heights.Count];
        var y = workTop + margin;
        for (var i = 0; i < heights.Count; i++)
        {
            tops[i] = y;
            y += heights[i] + gap;
        }
        return tops;
    }

    /// <summary>Left edge that centres a banner horizontally in the work area.</summary>
    public static double CenterLeft(double workLeft, double workWidth, double bannerWidth) =>
        Math.Round(workLeft + (workWidth - bannerWidth) / 2);

    /// <summary>Where a banner starts/ends its slide: fully above the top of the screen (shadow included).</summary>
    public static double AboveScreen(double workTop, double height, double shadow) => workTop - height - shadow;

    /// <summary>
    /// Fraction of the auto-dismiss time left, for the countdown bar (1 = full, 0 = closing).
    /// Time spent hovering doesn't count.
    /// </summary>
    public static double Remaining(TimeSpan duration, TimeSpan elapsedNotPaused) =>
        duration <= TimeSpan.Zero ? 1 : Math.Clamp(1 - elapsedNotPaused / duration, 0, 1);
}
