using System;
using System.Collections.Generic;
using Godot;
using Growth.Algorithms.Scoring;

namespace Growth.Algorithms.Generation;

/// <summary>生成配置（运行时按 seed 构建关卡时的输入）。</summary>
public sealed class GeneratorConfig
{
    public long Seed { get; set; }
    /// <summary>0 = 按 tier 从尺寸阶梯采样；>0 时固定尺寸（测试 / 工具用）。</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>0 = 按难度带采样目标步数。</summary>
    public int TargetSteps { get; set; }
    /// <summary>-1 = 按难度带采样机关数量。</summary>
    public int MechanismCount { get; set; } = -1;
    public int MaxAttempts { get; set; } = 48;
    /// <summary>每关生成多少个候选，再按目标难度分选。</summary>
    public int Candidates { get; set; } = 4;
    public string Difficulty { get; set; } = "normal";
}

/// <summary>生成结果（成功时携带完整 LevelSpec、规范路线与评分）。</summary>
public sealed class GenerationResult
{
    public bool Success { get; set; }
    public LevelSpec Spec { get; set; }
    public LevelSolution Solution { get; set; }
    public LevelScore Score { get; set; }
    public Dictionary<string, double> Metrics { get; set; } = new();
    public string Reason { get; set; } = "";
    public int Attempts { get; set; }
}

/// <summary>
/// 地形优先 + 随机规则行走的种子生成器：
///   1. 按 tier 采样尺寸（10×10–20×20）与每关参数（L0 / Gain / I / cap / initial_level）；
///   2. 噪波 + 细胞自动机生成洞窟地形，取最大连通分量；
///   3. 在真实 RuleState 上随机行走（相位排花），终点 = 行走终点；
///   4. 机关蓝噪声撒点 + 时间窗验证；诱饵只在假捷径候选中放；
///   5. 用同一 RuleState 重放验证，计算评分 v2；
///   6. 生成多个候选，按难度带目标分选。
/// </summary>
public partial class RouteFirstGenerator : RefCounted
{
    private static readonly Vector2I[] Directions =
    {
        new(-1, 0), new(1, 0), new(0, -1), new(0, 1),
    };

    private static readonly char[] DirChars = { 'A', 'D', 'W', 'S' };

    private readonly struct Band
    {
        public readonly int MinSteps;
        public readonly int MaxSteps;
        public readonly int InitialMaxLen;
        public readonly int GrowthInterval;
        public readonly int MechCap;
        public readonly int MinMechs;
        public readonly int MaxMechs;
        public readonly int TargetScore;

        public Band(int minSteps, int maxSteps, int initialMaxLen, int growthInterval, int mechCap,
            int minMechs, int maxMechs, int targetScore)
        {
            MinSteps = minSteps;
            MaxSteps = maxSteps;
            InitialMaxLen = initialMaxLen;
            GrowthInterval = growthInterval;
            MechCap = mechCap;
            MinMechs = minMechs;
            MaxMechs = maxMechs;
            TargetScore = targetScore;
        }
    }

    private static Band GetBand(string difficulty)
    {
        return difficulty switch
        {
            "easy" => new Band(8, 12, 5, 7, 1, 1, 1, 55),
            "normal" => new Band(12, 18, 4, 6, 2, 1, 2, 66),
            "hard" => new Band(18, 26, 3, 5, 2, 2, 3, 76),
            "expert" => new Band(26, 38, 4, 5, 4, 2, 3, 120),
            _ => new Band(12, 18, 4, 6, 2, 1, 2, 66),
        };
    }

    /// <summary>窄 API：纯 C# 生成（运行时 / 工具入口）。</summary>
    public static GenerationResult GenerateSpec(long seed, string tier)
    {
        return GenerateCore(new GeneratorConfig { Seed = seed, Difficulty = tier });
    }

