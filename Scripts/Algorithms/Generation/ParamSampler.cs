using System;
using System.Collections.Generic;

namespace Growth.Algorithms.Generation;

/// <summary>一次生成的参数元组（L0 / Gain / I / cap / 机关初始阶段）。</summary>
public sealed class ParamSet
{
    public int InitialMaxLen;
    public int FoodLenGain;
    public int GrowthInterval;
    public int MechanismCap;
    public List<int> InitialLevels = new();
    /// <summary>规范串：L0|Gain|I|cap|a_k（逗号分隔）。</summary>
    public string Signature = "";
    /// <summary>按目标步数反解的必需花瓣数。</summary>
    public int RequiredFoods;
}

/// <summary>
/// 每关参数采样 + 可行域代数过滤（生成器算法规划 §8.1 / §8.2）。
/// 采样域：L0 3–7、Gain 2–4、I 4–8、cap 1–4、initial_level 0–cap。
/// 不满足代数的元组在采样阶段直接拒绝（O(1)），不进入行走。
/// </summary>
public static class ParamSampler
{
    private const int MaxTries = 64;

    internal static ParamSet Sample(DeterministicRng rng, int targetSteps, int mechCount)
    {
        if (targetSteps <= 0)
        {
            return null;
        }
        mechCount = Math.Max(mechCount, 0);
        for (int attempt = 0; attempt < MaxTries; attempt++)
        {
            int l0 = 3 + rng.NextInt(5);
            int gain = 2 + rng.NextInt(3);
            int interval = 5 + rng.NextInt(4);
            int cap = 1 + rng.NextInt(4);

            var levels = new List<int>(mechCount);
            bool levelsOk = true;
            for (int k = 0; k < mechCount; k++)
            {
                int a = rng.NextInt(cap + 1);
                if (cap - a < 1)
                {
                    levelsOk = false;
                    break;
                }
                levels.Add(a);
            }
            if (!levelsOk)
            {
                continue;
            }
            if (!Feasible(l0, gain, interval, cap, targetSteps, 0))
            {
                continue;
            }

            int required = (int)Math.Ceiling((targetSteps - l0) / (double)gain);
            required = Math.Max(required, 0);
            return new ParamSet
            {
                InitialMaxLen = l0,
                FoodLenGain = gain,
                GrowthInterval = interval,
                MechanismCap = cap,
                InitialLevels = levels,
                Signature = BuildSignature(l0, gain, interval, cap, levels),
                RequiredFoods = required,
            };
        }
        return null;
    }

    /// <summary>
    /// §8.2 可行域代数；floorCount > 0 时额外检查路线余量。
    /// 花瓣上限按尺寸放宽：12×12 档 ≤5（规划稿初值），18/20 档 ≤8（26–38 步长路线）。
    /// 末段余量（S_eff - L0 ∈ [Gain, 4Gain]）在路线行走阶段检查，不在参数采样阶段。
    /// </summary>
    internal static bool Feasible(int l0, int gain, int interval, int cap, int targetSteps, int floorCount)
    {
        if (targetSteps <= 0 || gain <= 0 || interval <= 0 || targetSteps <= l0)
        {
            return false;
        }
        int fReq = (int)Math.Ceiling((targetSteps - l0) / (double)gain);
        fReq = Math.Max(fReq, 0);
        int maxFoods = targetSteps >= 24 ? 8 : 5;
        if (fReq > maxFoods)
        {
            return false;
        }
        if (fReq > targetSteps / interval + 1)
        {
            return false;
        }
        if (interval * (cap + 1) > targetSteps)
        {
            return false;
        }
        if (floorCount > 0 && targetSteps > 0.9 * floorCount)
        {
            return false;
        }
        return true;
    }

    internal static string BuildSignature(int l0, int gain, int interval, int cap, IReadOnlyList<int> levels)
    {
        return $"{l0}|{gain}|{interval}|{cap}|{string.Join(",", levels)}";
    }
}
