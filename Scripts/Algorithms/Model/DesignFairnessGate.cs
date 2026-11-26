using System;
using System.Collections.Generic;
using Growth.Algorithms.Search;

namespace Growth.Algorithms.Model;

/// <summary>设计公平门参数（v4 规划稿 §9 G3；初值按批次校准）。</summary>
public sealed class DesignFairnessOptions
{
    /// <summary>规范解允许的支配差距（步）：最优解比规范解短超过该值 = 存在支配捷径。</summary>
    public int DominanceDelta = 2;
    /// <summary>规范解与最优解的容差（步）。</summary>
    public int CanonicalTolerance = 2;
    /// <summary>诱饵容差：吃完诱饵后的最短胜利步数 &gt; 规范解 + 该值才算“必败诱饵”。</summary>
    public int DecoySlack = 2;
    /// <summary>机关必要性的最小步数下降。</summary>
    public int GateDropThreshold = 2;
    /// <summary>严格模式（hard / expert）：诱饵必败与机关必要也作为硬条件。</summary>
    public bool Strict;
    /// <summary>调用方已知规范解来自精确求解最短路时置 true，跳过重复全图求解。</summary>
    public bool AssumeCanonicalOptimal;
    /// <summary>单次探测求解的节点预算。</summary>
    public int SolveBudget = 400_000;
    /// <summary>单关最多检查的诱饵数。</summary>
    public int MaxDecoysChecked = 2;
    /// <summary>单关最多检查的机关数。</summary>
    public int MaxGatesChecked = 3;
    /// <summary>true = 每个机关都必须必要；false = 至少一个必要。</summary>
    public bool RequireEveryGateNecessary;

    public static DesignFairnessOptions ForTier(string tier)
    {
        bool strict = tier is "hard" or "expert";
        bool expert = tier == "expert";
        return new DesignFairnessOptions
        {
            Strict = strict,
            // hard 只做轻量结构复核（1 诱饵 + 1 机关），expert 才做完整 Top-K 证明。
            SolveBudget = expert ? 400_000 : 200_000,
            MaxDecoysChecked = expert ? 2 : 1,
            MaxGatesChecked = expert ? 3 : 1,
        };
    }
}

/// <summary>设计公平门结果。公平性是有效性约束，不进入难度分。</summary>
public sealed class DesignFairnessReport
{
    public bool Passed = true;
    /// <summary>所有结论均由求解器证实；UNKNOWN 时为 false（不因此判失败）。</summary>
    public bool Verified = true;
    public bool Strict;

    public int CanonicalSteps;
    public int OptimalSteps;
    /// <summary>规范解 - 最优解（≥0）。</summary>
    public int DominanceGap;
    public bool DominanceFree = true;
    public bool CanonicalNearOptimal = true;

    public int DecoysChecked;
    public int DecoysProven;
    public int DecoysHarmless;
    public int DecoysUnverified;
    public bool DecoyProofPassed = true;

    public int GatesChecked;
    public int GatesNecessary;
    public int GatesUnverified;
    public int MaxGateStepDrop;
    public bool GateNecessityPassed = true;

    public readonly List<string> Violations = new();

    public Dictionary<string, double> ToMetrics()
    {
        return new Dictionary<string, double>
        {
            ["fair_passed"] = Passed ? 1 : 0,
            ["fair_verified"] = Verified ? 1 : 0,
            ["fair_strict"] = Strict ? 1 : 0,
            ["fair_canonical_steps"] = CanonicalSteps,
            ["fair_optimal_steps"] = OptimalSteps,
            ["fair_dominance_gap"] = DominanceGap,
            ["fair_dominance_free"] = DominanceFree ? 1 : 0,
            ["fair_canonical_near_optimal"] = CanonicalNearOptimal ? 1 : 0,
            ["fair_decoys"] = DecoysChecked,
            ["fair_decoys_proven"] = DecoysProven,
            ["fair_decoys_harmless"] = DecoysHarmless,
            ["fair_decoys_unverified"] = DecoysUnverified,
            ["fair_decoy_proof"] = DecoyProofPassed ? 1 : 0,
            ["fair_gates"] = GatesChecked,
            ["fair_gates_necessary"] = GatesNecessary,
            ["fair_gates_unverified"] = GatesUnverified,
            ["fair_gate_max_drop"] = MaxGateStepDrop,
            ["fair_gate_necessity"] = GateNecessityPassed ? 1 : 0,
        };
    }
}

