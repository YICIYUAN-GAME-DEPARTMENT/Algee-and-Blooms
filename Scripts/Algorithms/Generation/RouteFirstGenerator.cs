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
    /// <summary>每个候选最多成功构建几次来逼近目标分（失败重试不计入）。</summary>
    public int SearchAttempts { get; set; } = 10;
    /// <summary>每关生成多少个候选，再按目标难度分选。</summary>
    public int Candidates { get; set; } = 4;
    public string Difficulty { get; set; } = "normal";
    /// <summary>障碍模型（v5 原型）：默认蓝噪声，可切换成噪声簇 / Poisson 簇 / 贴墙生长。</summary>
    public ObstacleModel ObstacleModel { get; set; } = ObstacleModel.BlueNoise;
    /// <summary>生成模型（v5 原型）：经典路线优先 / 黏菌枝干。</summary>
    public GeneratorModel GeneratorModel { get; set; } = GeneratorModel.RouteFirst;
    /// <summary>模板 id（仅 TemplateField 使用；空 = 按档位采样）。</summary>
    public string TemplateId { get; set; } = "";
    /// <summary>v4 调度来源（structured / noise / hybrid）；空 = 由生成引擎推导。</summary>
    public string GenerationSource { get; set; } = "";
    /// <summary>当前关卡序号（Portfolio 用 hash(seed, index) 选择引擎）。</summary>
    public int LevelIndex { get; set; }
    /// <summary>是否对最终 Top-K 候选执行设计公平门（G3）。</summary>
    public bool EnableFairnessGate { get; set; } = true;
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
    /// <summary>实际生成模式：template / fallback / route_first。</summary>
    public string GenerationMode { get; set; } = "route_first";
    /// <summary>模板 id（模板 / 降级时填写）。</summary>
    public string TemplateId { get; set; } = "route_first";
    public int Attempts { get; set; }
    /// <summary>本次生成的目标步数（真实最短路）。</summary>
    public int TargetSteps { get; set; }
    /// <summary>|真实步数 - TargetSteps|。</summary>
    public int TargetDelta { get; set; }
    /// <summary>目标分超出可达区间（最近格仍超出容差）。</summary>
    public bool TargetMiss { get; set; }
    /// <summary>本次存档占用格数（多样性代理）。</summary>
    public int ArchiveCells { get; set; }
    /// <summary>墙钟熔断触发（工作单元仍优先保证确定性）。</summary>
    public bool BudgetAborted { get; set; }
    /// <summary>实际消耗的工作单元（构建次数 × 面积折算）。</summary>
    public double WorkUnits { get; set; }
}

/// <summary>
/// 地形优先 + 随机规则行走的种子生成器：
///   1. 按 tier 采样尺寸（10×10–20×20）与每关参数（L0 / Gain / I / cap / initial_level）；
///   2. 噪波 + 细胞自动机生成洞窟地形，取最大连通分量；
///   3. 在真实 RuleState 上随机行走（相位排花），终点 = 行走终点；
///   4. 机关蓝噪声撒点 + 时间窗验证；诱饵只在假捷径候选中放；
///   5. 用同一 RuleState 重放验证，计算评分 v2；
///   6. 生成多个候选并入 MAP-Elites 存档，按偏正态采样的目标分选格（S5）。
/// </summary>
public partial class RouteFirstGenerator : RefCounted
{
    internal readonly struct Band
    {
        public readonly int MinSteps;
        public readonly int MaxSteps;
        public readonly int InitialMaxLen;
        public readonly int GrowthInterval;
        public readonly int MechCap;
        public readonly int MinMechs;
        public readonly int MaxMechs;

        public Band(int minSteps, int maxSteps, int initialMaxLen, int growthInterval, int mechCap,
            int minMechs, int maxMechs)
        {
            MinSteps = minSteps;
            MaxSteps = maxSteps;
            InitialMaxLen = initialMaxLen;
            GrowthInterval = growthInterval;
            MechCap = mechCap;
            MinMechs = minMechs;
            MaxMechs = maxMechs;
        }
    }

    internal static Band GetBand(string difficulty)
    {
        return difficulty switch
        {
            "easy" => new Band(10, 12, 5, 7, 1, 1, 1),
            "normal" => new Band(14, 20, 4, 6, 2, 1, 2),
            "hard" => new Band(20, 28, 3, 5, 2, 2, 3),
            "expert" => new Band(26, 38, 4, 5, 4, 2, 3),
            _ => new Band(14, 20, 4, 6, 2, 1, 2),
        };
    }

    // 高频生成下反复 new RefCounted 会触发引擎原生崩溃（0xC000001D）；
    // 以下实例按线程复用，Setup/ResetToSpec 会清空内部状态，可安全重复使用。
    [ThreadStatic] private static RuleState _walkState;
    [ThreadStatic] private static RuleState _replayState;
    [ThreadStatic] private static RuleState _solveState;
    [ThreadStatic] private static RuleState _mechState;
    [ThreadStatic] private static RuleState _probeState;
    [ThreadStatic] private static RuleState _probeReplayState;
    [ThreadStatic] private static Search.ExactBfsSolver _solver;

    private static RuleState WalkState()
    {
        _walkState ??= new RuleState();
        _walkState.SuppressDeadlockChecks = true;
        _walkState.SuppressPathCheck = false;
        return _walkState;
    }

