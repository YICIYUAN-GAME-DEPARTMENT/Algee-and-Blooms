using System;
using System.Collections.Generic;

namespace Growth.Algorithms.Generation;

/// <summary>一个“模糊枝干”：由高流量格连通而成的一束路线。</summary>
public sealed class Branch
{
    public int Index;
    public List<Cell> Cells = new();
    /// <summary>枝干粗壮度 = 格流量之和。</summary>
    public double Thickness;
    public bool ContainsGoal;
    public bool ContainsSpawn;
    /// <summary>枝干格到终点的最小 BFS 距离（0 = 已到终点）。</summary>
    public int MinGoalDist = int.MaxValue;
    /// <summary>接近终点的最后 3 个方向签名（A/D/W/S）。</summary>
    public string Signature = "";
    /// <summary>束内最长的一条代表路线。</summary>
    public List<Cell> Representative = new();
}

/// <summary>黏菌/Physarum 风格的枝干网络（原型）。</summary>
public sealed class BranchNetwork
{
    public Dictionary<Cell, double> Conductance = new();
    public List<Branch> Branches = new();
    public int Walkers;
    public int SuccessfulWalkers;
    public bool Reachable;

    public Branch Thickest => Branches.Count > 0 ? Branches[0] : null;
    public Branch Second => Branches.Count > 1 ? Branches[1] : null;
}

/// <summary>
/// Monte Carlo 黏菌模型：从起点撒大量随机行走体，向终点势场偏置并沉积流量；
/// 高流量格连通成枝干。它不替代精确求解器，只用来度量“看起来有几条粗路”。
/// </summary>
public static class PhysarumBranchModel
{
    private sealed class Options
    {
        public int Walkers = 600;
        public int MaxStepsPerWalker = 400;
        public double GreedyChance = 0.75;
        public double BranchThreshold = 0.18;
    }

    public static BranchNetwork Build(LevelSpec spec, DeterministicRng rng,
        int walkers = 600, int maxSteps = 400, double greedyChance = 0.75, double branchThreshold = 0.18)
    {
        var options = new Options
        {
            Walkers = walkers,
            MaxStepsPerWalker = maxSteps,
            GreedyChance = greedyChance,
            BranchThreshold = branchThreshold,
        };
        return Build(spec, rng, options);
    }

