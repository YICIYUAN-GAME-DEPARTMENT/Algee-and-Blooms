using System;
using System.Collections.Generic;

namespace Growth.Algorithms.Generation;

/// <summary>
/// MAP-Elites 存档（规划稿 §7.2 / §9 阶段 9）：
/// 按 (尺寸桶, 步数桶, min_slack 桶, mech_pressure 桶) 分格，每格保留最接近目标步数的候选；
/// 选格时取 |真实步数 - target_steps| 最小者，超出容差记 target_miss 并回退最近格。
/// 存档只服务单次生成调用，不跨 seed 持久化（保证 seed 决定性）。
/// </summary>
internal sealed class MapElitesArchive
{
    /// <summary>目标步数命中容差：超过即记 target_miss。</summary>
    public const int TargetTolerance = 4;

    private sealed class Entry
    {
        public GenerationResult Result;
        public int Delta;
        public int Count;
    }

    private readonly int _targetSteps;
    private readonly Dictionary<CellKey, Entry> _cells = new();

    public MapElitesArchive(int targetSteps)
    {
        _targetSteps = targetSteps;
    }

    public int Count => _cells.Count;

    public void Add(GenerationResult result)
    {
        if (result == null || !result.Success || result.Score == null || result.Solution == null)
        {
            return;
        }
        CellKey key = KeyOf(result);
        int delta = Math.Abs(result.Solution.Steps - _targetSteps);
        if (_cells.TryGetValue(key, out Entry existing))
        {
            existing.Count++;
            if (Better(delta, result.Score.Quality, existing.Delta, existing.Result.Score.Quality))
            {
                existing.Result = result;
                existing.Delta = delta;
            }
            return;
        }
        _cells[key] = new Entry { Result = result, Delta = delta, Count = 1 };
    }

    /// <summary>选出离目标最近的格子；miss 表示最近格也超出容差。</summary>
    public GenerationResult Select(out int delta, out bool miss)
    {
        delta = int.MaxValue;
        miss = true;
        GenerationResult best = null;
        CellKey bestKey = default;
        int bestCount = 0;
        foreach (KeyValuePair<CellKey, Entry> kv in _cells)
        {
            Entry entry = kv.Value;
            bool take;
            if (best == null)
            {
                take = true;
            }
            else if (entry.Delta != delta)
            {
                take = entry.Delta < delta;
            }
            else if (entry.Count != bestCount)
            {
                // 容量权重：同距离优先候选更多的格子（规划稿 §6.1 回退口径）。
                take = entry.Count > bestCount;
            }
            else
            {
                take = kv.Key.CompareTo(bestKey) < 0;
            }
            if (take)
            {
                best = entry.Result;
                delta = entry.Delta;
                bestCount = entry.Count;
                bestKey = kv.Key;
            }
        }
        if (best != null)
        {
            miss = delta > TargetTolerance;
        }
        return best;
    }

    private CellKey KeyOf(GenerationResult result)
    {
        int size = Math.Max(result.Spec.Width, result.Spec.Height);
        int sizeBucket = Math.Clamp((size - 10) / 2, 0, 5);
        int stepsBucket = Math.Clamp(result.Solution.Steps / 5, 0, 8);
        int slackBucket = Math.Clamp((int)Metric(result, "min_slack"), 0, 3);
        int mechBucket = Math.Clamp((int)(Metric(result, "mech_pressure") * 4.0), 0, 3);
        return new CellKey(sizeBucket, stepsBucket, slackBucket, mechBucket);
    }

    private static double Metric(GenerationResult result, string key)
    {
        return result.Metrics != null && result.Metrics.TryGetValue(key, out double value) ? value : 0.0;
    }

    private static bool Better(int delta, int quality, int otherDelta, int otherQuality)
    {
        if (delta != otherDelta)
        {
            return delta < otherDelta;
        }
        return quality > otherQuality;
    }

    private readonly struct CellKey : IEquatable<CellKey>, IComparable<CellKey>
    {
        private readonly int _size;
        private readonly int _steps;
        private readonly int _slack;
        private readonly int _mech;

        public CellKey(int size, int steps, int slack, int mech)
        {
            _size = size;
            _steps = steps;
            _slack = slack;
            _mech = mech;
        }

        public bool Equals(CellKey other)
        {
            return _size == other._size && _steps == other._steps
                && _slack == other._slack && _mech == other._mech;
        }

        public override bool Equals(object obj)
        {
            return obj is CellKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(_size, _steps, _slack, _mech);
        }

        public int CompareTo(CellKey other)
        {
            int cmp = _size.CompareTo(other._size);
            if (cmp != 0)
            {
                return cmp;
            }
            cmp = _steps.CompareTo(other._steps);
            if (cmp != 0)
            {
                return cmp;
            }
            cmp = _slack.CompareTo(other._slack);
            return cmp != 0 ? cmp : _mech.CompareTo(other._mech);
        }
    }
}
