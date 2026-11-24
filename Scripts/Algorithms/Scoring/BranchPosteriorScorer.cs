using System;
using System.Collections.Generic;
using Growth.Algorithms.Generation;

namespace Growth.Algorithms.Scoring;

/// <summary>
/// 黏菌枝干后验特征（v5 计分原型）：
/// 静态先验 = 生成时撒行走体得到的枝干粗细分布；
/// 动态后验 = 沿规范解每走一步，用当前身体 / 已生长机关 / 剩余步长预算
///            重新判断“显著分叉”是否还付得起，记录消亡。
/// 只做测量，不改动现有评分。
/// </summary>
public sealed class BranchPosteriorFeatures
{
    public int Steps;
    public int BranchCount;
    /// <summary>显著分叉数（与真解重叠 &lt; 50% 且粗细 ≥ 主枝干 15%）。</summary>
    public int SignificantBranches;
    public int TrueBranchIndex = -1;
    public bool TrueBranchMissing;
    public bool TopIsTrue;

    // ── 静态先验 ────────────────────────────────────────────
    public double PriorEntropy;
    public double TopThickness;
    public double SecondThickness;
    public double RatioSecondTop;
    /// <summary>显著分叉的粗细权重之和（仅统计偏离真解的枝干）。</summary>
    public double FakeMass;
    /// <summary>最粗显著分叉的权重。</summary>
    public double TopFakeMass;

    // ── 动态后验 ────────────────────────────────────────────
    /// <summary>显著分叉“仍付得起”的平均权重（1 = 一直可行，0 = 开局即不可行）。</summary>
    public double SurvivalAuc;
    public double AliveMassEnd;
    /// <summary>t=0 时可走的显著分叉权重（其余为“静态假主干”）。</summary>
    public double InitialAliveMass;
    /// <summary>开局就不可行的显著分叉权重。</summary>
    public double StaticFakeMass;
    /// <summary>Σ w × 消亡绝对步数（越晚越难）。</summary>
    public double TrapDepth;
    /// <summary>后半程消亡贡献的权重×步数。</summary>
    public double LateTrapDepth;
    public double EntropyDrop;
    public double MeanPosteriorEntropy;
    public double KlBits;
    /// <summary>曾可行、后来因预算/机关/身体而消亡的显著分叉权重。</summary>
    public double DeathMass;
    /// <summary>Σ w × 消亡步 / 总步；越晚消亡越接近 1。</summary>
    public double WeightedDeathFrac;
    /// <summary>后半程才消亡的显著分叉权重（“一直看着像能走”的诱饵）。</summary>
    public double LateDeathMass;
    /// <summary>显著分叉全部消亡的步数占比（1 = 直到终点仍有分叉可走）。</summary>
    public double CollapseStep;

    public Dictionary<string, double> ToDictionary()
    {
        return new Dictionary<string, double>
        {
            ["steps"] = Steps,
            ["branch_count"] = BranchCount,
            ["significant_branches"] = SignificantBranches,
            ["true_branch_index"] = TrueBranchIndex,
            ["true_branch_missing"] = TrueBranchMissing ? 1 : 0,
            ["top_is_true"] = TopIsTrue ? 1 : 0,
            ["prior_entropy"] = PriorEntropy,
            ["top_thickness"] = TopThickness,
            ["second_thickness"] = SecondThickness,
            ["ratio_second_top"] = RatioSecondTop,
            ["fake_mass"] = FakeMass,
            ["top_fake_mass"] = TopFakeMass,
            ["survival_auc"] = SurvivalAuc,
            ["alive_mass_end"] = AliveMassEnd,
            ["initial_alive_mass"] = InitialAliveMass,
            ["static_fake_mass"] = StaticFakeMass,
            ["trap_depth"] = TrapDepth,
            ["late_trap_depth"] = LateTrapDepth,
            ["entropy_drop"] = EntropyDrop,
            ["mean_posterior_entropy"] = MeanPosteriorEntropy,
            ["kl_bits"] = KlBits,
            ["death_mass"] = DeathMass,
            ["weighted_death_frac"] = WeightedDeathFrac,
            ["late_death_mass"] = LateDeathMass,
            ["collapse_step"] = CollapseStep,
        };
    }
}

/// <summary>黏菌枝干后验测量器（计分方案原型，不参与现有评分）。</summary>
public static class BranchPosteriorScorer
{
    private const int Walkers = 600;
    private const int WalkerSteps = 300;
    private const double GreedyChance = 0.55;
    private const double MinBranchThicknessRatio = 0.15;

