using System;

namespace Growth.Algorithms.Scoring;

/// <summary>基于枝干先验/后验的计分方案（v5 原型，彼此独立可切换）。</summary>
public enum BranchScoreScheme
{
    /// <summary>静态欺骗：看着粗、实际走不通的假主干质量。</summary>
    StaticDeception,
    /// <summary>动态塌缩：开局可走的枝干在推进过程中消亡的速度与信息量。</summary>
    PosteriorCollapse,
    /// <summary>混合：枝干项为主 + 现有核心难度项兜底（推荐）。</summary>
    Hybrid,
    /// <summary>信息量：后验相对先验的 KL / 熵塌缩。</summary>
    Information,
}

/// <summary>
/// 枝干计分方案（原型）：输入 BranchPosteriorFeatures（可选 MetricVector），输出 0–100 原始难度。
/// 显示分再由锚点样条映射；这里只负责“原始难度”的排序与拉开差距。
/// </summary>
public static class BranchScoreSchemes
{
    /// <summary>原始值刻度偏移：把 L5 从 51 抬到 Hard 中段，并为 Expert 留出 60+ 尾部。</summary>
    public const int ScoreOffset = 7;
    /// <summary>L2≈26 以下为 Easy。</summary>
    public const int EasyMax = 35;
    /// <summary>L3≈45 / L4≈47 为 Normal；L5≈58 为 Hard。</summary>
    public const int NormalMax = 48;
    public const int HardMax = 60;

    /// <summary>原始值 → 最终分数刻度（线性偏移，保持原始值排序与间距）。</summary>
    public static int ScoreForRaw(double raw)
    {
        return (int)Math.Round(Math.Clamp(raw + ScoreOffset, 0, 200), MidpointRounding.AwayFromZero);
    }

    /// <summary>原始分 → 难度档（Easy / Normal / Hard / Expert）。</summary>
    public static string TierForScore(int score)
    {
        if (score < EasyMax)
        {
            return "Easy";
        }
        return score < NormalMax ? "Normal" : score < HardMax ? "Hard" : "Expert";
    }

    public static double Evaluate(BranchScoreScheme scheme, BranchPosteriorFeatures f,
        MetricVector metrics = null)
    {
        return scheme switch
        {
            BranchScoreScheme.StaticDeception => StaticDeception(f, metrics),
            BranchScoreScheme.PosteriorCollapse => PosteriorCollapse(f, metrics),
            BranchScoreScheme.Information => Information(f),
            _ => Hybrid(f, metrics),
        };
    }

    /// <summary>方案 A：假主干质量。最粗假枝干 + 开局看似可行但实际走不通的静态假主干 + 晚死诱饵。</summary>
    public static double StaticDeception(BranchPosteriorFeatures f, MetricVector metrics)
    {
        double branch = PureStatic(f) * LengthGate(f);
        double core = CoreNorm(metrics) * 100;
        return 0.50 * branch + 0.50 * core;
    }

    /// <summary>方案 B：后验塌缩。开局可走的假枝干在推进中消亡的比例 + KL 信息量 + 绝对存活深度。</summary>
    public static double PosteriorCollapse(BranchPosteriorFeatures f, MetricVector metrics)
    {
        double branch = PurePosterior(f) * LengthGate(f);
        double core = CoreNorm(metrics) * 100;
        return 0.65 * branch + 0.35 * core;
    }

    /// <summary>方案 C（推荐）：枝干后验为主（55%），现有核心难度项兜底（45%）。</summary>
    public static double Hybrid(BranchPosteriorFeatures f, MetricVector metrics)
    {
        double branch = 0.5 * PureStatic(f) + 0.5 * PurePosterior(f);
        branch *= LengthGate(f);
        double core = CoreNorm(metrics) * 100;
        return 0.55 * branch + 0.45 * core;
    }

    /// <summary>方案 D：信息量。后验相对先验需要多少比特才能识别真解（诊断用）。</summary>
    public static double Information(BranchPosteriorFeatures f)
    {
        double kl = Clamp01(f.KlBits / 8.0);
        double loss = LossRate(f);
        double entropy = Clamp01(f.EntropyDrop / 3.0);
        return 100 * Clamp01(0.50 * kl + 0.30 * loss + 0.20 * entropy);
    }

    private static double PureStatic(BranchPosteriorFeatures f)
    {
        double staticFake = PlausibleStaticFake(f);
        double late = Clamp01(f.LateTrapDepth / 5.0);
        return 100 * Clamp01(0.55 * f.TopFakeMass + 0.30 * staticFake + 0.15 * late);
    }

    private static double PurePosterior(BranchPosteriorFeatures f)
    {
        double loss = LossRate(f);
        double kl = Clamp01(f.KlBits / 8.0);
        double trap = Clamp01(f.TrapDepth / 12.0);
        double late = Clamp01(f.LateTrapDepth / 5.0);
        return 100 * Clamp01(0.35 * loss + 0.15 * kl + 0.25 * trap + 0.25 * late);
    }

    /// <summary>短关的晚死陷阱没有足够的承诺深度，按路线长度平方衰减。</summary>
    private static double LengthGate(BranchPosteriorFeatures f)
    {
        double t = Math.Clamp(f.Steps / 20.0, 0.0, 1.0);
        return Math.Max(0.15, t * t);
    }

    private static double CoreNorm(MetricVector metrics)
    {
        return metrics != null ? Clamp01(metrics.DCore / 65.0) : 0;
    }

    /// <summary>开局看似可行、实际从未可走的假枝干质量；若开局根本没有任何假枝干可走，则不计。</summary>
    public static double PlausibleStaticFake(BranchPosteriorFeatures f)
    {
        if (f.InitialAliveMass <= 0.01)
        {
            return 0;
        }
        double plausibility = Clamp01(f.InitialAliveMass / 0.30);
        return f.StaticFakeMass * plausibility;
    }

    /// <summary>开局可走的假枝干中，到终点前消亡的权重比例。</summary>
    public static double LossRate(BranchPosteriorFeatures f)
    {
        if (f.InitialAliveMass <= 0.01)
        {
            return 0;
        }
        return Clamp01((f.InitialAliveMass - f.SurvivalAuc) / f.InitialAliveMass);
    }

    private static double Clamp01(double value)
    {
        return Math.Clamp(value, 0.0, 1.0);
    }
}