    private static BranchNetwork Build(LevelSpec spec, DeterministicRng rng, Options options)
    {
        var network = new BranchNetwork { Walkers = options.Walkers };
        var floor = new HashSet<Cell>(spec.Floor);
        foreach (Cell obstacle in spec.Obstacles)
        {
            floor.Remove(obstacle);
        }
        if (!floor.Contains(spec.Spawn) || !floor.Contains(spec.Goal))
        {
            return network;
        }

        Dictionary<Cell, int> distGoal = Bfs(spec.Goal, floor);
        if (!distGoal.ContainsKey(spec.Spawn))
        {
            return network;
        }
        network.Reachable = true;

        var deposits = new Dictionary<Cell, double>();
        var visited = new List<Cell>();
        var dirs = new Cell[] { new(-1, 0), new(1, 0), new(0, -1), new(0, 1) };
        var routes = new List<List<Cell>>();
        var routeGoals = new List<bool>();

        for (int w = 0; w < options.Walkers; w++)
        {
            Cell cur = spec.Spawn;
            Cell prev = new Cell(int.MinValue, int.MinValue);
            bool success = false;
            var route = new List<Cell>();
            for (int step = 0; step < options.MaxStepsPerWalker; step++)
            {
                if (!deposits.ContainsKey(cur))
                {
                    visited.Add(cur);
                }
                deposits[cur] = deposits.TryGetValue(cur, out double d) ? d + 1.0 : 1.0;
                route.Add(cur);
                if (cur.Equals(spec.Goal))
                {
                    success = true;
                    break;
                }

                var optionsList = new List<Cell>(4);
                foreach (Cell dir in dirs)
                {
                    var n = new Cell(cur.X + dir.X, cur.Y + dir.Y);
                    if (!floor.Contains(n))
                    {
                        continue;
                    }
                    if (n.Equals(prev) && floor.Count > 1)
                    {
                        continue;
                    }
                    optionsList.Add(n);
                }
                if (optionsList.Count == 0)
                {
                    prev = cur;
                    cur = spec.Spawn;
                    continue;
                }

                Cell next;
                if (rng.NextDouble() < options.GreedyChance)
                {
                    next = optionsList[0];
                    int best = int.MaxValue;
                    foreach (Cell n in optionsList)
                    {
                        if (distGoal.TryGetValue(n, out int dist) && dist < best)
                        {
                            best = dist;
                            next = n;
                        }
                    }
                }
                else
                {
                    next = optionsList[rng.NextInt(optionsList.Count)];
                }
                prev = cur;
                cur = next;
            }
            if (success)
            {
                network.SuccessfulWalkers++;
            }
            if (route.Count >= 6)
            {
                routes.Add(route);
                routeGoals.Add(success);
            }
        }

        double max = 0;
        foreach (double value in deposits.Values)
        {
            max = Math.Max(max, value);
        }
        if (max <= 0)
        {
            return network;
        }
        foreach (KeyValuePair<Cell, double> kv in deposits)
        {
            network.Conductance[kv.Key] = kv.Value / max;
        }

        // 模糊枝干（v5 原型）：按接近终点的最后 3 个方向分组，
        // 同一进入方向的多条路线视为一束枝干；粗壮度 = 束内路线长度之和。
        const int approachDirs = 3;
        var groups = new Dictionary<string, (HashSet<Cell> Cells, double Thickness, bool ContainsGoal,
            int MinGoalDist, List<Cell> Representative)>();
        foreach (List<Cell> route in routes)
        {
            int start = Math.Max(1, route.Count - approachDirs);
            var keyBuilder = new System.Text.StringBuilder();
            for (int i = start; i < route.Count; i++)
            {
                int dx = route[i].X - route[i - 1].X;
                int dy = route[i].Y - route[i - 1].Y;
                keyBuilder.Append(dx < 0 ? 'A' : dx > 0 ? 'D' : dy < 0 ? 'W' : 'S');
            }
            string key = keyBuilder.ToString();
            bool reachesGoal = route[route.Count - 1].Equals(spec.Goal);
            int minGoal = int.MaxValue;
            foreach (Cell c in route)
            {
                if (distGoal.TryGetValue(c, out int dist))
                {
                    minGoal = Math.Min(minGoal, dist);
                }
            }
            if (!groups.TryGetValue(key, out var group))
            {
                group = (new HashSet<Cell>(), 0, false, int.MaxValue, route);
            }
            group.Cells.UnionWith(route);
            group.Thickness += route.Count;
            group.ContainsGoal |= reachesGoal;
            group.MinGoalDist = Math.Min(group.MinGoalDist, minGoal);
            if (route.Count > group.Representative.Count)
            {
                group.Representative = route;
            }
            groups[key] = group;
        }

        var branches = new List<Branch>();
        foreach (var pair in groups)
        {
            var group = pair.Value;
            branches.Add(new Branch
            {
                Cells = new List<Cell>(group.Cells),
                Thickness = group.Thickness,
                ContainsGoal = group.ContainsGoal,
                ContainsSpawn = group.Cells.Contains(spec.Spawn),
                MinGoalDist = group.MinGoalDist,
                Signature = pair.Key,
                Representative = group.Representative,
            });
        }
        branches.Sort((a, b) => b.Thickness.CompareTo(a.Thickness));
        for (int i = 0; i < branches.Count && i < 8; i++)
        {
            branches[i].Index = i;
            network.Branches.Add(branches[i]);
        }
        return network;
    }

    private static Dictionary<Cell, int> Bfs(Cell from, HashSet<Cell> floor)
    {
        var dist = new Dictionary<Cell, int> { [from] = 0 };
        var queue = new Queue<Cell>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            Cell cur = queue.Dequeue();
            int nd = dist[cur] + 1;
            foreach (Cell n in GridModel.Neighbors(cur))
            {
                if (floor.Contains(n) && !dist.ContainsKey(n))
                {
                    dist[n] = nd;
                    queue.Enqueue(n);
                }
            }
        }
        return dist;
    }
}
