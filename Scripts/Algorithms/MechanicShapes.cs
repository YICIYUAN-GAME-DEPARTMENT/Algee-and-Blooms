using System;

namespace Growth.Algorithms;

/// <summary>
/// 机关生长形状（由 GDScript MechanicShapes 迁移，玩法权威见功能需求 §4.2.1）。
/// 统一八边形：曼哈顿菱形 ∩ 切比雪夫方框，面积序列 1/5/9/13/21。
/// </summary>
public static class MechanicShapes
{
    public const int MaxLevel = 4;
    /// <summary>机关初始阶段下限（负值只改核心颜色，不扩张、不占格）。玩法权威见功能需求 §4.2.5。</summary>
    public const int MinLevel = -10;

    /// <summary>格子 d 第一次进入机关形状的阶段；> MaxLevel 表示永不覆盖。</summary>
    public static int CoverLevel(Cell d)
    {
        int d1 = Math.Abs(d.X) + Math.Abs(d.Y);
        int dInf = Math.Max(Math.Abs(d.X), Math.Abs(d.Y));
        return Math.Max(0, Math.Max(2 * d1 - 2, 2 * dInf - 1));
    }

    /// <summary>(r, k)：r = 曼哈顿半径，k = 切比雪夫半径；lv 超出 0..MaxLevel 时钳制。</summary>
    public static (int R, int K) Radius(int level)
    {
        int lv = Math.Clamp(level, 0, MaxLevel);
        return ((int)Math.Floor((lv + 2) / 2.0), (int)Math.Floor((lv + 1) / 2.0));
    }
}
