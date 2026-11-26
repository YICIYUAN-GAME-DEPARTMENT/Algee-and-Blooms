using System;
using System.Collections.Generic;

namespace Growth.Algorithms.Scoring;

/// <summary>
/// 评分 v3 的单调校准（§3.3）：先用 isotonic（PAVA）保证单调，再做分段线性插值。
/// 与 ScoreSpline 的区别：允许锚点 D 值重复 / 分数倒挂（会先被合并平滑），
/// 用于交互分这种"定性锚点 + 噪声标签"的场景。
/// </summary>
public sealed class ScoreCalibration
{
    private readonly double[] _x;
    private readonly double[] _y;

    private ScoreCalibration(double[] x, double[] y)
    {
        _x = x;
        _y = y;
    }

    public static ScoreCalibration FromAnchors(IReadOnlyList<(double D, double Score)> anchors)
    {
        if (anchors == null || anchors.Count == 0)
        {
            throw new ArgumentException("锚点不能为空", nameof(anchors));
        }
        var points = new List<(double D, double Score)>(anchors);
        points.Sort((a, b) => a.D.CompareTo(b.D));

        // PAVA：相邻块均值倒挂时合并，得到单调不减的块。
        var blocks = new List<(double SumD, double SumY, int Count)>();
        foreach ((double d, double score) in points)
        {
            blocks.Add((d, score, 1));
            while (blocks.Count > 1)
            {
                (double prevD, double prevY, int prevCount) = blocks[^2];
                (double lastD, double lastY, int lastCount) = blocks[^1];
                if (prevY / prevCount <= lastY / lastCount)
                {
                    break;
                }
                blocks.RemoveAt(blocks.Count - 1);
                blocks.RemoveAt(blocks.Count - 1);
                blocks.Add((prevD + lastD, prevY + lastY, prevCount + lastCount));
            }
        }

        var xs = new List<double>(blocks.Count);
        var ys = new List<double>(blocks.Count);
        foreach ((double sumD, double sumY, int count) in blocks)
        {
            double x = sumD / count;
            double y = sumY / count;
            if (xs.Count > 0 && Math.Abs(x - xs[^1]) < 1e-9)
            {
                ys[^1] = Math.Max(ys[^1], y);
                continue;
            }
            xs.Add(x);
            ys.Add(y);
        }
        return new ScoreCalibration(xs.ToArray(), ys.ToArray());
    }

    /// <summary>D → 0–100；低于首锚取首分，高于末锚按末段斜率外推并钳到 100。</summary>
    public double Map(double d)
    {
        if (_x.Length == 1)
        {
            return Math.Clamp(_y[0], 0.0, 100.0);
        }
        if (d <= _x[0])
        {
            return Math.Clamp(_y[0], 0.0, 100.0);
        }
        int last = _x.Length - 1;
        if (d >= _x[last])
        {
            double span = _x[last] - _x[last - 1];
            double slope = span > 0 ? (_y[last] - _y[last - 1]) / span : 0;
            return Math.Clamp(_y[last] + (d - _x[last]) * slope, 0.0, 100.0);
        }
        for (int i = 1; i <= last; i++)
        {
            if (d <= _x[i])
            {
                double span = _x[i] - _x[i - 1];
                double t = span > 0 ? (d - _x[i - 1]) / span : 0;
                return Math.Clamp(_y[i - 1] + t * (_y[i] - _y[i - 1]), 0.0, 100.0);
            }
        }
        return Math.Clamp(_y[last], 0.0, 100.0);
    }

    public int MapRounded(double d)
    {
        return (int)Math.Round(Map(d), MidpointRounding.AwayFromZero);
    }
}
