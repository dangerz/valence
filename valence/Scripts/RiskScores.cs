using System;
using System.Collections.Generic;
using System.Linq;
using Valence.Schema;

namespace Valence.Viewer;

/// <summary>
/// One class's risk (0..1) and what drives it. Percentiles are the share of classes at or below its
/// value (0 when the value is 0); "top" is the share at or above it, for "top 4%" in the inspector.
/// </summary>
public sealed record RiskScore(
    float Score,
    float ChurnPercentile, float FanInPercentile, float SizePercentile,
    float ChurnTop, float FanInTop, float SizeTop)
{
    public static readonly RiskScore None = new(0f, 0f, 0f, 0f, 1f, 1f, 1f);
}

/// <summary>
/// The Risk lens: where is the next change most likely to hurt? Code that changes a lot AND that many
/// classes depend on. Each factor is a percentile within the repo (so one huge file doesn't flatten
/// everything else), and the score needs both: √(churn × dependents), scaled 0.75–1 by size. Without
/// git history it falls back to √(dependents × size).
/// </summary>
public static class RiskScores
{
    public const int TopCount = 10;

    public static void Compute(GraphModel model)
    {
        var nodes = model.Nodes.Values.ToList();
        var churn = Ranks(nodes, n => n.Metric(MetricKeys.Churn));
        var fanIn = Ranks(nodes, n => n.FanIn);
        var size = Ranks(nodes, n => n.Metric(MetricKeys.Loc));
        foreach (var node in nodes)
        {
            var (c, cTop) = churn[node];
            var (f, fTop) = fanIn[node];
            var (s, sTop) = size[node];
            var score = model.HasGit
                ? MathF.Sqrt(c * f) * (0.75f + 0.25f * s)
                : MathF.Sqrt(f * s);
            node.Risk = new RiskScore(score, c, f, s, cTop, fTop, sTop);
        }

        model.TopRisks.Clear();
        model.TopRisks.AddRange(nodes
            .Where(n => n.Risk.Score > 0f)
            .OrderByDescending(n => n.Risk.Score)
            .ThenBy(n => n.Info.Name, StringComparer.OrdinalIgnoreCase)
            .Take(TopCount));
    }

    /// <summary>(percentile, top share) per class for one metric.</summary>
    private static Dictionary<NodeData, (float Percentile, float Top)> Ranks(List<NodeData> nodes, Func<NodeData, double> metric)
    {
        var sorted = nodes.Select(metric).OrderBy(v => v).ToArray();
        var count = Math.Max(1, sorted.Length);
        var result = new Dictionary<NodeData, (float, float)>(nodes.Count);
        foreach (var node in nodes)
        {
            var value = metric(node);
            if (value <= 0)
            {
                result[node] = (0f, 1f);
                continue;
            }
            var atOrBelow = UpperBound(sorted, value);
            var above = sorted.Length - atOrBelow;
            var atOrAbove = sorted.Length - LowerBound(sorted, value);
            result[node] = ((float)atOrBelow / count, (float)Math.Max(atOrAbove, above + 1) / count);
        }
        return result;
    }

    /// <summary>How many values are &lt; value.</summary>
    private static int LowerBound(double[] sorted, double value)
    {
        int low = 0, high = sorted.Length;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (sorted[mid] < value) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    /// <summary>How many values are ≤ value.</summary>
    private static int UpperBound(double[] sorted, double value)
    {
        int low = 0, high = sorted.Length;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (sorted[mid] <= value) low = mid + 1;
            else high = mid;
        }
        return low;
    }
}