    [ThreadStatic] private static RuleState _simState;
    [ThreadStatic] private static RuleState _routeState;

    private static RuleState SimState()
    {
        _simState ??= new RuleState();
        _simState.SuppressDeadlockChecks = true;
        _simState.SuppressPathCheck = true;
        return _simState;
    }

    private static RuleState RouteState()
    {
        _routeState ??= new RuleState();
        _routeState.SuppressDeadlockChecks = true;
        _routeState.SuppressPathCheck = true;
        return _routeState;
    }

    public static BranchPosteriorFeatures Extract(LevelSpec spec, string route, long seed = 0)
    {
        var features = new BranchPosteriorFeatures { Steps = route.Length };
        if (string.IsNullOrEmpty(route))
        {
            return features;
        }
        var rng = new DeterministicRng(seed != 0
            ? (ulong)seed
            : (ulong)spec.LevelId * 0x9E3779B97F4A7C15UL
                + (ulong)(spec.Width * 131 + spec.Height * 17 + route.Length));
        BranchNetwork network = PhysarumBranchModel.Build(
            spec, rng, Walkers, WalkerSteps, GreedyChance);
        if (!network.Reachable || network.Branches.Count == 0)
        {
            features.TrueBranchMissing = true;
            features.SurvivalAuc = 1;
            return features;
        }

        List<Cell> routeCells = RouteCells(spec, route);
        var routeSet = new HashSet<Cell>(routeCells);
        features.BranchCount = network.Branches.Count;
        features.TopThickness = network.Branches[0].Thickness;
        features.SecondThickness = network.Branches.Count > 1 ? network.Branches[1].Thickness : 0;
        features.RatioSecondTop = features.TopThickness > 0
            ? features.SecondThickness / features.TopThickness : 0;

        // 真解 = 最后 3 个方向签名一致的枝干；找不到时退回“与路线重叠最高”。
        string suffix = route.Length >= 3 ? route.Substring(route.Length - 3) : "";
        var overlaps = new double[network.Branches.Count];
        features.TrueBranchIndex = FindTrueBranch(network, overlaps, routeSet, suffix);
        features.TrueBranchMissing = features.TrueBranchIndex < 0;
        features.TopIsTrue = features.TrueBranchIndex == 0;

        // 只统计“显著分叉”：终点入口签名不同、粗细够、且入口真的落在终点上。
        var tracked = new List<int>();
        var approaches = new List<Approach>();
        for (int i = 0; i < network.Branches.Count; i++)
        {
            if (i == features.TrueBranchIndex)
            {
                continue;
            }
            Branch branch = network.Branches[i];
            if (branch.Thickness < features.TopThickness * MinBranchThicknessRatio)
            {
                continue;
            }
            if (branch.Signature.Length != 3 || branch.Signature == suffix)
            {
                continue;
            }
            if (!TryBuildApproach(branch, spec, out Approach approach))
            {
                continue;
            }
            tracked.Add(i);
            approaches.Add(approach);
        }
        features.SignificantBranches = tracked.Count;
        if (tracked.Count == 0)
        {
            features.SurvivalAuc = 1;
            features.CollapseStep = 0;
            return features;
        }

        double allThickness = 0;
        foreach (Branch branch in network.Branches)
        {
            allThickness += branch.Thickness;
        }
        var weights = new double[tracked.Count];
        for (int i = 0; i < tracked.Count; i++)
        {
            weights[i] = allThickness > 0
                ? network.Branches[tracked[i]].Thickness / allThickness : 0;
            features.FakeMass += weights[i];
        }
        features.TopFakeMass = weights[0];
        double massTotal = Math.Max(1e-9, features.FakeMass);
        var normalized = new double[weights.Length];
        for (int i = 0; i < weights.Length; i++)
        {
            normalized[i] = weights[i] / massTotal;
        }
        double priorEntropy = Entropy(normalized);
        features.PriorEntropy = priorEntropy;

        var state = SimState();
        state.Setup(spec);
        state.Start();
        var grid = BuildGrid(spec);
        int interval = Math.Max(1, spec.GrowthInterval);
        var lastAlive = new int[weights.Length];
        var startAlive = new bool[weights.Length];
        for (int i = 0; i < lastAlive.Length; i++)
        {
            lastAlive[i] = -1;
        }
        var eaten = new HashSet<Cell>();
        double aucSum = 0;
        double entropySum = 0;
        double klSum = 0;
        int samples = 0;
        int collapseStep = route.Length;
        bool collapsed = false;

        for (int t = 0; t <= route.Length; t++)
        {
            var blocked = new HashSet<Cell>(spec.Obstacles);
            int grown = t / interval;
            foreach (KeyValuePair<Cell, int> kv in state.CoverSteps)
            {
                if (kv.Value <= grown)
                {
                    blocked.Add(kv.Key);
                }
            }
            foreach (Cell c in state.Trail)
            {
                blocked.Add(c);
            }
            Dictionary<Cell, int> fromHead = DistMap(grid, state.Head, blocked);
            int budget = state.GetRemainingSteps()
                + spec.FoodLenGain * Math.Max(0, spec.Foods.Count - eaten.Count);
            var alive = new bool[weights.Length];
            double aliveMass = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                alive[i] = ApproachAlive(approaches[i], grid, blocked, fromHead, spec.Goal, budget);
                if (alive[i])
                {
                    aliveMass += weights[i];
                    lastAlive[i] = t;
                    if (t == 0)
                    {
                        startAlive[i] = true;
                    }
                }
            }
            aucSum += aliveMass / massTotal;
            samples++;
            if (t == 0)
            {
                features.InitialAliveMass = aliveMass / massTotal;
            }
            double posteriorEntropy = AliveEntropy(normalized, alive, aliveMass / massTotal);
            entropySum += posteriorEntropy;
            klSum += Kl(normalized, alive, aliveMass / massTotal);
            if (!collapsed && aliveMass / massTotal <= 0.05)
            {
                collapseStep = t;
                collapsed = true;
            }
            if (t == route.Length)
            {
                features.AliveMassEnd = aliveMass / massTotal;
                features.MeanPosteriorEntropy = entropySum / samples;
                features.KlBits = klSum / samples;
                features.EntropyDrop = priorEntropy - posteriorEntropy;
            }
            if (t < route.Length)
            {
                Cell dir = DirCell(route[t]);
                RuleState.StepOutcome outcome = state.StepCore(dir);
                if (!outcome.Accepted)
                {
                    break;
                }
                if (outcome.AteFood)
                {
                    eaten.Add(state.Head);
                }
            }
        }