    private static RuleState ReplayState()
    {
        _replayState ??= new RuleState();
        _replayState.SuppressDeadlockChecks = false;
        _replayState.SuppressPathCheck = true;
        return _replayState;
    }

    private static RuleState MechState()
    {
        _mechState ??= new RuleState();
        _mechState.SuppressDeadlockChecks = false;
        _mechState.SuppressPathCheck = false;
        return _mechState;
    }

    internal static RuleState GetSolveState()
    {
        _solveState ??= new RuleState();
        _solveState.SuppressDeadlockChecks = false;
        _solveState.SuppressPathCheck = false;
        return _solveState;
    }

    internal static RuleState GetProbeState()
    {
        _probeState ??= new RuleState();
        _probeState.SuppressDeadlockChecks = false;
        _probeState.SuppressPathCheck = false;
        return _probeState;
    }

    internal static RuleState GetProbeReplayState()
    {
        _probeReplayState ??= new RuleState();
        _probeReplayState.SuppressDeadlockChecks = false;
        _probeReplayState.SuppressPathCheck = false;
        return _probeReplayState;
    }

    /// <summary>求解器复用实例（Solve 每调用自清缓存）；避免高频 new RefCounted 触发原生崩溃。</summary>
    internal static Search.ExactBfsSolver GetSolver()
    {
        _solver ??= new Search.ExactBfsSolver();
        return _solver;
    }

    /// <summary>窄 API：纯 C# 生成（运行时 / 工具入口）。</summary>
    public static GenerationResult GenerateSpec(long seed, string tier)
    {
        return GenerateCore(new GeneratorConfig { Seed = seed, Difficulty = tier });
    }

    internal static GenerationResult GenerateCore(GeneratorConfig cfg)
    {
        return new GenerationJob(cfg, GenerationBudget.Unlimited()).RunToCompletion();
    }

    /// <summary>档位的目标步数区间（供 GenerationJob 采样目标步数）。</summary>
    internal static (int Min, int Max) StepRange(string difficulty)
    {
        Band band = GetBand(difficulty);
        return (band.MinSteps, band.MaxSteps);
    }

    /// <summary>单次构建尝试（由 GenerationJob 逐次调用，保证分帧前后顺序一致）。</summary>
    internal readonly struct AttemptResult
    {
        public readonly bool Success;
        public readonly GenerationResult Candidate;
        public readonly string Reason;
        public readonly double AreaFactor;

        public AttemptResult(bool success, GenerationResult candidate, string reason, double areaFactor)
        {
            Success = success;
            Candidate = candidate;
            Reason = reason;
            AreaFactor = areaFactor;
        }
    }

    internal static AttemptResult RunAttempt(GeneratorConfig cfg, int targetSteps, int cand, int attempt)
    {
        Band band = GetBand(cfg.Difficulty);
        var rng = new DeterministicRng(
            (ulong)cfg.Seed * 0x9E3779B97F4A7C15UL
            + (ulong)cand * 0x94D049BB133111EBUL
            + (ulong)attempt * 0xBF58476D1CE4E5B9UL
            + 1UL);

        int mechCount = cfg.MechanismCount >= 0
            ? cfg.MechanismCount
            : band.MinMechs + rng.NextInt(band.MaxMechs - band.MinMechs + 1);

        (int width, int height) = cfg.Width > 0 && cfg.Height > 0
            ? (cfg.Width, cfg.Height)
            : SizeLadder.Choose(rng, cfg.Difficulty);
        double areaFactor = GenerationBudget.AreaFactor(width, height);

        ParamSet param = ParamSampler.Sample(rng, targetSteps, mechCount);
        if (param == null)
        {
            return new AttemptResult(false, null, "参数组合不可行", areaFactor);
        }

        GenerationResult candidate = TryBuild(cfg, band, rng, targetSteps, mechCount,
            width, height, param);
        return candidate.Success
            ? new AttemptResult(true, candidate, "", areaFactor)
            : new AttemptResult(false, null, candidate.Reason, areaFactor);
    }

    /// <summary>地形 / 障碍 / 出生点 / 终点的共用基底（路线优先与黏菌枝干共用）。</summary>
    internal sealed class BaseLayout
    {
        public List<Cell> Floor;
        public List<Cell> Obstacles;
        public GridModel Grid;
        public Cell Spawn;
        public Cell Goal;
    }

