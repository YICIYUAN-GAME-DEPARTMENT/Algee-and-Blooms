using System;

namespace Growth.Algorithms;

/// <summary>
/// 网格坐标（值类型）。算法层内部统一使用，避免依赖 Godot 的装箱/哈希差异。
/// </summary>
public readonly struct Cell : IEquatable<Cell>
{
    public readonly int X;
    public readonly int Y;

    public Cell(int x, int y)
    {
        X = x;
        Y = y;
    }

    public static Cell Zero => new(0, 0);

    public static Cell operator +(Cell a, Cell b) => new(a.X + b.X, a.Y + b.Y);

    public static Cell operator -(Cell a, Cell b) => new(a.X - b.X, a.Y - b.Y);

    public bool Equals(Cell other) => X == other.X && Y == other.Y;

    public override bool Equals(object obj) => obj is Cell other && Equals(other);

    /// <summary>固定哈希（FNV-1a 变体），不依赖进程随机种子。</summary>
    public override int GetHashCode()
    {
        unchecked
        {
            int h = (int)2166136261;
            h = (h ^ X) * 16777619;
            h = (h ^ Y) * 16777619;
            return h;
        }
    }

    public override string ToString() => $"({X},{Y})";
}
