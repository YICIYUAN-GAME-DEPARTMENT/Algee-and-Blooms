using System;
using System.Collections.Generic;
using Growth.Algorithms.Model;

namespace Growth.Algorithms.Scoring;

/// <summary>交互指标计算选项（评分 v3 §2.3；全部为复杂度有界的近似）。</summary>
public sealed class InteractionOptions
{
    /// <summary>承诺深度 / 前瞻上限 H_max。</summary>
    public int LookaheadCap = 8;
    /// <summary>每个决策点最多模拟的诱人分支数。</summary>
    public int BranchCap = 8;
    /// <summary>R1 时间展开在路线长度之外多留的步数。</summary>
    public int ExtraHorizon = 16;
}

/// <summary>交互指标向量（评分 v3 §2.3）。</summary>
public sealed class InteractionVector
{
    /// <summary>最小前瞻深度（见证上界近似，1 = 贪心即可）。</summary>
    public int H = 1;
    public bool HApprox = true;
    public double Deception;
    public double Cuts;
    public double Contention;
    public double LTight;
    /// <summary>按评分 v3 初值权重合成的 0–100 原始交互难度。</summary>
    public double DRaw;
    public int Steps;
    public int GrowthEvents;
    public int CutEvents;
    public int ContentionEvents;
    public int ThreatSteps;
    public int AttractiveUnsafe;
    public int ForcedTruncations;
    public int FoodRescues;

    public Dictionary<string, double> ToDictionary()
    {
        return new Dictionary<string, double>
        {
            ["h"] = H,
            ["h_norm"] = H / 8.0,
            ["h_approx"] = HApprox ? 1 : 0,
            ["deception"] = Deception,
            ["cuts"] = Cuts,
            ["contention"] = Contention,
            ["l_tight"] = LTight,
            ["d_raw"] = DRaw,
            ["steps"] = Steps,
            ["growth_events"] = GrowthEvents,
            ["cut_events"] = CutEvents,
            ["contention_events"] = ContentionEvents,
            ["threat_steps"] = ThreatSteps,
            ["attractive_unsafe"] = AttractiveUnsafe,
            ["forced_truncations"] = ForcedTruncations,
            ["food_rescues"] = FoodRescues,
        };
    }
}

/// <summary>交互评分 + 基线扣除（评分 v3 §2.4）。</summary>
public sealed class InteractionScore
{
    public InteractionVector Level;
    public InteractionVector Baseline;
    /// <summary>D_raw(Λ) − D_raw(Λ_without_mechanisms)，钳到 ≥0。</summary>
    public double DInteraction;

    public static InteractionScore Evaluate(LevelSpec spec, string route,
        InteractionOptions options = null)
    {
        InteractionVector level = InteractionMetrics.Extract(spec, route, options);
        InteractionVector baseline = InteractionMetrics.Extract(
            InteractionMetrics.CloneWithoutMechanisms(spec), route, options);
        return new InteractionScore
        {
            Level = level,
            Baseline = baseline,
            DInteraction = Math.Max(0.0, level.DRaw - baseline.DRaw),
        };
    }

    public Dictionary<string, double> ToDictionary()
    {
        return new Dictionary<string, double>
        {
            ["h"] = Level.H,
            ["h_norm"] = Level.H / 8.0,
            ["h_approx"] = Level.HApprox ? 1 : 0,
            ["deception"] = Level.Deception,
            ["cuts"] = Level.Cuts,
            ["contention"] = Level.Contention,
            ["l_tight"] = Level.LTight,
            ["d_raw_level"] = Level.DRaw,
            ["d_raw_baseline"] = Baseline.DRaw,
            ["d_interaction"] = DInteraction,
            ["attractive_unsafe"] = Level.AttractiveUnsafe,
            ["threat_steps"] = Level.ThreatSteps,
            ["forced_truncations"] = Level.ForcedTruncations,
        };
    }
}

