using System.Collections.Generic;

namespace Growth.Algorithms;

/// <summary>
/// 网格数据与可走判定（由 GDScript GridSystem 的判定部分迁移）。
/// 纯数据，不访问节点 / 场景 / 信号。
/// </summary>
public sealed class GridModel
{
    public Cell Origin;
    public int Width;
    public int Height;

    /// <summary>地板 = 可走区；空集合表示未启用地板限制（与 GridSystem 一致）。</summary>
    public readonly HashSet<Cell> Floor = new();

    public readonly HashSet<Cell> Obstacles = new();

    public GridModel(Cell origin, int width, int height)
    {
        Origin = origin;
        Width = width;
        Height = height;
    }

    public bool InBounds(Cell c)
    {
        return c.X >= Origin.X && c.Y >= Origin.Y
            && c.X < Origin.X + Width && c.Y < Origin.Y + Height;
    }

    public bool HasFloorRestriction => Floor.Count > 0;

    public bool IsFloor(Cell c)
    {
        return !HasFloorRestriction || Floor.Contains(c);
    }

    /// <summary>越界 / 非地板（若启用） / 静态障碍 / 机关占格 -> 不可走。</summary>
    public bool IsBlocked(Cell c, IReadOnlySet<Cell> mechCells)
    {
        if (!InBounds(c))
        {
            return true;
        }
        if (!IsFloor(c))
        {
            return true;
        }
        return Obstacles.Contains(c) || mechCells.Contains(c);
    }

    /// <summary>以 from 为源的最短步数（可走格同 IsBlocked，忽略自身身体）。</summary>
    public Dictionary<Cell, int> BfsDistMap(Cell from, IReadOnlySet<Cell> mechCells)
    {
        var dist = new Dictionary<Cell, int>();
        if (!InBounds(from))
        {
            return dist;
        }
        dist[from] = 0;
        var queue = new Queue<Cell>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            Cell cur = queue.Dequeue();
            int nd = dist[cur] + 1;
            foreach (Cell n in Neighbors(cur))
            {
                if (dist.ContainsKey(n) || IsBlocked(n, mechCells))
                {
                    continue;
                }
                dist[n] = nd;
                queue.Enqueue(n);
            }
        }
        return dist;
    }

    /// <summary>from -> to 是否存在通路（忽略身长 / 自身身体）。</summary>
    public bool HasPath(Cell from, Cell to, IReadOnlySet<Cell> mechCells)
    {
        if (!InBounds(from) || !InBounds(to))
        {
            return false;
        }
        if (IsBlocked(to, mechCells))
        {
            return false;
        }
        if (from.Equals(to))
        {
            return true;
        }
        var visited = new HashSet<Cell> { from };
        var queue = new Queue<Cell>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            Cell cur = queue.Dequeue();
            foreach (Cell n in Neighbors(cur))
            {
                if (n.Equals(to))
                {
                    return true;
                }
                if (visited.Add(n) && !IsBlocked(n, mechCells))
                {
                    queue.Enqueue(n);
                }
            }
        }
        return false;
    }

    /// <summary>固定方向顺序（与 GDScript 求解器一致）：LEFT, RIGHT, UP, DOWN。</summary>
    public static IEnumerable<Cell> Neighbors(Cell c)
    {
        yield return new Cell(c.X - 1, c.Y);
        yield return new Cell(c.X + 1, c.Y);
        yield return new Cell(c.X, c.Y - 1);
        yield return new Cell(c.X, c.Y + 1);
    }
}
