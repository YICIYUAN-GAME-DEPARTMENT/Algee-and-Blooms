using System;
using System.Collections.Generic;

namespace Growth.Algorithms.Scoring;

/// <summary>
/// D_raw → 难度分的单调分段线性样条（规划稿 §5.4）。
/// 低于首锚点取首分；高于末锚点按最后一段斜率外推，上限 200。
/// </summary>
public sealed class ScoreSpline
{
    private const double MaxScore = 200.0;

    private readonly double[] _dRaw;
    private readonly int[] _score;

    private static readonly Lazy<ScoreSpline> LazyDefault = new(() => new ScoreSpline(ScoreAnchors.Default));

    public static ScoreSpline Default => LazyDefault.Value;

    public ScoreSpline(ScoreAnchors anchors) : this(anchors.Anchors)
    {
    }

    public ScoreSpline(IReadOnlyList<ScoreAnchor> anchors)
    {
        if (anchors == null || anchors.Count == 0)
        {
            throw new ArgumentException("锚点不能为空", nameof(anchors));
        }
        var sorted = new List<ScoreAnchor>(anchors);
        sorted.Sort((a, b) => a.DRaw.CompareTo(b.DRaw));

        _dRaw = new double[sorted.Count];
        _score = new int[sorted.Count];
        for (int i = 0; i < sorted.Count; i++)
        {
            if (i > 0 && sorted[i].DRaw <= sorted[i - 1].DRaw)
            {
                throw new ArgumentException($"锚点 D_raw 必须严格递增：{sorted[i - 1].Id} -> {sorted[i].Id}");
            }
            if (i > 0 && sorted[i].Score < sorted[i - 1].Score)
            {
                throw new ArgumentException($"锚点分数必须单调不减：{sorted[i - 1].Id} -> {sorted[i].Id}");
            }
            _dRaw[i] = sorted[i].DRaw;
            _score[i] = sorted[i].Score;
        }
    }

    public double Interpolate(double dRaw)
    {
        if (dRaw <= _dRaw[0])
        {
            return _score[0];
        }
        int last = _dRaw.Length - 1;
        if (dRaw >= _dRaw[last])
        {
            double slope = (_score[last] - _score[last - 1]) / (_dRaw[last] - _dRaw[last - 1]);
            return Math.Min(MaxScore, _score[last] + (dRaw - _dRaw[last]) * slope);
        }
        for (int i = 1; i <= last; i++)
        {
            if (dRaw <= _dRaw[i])
            {
                double t = (dRaw - _dRaw[i - 1]) / (_dRaw[i] - _dRaw[i - 1]);
                return _score[i - 1] + t * (_score[i] - _score[i - 1]);
            }
        }
        return _score[last];
    }

    public int InterpolateRounded(double dRaw)
    {
        return (int)Math.Round(Interpolate(dRaw), MidpointRounding.AwayFromZero);
    }
}