/// <summary>
/// 交互指标（评分 v3 §2.2–2.4）：
///   H：见证路线上"看起来更近、实际会在 ≤H 步内死亡"的分支带来的前瞻需求；
///   Deception：这些诱人分支的承诺深度之和；
///   Cuts / Contention：生长事件造成 slack 下降 / 身体屏蔽机关格的比例；
///   L_tight：被迫截断 + 靠吃花续命的步数占比。
/// 只使用 R1 与真实重放，不访问节点 / 场景 / 信号。
/// </summary>
public static class InteractionMetrics
{
    private static readonly Cell[] Directions =
    {
        new(-1, 0), new(1, 0), new(0, -1), new(0, 1),
    };

    public static InteractionVector Extract(LevelSpec spec, string route, InteractionOptions options = null)
    {
        options ??= new InteractionOptions();
        var vector = new InteractionVector();
        if (spec == null || string.IsNullOrEmpty(route))
        {
            return vector;
        }

        vector.Steps = route.Length;
        int horizon = route.Length + Math.Max(4, options.ExtraHorizon);
        var model = new TimeExpandedModel(spec, horizon);
        // 对照模型：删除全部机关。用于把"静态死路"从交互欺骗里剔除（纯路线难度不计入交互）。
        var staticModel = new TimeExpandedModel(CloneWithoutMechanisms(spec), horizon);
        var grid = BuildGrid(spec);
        Dictionary<Cell, int> distGoal = grid.BfsDistMap(spec.Goal, new HashSet<Cell>());
        var foodSet = new HashSet<Cell>(spec.Foods);
        int interval = Math.Max(spec.GrowthInterval, 1);

        var state = new RuleState();
        state.Setup(spec);
        state.Start();

        int k = 0;
        int prevGrowth = state.GrowthCount;
        double deceptionSum = 0;
        int maxLookahead = 1;

        for (int i = 0; i < route.Length; i++)
        {
            Cell head = state.Head;
            Cell dir = DirForChar(route[i]);
            Cell next = head + dir;
            int remaining = route.Length - i;

            int walkOptions = 0;
            foreach (Cell d in Directions)
            {
                if (state.ClassifyMove(head + d) == RuleState.MoveKind.Walk)
                {
                    walkOptions++;
                }
            }
            bool truncation = state.ClassifyMove(next) == RuleState.MoveKind.Body;
            if (truncation && walkOptions == 0)
            {
                vector.ForcedTruncations++;
            }
            if (state.GetRemainingSteps() == 0 && foodSet.Contains(next))
            {
                vector.FoodRescues++;
            }

            // 诱人但会死的分支：静态更近（≥2 步）且承诺深度 < H_max。
            int branchSeen = 0;
            foreach (Cell n in GridModel.Neighbors(head))
            {
                if (n.Equals(next) || model.IsBlockedAt(n, i))
                {
                    continue;
                }
                if (!distGoal.TryGetValue(n, out int dist) || dist > remaining - 2)
                {
                    continue;
                }
                branchSeen++;
                if (branchSeen > options.BranchCap)
                {
                    break;
                }
                int branchK = Math.Min(k + (foodSet.Contains(n) ? 1 : 0), model.FoodCount);
                int commit = CommitDepth(model, distGoal, n, i + 1, branchK, options.LookaheadCap);
                if (commit >= options.LookaheadCap)
                {
                    continue; // 带机关也能一直走下去，不算陷阱
                }
                int staticCommit = CommitDepth(staticModel, distGoal, n, i + 1, branchK, options.LookaheadCap);
                if (staticCommit < options.LookaheadCap)
                {
                    continue; // 静态死路：属于路线难度，不算机关交互
                }
                deceptionSum += commit;
                vector.AttractiveUnsafe++;
                maxLookahead = Math.Max(maxLookahead, 1 + commit);
            }

            int coverTime = model.CoverTimeOf(next);
            if (coverTime < TimeExpandedModel.INF
                && coverTime >= i + 1 && coverTime <= i + 1 + 2 * interval)
            {
                vector.ThreatSteps++;
            }

            int slackBefore = model.Slack(k, i, head);
            RuleState.StepOutcome outcome = state.StepCore(dir);
            if (!outcome.Accepted)
            {
                break;
            }
            if (foodSet.Contains(next))
            {
                k = Math.Min(k + 1, model.FoodCount);
            }

            if (state.GrowthCount > prevGrowth)
            {
                prevGrowth = state.GrowthCount;
                vector.GrowthEvents++;
                int slackAfter = model.Slack(k, i + 1, state.Head);
                if (slackAfter < slackBefore - 1)
                {
                    vector.CutEvents++;
                }
                if (state.ShieldedCells.Count > 0)
                {
                    vector.ContentionEvents++;
                }
            }
        }

        vector.H = Math.Clamp(maxLookahead, 1, options.LookaheadCap);
        vector.Deception = deceptionSum / Math.Max(1.0, route.Length * (double)options.LookaheadCap);
        vector.Cuts = vector.GrowthEvents > 0
            ? vector.CutEvents / (double)vector.GrowthEvents : 0.0;
        vector.Contention = vector.GrowthEvents > 0
            ? vector.ContentionEvents / (double)vector.GrowthEvents : 0.0;
        vector.LTight = Math.Min(1.0,
            (vector.ForcedTruncations + vector.FoodRescues) / (double)Math.Max(1, route.Length));
        vector.DRaw = 100.0 * (
            0.40 * (vector.H / (double)options.LookaheadCap)
            + 0.20 * vector.Deception
            + 0.15 * vector.Cuts
            + 0.15 * vector.Contention
            + 0.10 * vector.LTight);
        return vector;
    }