    internal static GenerationResult GenerateCore(GeneratorConfig cfg)
    {
        Band band = GetBand(cfg.Difficulty);
        int candidates = Math.Max(cfg.Candidates, 1);
        int attemptsPerCandidate = Math.Max(cfg.MaxAttempts, 1);
        GenerationResult best = null;
        double bestDelta = double.MaxValue;
        int totalAttempts = 0;
        string lastReason = "";

        for (int cand = 0; cand < candidates; cand++)
        {
            for (int attempt = 0; attempt < attemptsPerCandidate; attempt++)
            {
                totalAttempts++;
                var rng = new DeterministicRng(
                    (ulong)cfg.Seed * 0x9E3779B97F4A7C15UL
                    + (ulong)cand * 0x94D049BB133111EBUL
                    + (ulong)attempt * 0xBF58476D1CE4E5B9UL
                    + 1UL);

                int targetSteps = cfg.TargetSteps > 0
                    ? cfg.TargetSteps
                    : band.MinSteps + rng.NextInt(band.MaxSteps - band.MinSteps + 1);
                int mechCount = cfg.MechanismCount >= 0
                    ? cfg.MechanismCount
                    : band.MinMechs + rng.NextInt(band.MaxMechs - band.MinMechs + 1);

                (int width, int height) = cfg.Width > 0 && cfg.Height > 0
                    ? (cfg.Width, cfg.Height)
                    : SizeLadder.Choose(rng, cfg.Difficulty);
                ParamSet param = ParamSampler.Sample(rng, targetSteps, mechCount);
                if (param == null)
                {
                    lastReason = "参数组合不可行";
                    continue;
                }

                GenerationResult candidate = TryBuild(cfg, band, rng, targetSteps, mechCount,
                    width, height, param);
                if (!candidate.Success)
                {
                    lastReason = candidate.Reason;
                    continue;
                }

                double delta = Math.Abs(candidate.Score.Difficulty - band.TargetScore);
                if (best == null || delta < bestDelta)
                {
                    best = candidate;
                    bestDelta = delta;
                }
                break; // 该候选成功，继续下一个候选
            }
        }

        if (best == null)
        {
            return new GenerationResult
            {
                Success = false,
                Reason = string.IsNullOrEmpty(lastReason) ? "多次尝试未找到可验证关卡" : lastReason,
                Attempts = totalAttempts,
            };
        }
        best.Attempts = totalAttempts;
        return best;
    }

    private static GenerationResult TryBuild(GeneratorConfig cfg, Band band, DeterministicRng rng,
        int targetSteps, int mechCount, int width, int height, ParamSet param)
    {
        List<Cell> floor = BuildTerrain(rng, width, height, targetSteps + 8 + width);
        if (floor == null)
        {
            return Fail("地形连通分量过小");
        }
        if (targetSteps > 0.9 * floor.Count)
        {
            return Fail("地板不足以承载路线");
        }

        Cell spawn = floor[rng.NextInt(floor.Count)];

        var spec = new LevelSpec
        {
            Seed = cfg.Seed,
            Name = cfg.Difficulty,
            Tier = cfg.Difficulty,
            Origin = Cell.Zero,
            Width = width,
            Height = height,
            Floor = floor,
            Spawn = spawn,
            Goal = new Cell(-1, -1), // 行走阶段占位，完成后替换为终点
            InitialMaxLen = param.InitialMaxLen,
            FoodLenGain = param.FoodLenGain,
            GrowthInterval = param.GrowthInterval,
            MechanismCap = param.MechanismCap,
            TargetScore = band.TargetScore,
            ParamSignature = param.Signature,
        };

        var requiredFoods = new List<Cell>();
        var walked = new HashSet<Cell> { spawn };
        var trailOrder = new List<Cell>();
        if (!RandomWalk(spec, rng, targetSteps, requiredFoods, walked, out List<char> dirs,
                out Cell head, trailOrder, param.RequiredFoods + 2))
        {
            return Fail("随机行走失败");
        }

        spec.Goal = head;
        string route = new(dirs.ToArray());

        // 路线确定后放机关：蓝噪声撒点 + 时间窗验证；极端地形放不下时允许 0 机关。
        int placed = PlaceMechanisms(spec, rng, walked, trailOrder, mechCount, param.InitialLevels);
        if (placed == 0 && mechCount > 0)
        {
            // 保留关卡可玩性：无机关仍可作为低难度样本，不因放置失败丢弃整张图。
            spec.MechCenters.Clear();
            spec.MechInitialLevels.Clear();
        }

        AddDecoyFoods(rng, spec, walked, route.Length, 0, 2);

        if (!ReplayVerify(spec, route))
        {
            return Fail("路线重放失败");
        }

        var result = new GenerationResult
        {
            Success = true,
            Spec = spec,
            Solution = new LevelSolution
            {
                Status = "SOLVABLE",
                Steps = route.Length,
                Route = route,
            },
        };
        result.Score = ComputeScore(spec, route, result.Metrics, BuildScoreOptions(Math.Max(width, height)));
        return result;
    }

