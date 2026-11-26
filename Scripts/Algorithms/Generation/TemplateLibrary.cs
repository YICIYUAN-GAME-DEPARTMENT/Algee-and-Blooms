using System;
using System.Collections.Generic;
using Growth.Algorithms.Model;

namespace Growth.Algorithms.Generation;

/// <summary>S/E 槽位风格（P3 选对时使用）。</summary>
public enum SeStyle
{
    /// <summary>出生点与终点尽量远（空场 / 顺路线）。</summary>
    FarApart,
    /// <summary>两端在较长走廊两侧。</summary>
    CorridorEnds,
    /// <summary>终点靠近机关场核心，出生点在外围。</summary>
    CoreVsOuter,
}

/// <summary>机关初始阶段策略。</summary>
public enum LevelStrategy
{
    /// <summary>全部从阶段 0 开始（第一轮生长才扩张）。</summary>
    AllZero,
    /// <summary>按索引错开，形成先后满级。</summary>
    Staggered,
    /// <summary>接近满级，开场即压。</summary>
    NearCap,
}

/// <summary>模板定义（生成算法 v3 §3.2.2 / §3.2.3）。</summary>
public sealed class TemplateDef
{
    public string Id = "";
    public string Name = "";
    public string Summary = "";
    /// <summary>适用档位；空数组表示全档位。</summary>
    public string[] Tiers = Array.Empty<string>();
    /// <summary>机关中心相对偏移（首个为锚点 0,0；会按 4 向旋转）。</summary>
    public Cell[] Pattern = Array.Empty<Cell>();
    public SeStyle SeStyle = SeStyle.FarApart;
    public LevelStrategy LevelStrategy = LevelStrategy.AllZero;
    /// <summary>允许的起始负阶段下限（0 = 不从负值起步；负值只改颜色，不扩张）。</summary>
    public int MinInitial = 0;
    public int L0Hint = 6;
    public int GainHint = 4;
    public int IntervalHint = 6;
    public int CapHint = 2;
    /// <summary>0 = 无花瓣模板（靠初始身长）；&gt;0 时花瓣数量由 L0 / Gain 与路线长度推导。</summary>
    public int MaxFoods = 2;
    /// <summary>模板先验（0–1），用于候选排序的小权重。</summary>
    public double Prior = 0.6;

