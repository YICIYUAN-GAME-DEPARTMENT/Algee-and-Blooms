using System;

namespace Growth.Algorithms.Generation;

/// <summary>v4 生成来源：结构图 / 噪波负型 / 两者混合（混合留待双引擎真正并行后启用）。</summary>
public enum GenerationSource
{
    Structured,
    Noise,
    Hybrid,
}

/// <summary>
/// v4 Portfolio 调度骨架（规划稿 §4.1）：
/// 用 hash(seed, level_index) 确定性地按档位比例选择结构图 / 噪波负型引擎。
/// 当前两个引擎分别映射到既有的模板机关（structured）与路线优先（noise）生成器，
/// 新引擎落地后只需替换映射，不影响调度比例与可复现性。
/// </summary>
public static class GenerationPortfolio
{
    /// <summary>结构图引擎占比（%），其余走噪波负型。初值待批次校准。</summary>
    public static int StructuredPercent(string tier)
    {
        return tier switch
        {
            "easy" => 20,
            "normal" => 40,
            "hard" => 60,
            "expert" => 80,
            _ => 40,
        };
    }

    /// <summary>按 seed / 关卡序号 / 档位确定性地选择来源（同输入永远同结果）。</summary>
    public static GenerationSource Choose(long seed, int levelIndex, string tier)
    {
        ulong hash = Mix((ulong)seed, (ulong)(uint)levelIndex);
        int roll = (int)(hash % 100UL);
        return roll < StructuredPercent(tier) ? GenerationSource.Structured : GenerationSource.Noise;
    }

    public static string Key(GenerationSource source)
    {
        return source switch
        {
            GenerationSource.Structured => "structured",
            GenerationSource.Noise => "noise",
            _ => "hybrid",
        };
    }

    /// <summary>splitmix64 双输入混合：固定、跨平台、无浮点。</summary>
    public static ulong Mix(ulong seed, ulong index)
    {
        ulong z = seed
            + 0x9E3779B97F4A7C15UL * (index + 1UL)
            + 0xD6E8FEB86659FD93UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