    /// <summary>从分支格按"静态更近"贪心前进，返回首次不可行的深度；≥cap 表示能走满前瞻。</summary>
    private static int CommitDepth(TimeExpandedModel model, Dictionary<Cell, int> distGoal,
        Cell start, int time, int k, int cap)
    {
        Cell cur = start;
        int t = time;
        for (int depth = 1; depth <= cap; depth++)
        {
            if (!model.IsViable(k, t, cur))
            {
                return depth;
            }
            if (cur.Equals(model.Goal))
            {
                return cap;
            }
            if (!TryBestNeighbor(model, distGoal, cur, t, out Cell next))
            {
                return depth;
            }
            cur = next;
            t++;
        }
        return cap;
    }

    private static bool TryBestNeighbor(TimeExpandedModel model, Dictionary<Cell, int> distGoal,
        Cell cur, int time, out Cell best)
    {
        best = Cell.Zero;
        bool found = false;
        int bestDist = int.MaxValue;
        foreach (Cell n in GridModel.Neighbors(cur))
        {
            if (model.IsBlockedAt(n, time) || !distGoal.TryGetValue(n, out int dist))
            {
                continue;
            }
            if (dist < bestDist)
            {
                bestDist = dist;
                best = n;
                found = true;
            }
        }
        return found;
    }

    internal static LevelSpec CloneWithoutMechanisms(LevelSpec spec)
    {
        var copy = new LevelSpec
        {
            SchemaVersion = spec.SchemaVersion,
            GeneratorVersion = spec.GeneratorVersion,
            Seed = spec.Seed,
            LevelId = spec.LevelId,
            Name = spec.Name,
            Tier = spec.Tier,
            Origin = spec.Origin,
            Width = spec.Width,
            Height = spec.Height,
            Spawn = spec.Spawn,
            Goal = spec.Goal,
            InitialMaxLen = spec.InitialMaxLen,
            FoodLenGain = spec.FoodLenGain,
            GrowthInterval = spec.GrowthInterval,
            MechanismCap = spec.MechanismCap,
            TargetSteps = spec.TargetSteps,
            ParamSignature = spec.ParamSignature,
        };
        foreach (Cell c in spec.Floor)
        {
            copy.Floor.Add(c);
        }
        foreach (Cell c in spec.Obstacles)
        {
            copy.Obstacles.Add(c);
        }
        foreach (Cell c in spec.Foods)
        {
            copy.Foods.Add(c);
        }
        return copy;
    }

    private static GridModel BuildGrid(LevelSpec spec)
    {
        var grid = new GridModel(spec.Origin, spec.Width, spec.Height);
        foreach (Cell c in spec.Floor)
        {
            grid.Floor.Add(c);
        }
        foreach (Cell c in spec.Obstacles)
        {
            grid.Obstacles.Add(c);
        }
        return grid;
    }

    private static Cell DirForChar(char ch)
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
}
