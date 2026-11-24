using System;
using System.Collections.Generic;

namespace Growth.Algorithms.Generation;

/// <summary>障碍物生成模型（v5 原型，可按需选择）。</summary>
public enum ObstacleModel
{
    /// <summary>最小间距蓝噪声散布（旧方案，容易有网格感）。</summary>
    BlueNoise,
    /// <summary>低频率值噪声 + 排序取高值：成片岩群，最接近 L5。</summary>
    NoiseCluster,
    /// <summary>Poisson 簇过程：父点 + 高斯子点，疏密不均。</summary>
    PoissonCluster,
    /// <summary>从洞壁向内生长 1–3 格，形成贴墙岩牙。</summary>
    WallGrowth,
}

/// <summary>障碍模型工厂：只负责产出障碍格，连通性由调用方修复。</summary>
public static class ObstacleModels
{
    public static List<Cell> Generate(ObstacleModel model, IReadOnlyList<Cell> floor,
        int width, int height, DeterministicRng rng, double density)
    {
        int target = Math.Clamp((int)Math.Round(floor.Count * density), 0, floor.Count / 3);
        if (target <= 0)
        {
            return new List<Cell>();
        }
        return model switch
        {
            ObstacleModel.NoiseCluster => NoiseCluster(floor, rng, target),
            ObstacleModel.PoissonCluster => PoissonCluster(floor, rng, target),
            ObstacleModel.WallGrowth => WallGrowth(floor, width, height, rng, target),
            _ => BlueNoise(floor, rng, target),
        };
    }

    /// <summary>旧方案：Chebyshev ≥2 的最小间距散布。</summary>
    private static List<Cell> BlueNoise(IReadOnlyList<Cell> floor, DeterministicRng rng, int target)
    {
        var candidates = new List<Cell>(floor);
        rng.Shuffle(candidates);
        var obstacles = new List<Cell>(target);
        foreach (Cell c in candidates)
        {
            if (obstacles.Count >= target)
            {
                break;
            }
            bool tooClose = false;
            foreach (Cell o in obstacles)
            {
                if (Chebyshev(c, o) < 2)
                {
                    tooClose = true;
                    break;
                }
            }
            if (!tooClose)
            {
                obstacles.Add(c);
            }
        }
        return obstacles;
    }

    /// <summary>噪声成簇：fBm 值噪声取最高的一批格，形成自然岩群。</summary>
    private static List<Cell> NoiseCluster(IReadOnlyList<Cell> floor, DeterministicRng rng, int target)
    {
        ulong seed = rng.NextU64();
        var scored = new List<(Cell Cell, double Score)>(floor.Count);
        foreach (Cell c in floor)
        {
            double noise = Fbm(c.X * 0.21, c.Y * 0.21, seed);
            scored.Add((c, noise + rng.NextDouble() * 0.05));
        }
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        var result = new List<Cell>(target);
        for (int i = 0; i < scored.Count && result.Count < target; i++)
        {
            result.Add(scored[i].Cell);
        }
        return result;
    }

    /// <summary>Poisson 簇：父点 + 高斯偏移子点。</summary>
    private static List<Cell> PoissonCluster(IReadOnlyList<Cell> floor, DeterministicRng rng, int target)
    {
        if (floor.Count == 0)
        {
            return new List<Cell>();
        }
        var floorSet = new HashSet<Cell>(floor);
        int parents = Math.Max(1, target / 5);
        var centers = new List<Cell>(parents);
        for (int i = 0; i < parents; i++)
        {
            centers.Add(floor[rng.NextInt(floor.Count)]);
        }
        var result = new HashSet<Cell>();
        int guard = 0;
        while (result.Count < target && guard++ < target * 40)
        {
            Cell center = centers[rng.NextInt(centers.Count)];
            double gx = Gaussian(rng) * 2.4;
            double gy = Gaussian(rng) * 2.4;
            var p = new Cell((int)Math.Round(center.X + gx), (int)Math.Round(center.Y + gy));
            if (floorSet.Contains(p))
            {
                result.Add(p);
            }
        }
        return new List<Cell>(result);
    }