    public bool FitsTier(string tier)
    {
        if (Tiers.Length == 0)
        {
            return true;
        }
        foreach (string t in Tiers)
        {
            if (t == tier)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>一次模板实例化结果（机关场 + 参数 + 花瓣预算）。</summary>
public sealed class TemplateInstance
{
    public string TemplateId = "";
    public List<Cell> Centers = new();
    public List<int> InitialLevels = new();
    public int InitialMaxLen;
    public int FoodLenGain;
    public int GrowthInterval;
    public int MechanismCap;
    /// <summary>参数侧估算的花瓣预算（PairSelector 会按实际路线重算必修 + 诱饵）。</summary>
    public int FoodBudget;
    /// <summary>是否允许花瓣；false = 无花瓣模板（初始身长覆盖路线）。</summary>
    public bool AllowsFoods;
    public double Prior;
    public SeStyle SeStyle;
}

/// <summary>
/// 模板库（T0–T8）：按档位采样模板，并在给定地形上实例化机关场。
/// 只做机关优先的场地 + 参数生成，不选择 S/E（交给 PairSelector），不复制规则。
/// </summary>
public static class TemplateLibrary
{
    public static readonly IReadOnlyList<TemplateDef> All = new[]
    {
        new TemplateDef
        {
            Id = "T0", Name = "空场直达", Summary = "无机关无花瓣，纯走廊",
            Tiers = new[] { "easy" }, Pattern = Array.Empty<Cell>(),
            L0Hint = 12, GainHint = 2, IntervalHint = 6, CapHint = 1, MaxFoods = 0, Prior = 0.5,
        },
        new TemplateDef
        {
            Id = "T1", Name = "花瓣资源链", Summary = "身长不足，沿路吃花瓣续命",
            Tiers = new[] { "easy", "normal" }, Pattern = Array.Empty<Cell>(),
            L0Hint = 3, GainHint = 4, IntervalHint = 8, CapHint = 1, MaxFoods = 4, Prior = 0.6,
        },
        new TemplateDef
        {
            Id = "T2", Name = "静态障碍绕行", Summary = "障碍封直路，绕行段放花瓣",
            Tiers = new[] { "normal", "hard" }, Pattern = Array.Empty<Cell>(),
            L0Hint = 6, GainHint = 3, IntervalHint = 8, CapHint = 1, MaxFoods = 2, Prior = 0.6,
        },
        new TemplateDef
        {
            Id = "T3", Name = "单机关生长闸门", Summary = "1 个机关靠近路线，短周期压时间",
            Tiers = new[] { "easy", "normal", "hard" },
            Pattern = new[] { new Cell(0, 0) }, MinInitial = -3,
            L0Hint = 9, GainHint = 6, IntervalHint = 3, CapHint = 4, MaxFoods = 2, Prior = 0.8,
        },
        new TemplateDef
        {
            Id = "T4", Name = "多机关顺序闸门", Summary = "2 个机关初始阶段错开，先后满级",
            Tiers = new[] { "normal", "hard", "expert" },
            Pattern = new[] { new Cell(0, 0), new Cell(3, 1) },
            SeStyle = SeStyle.CoreVsOuter, LevelStrategy = LevelStrategy.Staggered, MinInitial = -5,
            L0Hint = 7, GainHint = 4, IntervalHint = 6, CapHint = 4, MaxFoods = 3, Prior = 0.9,
        },
        new TemplateDef
        {
            Id = "T5", Name = "嵌套闸门", Summary = "2–3 个机关围绕终点，由外到内依次满级",
            Tiers = new[] { "hard", "expert" },
            Pattern = new[] { new Cell(0, 0), new Cell(1, 3), new Cell(3, 0) },
            SeStyle = SeStyle.CoreVsOuter, LevelStrategy = LevelStrategy.NearCap, MinInitial = -6,
            L0Hint = 6, GainHint = 4, IntervalHint = 5, CapHint = 4, MaxFoods = 3, Prior = 1.0,
        },
        new TemplateDef
        {
            Id = "T6", Name = "假捷径", Summary = "机关封住静态最短路中段，逼出真绕行",
            Tiers = new[] { "hard", "expert" },
            Pattern = new[] { new Cell(0, 0), new Cell(3, 2) }, MinInitial = -4,
            L0Hint = 8, GainHint = 5, IntervalHint = 6, CapHint = 3, MaxFoods = 2, Prior = 0.9,
        },
        new TemplateDef
        {
            Id = "T7", Name = "收口袋", Summary = "2 个机关从背后合拢，窄口在终点前",
            Tiers = new[] { "hard", "expert" },
            Pattern = new[] { new Cell(0, 0), new Cell(3, -1) },
            SeStyle = SeStyle.CoreVsOuter, LevelStrategy = LevelStrategy.Staggered, MinInitial = -5,
            L0Hint = 7, GainHint = 4, IntervalHint = 5, CapHint = 4, MaxFoods = 3, Prior = 0.9,
        },
        new TemplateDef
        {
            Id = "T8", Name = "身体屏蔽走廊", Summary = "机关在单格走廊侧后方，靠身体挡花",
            Tiers = new[] { "normal", "hard", "expert" },
            Pattern = new[] { new Cell(0, 0) }, MinInitial = -4,
            L0Hint = 9, GainHint = 5, IntervalHint = 4, CapHint = 3, MaxFoods = 2, Prior = 0.85,
        },
    };

    public static TemplateDef ById(string id)
    {
        foreach (TemplateDef def in All)
        {
            if (def.Id == id || def.Name == id)
            {
                return def;
            }
        }
        return null;
    }

    /// <summary>按档位加权采样模板（权重 = Prior）；无匹配时回退全表。</summary>
    public static TemplateDef Sample(DeterministicRng rng, string tier)
    {
        var pool = new List<TemplateDef>();
        foreach (TemplateDef def in All)
        {
            if (def.FitsTier(tier))
            {
                pool.Add(def);
            }
        }
        if (pool.Count == 0)
        {
            pool.AddRange(All);
        }
        double total = 0;
        foreach (TemplateDef def in pool)
        {
            total += Math.Max(def.Prior, 0.01);
        }
        double roll = rng.NextDouble() * total;
        foreach (TemplateDef def in pool)
        {
            roll -= Math.Max(def.Prior, 0.01);
            if (roll <= 0)
            {
                return def;
            }
        }
        return pool[^1];
    }

    /// <summary>在给定可走格上实例化模板；失败返回 null。</summary>
    public static TemplateInstance Instantiate(TemplateDef def, DeterministicRng rng,
        int targetSteps, IReadOnlyList<Cell> walkable, string tier = "normal")
    {
        if (def == null || walkable == null || walkable.Count == 0 || targetSteps <= 0)
        {
            return null;
        }
        (int l0, int gain, int interval, int cap, int foods) = DeriveParams(def, rng, targetSteps, tier);
        if (l0 <= 0)
        {
            return null;
        }
        List<Cell> centers = PlaceCenters(def, rng, walkable);
        if (centers == null)
        {
            return null;
        }
        // 负起始阶段要能在路线中段前"苏醒"：最多消耗约 2/3 的生长次数。
        int growths = Math.Max(1, targetSteps / Math.Max(interval, 2));
        int maxDormant = Math.Max(0, growths * 2 / 3);
        int floorLevel = Math.Clamp(Math.Max(def.MinInitial, -maxDormant),
            MechanicShapes.MinLevel, 0);
        return new TemplateInstance
        {
            TemplateId = def.Id,
            Centers = centers,
            InitialLevels = LevelsFor(def, cap, centers.Count, floorLevel),
            InitialMaxLen = l0,
            FoodLenGain = gain,
            GrowthInterval = interval,
            MechanismCap = cap,
            FoodBudget = foods,
            AllowsFoods = def.MaxFoods > 0,
            Prior = def.Prior,
            SeStyle = def.SeStyle,
        };
    }

    /// <summary>
    /// 参数推导（结构优先）：L0 与花瓣效益同量级，不再为了塞进少量花瓣而抬高 L0。
    /// 在可行域内搜索 L0 / Gain / I / cap 组合；花瓣预算 = 必修花瓣 + 诱饵。
    /// </summary>
    private static (int L0, int Gain, int Interval, int Cap, int Foods) DeriveParams(
        TemplateDef def, DeterministicRng rng, int targetSteps, string tier)
    {
        int hintInterval = Math.Max(2, def.IntervalHint + rng.NextInt(2));

        if (def.MaxFoods <= 0)
        {
            // 无花瓣模板（仅 easy）：初始身长覆盖目标步数。
            int interval = Math.Clamp(hintInterval, 3, 7);
            int cap = Math.Clamp(targetSteps / interval - 1, 1, Math.Max(def.CapHint, 1));
            return (targetSteps + 3, Math.Max(2, def.GainHint), interval, cap, 0);
        }

        bool easy = tier == "easy";
        int l0Min = easy ? 5 : 3;
        int l0Max = easy ? 8 : 5;
        int gainMin = 3;
        int gainMax = easy ? 4 : 5;

        // 收集可行组合：L0 与 Gain 同量级，间隔围绕模板提示微调。
        var feasible = new List<(int L0, int Gain, int Interval, int Cap)>();
        for (int l0 = l0Min; l0 <= l0Max; l0++)
        {
            for (int gain = gainMin; gain <= gainMax; gain++)
            {
                if (Math.Abs(l0 - gain) > 2)
                {
                    continue;
                }
                for (int delta = -2; delta <= 2; delta++)
                {
                    int intervalMax = Math.Max(3, Math.Min(7, targetSteps / 2));
                    int interval = Math.Clamp(hintInterval + delta, 3, intervalMax);
                    int cap = Math.Clamp(targetSteps / interval - 1, 1, Math.Max(def.CapHint, 1));
                    if (ParamSampler.Feasible(l0, gain, interval, cap, targetSteps, 0))
                    {
                        feasible.Add((l0, gain, interval, cap));
                    }
                }
            }
        }
        if (feasible.Count == 0)
        {
            return (0, 0, 0, 0, 0);
        }

        (int L0, int Gain, int Interval, int Cap) pick = feasible[rng.NextInt(feasible.Count)];
        int required = (int)Math.Ceiling((targetSteps + 2 - pick.L0) / (double)pick.Gain);
        required = Math.Max(required, 1);
        // 必修花瓣之外固定留 2 片路线外诱饵，由 PairSelector 放置。
        return (pick.L0, pick.Gain, pick.Interval, pick.Cap, required + 2);
    }

    /// <summary>按 4 向旋转在可走格上放置机关中心；间距 ≥3（切比雪夫）。</summary>
    private static List<Cell> PlaceCenters(TemplateDef def, DeterministicRng rng, IReadOnlyList<Cell> walkable)
    {
        if (def.Pattern.Length == 0)
        {
            return new List<Cell>();
        }
        var walkableSet = new HashSet<Cell>(walkable);
        var anchors = new List<Cell>(walkable);
        anchors.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
        rng.Shuffle(anchors);
        int rotation = rng.NextInt(4);
        int tries = Math.Min(anchors.Count, 64);
        for (int t = 0; t < tries; t++)
        {
            Cell anchor = anchors[t];
            var placed = new List<Cell>(def.Pattern.Length);
            bool ok = true;
            foreach (Cell offset in def.Pattern)
            {
                Cell c = anchor + Rotate(offset, rotation);
                if (!walkableSet.Contains(c))
                {
                    ok = false;
                    break;
                }
                foreach (Cell prev in placed)
                {
                    if (Chebyshev(prev, c) < 3)
                    {
                        ok = false;
                        break;
                    }
                }
                if (!ok)
                {
                    break;
                }
                placed.Add(c);
            }
            if (ok)
            {
                return placed;
            }
        }
        return null;
    }

    /// <summary>
    /// 初始阶段分配：负值 = 尚未激活（只染蓝、不扩张），0 级以上按策略错开。
    /// floorLevel 保证在路线中段之前能苏醒。
    /// </summary>
    private static List<int> LevelsFor(TemplateDef def, int cap, int count, int floorLevel)
    {
        var levels = new List<int>(count);
        int ceiling = Math.Max(cap - 1, 0);
        for (int i = 0; i < count; i++)
        {
            int level;
            switch (def.LevelStrategy)
            {
                case LevelStrategy.Staggered:
                    level = count <= 1
                        ? floorLevel
                        : floorLevel + (int)Math.Round((ceiling - floorLevel) * (i / (double)(count - 1)));
                    break;
                case LevelStrategy.NearCap:
                    level = Math.Max(ceiling - i, floorLevel);
                    break;
                default:
                    level = floorLevel;
                    break;
            }
            levels.Add(Math.Clamp(level, MechanicShapes.MinLevel, ceiling));
        }
        return levels;
    }

    private static Cell Rotate(Cell offset, int rotation)
    {
        return rotation switch
        {
            1 => new Cell(-offset.Y, offset.X),
            2 => new Cell(-offset.X, -offset.Y),
            3 => new Cell(offset.Y, -offset.X),
            _ => offset,
        };
    }

    private static int Chebyshev(Cell a, Cell b)
    {
        return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }
}