    /// <summary>
    /// 构建共用基底：洞窟地形 → 障碍模型撒点 → 外围出生点 + 目标步数带内的终点。
    /// 路线优先与黏菌枝干两种生成模型都从这里起步；失败时返回 null 并给出原因。
    /// </summary>
    internal static BaseLayout BuildBase(GeneratorConfig cfg, DeterministicRng rng, int targetSteps,
        int width, int height, int bandMinSteps, int bandMaxSteps, out string reason)
    {
        reason = "";
        List<Cell> floor = BuildTerrain(rng, width, height, targetSteps + 8 + width);
        if (floor == null)
        {
            reason = "地形连通分量过小";
            return null;
        }
        if (targetSteps > 0.9 * floor.Count)
        {
            reason = "地板不足以承载路线";
            return null;
        }

        // 障碍相（v3）：像 L5 一样在地板上撒障碍，再在其上选起点/终点/路线。
        double obstacleRatio = cfg.Difficulty switch
        {
            "easy" => 0.05,
            "normal" => 0.10,
            "hard" => 0.14,
            "expert" => 0.18,
            _ => 0.10,
        };
        List<Cell> obstacles = ObstacleModels.Generate(
            cfg.ObstacleModel, floor, width, height, rng, obstacleRatio);
        var obstacleSet = new HashSet<Cell>(obstacles);
        var walkable = new List<Cell>(floor.Count - obstacles.Count);
        foreach (Cell c in floor)
        {
            if (!obstacleSet.Contains(c))
            {
                walkable.Add(c);
            }
        }
        if (targetSteps > 0.85 * walkable.Count)
        {
            reason = "障碍后地板不足以承载路线";
            return null;
        }

        // 目标优先：终点取静态距离 ≥ 档位下限的远处格。静态最短路是任何胜利路线的下界，
        // 因此实测最短胜利步数（真实难度）有硬保证。出生点优先取外围格，保证地形有足够远的终点。
        var grid = new GridModel(Cell.Zero, width, height);
        foreach (Cell c in floor)
        {
            grid.Floor.Add(c);
        }
        foreach (Cell c in obstacles)
        {
            grid.Obstacles.Add(c);
        }
        var empty = new HashSet<Cell>();
        Cell spawn = Cell.Zero;
        Dictionary<Cell, int> distMap = null;
        var goalPool = new List<Cell>();
        List<Cell> spawnOrder = PeripheralSpawns(walkable, rng);
        int spawnTries = Math.Min(spawnOrder.Count, 8);
        for (int i = 0; i < spawnTries; i++)
        {
            Cell trySpawn = spawnOrder[i];
            Dictionary<Cell, int> dm = grid.BfsDistMap(trySpawn, empty);
            var pool = new List<Cell>();
            foreach (KeyValuePair<Cell, int> kv in dm)
            {
                if (kv.Value >= bandMinSteps && kv.Value <= bandMaxSteps)
                {
                    pool.Add(kv.Key);
                }
            }
            if (pool.Count == 0)
            {
                continue;
            }
            spawn = trySpawn;
            distMap = dm;
            goalPool = pool;
            break;
        }
        if (goalPool.Count == 0)
        {
            reason = "地形缺少足够远的终点";
            return null;
        }
        goalPool.Sort((a, b) =>
        {
            int da = Math.Abs(distMap[a] - targetSteps);
            int db = Math.Abs(distMap[b] - targetSteps);
            return da != db ? da.CompareTo(db) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);
        });
        Cell goal = goalPool[rng.NextInt(Math.Min(goalPool.Count, 16))];

