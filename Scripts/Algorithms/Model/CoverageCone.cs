using System;
using System.Collections.Generic;

namespace Growth.Algorithms.Model;

/// <summary>
/// 单个机关中心 + 初始阶段的覆盖锥（生成算法 v3 §3.3-P1）。
/// 记录每个格首次被该机关覆盖时的生长次数（stage），口径与 MechanicShapes.CoverLevel 一致。
/// 纯数据；不修改 LevelSpec，不访问节点 / 场景 / 信号，不复制规则。
/// </summary>
public sealed class CoverageCone
{
    /// <summary>永不覆盖。</summary>
    public const int Never = int.MaxValue;

    public Cell Center { get; }
    /// <summary>机关初始阶段（已按 cap 钳制）。</summary>
    public int InitialLevel { get; }
    /// <summary>机关阶段上限（已按 MechanicShapes.MaxLevel 钳制）。</summary>
    public int Cap { get; }

    private readonly Dictionary<Cell, int> _stage = new();

    /// <summary>格 -> 首次覆盖所需生长次数（0 = 开局即覆盖）。</summary>
    public IReadOnlyDictionary<Cell, int> Stages => _stage;

    public CoverageCone(Cell center, int initialLevel, int cap)
    {
        Center = center;
        Cap = Math.Clamp(cap, 0, MechanicShapes.MaxLevel);
        InitialLevel = Math.Clamp(initialLevel, MechanicShapes.MinLevel, Cap);
        Build();
    }

    /// <summary>覆盖所需生长次数；超出 cap 的格返回 <see cref="Never"/>。</summary>
    public int StageAt(Cell c)
    {
        return _stage.TryGetValue(c, out int stage) ? stage : Never;
    }

    /// <summary>首次被覆盖的时刻（已走步数）：stage × 生长周期；Never 表示不覆盖。</summary>
    public int TimeAt(Cell c, int growthInterval)
    {
        int stage = StageAt(c);
        if (stage == Never)
        {
            return Never;
        }
        return stage * Math.Max(1, growthInterval);
    }

    /// <summary>多机关并集：同一格取最小 stage（先覆盖者优先）。确定性，与迭代顺序无关。</summary>
    public static Dictionary<Cell, int> Union(IEnumerable<CoverageCone> cones)
    {
        var merged = new Dictionary<Cell, int>();
        if (cones == null)
        {
            return merged;
        }
        foreach (CoverageCone cone in cones)
        {
            foreach (KeyValuePair<Cell, int> kv in cone._stage)
            {
                if (!merged.TryGetValue(kv.Key, out int existing) || kv.Value < existing)
                {
                    merged[kv.Key] = kv.Value;
                }
            }
        }
        return merged;
    }

    private void Build()
    {
        (int _, int radius) = MechanicShapes.Radius(Cap);
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                var cell = new Cell(Center.X + dx, Center.Y + dy);
                int level = MechanicShapes.CoverLevel(cell - Center);
                if (level > Cap)
                {
                    continue;
                }
                _stage[cell] = Math.Max(0, level - InitialLevel);
            }
        }
    }
}
