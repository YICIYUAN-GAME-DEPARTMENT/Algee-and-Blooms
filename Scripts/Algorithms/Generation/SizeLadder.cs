namespace Growth.Algorithms.Generation;

/// <summary>
/// 尺寸阶梯（生成器算法规划 §6.2）：10×10 – 20×20。
/// 难度档决定采样区间；生成失败时由外层换 attempt 重新采样（自然降档）。
/// </summary>
public static class SizeLadder
{
    internal static (int Width, int Height) Choose(DeterministicRng rng, string tier)
    {
        int[] sizes = tier switch
        {
            "easy" => new[] { 10, 12 },
            "normal" => new[] { 12, 14 },
            "hard" => new[] { 14, 16, 18 },
            "expert" => new[] { 18, 20 },
            _ => new[] { 12, 14 },
        };
        int size = sizes[rng.NextInt(sizes.Length)];
        return (size, size);
    }
}
