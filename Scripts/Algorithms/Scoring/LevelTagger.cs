using System;
using System.Collections.Generic;

namespace Growth.Algorithms.Scoring;

/// <summary>事后标签结果：标签列表 + 每个标签的证据（指标值 / 格坐标 / 步序）。</summary>
public sealed class TagResult
{
    public List<string> Tags { get; } = new();
    public Dictionary<string, string> Evidence { get; } = new();

    public void Add(string tag, string evidence)
    {
        if (Tags.Contains(tag))
        {
            return;
        }
        Tags.Add(tag);
        Evidence[tag] = evidence;
    }
}

/// <summary>
/// 关卡标签分类器（生成器算法规划 §7.1，11 个标签，事后分类）。
/// 只读 LevelSpec + 规范路线 + MetricVector（含一次重放采样的 trace），
/// 不创建 RuleState、不参与生成决策、不回流到同批生成。
/// </summary>
public static class LevelTagger
{
    private static readonly Cell[] Directions =
    {
        new(-1, 0), new(1, 0), new(0, -1), new(0, 1),
    };

    public static TagResult Tag(LevelSpec spec, string route, MetricVector vector,
        MetricOptions options = null)
    {
        options ??= new MetricOptions();
        var result = new TagResult();
        if (vector == null || string.IsNullOrEmpty(route))
        {
            return result;
        }

        // ── A 类：直接由指标判定 ──────────────────────────────
        if (vector.M14FalseAffordance >= 1 && vector.M15MisleadGap >= 2)
        {
            result.Add("false_shortcut",
                $"false_affordance={vector.M14FalseAffordance}; mislead_gap={vector.M15MisleadGap}");
        }
        if (vector.M12GateLayers >= 2)
        {
            result.Add("gate_sequence", $"gate_layers={vector.M12GateLayers}");
        }
        if (vector.M06ForcedTruncations >= 1)
        {
            result.Add("forced_truncation", $"forced_truncations={vector.M06ForcedTruncations}");
        }
        if (vector.M03EndSlack <= 1)
        {
            result.Add("tight_budget", $"end_slack={vector.M03EndSlack}");
        }
        if (spec.MechCenters.Count == 0)
        {
            result.Add("no_mechanism", "mech_count=0");
        }
        if (vector.M10Openness <= 0.35)
        {
            result.Add("dense_maze", $"openness={vector.M10Openness:F2}");
        }

        // ── 重放 trace：overshoot / body_key / last_gasp ────────
        if (vector.OvershootReturn)
        {
            result.Add("overshoot_return", $"overshoot_max={vector.OvershootMax}");
        }
        if (vector.GrewSteps.Count > 0)
        {
            int last = vector.GrewSteps[^1];
            if (last >= route.Length - 2)
            {
                result.Add("last_gasp", $"grew_at={last}");
            }
        }
        if (vector.BodyKeyStep >= 0)
        {
            result.Add("body_key",
                $"step={vector.BodyKeyStep}; walk_options={vector.BodyKeyOptions}; " +
                $"remaining={vector.BodyKeyRemaining}");
        }

        // ── mirror_fork：走廊岔路 + 对称分支 + 机关覆盖 + 不回到路线 ──
        if (vector.MechCovered.Count > 0 && vector.RouteCells.Count > 0)
        {
            GridModel grid = BuildGrid(spec);
            var routeCells = new HashSet<Cell>(vector.RouteCells);
            for (int i = 0; i < vector.RouteDirs.Count && i < vector.RouteCells.Count; i++)
            {
                if (CheckMirrorFork(grid, vector.RouteCells[i], routeCells, vector.MechCovered,
                        vector.RouteDirs[i], out Cell forkCell, out int branch))
                {
                    result.Add("mirror_fork",
                        $"step={i + 1}; fork=({forkCell.X},{forkCell.Y}); branch_len={branch}");
                    break;
                }
            }
        }

        // ── mutual_exclusion（近似）：两片假捷径诱饵，双吃必超长 ──
        if (options.EnableFalseAffordance
            && vector.M14FalseAffordance >= 2
            && vector.M15MisleadGap >= 2)
        {
            GridModel grid = BuildGrid(spec);
            var empty = new HashSet<Cell>();
            Dictionary<Cell, int> distS = grid.BfsDistMap(spec.Spawn, empty);
            Dictionary<Cell, int> distG = grid.BfsDistMap(spec.Goal, empty);
            var decoys = new List<Cell>();
            foreach (Cell f in spec.Foods)
            {
                if (!vector.EatenFoods.Contains(f))
                {
                    decoys.Add(f);
                }
            }
            if (decoys.Count >= 2)
            {
                for (int a = 0; a < decoys.Count && a < 3; a++)
                {
                    for (int b = a + 1; b < decoys.Count && b < 3; b++)
                    {
                        if (!distS.TryGetValue(decoys[a], out int sa)
                            || !distS.TryGetValue(decoys[b], out int sb)
                            || !distG.TryGetValue(decoys[a], out int ga)
                            || !distG.TryGetValue(decoys[b], out int gb))
                        {
                            continue;
                        }
                        int ab = Manhattan(decoys[a], decoys[b]);
                        int apparent = Math.Min(sa + ab + gb, sb + ab + ga);
                        if (apparent <= route.Length - 2)
                        {
                            result.Add("mutual_exclusion",
                                $"decoys={decoys.Count}; pair=({decoys[a].X},{decoys[a].Y})" +
                                $"/({decoys[b].X},{decoys[b].Y}); apparent_gain={route.Length - apparent}");
                            a = decoys.Count;
                            break;
                        }
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// mirror_fork 近似：走廊岔路（静态邻居 ≥3）处存在一个未选择、长度 ≥4 的对称分支，
    /// 分支起点被机关覆盖且不回到规范路线。
    /// </summary>
    private static bool CheckMirrorFork(GridModel grid, Cell head, IReadOnlySet<Cell> routeCells,
        IReadOnlySet<Cell> mechCovered, Cell chosenDir, out Cell forkCell, out int branch)
    {
        forkCell = Cell.Zero;
        branch = 0;
        int chosenLen = BranchLength(grid, head + chosenDir, 4);
        var staticNeighbors = new List<Cell>(4);
        foreach (Cell dir in Directions)
        {
            Cell n = head + dir;
            if (grid.InBounds(n) && grid.IsFloor(n) && !grid.Obstacles.Contains(n))
            {
                staticNeighbors.Add(n);
            }
        }
        if (staticNeighbors.Count < 3)
        {
            return false;
        }
        foreach (Cell n in staticNeighbors)
        {
            if (routeCells.Contains(n))
            {
                continue;
            }
            int len = BranchLength(grid, n, 4);
            if (len < 4 || Math.Abs(len - chosenLen) > 1)
            {
                continue;
            }
            if (!mechCovered.Contains(n))
            {
                continue;
            }
            if (BranchTouchesRoute(grid, n, routeCells, 4))
            {
                continue;
            }
            forkCell = head;
            branch = len;
            return true;
        }
        return false;
    }

    private static bool BranchTouchesRoute(GridModel grid, Cell start,
        IReadOnlySet<Cell> routeCells, int limit)
    {
        var visited = new HashSet<Cell> { start };
        var queue = new Queue<Cell>();
        queue.Enqueue(start);
        int count = 0;
        while (queue.Count > 0 && count < limit)
        {
            Cell cur = queue.Dequeue();
            if (!grid.InBounds(cur) || !grid.IsFloor(cur) || grid.Obstacles.Contains(cur))
            {
                continue;
            }
            count++;
            if (routeCells.Contains(cur))
            {
                return true;
            }
            foreach (Cell n in GridModel.Neighbors(cur))
            {
                if (visited.Add(n))
                {
                    queue.Enqueue(n);
                }
            }
        }
        return false;
    }

    private static int BranchLength(GridModel grid, Cell start, int limit)
    {
        var visited = new HashSet<Cell> { start };
        var queue = new Queue<Cell>();
        queue.Enqueue(start);
        int count = 0;
        while (queue.Count > 0 && count < limit)
        {
            Cell cur = queue.Dequeue();
            if (!grid.InBounds(cur) || !grid.IsFloor(cur) || grid.Obstacles.Contains(cur))
            {
                continue;
            }
            count++;
            foreach (Cell n in GridModel.Neighbors(cur))
            {
                if (visited.Add(n))
                {
                    queue.Enqueue(n);
                }
            }
        }
        return count;
    }

    private static GridModel BuildGrid(LevelSpec spec)
    {
        var grid = new GridModel(spec.Origin, spec.Width, spec.Height);
        foreach (Cell c in spec.Floor)
        {
            grid.Floor.Add(c);
        }
        foreach (Cell c in spec.Obstacles)
        {
            grid.Obstacles.Add(c);
        }
        return grid;
    }

    private static int Manhattan(Cell a, Cell b)
    {
        return Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
    }
}