        features.SurvivalAuc = samples > 0 ? aucSum / samples : 1;
        features.CollapseStep = route.Length > 0 ? Math.Clamp(collapseStep / (double)route.Length, 0, 1) : 0;
        for (int i = 0; i < weights.Length; i++)
        {
            if (!startAlive[i])
            {
                features.StaticFakeMass += weights[i];
                continue;
            }
            if (lastAlive[i] >= route.Length)
            {
                continue;
            }
            int deathStep = Math.Max(0, lastAlive[i]);
            features.TrapDepth += weights[i] * deathStep;
            features.LateTrapDepth += weights[i] * Math.Max(0, deathStep - route.Length / 2);
            features.DeathMass += weights[i];
            double deathFrac = route.Length > 0 ? lastAlive[i] / (double)route.Length : 0;
            features.WeightedDeathFrac += weights[i] * deathFrac;
            if (deathFrac >= 0.5)
            {
                features.LateDeathMass += weights[i];
            }
        }
        return features;
    }

    private static int FindTrueBranch(BranchNetwork network, double[] overlaps,
        HashSet<Cell> routeSet, string suffix)
    {
        int bySignature = -1;
        int byOverlap = -1;
        double bestOverlap = 0;
        for (int i = 0; i < network.Branches.Count; i++)
        {
            Branch branch = network.Branches[i];
            int hit = 0;
            foreach (Cell c in branch.Representative)
            {
                if (routeSet.Contains(c))
                {
                    hit++;
                }
            }
            overlaps[i] = branch.Representative.Count > 0
                ? hit / (double)branch.Representative.Count : 0;
            if (overlaps[i] > bestOverlap)
            {
                bestOverlap = overlaps[i];
                byOverlap = i;
            }
            if (suffix.Length == 3 && branch.Signature == suffix
                && (bySignature < 0 || branch.Thickness > network.Branches[bySignature].Thickness))
            {
                bySignature = i;
            }
        }
        if (bySignature >= 0)
        {
            return bySignature;
        }
        return bestOverlap >= 0.35 ? byOverlap : -1;
    }

    /// <summary>从枝干代表路线里找一段“签名 3 步刚好落在终点”的入口。</summary>
    private static bool TryBuildApproach(Branch branch, LevelSpec spec, out Approach approach)
    {
        approach = default;
        if (branch.Signature.Length != 3 || branch.Representative.Count == 0)
        {
            return false;
        }
        var floor = new HashSet<Cell>(spec.Floor);
        var obstacles = new HashSet<Cell>(spec.Obstacles);
        Cell d0 = DirCell(branch.Signature[0]);
        Cell d1 = DirCell(branch.Signature[1]);
        Cell d2 = DirCell(branch.Signature[2]);
        foreach (Cell cell in branch.Cells)
        {
            Cell anchor = cell;
            Cell a = anchor + d0;
            Cell b = a + d1;
            Cell c = b + d2;
            if (!c.Equals(spec.Goal))
            {
                continue;
            }
            if (!Walkable(floor, obstacles, a) || !Walkable(floor, obstacles, b)
                || !Walkable(floor, obstacles, c))
            {
                continue;
            }
            approach = new Approach(anchor, d0, d1, d2);
            return true;
        }
        return false;
    }

    private static bool Walkable(HashSet<Cell> floor, HashSet<Cell> obstacles, Cell c)
    {
        return floor.Contains(c) && !obstacles.Contains(c);
    }

    /// <summary>枝干进入终点的最后 3 步（锚点 + 3 个方向）。</summary>
    private readonly struct Approach
    {
        public readonly bool Valid;
        public readonly Cell Anchor;
        public readonly Cell D0;
        public readonly Cell D1;
        public readonly Cell D2;

        public Approach(Cell anchor, Cell d0, Cell d1, Cell d2)
        {
            Valid = true;
            Anchor = anchor;
            D0 = d0;
            D1 = d1;
            D2 = d2;
        }
    }

    /// <summary>入口是否仍可达且最后 3 步可走（受身体 / 机关 / 剩余步长预算限制）。</summary>
    private static bool ApproachAlive(Approach approach, GridModel grid, HashSet<Cell> blocked,
        Dictionary<Cell, int> fromHead, Cell goal, int budget)
    {
        if (!approach.Valid || !fromHead.TryGetValue(approach.Anchor, out int dh))
        {
            return false;
        }
        if (dh + 3 > budget)
        {
            return false;
        }
        Cell cur = approach.Anchor;
        foreach (Cell dir in new[] { approach.D0, approach.D1, approach.D2 })
        {
            cur += dir;
            if (!grid.IsFloor(cur) || blocked.Contains(cur))
            {
                return false;
            }
        }
        return cur.Equals(goal);
    }

    private static double Entropy(double[] weights)
    {
        double h = 0;
        foreach (double w in weights)
        {
            if (w > 1e-9)
            {
                h -= w * Math.Log2(w);
            }
        }
        return h;
    }

    private static double AliveEntropy(double[] weights, bool[] alive, double aliveMass)
    {
        if (aliveMass <= 1e-9)
        {
            return 0;
        }
        double h = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            if (!alive[i])
            {
                continue;
            }
            double q = weights[i] / aliveMass;
            if (q > 1e-9)
            {
                h -= q * Math.Log2(q);
            }
        }
        return h;
    }

    private static double Kl(double[] weights, bool[] alive, double aliveMass)
    {
        if (aliveMass <= 1e-9)
        {
            return 0;
        }
        double kl = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            double p = weights[i];
            double q = alive[i] ? p / aliveMass : 1e-6;
            if (p > 1e-9)
            {
                kl += p * Math.Log2(p / q);
            }
        }
        return kl;
    }

    private static Dictionary<Cell, int> DistMap(GridModel grid, Cell start, HashSet<Cell> blocked)
    {
        var dist = new Dictionary<Cell, int> { [start] = 0 };
        var queue = new Queue<Cell>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            Cell cur = queue.Dequeue();
            int nd = dist[cur] + 1;
            foreach (Cell n in GridModel.Neighbors(cur))
            {
                if (dist.ContainsKey(n) || !grid.IsFloor(n) || blocked.Contains(n))
                {
                    continue;
                }
                dist[n] = nd;
                queue.Enqueue(n);
            }
        }
        return dist;
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

    private static List<Cell> RouteCells(LevelSpec spec, string route)
    {
        var state = RouteState();
        state.Setup(spec);
        state.Start();
        var cells = new List<Cell> { state.Head };
        foreach (char ch in route)
        {
            RuleState.StepOutcome outcome = state.StepCore(DirCell(ch));
            if (!outcome.Accepted)
            {
                break;
            }
            cells.Add(state.Head);
        }
        return cells;
    }

    private static Cell DirCell(char ch)
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
