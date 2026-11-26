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

    /// <summary>单次构建尝试的时间片上限（毫秒，0 = 不限）；超时由生成器协作中止并换下一次尝试。</summary>
    public double AttemptMilliseconds { get; set; }

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
                MaxUnits = 80, MaxMilliseconds = 4000, MaxCandidates = 2, MaxSearchAttempts = 12,
                AttemptMilliseconds = 300,
            },
            GenerationBudgetTier.Background => new GenerationBudget
            {
                MaxUnits = 300, MaxMilliseconds = 8000, MaxCandidates = 4, MaxSearchAttempts = 24,
                AttemptMilliseconds = 20,
            },
            // 加载屏生成已移到后台线程：不再靠时间片让出主线程，
            // 释放为 0（不限单次尝试），避免中途丢弃有效尝试；总预算 / 工作单元仍兜底。
            GenerationBudgetTier.Loading => new GenerationBudget
            {
                MaxUnits = 3000, MaxMilliseconds = 90000, MaxCandidates = 8, MaxSearchAttempts = 64,
                AttemptMilliseconds = 0,
            },
            GenerationBudgetTier.Prefetch => new GenerationBudget
            {
                MaxUnits = 200, MaxMilliseconds = 6000, MaxCandidates = 3, MaxSearchAttempts = 16,
                AttemptMilliseconds = 15,
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