    /// <summary>贴墙生长：从洞壁格向内走 1–3 格。</summary>
    private static List<Cell> WallGrowth(IReadOnlyList<Cell> floor, int width, int height,
        DeterministicRng rng, int target)
    {
        var floorSet = new HashSet<Cell>(floor);
        var boundary = new List<Cell>();
        foreach (Cell c in floor)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                bool edge = false;
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }
                    var n = new Cell(c.X + dx, c.Y + dy);
                    if (n.X < 0 || n.Y < 0 || n.X >= width || n.Y >= height || !floorSet.Contains(n))
                    {
                        edge = true;
                        break;
                    }
                }
                if (edge)
                {
                    boundary.Add(c);
                    break;
                }
            }
        }
        if (boundary.Count == 0)
        {
            return BlueNoise(floor, rng, target);
        }

        var result = new HashSet<Cell>();
        int guard = 0;
        while (result.Count < target && guard++ < target * 30)
        {
            Cell cur = boundary[rng.NextInt(boundary.Count)];
            int depth = 1 + rng.NextInt(3);
            for (int step = 0; step < depth; step++)
            {
                if (floorSet.Contains(cur))
                {
                    result.Add(cur);
                }
                Cell best = cur;
                int bestScore = int.MinValue;
                foreach (Cell n in GridModel.Neighbors(cur))
                {
                    if (!floorSet.Contains(n) || result.Contains(n))
                    {
                        continue;
                    }
                    int interior = 0;
                    foreach (Cell nn in GridModel.Neighbors(n))
                    {
                        if (floorSet.Contains(nn))
                        {
                            interior++;
                        }
                    }
                    int score = interior * 2 + rng.NextInt(3);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = n;
                    }
                }
                if (best.Equals(cur))
                {
                    break;
                }
                cur = best;
            }
        }
        return new List<Cell>(result);
    }

    private static int Chebyshev(Cell a, Cell b)
    {
        return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private static double Gaussian(DeterministicRng rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static double Hash01(int x, int y, ulong seed)
    {
        ulong h = seed;
        h ^= (ulong)(uint)x * 0x9E3779B97F4A7C15UL;
        h ^= (ulong)(uint)y * 0xC2B2AE3D27D4EB4FUL;
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        return (h & 0xFFFFFFUL) / (double)0x1000000;
    }

    private static double Smooth(double t)
    {
        return t * t * (3.0 - 2.0 * t);
    }

    private static double ValueNoise(double x, double y, ulong seed)
    {
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        double fx = x - x0;
        double fy = y - y0;
        double a = Hash01(x0, y0, seed);
        double b = Hash01(x0 + 1, y0, seed);
        double c = Hash01(x0, y0 + 1, seed);
        double d = Hash01(x0 + 1, y0 + 1, seed);
        double u = Smooth(fx);
        double v = Smooth(fy);
        return (a * (1 - u) + b * u) * (1 - v) + (c * (1 - u) + d * u) * v;
    }

    private static double Fbm(double x, double y, ulong seed)
    {
        double sum = 0;
        double amplitude = 0.5;
        double frequency = 1;
        for (int octave = 0; octave < 3; octave++)
        {
            sum += amplitude * ValueNoise(x * frequency, y * frequency, seed + (ulong)octave * 0x9E37UL);
            frequency *= 2;
            amplitude *= 0.5;
        }
        return sum;
    }
}

/// <summary>障碍自然度指标（原型用，不参与游戏运行）。</summary>
public static class ObstacleMetrics
{
    /// <summary>最近邻距离变异系数：越大越不规则；网格状通常偏小。</summary>
    public static double NearestNeighborCv(IReadOnlyList<Cell> cells)
    {
        if (cells.Count < 2)
        {
            return 0;
        }
        double sum = 0;
        double sumSq = 0;
        foreach (Cell a in cells)
        {
            int best = int.MaxValue;
            foreach (Cell b in cells)
            {
                if (a.Equals(b))
                {
                    continue;
                }
                int d = Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
                if (d < best)
                {
                    best = d;
                }
            }
            sum += best;
            sumSq += best * best;
        }
        double mean = sum / cells.Count;
        double variance = sumSq / cells.Count - mean * mean;
        return mean > 1e-9 ? Math.Sqrt(Math.Max(0, variance)) / mean : 0;
    }

    /// <summary>最近邻方向熵（0=方向单一，1=各方向均匀）。</summary>
    public static double OrientationEntropy(IReadOnlyList<Cell> cells)
    {
        if (cells.Count < 2)
        {
            return 0;
        }
        var bins = new int[8];
        foreach (Cell a in cells)
        {
            Cell best = Cell.Zero;
            int bestDist = int.MaxValue;
            foreach (Cell b in cells)
            {
                if (a.Equals(b))
                {
                    continue;
                }
                int d = Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
                if (d < bestDist)
                {
                    bestDist = d;
                    best = b;
                }
            }
            double angle = Math.Atan2(best.Y - a.Y, best.X - a.X);
            int bin = (int)Math.Floor((angle + Math.PI) / (2 * Math.PI) * 8) % 8;
            bins[bin]++;
        }
        double entropy = 0;
        for (int i = 0; i < bins.Length; i++)
        {
            if (bins[i] > 0)
            {
                double p = bins[i] / (double)cells.Count;
                entropy -= p * Math.Log(p, 2);
            }
        }
        return entropy / 3.0;
    }

    /// <summary>8 邻接障碍簇数。</summary>
    public static int ClusterCount(IReadOnlyList<Cell> cells)
    {
        var set = new HashSet<Cell>(cells);
        int clusters = 0;
        var visited = new HashSet<Cell>();
        var queue = new Queue<Cell>();
        foreach (Cell start in cells)
        {
            if (!visited.Add(start))
            {
                continue;
            }
            clusters++;
            queue.Clear();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                Cell cur = queue.Dequeue();
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0)
                        {
                            continue;
                        }
                        var n = new Cell(cur.X + dx, cur.Y + dy);
                        if (set.Contains(n) && visited.Add(n))
                        {
                            queue.Enqueue(n);
                        }
                    }
                }
            }
        }
        return clusters;
    }
}