/// <summary>
/// v4 设计公平门（G3）：不猜“玩家能不能看见”，只查“设计有没有被绕过”。
///   DominanceGap      是否存在明显更短的胜利路线（规范解被支配）；
///   CanonicalNearOptimal 规范解是否接近最优；
///   DecoyProof        吃完诱饵后是否仍能在 S + slack 内胜利（否则诱饵是假陷阱）；
///   GateNecessity     去掉单个机关后最优步数是否显著下降（机关是否真的挡路）。
/// 纯数据计算，不访问节点 / 场景 / 信号；求解失败只标记 unverified，不崩溃。
/// </summary>
public static class DesignFairnessGate
{
    [ThreadStatic] private static RuleState _state;
    [ThreadStatic] private static ExactBfsSolver _solver;

    private static RuleState State()
    {
        _state ??= new RuleState();
        _state.SuppressDeadlockChecks = false;
        _state.SuppressPathCheck = false;
        return _state;
    }

    private static ExactBfsSolver Solver()
    {
        _solver ??= new ExactBfsSolver();
        return _solver;
    }

    public static DesignFairnessReport Evaluate(LevelSpec spec, string canonicalRoute,
        DesignFairnessOptions options = null)
    {
        DesignFairnessOptions opts = options ?? new DesignFairnessOptions();
        var report = new DesignFairnessReport
        {
            Strict = opts.Strict,
            CanonicalSteps = canonicalRoute?.Length ?? 0,
        };
        if (spec == null || string.IsNullOrEmpty(canonicalRoute))
        {
            report.Passed = false;
            report.Verified = false;
            report.Violations.Add("empty_route");
            return report;
        }

        if (opts.AssumeCanonicalOptimal)
        {
            report.OptimalSteps = report.CanonicalSteps;
        }
        else
        {
            int optimal = SolveSteps(spec, -1, out string status, opts.SolveBudget);
            if (status == "SOLVABLE")
            {
                report.OptimalSteps = optimal;
            }
            else if (status == "UNSOLVABLE")
            {
                report.Verified = false;
                report.DominanceFree = false;
                report.CanonicalNearOptimal = false;
                report.Violations.Add("unsolvable");
            }
            else
            {
                report.Verified = false;
                report.OptimalSteps = report.CanonicalSteps;
            }
        }

        report.DominanceGap = Math.Max(0, report.CanonicalSteps - report.OptimalSteps);
        if (report.OptimalSteps > 0 && report.Verified)
        {
            report.DominanceFree = report.DominanceGap <= opts.DominanceDelta;
            report.CanonicalNearOptimal = report.DominanceGap <= opts.CanonicalTolerance;
            if (!report.DominanceFree)
            {
                report.Violations.Add("dominance_shortcut");
            }
        }
        else if (report.OptimalSteps <= 0)
        {
            report.Verified = false;
        }

        HashSet<Cell> visited = RouteCells(spec, canonicalRoute);
        if (opts.Strict)
        {
            EvaluateDecoys(spec, visited, opts, report);
            EvaluateGates(spec, opts, report);
        }

        report.Passed = report.DominanceFree
            && report.CanonicalNearOptimal
            && report.Violations.Count == 0
            && (!opts.Strict || (report.DecoyProofPassed && report.GateNecessityPassed));
        return report;
    }

    private static void EvaluateDecoys(LevelSpec spec, HashSet<Cell> visited,
        DesignFairnessOptions opts, DesignFairnessReport report)
    {
        var decoys = new List<int>();
        for (int i = 0; i < spec.Foods.Count; i++)
        {
            if (!visited.Contains(spec.Foods[i]))
            {
                decoys.Add(i);
            }
        }
        int check = Math.Min(decoys.Count, Math.Max(0, opts.MaxDecoysChecked));
        report.DecoysChecked = check;
        int limit = report.CanonicalSteps + opts.DecoySlack;
        for (int k = 0; k < check; k++)
        {
            int index = decoys[k];
            int steps = SolveSteps(spec, index, out string status, opts.SolveBudget);
            if (status == "UNSOLVABLE")
            {
                // 吃完诱饵后完全无解：标准陷阱。
                report.DecoysProven++;
            }
            else if (status == "SOLVABLE")
            {
                if (steps > limit)
                {
                    report.DecoysProven++;
                }
                else
                {
                    report.DecoysHarmless++;
                    report.DecoyProofPassed = false;
                    report.Violations.Add("decoy_harmless");
                }
            }
            else
            {
                report.DecoysUnverified++;
                report.Verified = false;
            }
        }
    }

