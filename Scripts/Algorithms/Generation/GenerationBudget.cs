using System;

namespace Growth.Algorithms.Generation;

/// <summary>生成预算档位（规划稿 §10.1）。</summary>
public enum GenerationBudgetTier
{
    /// <summary>首关：120 ms 内必须出关。</summary>
    Bootstrap,
    /// <summary>默认：玩家解当前关时生成下一关（分帧）。</summary>
    Background,
    /// <summary>关卡间加载屏。</summary>
    Loading,
    /// <summary>空闲预取（每帧 2–4 ms 时间切片）。</summary>
    Prefetch,
}

/// <summary>
/// 单关生成预算：工作单元 = 构建次数 × 面积折算（W·H / 144）。
/// 工作单元是确定性上限；墙钟毫秒只作熔断安全阀，触发时返回存档最优并标记 budget_aborted。
/// </summary>
public sealed class GenerationBudget
{
    /// <summary>工作单元上限；0 = 不限。</summary>
    public int MaxUnits { get; set; }

    /// <summary>本次生成累计工作毫秒上限；0 = 不限。</summary>
    public double MaxMilliseconds { get; set; }

    /// <summary>候选数上限；0 = 沿用 GeneratorConfig。</summary>
    public int MaxCandidates { get; set; }

    /// <summary>每个候选的成功构建次数上限；0 = 沿用 GeneratorConfig。</summary>
    public int MaxSearchAttempts { get; set; }

    public static GenerationBudget Unlimited()
    {
        return new GenerationBudget();
    }

    public static GenerationBudget For(GenerationBudgetTier tier)
    {
        return tier switch
        {
            GenerationBudgetTier.Bootstrap => new GenerationBudget
            {
                MaxUnits = 12, MaxMilliseconds = 120, MaxCandidates = 1, MaxSearchAttempts = 4,
            },
            GenerationBudgetTier.Background => new GenerationBudget
            {
                MaxUnits = 60, MaxMilliseconds = 1500, MaxCandidates = 3, MaxSearchAttempts = 10,
            },
            GenerationBudgetTier.Loading => new GenerationBudget
            {
                MaxUnits = 600, MaxMilliseconds = 10000, MaxCandidates = 6, MaxSearchAttempts = 24,
            },
            GenerationBudgetTier.Prefetch => new GenerationBudget
            {
                MaxUnits = 60, MaxMilliseconds = 1500, MaxCandidates = 2, MaxSearchAttempts = 10,
            },
            _ => new GenerationBudget(),
        };
    }

    public static GenerationBudgetTier ParseTier(string name)
    {
        return name switch
        {
            "bootstrap" => GenerationBudgetTier.Bootstrap,
            "loading" => GenerationBudgetTier.Loading,
            "prefetch" => GenerationBudgetTier.Prefetch,
            _ => GenerationBudgetTier.Background,
        };
    }

    /// <summary>面积折算：12×12 = 1 单元，20×20 ≈ 2.8 单元。</summary>
    public static double AreaFactor(int width, int height)
    {
        return Math.Max(1, width) * Math.Max(1, height) / 144.0;
    }
}