        return new BaseLayout
        {
            Floor = floor,
            Obstacles = obstacles,
            Grid = grid,
            Spawn = spawn,
            Goal = goal,
        };
    }

    private static GenerationResult TryBuild(GeneratorConfig cfg, Band band, DeterministicRng rng,
        int targetSteps, int mechCount, int width, int height, ParamSet param)
    {
        BaseLayout layout = BuildBase(cfg, rng, targetSteps, width, height,
            band.MinSteps, band.MaxSteps, out string baseReason);
        if (layout == null)
        {
            return Fail(baseReason);
        }
        GenerationTrace.Report("地形与障碍");
        List<Cell> floor = layout.Floor;
        List<Cell> obstacles = layout.Obstacles;
        GridModel grid = layout.Grid;
        Cell spawn = layout.Spawn;
        Cell goal = layout.Goal;

        List<Cell> path = BfsPath(grid, spawn, goal);
        if (path == null || path.Count - 1 < band.MinSteps)
        {
            return Fail("终点路径不足");
        }

        // v3：在基础最短路上选瓶颈格放“封路核心”，逼出真实绕行（像 L5 的花墙）。
        var blockers = new List<Cell>();
        int blockerTarget = cfg.Difficulty switch
        {
            "expert" => 2,
            _ => 1,
        };
        for (int b = 0; b < blockerTarget && b < mechCount; b++)
        {
            Cell blocker = PickBlocker(grid, spawn, goal, path, rng);
            if (blocker.Equals(Cell.Zero))
            {
                break;
            }
            grid.Obstacles.Add(blocker);
            List<Cell> detour = BfsPath(grid, spawn, goal);
            if (detour == null || detour.Count - 1 < band.MinSteps)
            {
                grid.Obstacles.Remove(blocker);
                break;
            }
            blockers.Add(blocker);
            path = detour;
        }

        var spec = new LevelSpec
        {
            Seed = cfg.Seed,
            Name = cfg.Difficulty,
            Tier = cfg.Difficulty,
            Origin = Cell.Zero,
            Width = width,
            Height = height,
            Floor = floor,
            Obstacles = obstacles,
            Spawn = spawn,
            Goal = goal,
            InitialMaxLen = param.InitialMaxLen,
            FoodLenGain = param.FoodLenGain,
            GrowthInterval = param.GrowthInterval,
            MechanismCap = param.MechanismCap,
            TargetSteps = targetSteps,
            ParamSignature = param.Signature,
        };
        foreach (Cell blocker in blockers)
        {
            spec.MechCenters.Add(blocker);
            spec.MechInitialLevels.Add(0);
        }

        var walked = new HashSet<Cell> { spawn };
        var trailOrder = new List<Cell>();
        int foodBudget = (int)Math.Ceiling(
            (path.Count - 1 - param.InitialMaxLen) / (double)param.FoodLenGain) + 3;
        if (!WalkPath(spec, path, walked, out string route, trailOrder, foodBudget))
        {
            return Fail("路径行走失败");
        }

        // 路线确定后放机关：蓝噪声撒点 + 时间窗验证；极端地形放不下时允许 0 机关。
        int remainingMechs = Math.Max(0, mechCount - blockers.Count);
        int placed = PlaceMechanisms(spec, rng, walked, trailOrder, remainingMechs, param.InitialLevels);
        if (spec.MechCenters.Count == 0 && mechCount > 0)
        {
            // 保留关卡可玩性：无机关仍可作为低难度样本，不因放置失败丢弃整张图。
            spec.MechCenters.Clear();
            spec.MechInitialLevels.Clear();
        }

        // v3：尝试把 1–2 片“面包屑”花瓣移到路线外侧，逼出真实绕行；
        // 只有精确求解仍可解且路线确实经过新位置时才保留。
        ShiftFoodsOffPath(spec, walked, band);

        int decoyMin = cfg.Difficulty is "hard" or "expert" ? 1 : 0;
        AddDecoyFoods(rng, spec, walked, route.Length, decoyMin, 2);

        // v3：用精确求解器的最短路作为规范路线（真实难度）；UNKNOWN 的 hard/expert 候选直接丢弃。
        GenerationTrace.Report("精确求解");
        int solveStates = 0;
        RuleState solveState = GetSolveState();
        solveState.Setup(spec);
        solveState.Start();
        var solver = GetSolver();
        Godot.Collections.Dictionary solved = solver.Solve(solveState, 1_200_000);
        string solveStatus = solved["status"].AsString();
        if (solveStatus == "SOLVABLE")
        {
            int minSteps = solved["steps"].AsInt32();
            if (minSteps < band.MinSteps)
            {
                return Fail("真实最短路短于档位下限");
            }
            route = solved["route"].AsString();
            solveStates = solved["states"].AsInt32();
        }
        else if (solveStatus == "UNSOLVABLE")
        {
            return Fail("精确求解无解");
        }
        else if (band.MinSteps >= 16)
        {
            return Fail("精确求解超预算");
        }

        // v4 结构注入（最小版）：在规范路线旁加 1–2 个死胡同假岔路，末端放花瓣制造“看起来更近”的诱饵。
        // 死胡同只与路线单点相连，理论上不改变最短路；仍用一次重解兜底，异常时回滚。
        int injectedForks = 0;
        int forkTarget = cfg.Difficulty switch
        {
            "expert" => 2,
            "hard" => 1,
            _ => 0,
        };
        if (forkTarget > 0)
        {
            int floorBefore = spec.Floor.Count;
            var foodsBefore = new List<Cell>(spec.Foods);
            injectedForks = InjectDeadEndForks(rng, spec, route, forkTarget);
            if (injectedForks > 0)
            {
                GenerationTrace.Report("结构复核");
                RuleState checkState = GetProbeState();
                checkState.Setup(spec);
                checkState.Start();
                Godot.Collections.Dictionary recheck = GetSolver().Solve(checkState, 1_200_000);
                bool ok = recheck["status"].AsString() == "SOLVABLE"
                    && recheck["steps"].AsInt32() == route.Length;
                if (!ok)
                {
                    spec.Floor.RemoveRange(floorBefore, spec.Floor.Count - floorBefore);
                    spec.Foods.Clear();
                    spec.Foods.AddRange(foodsBefore);
                    injectedForks = 0;
                }
            }
        }

        if (!ReplayVerify(spec, route))
        {
            return Fail("路线重放失败");
        }

        GenerationTrace.Report("评分");
        var result = new GenerationResult
        {
            Success = true,
            Spec = spec,
            GenerationMode = "route_first",
            TemplateId = "route_first",
            Solution = new LevelSolution
            {
                Status = "SOLVABLE",
                Steps = route.Length,
                Route = route,
            },
        };
        result.Score = ComputeScore(spec, route, result.Metrics,
            BuildScoreOptions(Math.Max(width, height)), solveStates);
        if (injectedForks > 0)
        {
            result.Metrics["injected_forks"] = injectedForks;
            result.Score.Metrics["injected_forks"] = injectedForks;
        }
        return result;
    }

    /// <summary>
    /// 从基础最短路中挑一个“封路”瓶颈：移除它后静态距离增加最多的格（前 3 随机）。
    /// 该格随后作为初始机关核心放在路径上，迫使真实路线绕行。
    /// </summary>
    private static Cell PickBlocker(GridModel grid, Cell spawn, Cell goal,
        IReadOnlyList<Cell> path, DeterministicRng rng)
    {
        if (path.Count < 6)
        {
            return Cell.Zero;
        }
        int baseDist = path.Count - 1;
        var candidates = new List<(Cell Cell, int Dist)>();
        var noMech = new HashSet<Cell>();
        for (int i = 2; i < path.Count - 1; i++)
        {
            if (GenerationTrace.Expired)
            {
                return Cell.Zero;
            }
            Cell c = path[i];
            if (c.Equals(spawn) || c.Equals(goal))
            {
                continue;
            }
            grid.Obstacles.Add(c);
            Dictionary<Cell, int> dm = grid.BfsDistMap(spawn, noMech);
            grid.Obstacles.Remove(c);
            if (dm.TryGetValue(goal, out int dist) && dist > baseDist)
            {
                candidates.Add((c, dist));
            }
        }
        if (candidates.Count == 0)
        {
            return Cell.Zero;
        }
        candidates.Sort((a, b) => b.Dist.CompareTo(a.Dist));
        return candidates[rng.NextInt(Math.Min(candidates.Count, 3))].Cell;
    }

    /// <summary>
    /// 把路径上的花瓣移到相邻的路线外格：精确求解仍可解且确实经过新格才保留，
    /// 用来制造“必须绕路吃花”的真实路线（而不是沿路面包屑）。
    /// </summary>
    private static void ShiftFoodsOffPath(LevelSpec spec, HashSet<Cell> walked, Band band)
    {
        var floorSet = new HashSet<Cell>(spec.Floor);
        var obstacleSet = new HashSet<Cell>(spec.Obstacles);
        var mechSet = new HashSet<Cell>(spec.MechCenters);
        Cell[] dirs =
        {
            new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
        };
        int shifted = 0;
        for (int i = spec.Foods.Count - 1; i >= 0 && shifted < 2; i--)
        {
            if (GenerationTrace.Expired)
            {
                break;
            }
            Cell food = spec.Foods[i];
            if (!walked.Contains(food))
            {
                continue;
            }
            Cell replacement = Cell.Zero;
            foreach (Cell d in dirs)
            {
                Cell c = food + d;
                if (!floorSet.Contains(c) || obstacleSet.Contains(c) || mechSet.Contains(c)
                    || c.Equals(spec.Spawn) || c.Equals(spec.Goal) || spec.Foods.Contains(c))
                {
                    continue;
                }
                replacement = c;
                break;
            }
            if (replacement.Equals(Cell.Zero))
            {
                continue;
            }
            spec.Foods[i] = replacement;
            if (ProbeSolveVisits(spec, replacement, band.MinSteps))
            {
                shifted++;
            }
            else
            {
                spec.Foods[i] = food;
            }
        }
    }

    /// <summary>探测：完整规则下可解、步数达标、且路线确实经过 target。</summary>
    private static bool ProbeSolveVisits(LevelSpec spec, Cell target, int minSteps)
    {
        var state = GetProbeState();
        state.Setup(spec);
        state.Start();
        var solver = GetSolver();
        Godot.Collections.Dictionary solved = solver.Solve(state, 300_000);
        if (solved["status"].AsString() != "SOLVABLE" || solved["steps"].AsInt32() < minSteps)
        {
            return false;
        }
        string route = solved["route"].AsString();
        var replay = GetProbeReplayState();
        replay.Setup(spec);
        replay.Start();
        foreach (char ch in route)
        {
            replay.StepCore(DirCellForChar(ch));
            if (replay.Head.Equals(target))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>出生点候选：优先远离地形质心的外围格（随机打散前 32 个）。</summary>
    internal static List<Cell> PeripheralSpawns(IReadOnlyList<Cell> floor, DeterministicRng rng)
    {
        double cx = 0;
        double cy = 0;
        foreach (Cell c in floor)
        {
            cx += c.X;
            cy += c.Y;
        }
        cx /= floor.Count;
        cy /= floor.Count;

        var ordered = new List<Cell>(floor);
        ordered.Sort((a, b) =>
        {
            double da = Math.Abs(a.X - cx) + Math.Abs(a.Y - cy);
            double db = Math.Abs(b.X - cx) + Math.Abs(b.Y - cy);
            int cmp = db.CompareTo(da);
            return cmp != 0 ? cmp : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);
        });
        int pool = Math.Min(ordered.Count, 32);
        var head = ordered.GetRange(0, pool);
        rng.Shuffle(head);
        return head;
    }

    /// <summary>BFS 最短路（含起点与终点）；不可达返回 null。</summary>
    internal static List<Cell> BfsPath(GridModel grid, Cell from, Cell to)
    {
        if (from.Equals(to))
        {
            return new List<Cell> { from };
        }
        var parent = new Dictionary<Cell, Cell>();
        var visited = new HashSet<Cell> { from };
        var queue = new Queue<Cell>();
        var noMech = new HashSet<Cell>();
        queue.Enqueue(from);
        bool found = false;
        while (queue.Count > 0 && !found)
        {
            Cell cur = queue.Dequeue();
            foreach (Cell n in GridModel.Neighbors(cur))
            {
                if (visited.Contains(n) || !grid.InBounds(n) || grid.IsBlocked(n, noMech))
                {
                    continue;
                }
                visited.Add(n);
                parent[n] = cur;
                if (n.Equals(to))
                {
                    found = true;
                    break;
                }
                queue.Enqueue(n);
            }
        }
        if (!found)
        {
            return null;
        }
        var path = new List<Cell>();
        Cell step = to;
        while (!step.Equals(from))
        {
            path.Add(step);
            step = parent[step];
        }
        path.Add(from);
        path.Reverse();
        return path;
    }

    /// <summary>沿给定简单路径逐格行走；濒死时在下一步自动排花（相位排花）。</summary>
    internal static bool WalkPath(LevelSpec spec, IReadOnlyList<Cell> path, HashSet<Cell> walked,
        out string route, List<Cell> trailOrder, int foodBudget)
    {
        route = "";
        trailOrder.Clear();
        trailOrder.Add(spec.Spawn);
        var dirs = new List<char>(path.Count - 1);
        RuleState st = WalkState();
        st.Setup(spec);
        st.Start();

        int inserted = 0;
        for (int i = 1; i < path.Count; i++)
        {
            Cell next = path[i];
            Cell delta = next - st.Head;
            RuleState.MoveKind kind = st.ClassifyMove(next);
            if (kind == RuleState.MoveKind.Rejected)
            {
                return false;
            }
            if (kind == RuleState.MoveKind.Walk
                && st.GetRemainingSteps() <= 1
                && inserted < foodBudget
                && !spec.Foods.Contains(next)
                && !next.Equals(spec.Goal)
                && !next.Equals(spec.Spawn))
            {
                spec.Foods.Add(next);
                st.AddFood(next);
                inserted++;
            }

            RuleState.StepOutcome outcome = st.StepCore(delta);
            if (!outcome.Accepted)
            {
                return false;
            }
            dirs.Add(DirCharFor(delta));
            walked.Add(st.Head);
            trailOrder.Add(st.Head);
            if (st.GetRemainingSteps() <= 0 && kind != RuleState.MoveKind.Body)
            {
                return false;
            }
        }
        route = new string(dirs.ToArray());
        return st.Head.Equals(spec.Goal);
    }

    private static char DirCharFor(Cell delta)
    {
        if (delta.X < 0)
        {
            return 'A';
        }
        if (delta.X > 0)
        {
            return 'D';
        }
        return delta.Y < 0 ? 'W' : 'S';
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
    internal static int PlaceMechanisms(LevelSpec spec, DeterministicRng rng, HashSet<Cell> walked,
        IReadOnlyList<Cell> trailOrder, int count, IReadOnlyList<int> initialLevels)
    {
        if (count <= 0)
        {
            return 0;
        }
        var pool = new List<(Cell Cell, int Step)>();
        var obstacleSet = new HashSet<Cell>(spec.Obstacles);
        foreach (Cell c in spec.Floor)
        {
            if (obstacleSet.Contains(c) || walked.Contains(c)
                || c.Equals(spec.Spawn) || c.Equals(spec.Goal))
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
        if (pool.Count == 0)
        {
            return 0;
        }

        // v3：按路线弧长分层，每段优先放一个核心，让核心沿路线均布，而不是全挤在出生点。
        int segments = Math.Max(1, Math.Min(count, trailOrder.Count));
        var buckets = new List<(Cell Cell, int Step)>[segments];
        for (int i = 0; i < segments; i++)
        {
            buckets[i] = new List<(Cell, int)>();
        }
        foreach ((Cell c, int step) in pool)
        {
            int seg = Math.Min(segments - 1, step * segments / Math.Max(1, trailOrder.Count));
            buckets[seg].Add((c, step));
        }
        foreach (List<(Cell Cell, int Step)> bucket in buckets)
        {
            rng.Shuffle(bucket);
        }

        RuleState state = MechState();
        int placed = 0;
        for (int seg = 0; seg < segments && placed < count; seg++)
        {
            if (GenerationTrace.Expired)
            {
                break;
            }
            foreach ((Cell c, int _) in buckets[seg])
            {
                if (TryPlaceMechanism(spec, state, trailOrder, c, placed, initialLevels))
                {
                    placed++;
                    break;
                }
            }
        }
        if (placed < count)
        {
            rng.Shuffle(pool);
            foreach ((Cell c, int _) in pool)
            {
                if (placed >= count || GenerationTrace.Expired)
                {
                    break;
                }
                if (TryPlaceMechanism(spec, state, trailOrder, c, placed, initialLevels))
                {
                    placed++;
                }
            }
        }
        return placed;
    }

    private static bool TryPlaceMechanism(LevelSpec spec, RuleState state,
        IReadOnlyList<Cell> trailOrder, Cell c, int index, IReadOnlyList<int> initialLevels)
    {
        foreach (Cell chosen in spec.MechCenters)
        {
            if (Chebyshev(chosen, c) < 3)
            {
                return false;
            }
        }
        int initial = index < initialLevels.Count ? initialLevels[index] : 0;
        initial = Math.Clamp(initial, MechanicShapes.MinLevel, Math.Max(spec.MechanismCap - 1, 0));
        spec.MechCenters.Add(c);
        spec.MechInitialLevels.Add(initial);
        state.ResetToSpec(spec);
        if (ValidateMechanismWindows(spec, state, trailOrder))
        {
            return true;
        }
        spec.MechCenters.RemoveAt(spec.MechCenters.Count - 1);
        spec.MechInitialLevels.RemoveAt(spec.MechInitialLevels.Count - 1);
        return false;
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

    /// <summary>
    /// 诱饵：只在「看起来更短」的假捷径候选里放（dist(S,d)+dist(d,E) ≤ S-2），
    /// 且不与规范路线重叠（不会被吃到）。精确真假由评分期的 M14 复核。
    /// </summary>
    internal static void AddDecoyFoods(DeterministicRng rng, LevelSpec spec, HashSet<Cell> walked,
        int steps, int min, int max)
    {
        int count;
        if (min == 0 && max == 2)
        {
            // 诱饵数量权重：0 个 40% / 1 个 40% / 2 个 20%，避免假捷径标签过密。
            int roll = rng.NextInt(10);
            count = roll < 4 ? 0 : roll < 8 ? 1 : 2;
        }
        else
        {
            count = min + rng.NextInt(max - min + 1);
        }
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

    // ── v4 结构注入 ──────────────────────────────────────────
    /// <summary>
    /// 在规范路线旁开 1–maxForks 个死胡同假岔路，末端放一片花瓣当诱饵。
    /// 只开“与路线单点相连、4 邻域不碰其它地板”的格，保证不会形成环路或意外捷径；
    /// 返回实际注入数量。纯确定性，调用方负责在异常时回滚。
    /// </summary>
    internal static int InjectDeadEndForks(DeterministicRng rng, LevelSpec spec,
        string canonicalRoute, int maxForks)
    {
        if (spec == null || rng == null || maxForks <= 0 || string.IsNullOrEmpty(canonicalRoute))
        {
            return 0;
        }
        List<Cell> routeCells = RouteCellsFrom(spec, canonicalRoute);
        if (routeCells.Count < 4)
        {
            return 0;
        }
        var floor = new HashSet<Cell>(spec.Floor);
        var obstacles = new HashSet<Cell>(spec.Obstacles);
        var foods = new HashSet<Cell>(spec.Foods);
        Cell[] dirs =
        {
            new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
        };

        var candidates = new List<ForkCandidate>();
        for (int i = 1; i < routeCells.Count - 1; i++)
        {
            Cell attach = routeCells[i];
            if (attach.Equals(spec.Spawn) || attach.Equals(spec.Goal))
            {
                continue;
            }
            for (int di = 0; di < dirs.Length; di++)
            {
                Cell dir = dirs[di];
                Cell c1 = new(attach.X + dir.X, attach.Y + dir.Y);
                if (!IsForkCell(c1, spec, floor, obstacles, foods))
                {
                    continue;
                }
                var allowed = new HashSet<Cell> { attach };
                if (TouchesOtherFloor(c1, floor, allowed))
                {
                    continue;
                }
                // 只注入深度 2 的死胡同：来回 4 步，吃诱饵后必然超过规范解 +2 步，
                // 才能作为“必败诱饵”通过严格公平门；深度 1 会天然是“无害诱饵”。
                Cell deepest = new(attach.X + dir.X * 2, attach.Y + dir.Y * 2);
                if (!IsForkCell(deepest, spec, floor, obstacles, foods))
                {
                    continue;
                }
                allowed.Add(c1);
                if (TouchesOtherFloor(deepest, floor, allowed))
                {
                    continue;
                }
                int score = 8;
                int dot = (spec.Goal.X - attach.X) * dir.X + (spec.Goal.Y - attach.Y) * dir.Y;
                if (dot > 0)
                {
                    score += 3;
                }
                candidates.Add(new ForkCandidate(attach, c1, deepest, 2, deepest, score));
            }
        }
        candidates.Sort((a, b) =>
        {
            int cmp = b.Score.CompareTo(a.Score);
            if (cmp != 0)
            {
                return cmp;
            }
            cmp = a.Attach.Y.CompareTo(b.Attach.Y);
            if (cmp != 0)
            {
                return cmp;
            }
            cmp = a.Attach.X.CompareTo(b.Attach.X);
            return cmp != 0 ? cmp : a.C1.Y != b.C1.Y
                ? a.C1.Y.CompareTo(b.C1.Y)
                : a.C1.X.CompareTo(b.C1.X);
        });

        var usedAttach = new List<Cell>();
        int placed = 0;
        foreach (ForkCandidate candidate in candidates)
        {
            if (placed >= maxForks)
            {
                break;
            }
            if (NearAny(candidate.Attach, usedAttach, 3))
            {
                continue;
            }
            var allowed = new HashSet<Cell> { candidate.Attach };
            if (!IsForkCell(candidate.C1, spec, floor, obstacles, foods)
                || TouchesOtherFloor(candidate.C1, floor, allowed))
            {
                continue;
            }
            if (candidate.Depth == 2)
            {
                allowed.Add(candidate.C1);
                if (!IsForkCell(candidate.C2, spec, floor, obstacles, foods)
                    || TouchesOtherFloor(candidate.C2, floor, allowed))
                {
                    continue;
                }
            }

            AddFloorCell(spec, floor, candidate.C1);
            if (candidate.Depth == 2)
            {
                AddFloorCell(spec, floor, candidate.C2);
            }
            if (foods.Add(candidate.Deepest))
            {
                spec.Foods.Add(candidate.Deepest);
            }
            usedAttach.Add(candidate.Attach);
            placed++;
        }
        return placed;
    }

    private readonly struct ForkCandidate
    {
        public readonly Cell Attach;
        public readonly Cell C1;
        public readonly Cell C2;
        public readonly int Depth;
        public readonly Cell Deepest;
        public readonly int Score;

        public ForkCandidate(Cell attach, Cell c1, Cell c2, int depth, Cell deepest, int score)
        {
            Attach = attach;
            C1 = c1;
            C2 = c2;
            Depth = depth;
            Deepest = deepest;
            Score = score;
        }
    }

    private static bool IsForkCell(Cell c, LevelSpec spec, HashSet<Cell> floor,
        HashSet<Cell> obstacles, HashSet<Cell> foods)
    {
        if (c.X < spec.Origin.X || c.Y < spec.Origin.Y
            || c.X >= spec.Origin.X + spec.Width || c.Y >= spec.Origin.Y + spec.Height)
        {
            return false;
        }
        if (c.Equals(spec.Spawn) || c.Equals(spec.Goal))
        {
            return false;
        }
        return !floor.Contains(c) && !obstacles.Contains(c) && !foods.Contains(c);
    }

    /// <summary>
    /// 新格是否与“非预期”地板正交相邻（会形成环路 / 意外捷径）。
    /// 只查 4 邻接：斜角相邻不会连通，且 1 格宽走廊的凹室必然与走廊斜角相邻。
    /// </summary>
    private static bool TouchesOtherFloor(Cell c, HashSet<Cell> floor, HashSet<Cell> allowed)
    {
        Cell[] orthogonal =
        {
            new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
        };
        foreach (Cell d in orthogonal)
        {
            var n = new Cell(c.X + d.X, c.Y + d.Y);
            if (allowed.Contains(n))
            {
                continue;
            }
            if (floor.Contains(n))
            {
                return true;
            }
        }
        return false;
    }

    private static void AddFloorCell(LevelSpec spec, HashSet<Cell> floor, Cell c)
    {
        if (floor.Add(c))
        {
            spec.Floor.Add(c);
        }
    }

    private static bool NearAny(Cell c, List<Cell> cells, int radius)
    {
        foreach (Cell other in cells)
        {
            if (Math.Abs(c.X - other.X) <= radius && Math.Abs(c.Y - other.Y) <= radius)
            {
                return true;
            }
        }
        return false;
    }

    private static List<Cell> RouteCellsFrom(LevelSpec spec, string route)
    {
        var cells = new List<Cell> { spec.Spawn };
        Cell head = spec.Spawn;
        foreach (char ch in route)
        {
            Cell d = DirCellForChar(ch);
            head = new Cell(head.X + d.X, head.Y + d.Y);
            cells.Add(head);
        }
        return cells;
    }

    // ── 验证与评分 ──────────────────────────────────────────
    /// <summary>用同一套 RuleState 重放路线：必须全部 accepted、途中无死局、最终到 E。</summary>
    internal static bool ReplayVerify(LevelSpec spec, string route)
    {
        RuleState st = ReplayState();
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
    internal static MetricOptions BuildScoreOptions(int maxSize)
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
            EnableFalseAffordance = true,
            EnableFunnel = false,
            EnableNaiveStrategies = true,
            SolveNodeBudget = 8_000,
        };
    }

    internal static LevelScore ComputeScore(LevelSpec spec, string route,
        Dictionary<string, double> metrics, MetricOptions options, int solveStates = 0)
    {
        if (solveStates > 0)
        {
            options.SolveStatesOverride = solveStates;
            options.EnableSolveStates = false;
        }
        MetricVector vector = MetricExtractor.Extract(spec, route, options);
        // v4（方案 C）：难度分在 GenerationJob.Finish 里用枝干先验/后验计算，
        // 这里先保留核心指标与质量分，避免每个候选都重复跑枝干测量。
        int difficulty = 0;
        Dictionary<string, double> values = vector.ToDictionary();
        foreach (KeyValuePair<string, double> kv in values)
        {
            metrics[kv.Key] = kv.Value;
        }

        // 事后分类：只写标签与证据，不参与生成决策。
        TagResult tags = LevelTagger.Tag(spec, route, vector, options);
        spec.Tags.Clear();
        spec.Tags.AddRange(tags.Tags);
        spec.TagEvidence.Clear();
        foreach (KeyValuePair<string, string> kv in tags.Evidence)
        {
            spec.TagEvidence[kv.Key] = kv.Value;
        }

        // 质量分先沿用旧三项口径（S4 再按规划稿 §5.5 补 cue / 结构 / 可读性）。
        double quality = 100.0 * (0.5 * vector.MechNorm + 0.3 * vector.DetourNorm + 0.2 * vector.TightNorm);
        return new LevelScore
        {
            Total = difficulty,
            Difficulty = difficulty,
            Quality = (int)Math.Round(quality),
            ScoringVersion = 4,
            Metrics = values,
            Tags = tags.Tags,
            TagEvidence = tags.Evidence,
            TargetSteps = spec.TargetSteps,
            CEstimated = vector.CEstimated,
        };
    }

    /// <summary>
    /// v4 最终评分（方案 C）：枝干静态先验 + 逐步动态后验 + 核心难度项，
    /// 只对 MAP-Elites 选出的最终关卡计算一次，避免每个候选重复跑枝干测量。
    /// </summary>
    internal static void ApplyBranchScore(GenerationResult result, long seed)
    {
        if (result == null || !result.Success || result.Spec == null
            || result.Solution == null || result.Score == null)
        {
            return;
        }
        BranchPosteriorFeatures branch = BranchPosteriorScorer.Extract(
            result.Spec, result.Solution.Route, seed);
        double dCore = result.Metrics.TryGetValue("d_core", out double core) ? core : 0;
        var vector = new MetricVector { DCore = dCore };
        double raw = BranchScoreSchemes.Hybrid(branch, vector);
        // v5：原始值线性刻度（ScoreOffset），不再经锚点样条映射。
        int difficulty = BranchScoreSchemes.ScoreForRaw(raw);
        result.Score.Difficulty = difficulty;
        result.Score.Total = difficulty;
        result.Score.ScoringVersion = 5;
        result.Metrics["d_v5"] = raw;
        result.Score.Metrics["d_v5"] = raw;
        foreach (KeyValuePair<string, double> kv in branch.ToDictionary())
        {
            string key = "posterior_" + kv.Key;
            result.Metrics[key] = kv.Value;
            result.Score.Metrics[key] = kv.Value;
        }
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
public sealed class DeterministicRng
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

    /// <summary>uniform [0, 1)。</summary>
    public double NextDouble()
    {
        return (NextU64() >> 11) * (1.0 / 9007199254740992.0);
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