    private static void EvaluateGates(LevelSpec spec, DesignFairnessOptions opts,
        DesignFairnessReport report)
    {
        int count = spec.MechCenters.Count;
        if (count == 0)
        {
            report.GateNecessityPassed = true;
            return;
        }
        if (!report.Verified)
        {
            report.GatesUnverified = count;
            return;
        }
        int check = Math.Min(count, Math.Max(0, opts.MaxGatesChecked));
        report.GatesChecked = check;
        for (int i = 0; i < check; i++)
        {
            LevelSpec variant = WithoutMechanism(spec, i);
            int steps = SolveSteps(variant, -1, out string status, opts.SolveBudget);
            if (status == "SOLVABLE")
            {
                int drop = report.OptimalSteps - steps;
                if (drop > report.MaxGateStepDrop)
                {
                    report.MaxGateStepDrop = drop;
                }
                if (drop >= opts.GateDropThreshold)
                {
                    report.GatesNecessary++;
                }
            }
            else
            {
                report.GatesUnverified++;
                report.Verified = false;
            }
        }
        report.GateNecessityPassed = opts.RequireEveryGateNecessary
            ? report.GatesNecessary == check && report.GatesUnverified == 0
            : report.GatesNecessary > 0;
        if (!report.GateNecessityPassed)
        {
            report.Violations.Add("gate_decorative");
        }
    }

    /// <summary>求解指定关卡；requiredFood ≥ 0 时要求胜利前吃过该下标食物。</summary>
    private static int SolveSteps(LevelSpec spec, int requiredFood, out string status, int budget)
    {
        RuleState state = State();
        state.Setup(spec);
        state.Start();
        ExactBfsSolver solver = Solver();
        solver.RequiredFoodIndex = requiredFood;
        Godot.Collections.Dictionary solved = solver.Solve(state, Math.Max(1, budget));
        solver.RequiredFoodIndex = -1;
        status = solved["status"].AsString();
        return status == "SOLVABLE" ? solved["steps"].AsInt32() : 0;
    }

    /// <summary>重放规范路线，收集头部经过的格（= 吃到 / 可吃到的食物集合）。</summary>
    private static HashSet<Cell> RouteCells(LevelSpec spec, string route)
    {
        var visited = new HashSet<Cell>();
        RuleState state = State();
        state.Setup(spec);
        state.Start();
        visited.Add(state.Head);
        foreach (char ch in route)
        {
            RuleState.StepOutcome outcome = state.StepCore(DirForChar(ch));
            if (!outcome.Accepted)
            {
                break;
            }
            visited.Add(state.Head);
        }
        return visited;
    }

    private static LevelSpec WithoutMechanism(LevelSpec spec, int index)
    {
        var clone = new LevelSpec
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
            Floor = new List<Cell>(spec.Floor),
            Obstacles = new List<Cell>(spec.Obstacles),
            Spawn = spec.Spawn,
            Goal = spec.Goal,
            Foods = new List<Cell>(spec.Foods),
            MechCenters = new List<Cell>(spec.MechCenters),
            MechInitialLevels = new List<int>(spec.MechInitialLevels),
            InitialMaxLen = spec.InitialMaxLen,
            FoodLenGain = spec.FoodLenGain,
            GrowthInterval = spec.GrowthInterval,
            MechanismCap = spec.MechanismCap,
            TargetSteps = spec.TargetSteps,
            ParamSignature = spec.ParamSignature,
            GenerationSource = spec.GenerationSource,
        };
        if (index >= 0 && index < clone.MechCenters.Count)
        {
            clone.MechCenters.RemoveAt(index);
        }
        if (index >= 0 && index < clone.MechInitialLevels.Count)
        {
            clone.MechInitialLevels.RemoveAt(index);
        }
        return clone;
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
