using System;
using System.Collections.Generic;

namespace Growth.Algorithms.Model;

/// <summary>公平性判定参数（生成算法 v3 §3.3-P4）；初值需按人工验收调整。</summary>
public sealed class FairnessOptions
{
    /// <summary>AUC 硬约束阈值 θ_A。</summary>
    public double ThetaA = 0.75;
    /// <summary>模糊隶属度硬约束阈值 θ_F。</summary>
    public double ThetaF = 0.6;
    /// <summary>μ_fair 的软化宽度 δ。</summary>
    public double Delta = 0.1;
    /// <summary>“有吸引力”判定：静态剩余距离领先规范解的格数。</summary>
    public int AttractionGap = 2;
}

/// <summary>规范路线上的一个决策分支样本。</summary>
public sealed class BranchSample
{
    /// <summary>到达该分支的步数（1-based）。</summary>
    public int Step;
    public Cell Cell;
    /// <summary>感知危险 P(b, step)。</summary>
    public double Perceived;
    /// <summary>R1 可行核判定为不可赢。</summary>
    public bool Unsafe;
    /// <summary>静态距离看起来比规范解更短（容易骗到玩家）。</summary>
    public bool Attractive;
    /// <summary>模糊隶属度（仅 Unsafe &amp; Attractive 分支有意义）。</summary>
    public double MuFair = 1.0;
}

/// <summary>公平性评估结果。公平性是有效性约束，不进入难度分。</summary>
public sealed class FairnessReport
{
    /// <summary>AUC：用 P_b 预测 unsafe(b)；无 unsafe 分支记 1，无 safe 分支记 0。</summary>
    public double Auc = 1.0;
    /// <summary>有吸引力的 unsafe 分支中最小的 μ_fair；无此类分支记 1。</summary>
    public double MinMuFair = 1.0;
    public int BranchCount;
    public int UnsafeCount;
    public int AttractiveUnsafeCount;
    /// <summary>该关卡是否存在 safe 分支参照（否则 AUC 不可信）。</summary>
    public bool HasSafeReference = true;
    public List<BranchSample> Branches = new();

    public bool Passed(FairnessOptions options = null)
    {
        FairnessOptions opts = options ?? new FairnessOptions();
        return Auc >= opts.ThetaA && MinMuFair >= opts.ThetaF;
    }
}

/// <summary>
/// 公平性评估：对规范路线上每个决策点的合法分支，
/// 用 R1 可行核判定 unsafe，用感知危险场预测，计算 AUC 与 μ_fair。
/// 不参与规则判定；分支合法性使用 M(t−1)（与 RuleState 的移动时序一致）。
/// </summary>
public sealed class FairnessEvaluator
{
    private readonly LevelSpec _spec;
    private readonly TimeExpandedModel _model;
    private readonly PerceivedDangerField _field;
    private readonly FairnessOptions _options;
    private readonly HashSet<Cell> _foodSet;

    public FairnessEvaluator(
        LevelSpec spec,
        TimeExpandedModel model,
        PerceivedDangerField field,
        FairnessOptions options = null)
    {
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _field = field ?? throw new ArgumentNullException(nameof(field));
        _options = options ?? new FairnessOptions();
        _foodSet = new HashSet<Cell>(_spec.Foods);
    }

    public FairnessReport Evaluate(string route)
    {
        var report = new FairnessReport();
        if (string.IsNullOrEmpty(route))
        {
            return report;
        }

        var samples = new List<BranchSample>();
        Cell head = _spec.Spawn;
        int k = 0;

        for (int i = 0; i < route.Length; i++)
        {
            if (!_model.TryIndexOf(head, out int headIndex))
            {
                break;
            }
            Cell chosen = head + DirForChar(route[i]);
            int remaining = route.Length - i;
            int growthStage = _model.GrowthCount(i);

            foreach (int neighbor in _model.WalkableNeighbors(headIndex))
            {
                Cell branchCell = _model.CellAt(neighbor);
                if (branchCell.Equals(chosen))
                {
                    continue;
                }
                if (_model.IsMechAt(growthStage, neighbor))
                {
                    continue;
                }
                int branchK = Math.Min(k + (_foodSet.Contains(branchCell) ? 1 : 0), _model.FoodCount);
                bool unsafeBranch = !_model.IsViable(branchK, i + 1, neighbor);
                double perceived = _field.At(neighbor, i + 1);
                int distGoal = _model.StaticDistanceFromGoal(branchCell);
                bool attractive = distGoal < TimeExpandedModel.INF
                    && distGoal <= remaining - _options.AttractionGap;
                samples.Add(new BranchSample
                {
                    Step = i + 1,
                    Cell = branchCell,
                    Perceived = perceived,
                    Unsafe = unsafeBranch,
                    Attractive = attractive,
                });
            }

            if (_foodSet.Contains(chosen))
            {
                k = Math.Min(k + 1, _model.FoodCount);
            }
            head = chosen;
        }

        report.Branches = samples;
        report.BranchCount = samples.Count;
        report.Auc = Auc(samples);

        var safePerceived = new Dictionary<int, (double Sum, int Count)>();
        int unsafeCount = 0;
        foreach (BranchSample sample in samples)
        {
            if (sample.Unsafe)
            {
                unsafeCount++;
                continue;
            }
            if (!safePerceived.TryGetValue(sample.Step, out var agg))
            {
                agg = (0.0, 0);
            }
            safePerceived[sample.Step] = (agg.Sum + sample.Perceived, agg.Count + 1);
        }
        report.UnsafeCount = unsafeCount;
        report.HasSafeReference = safePerceived.Count > 0;

        double minMuFair = 1.0;
        int attractiveUnsafe = 0;
        foreach (BranchSample sample in samples)
        {
            if (!sample.Unsafe || !sample.Attractive)
            {
                continue;
            }
            attractiveUnsafe++;
            double safe = 0.0;
            if (safePerceived.TryGetValue(sample.Step, out var agg) && agg.Count > 0)
            {
                safe = agg.Sum / agg.Count;
            }
            sample.MuFair = Smoothstep((sample.Perceived - safe + _options.Delta) / _options.Delta);
            minMuFair = Math.Min(minMuFair, sample.MuFair);
        }
        report.AttractiveUnsafeCount = attractiveUnsafe;
        report.MinMuFair = minMuFair;

        return report;
    }

    /// <summary>AUC：用感知危险预测 unsafe。无 unsafe -> 1（无可骗分支）；无 safe -> 0（无法区分）。</summary>
    public static double Auc(IReadOnlyList<BranchSample> samples)
    {
        int positives = 0;
        int negatives = 0;
        foreach (BranchSample sample in samples)
        {
            if (sample.Unsafe)
            {
                positives++;
            }
            else
            {
                negatives++;
            }
        }
        if (positives == 0)
        {
            return 1.0;
        }
        if (negatives == 0)
        {
            return 0.0;
        }

        long wins = 0;
        long ties = 0;
        foreach (BranchSample p in samples)
        {
            if (!p.Unsafe)
            {
                continue;
            }
            foreach (BranchSample n in samples)
            {
                if (n.Unsafe)
                {
                    continue;
                }
                if (p.Perceived > n.Perceived)
                {
                    wins++;
                }
                else if (Math.Abs(p.Perceived - n.Perceived) < 1e-12)
                {
                    ties++;
                }
            }
        }
        return (wins + 0.5 * ties) / (positives * (double)negatives);
    }

    private static double Smoothstep(double x)
    {
        double t = Math.Clamp(x, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
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