    // ── 地形：噪波 + 细胞自动机 + 最大连通分量 ─────────────────
    private static List<Cell> BuildTerrain(DeterministicRng rng, int width, int height, int minFloor)
    {
        // 由密到疏尝试：先保留较封闭的洞窟；连通分量不够大时提高初始开放率。
        // 长路线（minFloor 大）直接提高初始开放率，减少重试。
        int[] fills = minFloor >= 24 ? new[] { 60, 64, 68, 72 } : new[] { 52, 56, 60, 64 };
        foreach (int fill in fills)
        {
            var open = new bool[width, height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    open[x, y] = rng.NextInt(100) < fill;
                }
            }

            int iterations = fill <= 52 ? 4 : 3;
            for (int iter = 0; iter < iterations; iter++)
            {
                var next = new bool[width, height];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int count = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0)
                                {
                                    continue;
                                }
                                int nx = x + dx;
                                int ny = y + dy;
                                if (nx >= 0 && ny >= 0 && nx < width && ny < height && open[nx, ny])
                                {
                                    count++;
                                }
                            }
                        }
                        next[x, y] = count >= 5;
                    }
                }
                open = next;
            }

            List<Cell> component = LargestComponent(open, width, height);
            if (component != null && component.Count >= minFloor)
            {
                component.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
                return component;
            }
        }
        return null;
    }

    private static List<Cell> LargestComponent(bool[,] open, int width, int height)
    {
        var visited = new bool[width, height];
        List<Cell> best = null;
        var queue = new Queue<Cell>();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!open[x, y] || visited[x, y])
                {
                    continue;
                }
                var component = new List<Cell>();
                queue.Clear();
                queue.Enqueue(new Cell(x, y));
                visited[x, y] = true;
                while (queue.Count > 0)
                {
                    Cell cur = queue.Dequeue();
                    component.Add(cur);
                    foreach (Cell n in GridModel.Neighbors(cur))
                    {
                        if (n.X < 0 || n.Y < 0 || n.X >= width || n.Y >= height
                            || visited[n.X, n.Y] || !open[n.X, n.Y])
                        {
                            continue;
                        }
                        visited[n.X, n.Y] = true;
                        queue.Enqueue(n);
                    }
                }
                if (best == null || component.Count > best.Count)
                {
                    best = component;
                }
            }
        }
        return best;
    }

    // ── 机关：蓝噪声撒点 + 时间窗验证（不做逐候选整条重放）────────────
    private static int PlaceMechanisms(LevelSpec spec, DeterministicRng rng, HashSet<Cell> walked,
        IReadOnlyList<Cell> trailOrder, int count, IReadOnlyList<int> initialLevels)
    {
        if (count <= 0)
        {
            return 0;
        }
        var pool = new List<(Cell Cell, int Step)>();
        foreach (Cell c in spec.Floor)
        {
            if (walked.Contains(c) || c.Equals(spec.Spawn) || c.Equals(spec.Goal))
            {
                continue;
            }
            int nearestStep = int.MaxValue;
            for (int t = 0; t < trailOrder.Count; t++)
            {
                if (Chebyshev(c, trailOrder[t]) <= 2)
                {
                    nearestStep = t;
                    break;
                }
            }
            if (nearestStep != int.MaxValue)
            {
                pool.Add((c, nearestStep));
            }
        }
        // 优先路线前段：机关越早出现，时间窗越容易满足，也形成"从起点开始的压力"。
        pool.Sort((a, b) => a.Step.CompareTo(b.Step));
        for (int i = 0; i < pool.Count;)
        {
            int j = i;
            while (j < pool.Count && pool[j].Step == pool[i].Step)
            {
                j++;
            }
            for (int k = j - 1; k > i; k--)
            {
                int r = i + rng.NextInt(k - i + 1);
                (pool[k], pool[r]) = (pool[r], pool[k]);
            }
            i = j;
        }

        var state = new RuleState();
        int placed = 0;
        foreach ((Cell c, int _) in pool)
        {
            if (placed >= count)
            {
                break;
            }
            bool tooClose = false;
            foreach (Cell chosen in spec.MechCenters)
            {
                if (Chebyshev(chosen, c) < 3)
                {
                    tooClose = true;
                    break;
                }
            }
            if (tooClose)
            {
                continue;
            }

            int initial = placed < initialLevels.Count ? initialLevels[placed] : 0;
            initial = Math.Clamp(initial, 0, Math.Max(spec.MechanismCap - 1, 0));
            spec.MechCenters.Add(c);
            spec.MechInitialLevels.Add(initial);
            state.ResetToSpec(spec);
            if (ValidateMechanismWindows(spec, state, trailOrder))
            {
                placed++;
            }
            else
            {
                spec.MechCenters.RemoveAt(spec.MechCenters.Count - 1);
                spec.MechInitialLevels.RemoveAt(spec.MechInitialLevels.Count - 1);
            }
        }
        return placed;
    }

    /// <summary>
    /// 时间窗验证：机关覆盖路线格的时刻必须晚于（或等于）玩家经过该格的步序。
    /// 覆盖时刻 = coverStep × 生长周期；玩家第 t 步进入 trailOrder[t]。
    /// </summary>
    private static bool ValidateMechanismWindows(LevelSpec spec, RuleState state,
        IReadOnlyList<Cell> trailOrder)
    {
        int interval = Math.Max(spec.GrowthInterval, 1);
        for (int step = 1; step < trailOrder.Count; step++)
        {
            Cell c = trailOrder[step];
            if (!state.CoverSteps.TryGetValue(c, out int coverStep))
            {
                continue;
            }
            if (coverStep * interval < step)
            {
                return false;
            }
        }
        return true;
    }

    // ── 随机规则行走：相位排花，不做"濒死回填 + 整条重放" ─────────────
    private static bool RandomWalk(LevelSpec spec, DeterministicRng rng, int targetSteps,
        List<Cell> requiredFoods, HashSet<Cell> walked, out List<char> dirs, out Cell head,
        List<Cell> trailOrder, int foodBudget)
    {
        dirs = new List<char>(targetSteps);
        trailOrder.Clear();
        trailOrder.Add(spec.Spawn);
        head = spec.Spawn;

        var st = new RuleState { SuppressDeadlockChecks = true };
        st.Setup(spec);
        st.Start();

        int inserted = 0;

        for (int step = 0; step < targetSteps; step++)
        {
            int pick = ChooseDirection(st, rng);
            if (pick < 0)
            {
                // 没有合法方向：地形堵死，换下一张候选图。
                return false;
            }

            Cell next = new(st.Head.X + Directions[pick].X, st.Head.Y + Directions[pick].Y);
            RuleState.MoveKind kind = st.ClassifyMove(next);
            if (kind == RuleState.MoveKind.Walk
                && st.GetRemainingSteps() <= 1
                && inserted < foodBudget
                && !spec.Foods.Contains(next)
                && !next.Equals(spec.Goal)
                && !next.Equals(spec.Spawn))
            {
                // 相位排花：把花瓣放在下一步，走进去立即吃；直接生效，不重放。
                spec.Foods.Add(next);
                requiredFoods.Add(next);
                st.AddFood(next);
                inserted++;
            }

            RuleState.StepOutcome outcome = st.StepCore(
                new Cell(Directions[pick].X, Directions[pick].Y));
            if (!outcome.Accepted)
            {
                return false;
            }
            dirs.Add(DirChars[pick]);
            walked.Add(st.Head);
            trailOrder.Add(st.Head);

            if (st.GetRemainingSteps() <= 0 && kind != RuleState.MoveKind.Body)
            {
                // 食物预算不足，继续必饥饿。
                return false;
            }
        }

        head = st.Head;
        return true;
    }

    /// <summary>方向选择：Walk 优先，15% 概率故意截断制造回头路线；无路可走返回 -1。</summary>
    private static int ChooseDirection(RuleState st, DeterministicRng rng)
    {
        Span<int> walkOptions = stackalloc int[4];
        Span<int> bodyOptions = stackalloc int[4];
        int walkCount = 0;
        int bodyCount = 0;
        for (int i = 0; i < Directions.Length; i++)
        {
            Cell next = new(st.Head.X + Directions[i].X, st.Head.Y + Directions[i].Y);
            RuleState.MoveKind kind = st.ClassifyMove(next);
            if (kind == RuleState.MoveKind.Walk)
            {
                walkOptions[walkCount++] = i;
            }
            else if (kind == RuleState.MoveKind.Body)
            {
                bodyOptions[bodyCount++] = i;
            }
        }
        if (walkCount > 0)
        {
            if (bodyCount > 0 && rng.NextInt(100) < 15)
            {
                return bodyOptions[rng.NextInt(bodyCount)];
            }
            return walkOptions[rng.NextInt(walkCount)];
        }
        return bodyCount > 0 ? bodyOptions[rng.NextInt(bodyCount)] : -1;
    }

    /// <summary>
    /// 诱饵：只在「看起来更短」的假捷径候选里放（dist(S,d)+dist(d,E) ≤ S-2），
    /// 且不与规范路线重叠（不会被吃到）。精确真假由评分期的 M14 复核。
    /// </summary>
    private static void AddDecoyFoods(DeterministicRng rng, LevelSpec spec, HashSet<Cell> walked,
        int steps, int min, int max)
    {
        int count = min + rng.NextInt(max - min + 1);
        if (count <= 0)
        {
            return;
        }
        var grid = new GridModel(spec.Origin, spec.Width, spec.Height);
        foreach (Cell c in spec.Floor)
        {
            grid.Floor.Add(c);
        }
        foreach (Cell c in spec.Obstacles)
        {
            grid.Obstacles.Add(c);
        }
        var empty = new HashSet<Cell>();
        Dictionary<Cell, int> distS = grid.BfsDistMap(spec.Spawn, empty);
        Dictionary<Cell, int> distG = grid.BfsDistMap(spec.Goal, empty);

        var existing = new HashSet<Cell>(spec.Foods) { spec.Goal, spec.Spawn };
        var pool = new List<Cell>();
        foreach (Cell c in spec.Floor)
        {
            if (walked.Contains(c) || existing.Contains(c))
            {
                continue;
            }
            if (!distS.TryGetValue(c, out int ds) || !distG.TryGetValue(c, out int dg))
            {
                continue;
            }
            // 假捷径候选：看起来能省 2 步以上，但不在规范路线上。
            if (ds + dg <= steps - 2)
            {
                pool.Add(c);
            }
        }
        rng.Shuffle(pool);
        for (int i = 0; i < pool.Count && i < count; i++)
        {
            spec.Foods.Add(pool[i]);
        }
    }

    // ── 验证与评分 ──────────────────────────────────────────
    /// <summary>用同一套 RuleState 重放路线：必须全部 accepted、途中无死局、最终到 E。</summary>
    private static bool ReplayVerify(LevelSpec spec, string route)
    {
        var st = new RuleState { SuppressPathCheck = true };
        st.Setup(spec);
        st.Start();
        foreach (char ch in route)
        {
            RuleState.StepOutcome outcome = st.StepCore(DirCellForChar(ch));
            if (!outcome.Accepted)
            {
                return false;
            }
            if (!outcome.Won && outcome.DeadlockMsg.Length > 0)
            {
                return false;
            }
        }
        return st.Head.Equals(st.Goal);
    }

    /// <summary>
    /// 尺寸降级矩阵（S3 起）：小图完整算 C 类；18/20 关闭 M14/M16 以守住预算。
    /// Top-K 数量在 S5 的 MAP-Elites 中落地，这里先控制评分开销。
    /// </summary>
    private static MetricOptions BuildScoreOptions(int maxSize)
    {
        if (maxSize <= 14)
        {
            return new MetricOptions
            {
                FunnelNodeBudget = 600,
                FunnelMaxCount = 200,
                SolveNodeBudget = 12_000,
            };
        }
        if (maxSize <= 16)
        {
            return new MetricOptions
            {
                FunnelNodeBudget = 400,
                FunnelMaxCount = 120,
                SolveNodeBudget = 10_000,
            };
        }
        return new MetricOptions
        {
            EnableFalseAffordance = false,
            EnableFunnel = false,
            EnableNaiveStrategies = true,
            SolveNodeBudget = 8_000,
        };
    }

    private static LevelScore ComputeScore(LevelSpec spec, string route,
        Dictionary<string, double> metrics, MetricOptions options)
    {
        MetricVector vector = MetricExtractor.Extract(spec, route, options);
        int difficulty = ScoreSpline.Default.InterpolateRounded(vector.DRaw);
        Dictionary<string, double> values = vector.ToDictionary();
        foreach (KeyValuePair<string, double> kv in values)
        {
            metrics[kv.Key] = kv.Value;
        }

        // 质量分先沿用旧三项口径（S4 再按规划稿 §5.5 补 cue / 结构 / 可读性）。
        double quality = 100.0 * (0.5 * vector.MechNorm + 0.3 * vector.DetourNorm + 0.2 * vector.TightNorm);
        return new LevelScore
        {
            Total = difficulty,
            Difficulty = difficulty,
            Quality = (int)Math.Round(quality),
            ScoringVersion = 2,
            Metrics = values,
            CEstimated = vector.CEstimated,
        };
    }

    private static int Chebyshev(Cell a, Cell b)
    {
        return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private static Cell DirCellForChar(char ch)
    {
        return ch switch
        {
            'A' => new Cell(-1, 0),
            'D' => new Cell(1, 0),
            'W' => new Cell(0, -1),
            'S' => new Cell(0, 1),
            _ => Cell.Zero,
        };
    }

    private static GenerationResult Fail(string reason)
    {
        return new GenerationResult { Success = false, Reason = reason };
    }
}

/// <summary>确定性 RNG（xorshift64*），保证同一 seed 跨平台/跨进程结果一致。</summary>
internal sealed class DeterministicRng
{
    private ulong _state;

    public DeterministicRng(ulong seed)
    {
        _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
    }

    public ulong NextU64()
    {
        _state ^= _state >> 12;
        _state ^= _state << 25;
        _state ^= _state >> 27;
        return _state * 2685821657736338717UL;
    }

    public int NextInt(int maxExclusive)
    {
        return maxExclusive <= 0 ? 0 : (int)(NextU64() % (ulong)maxExclusive);
    }

    public void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = NextInt(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
