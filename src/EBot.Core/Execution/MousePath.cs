namespace EBot.Core.Execution;

/// <summary>
/// Generates humanized mouse movement paths between two screen points.
///
/// Algorithm:
///   • Quadratic Bézier curve with a randomly-offset control point perpendicular
///     to the midpoint — produces a natural arc instead of a straight line.
///   • Smoothstep (ease-in-out) speed profile: cursor accelerates out of the
///     start and decelerates into the target, matching how a human hand moves.
///   • Optional per-step micro-tremors (sub-pixel noise on intermediate points).
/// </summary>
public static class MousePath
{
    /// <summary>
    /// Returns an array of <paramref name="steps"/> screen-coordinate waypoints
    /// from (x0,y0) to (x1,y1) along a randomised Bézier arc.
    ///
    /// The last element is always (x1,y1) exactly — jitter is only applied to
    /// intermediate points so the cursor lands precisely on the target.
    /// </summary>
    /// <param name="curveStrength">
    ///   Controls how much the path deviates from a straight line.
    ///   0 = perfectly straight; 0.25 = gentle arc (default); 0.6 = strong curve.
    /// </param>
    public static (int X, int Y)[] Generate(
        int x0, int y0, int x1, int y1,
        int steps, float curveStrength, Random rng,
        bool microTremors = true)
    {
        if (steps <= 1)
            return [(x1, y1)];

        float dx = x1 - x0;
        float dy = y1 - y0;
        float len = MathF.Sqrt(dx * dx + dy * dy);

        if (len < 1f)
            return [(x1, y1)];

        // Perpendicular control point (quadratic Bézier hump)
        float mx = (x0 + x1) * 0.5f;
        float my = (y0 + y1) * 0.5f;
        float perpX = -dy / len;
        float perpY =  dx / len;
        float devAmount = len * curveStrength * ((rng.NextSingle() - 0.5f) * 2f);
        float cpX = mx + perpX * devAmount;
        float cpY = my + perpY * devAmount;

        var points = new (int X, int Y)[steps];
        for (int i = 0; i < steps; i++)
        {
            // t: 1/steps … 1  (we skip t=0 which is the start position)
            float t  = (float)(i + 1) / steps;

            // Smoothstep: ease-in-out — matches natural hand deceleration
            float ts = t * t * (3f - 2f * t);

            // Quadratic Bézier interpolation
            float oneMinusTs = 1f - ts;
            float bx = oneMinusTs * oneMinusTs * x0 + 2f * oneMinusTs * ts * cpX + ts * ts * x1;
            float by = oneMinusTs * oneMinusTs * y0 + 2f * oneMinusTs * ts * cpY + ts * ts * y1;

            // Micro-tremors on intermediate points only (final point lands exactly)
            bool isLast = i == steps - 1;
            if (microTremors && !isLast)
            {
                bx += (rng.NextSingle() - 0.5f) * 2f;
                by += (rng.NextSingle() - 0.5f) * 2f;
            }

            points[i] = isLast ? (x1, y1) : ((int)MathF.Round(bx), (int)MathF.Round(by));
        }

        return points;
    }

    /// <summary>
    /// Calculates a natural step count based on movement distance.
    /// Short moves (few pixels) need only 2-4 steps; long moves need more.
    /// </summary>
    public static int StepsForDistance(
        float distancePx,
        int minSteps = 4,
        int maxSteps = 18,
        float stepsPerPx = 0.065f)
    {
        int natural = (int)MathF.Round(distancePx * stepsPerPx);
        return Math.Clamp(natural, minSteps, maxSteps);
    }

    /// <summary>
    /// Calculates the Euclidean distance between two screen points.
    /// </summary>
    public static float Distance(int x0, int y0, int x1, int y1)
    {
        float dx = x1 - x0;
        float dy = y1 - y0;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
