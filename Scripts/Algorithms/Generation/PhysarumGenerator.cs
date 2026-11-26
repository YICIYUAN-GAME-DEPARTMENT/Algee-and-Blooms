using System;
using System.Collections.Generic;
using Growth.Algorithms.Search;

namespace Growth.Algorithms.Generation;

/// <summary>生成模型（v5 起可在游戏内切换）。</summary>
public enum GeneratorModel
{
    /// <summary>经典路线优先：先定最短路线，再排花 / 机关 / 封路。</summary>
    RouteFirst,
    /// <summary>黏菌枝干：先算模糊枝干网络，选最粗枝干做假主干，真解落在次粗枝干。</summary>
    Physarum,
    /// <summary>机关优先 + 模板（v3）：先放机关场，再选 S/E 与见证路线。</summary>
    TemplateField,
    /// <summary>v4 Portfolio：按档位比例在结构化模板与噪波负型之间确定性调度。</summary>
    Portfolio,
}

/// <summary>
/// 黏菌枝干生成器（v5 原型）：
///   1. 共用基底（地形 / 障碍 / 出生点 / 终点）；
///   2. PhysarumBranchModel 撒行走体，得到多条模糊粗枝干；
///   3. 最粗且与基准路线不同的枝干用静态障碍封住，成为“假主干”；
///   4. 真路线（封路后最短路）上均布花瓣，机关按弧长分段铺开；
///   5. 精确求解 + 重放校验；hard/expert 要求最粗枝干不是真解（假主干结构）。
/// </summary>
public static class PhysarumGenerator
{
    private const int Walkers = 480;
    private const int WalkerSteps = 260;
    private const double GreedyChance = 0.62;

    internal static RouteFirstGenerator.AttemptResult RunAttempt(
        GeneratorConfig cfg, int targetSteps, int cand, int attempt)
    {
        RouteFirstGenerator.Band band = RouteFirstGenerator.GetBand(cfg.Difficulty);
        var rng = new DeterministicRng(
            (ulong)cfg.Seed * 0x9E3779B97F4A7C15UL
            + (ulong)cand * 0x94D049BB133111EBUL
            + (ulong)attempt * 0xBF58476D1CE4E5B9UL
            + 1UL);

        int mechCount = cfg.MechanismCount >= 0
            ? cfg.MechanismCount
            : band.MinMechs + rng.NextInt(band.MaxMechs - band.MinMechs + 1);

        (int width, int height) = cfg.Width > 0 && cfg.Height > 0
            ? (cfg.Width, cfg.Height)
            : SizeLadder.Choose(rng, cfg.Difficulty);
        double areaFactor = GenerationBudget.AreaFactor(width, height);

        ParamSet param = ParamSampler.Sample(rng, targetSteps, mechCount);
        if (param == null)
        {
            return new RouteFirstGenerator.AttemptResult(false, null, "参数组合不可行", areaFactor);
        }

        GenerationResult candidate = TryBuild(cfg, band, rng, targetSteps, mechCount,
            width, height, param);
        return candidate.Success
            ? new RouteFirstGenerator.AttemptResult(true, candidate, "", areaFactor)
            : new RouteFirstGenerator.AttemptResult(false, null, candidate.Reason, areaFactor);
    }

    private static GenerationResult TryBuild(GeneratorConfig cfg, RouteFirstGenerator.Band band,
        DeterministicRng rng, int targetSteps, int mechCount, int width, int height, ParamSet param)
    {
        RouteFirstGenerator.BaseLayout layout = RouteFirstGenerator.BuildBase(
            cfg, rng, targetSteps, width, height, band.MinSteps, band.MaxSteps, out string baseReason);
        if (layout == null)
        {
            return Fail(baseReason);
        }

        var nav = new LevelSpec
        {
            Seed = cfg.Seed,
            Tier = cfg.Difficulty,
            Origin = Cell.Zero,
            Width = width,
            Height = height,
            Floor = layout.Floor,
            Obstacles = layout.Obstacles,
            Spawn = layout.Spawn,
            Goal = layout.Goal,
            InitialMaxLen = param.InitialMaxLen,
            FoodLenGain = param.FoodLenGain,
            GrowthInterval = param.GrowthInterval,
            MechanismCap = param.MechanismCap,
        };
        BranchNetwork network = PhysarumBranchModel.Build(
            nav, rng, Walkers, WalkerSteps, GreedyChance);
        if (!network.Reachable)
        {
            return Fail("枝干网络不可达");
        }
        Branch top = network.Thickest;
        Branch second = network.Second;

        List<Cell> basePath = RouteFirstGenerator.BfsPath(layout.Grid, layout.Spawn, layout.Goal);
        if (basePath == null || basePath.Count - 1 < band.MinSteps)
        {
            return Fail("终点路径不足");
        }

        Branch fake = PickFakeBranch(network, basePath);
        Cell wall = fake != null ? PickWallCell(layout.Grid, fake, layout.Spawn, layout.Goal) : Cell.Zero;
        if (wall.Equals(Cell.Zero))
        {
            // 兜底：没有可用假主干时，在基准路线靠终点一侧封路，至少逼出真实绕行。
            wall = PickFallbackWall(layout.Grid, basePath, layout.Spawn, layout.Goal);
        }
        if (wall.Equals(Cell.Zero))
        {
            return Fail("无法封路");
        }
        // 封路用静态障碍（不随机关生长），避免机关长到真路线上把唯一通路封死。
        layout.Grid.Obstacles.Add(wall);
        List<Cell> truePath = RouteFirstGenerator.BfsPath(layout.Grid, layout.Spawn, layout.Goal);
        if (truePath == null || truePath.Count - 1 < band.MinSteps)
        {
            layout.Grid.Obstacles.Remove(wall);
            return Fail("封路后真路线不足");
        }
        if (!layout.Obstacles.Contains(wall))
        {
            layout.Obstacles.Add(wall);
        }

        var spec = new LevelSpec
        {
            Seed = cfg.Seed,
            Name = cfg.Difficulty,
            Tier = cfg.Difficulty,
            Origin = Cell.Zero,
            Width = width,
            Height = height,
            Floor = layout.Floor,
            Obstacles = layout.Obstacles,
            Spawn = layout.Spawn,
            Goal = layout.Goal,
            InitialMaxLen = param.InitialMaxLen,
            FoodLenGain = param.FoodLenGain,
            GrowthInterval = param.GrowthInterval,
            MechanismCap = param.MechanismCap,
            TargetSteps = targetSteps,
            ParamSignature = param.Signature,
        };
        PlaceEvenFoods(spec, truePath, param);

        var walked = new HashSet<Cell> { spec.Spawn };
        var trailOrder = new List<Cell>();
        if (!RouteFirstGenerator.WalkPath(spec, truePath, walked, out string route, trailOrder, 2))
        {
            return Fail("路径行走失败");
        }

        int remainingMechs = Math.Max(0, mechCount);
        RouteFirstGenerator.PlaceMechanisms(
            spec, rng, walked, trailOrder, remainingMechs, param.InitialLevels);
        int decoyMin = cfg.Difficulty is "hard" or "expert" ? 1 : 0;
        RouteFirstGenerator.AddDecoyFoods(rng, spec, walked, route.Length, decoyMin, 2);

        int solveStates = 0;
        RuleState solveState = RouteFirstGenerator.GetSolveState();
        solveState.Setup(spec);
        solveState.Start();
        var solver = RouteFirstGenerator.GetSolver();
        Godot.Collections.Dictionary solved = solver.Solve(solveState, 1_200_000);
        string solveStatus = solved["status"].AsString();
        bool topIsTrue = false;
        if (solveStatus == "SOLVABLE")
        {
            int minSteps = solved["steps"].AsInt32();
            if (minSteps < band.MinSteps)
            {
                return Fail("真实最短路短于档位下限");
            }
            route = solved["route"].AsString();
            solveStates = solved["states"].AsInt32();
            topIsTrue = route.Length >= 3 && top != null
                && route.Substring(route.Length - 3) == top.Signature;
            if (topIsTrue && cfg.Difficulty is "hard" or "expert")
            {
                return Fail("最粗枝干即真解（缺少假主干）");
            }
        }
        else if (solveStatus == "UNSOLVABLE")
        {
            return Fail("精确求解无解");
        }
        else if (band.MinSteps >= 16)
        {
            return Fail("精确求解超预算");
        }

        if (!RouteFirstGenerator.ReplayVerify(spec, route))
        {
            return Fail("路线重放失败");
        }

        var result = new GenerationResult
        {
            Success = true,
            Spec = spec,
            GenerationMode = "physarum",
            TemplateId = "physarum",
            Solution = new LevelSolution
            {
                Status = "SOLVABLE",
                Steps = route.Length,
                Route = route,
            },
        };
        result.Score = RouteFirstGenerator.ComputeScore(spec, route, result.Metrics,
            RouteFirstGenerator.BuildScoreOptions(Math.Max(width, height)), solveStates);
        double topThickness = top?.Thickness ?? 0;
        double secondThickness = second?.Thickness ?? 0;
        result.Metrics["branch_top"] = topThickness;
        result.Metrics["branch_second"] = secondThickness;
        result.Metrics["branch_ratio"] = topThickness > 0 ? secondThickness / topThickness : 0;
        result.Metrics["branch_count"] = network.Branches.Count;
        result.Metrics["branch_top_true"] = topIsTrue ? 1 : 0;
        return result;
    }

    /// <summary>选假主干：按粗壮度遍历，与基准路线重叠不过半的最粗枝干。</summary>
    private static Branch PickFakeBranch(BranchNetwork network, IReadOnlyList<Cell> basePath)
    {
        var baseSet = new HashSet<Cell>(basePath);
        foreach (Branch branch in network.Branches)
        {
            if (branch.Representative.Count == 0)
            {
                continue;
            }
            int overlap = 0;
            foreach (Cell c in branch.Representative)
            {
                if (baseSet.Contains(c))
                {
                    overlap++;
                }
            }
            if (overlap * 2 > branch.Representative.Count)
            {
                continue;
            }
            return branch;
        }
        return null;
    }

    /// <summary>兜底封路：基准路线靠终点一侧、且不在首尾的格。</summary>
    private static Cell PickFallbackWall(GridModel grid, IReadOnlyList<Cell> basePath, Cell spawn, Cell goal)
    {
        if (basePath.Count < 6)
        {
            return Cell.Zero;
        }
        var empty = new HashSet<Cell>();
        Dictionary<Cell, int> distGoal = grid.BfsDistMap(goal, empty);
        Cell best = Cell.Zero;
        int bestDist = int.MaxValue;
        for (int i = 2; i < basePath.Count - 1; i++)
        {
            Cell c = basePath[i];
            if (c.Equals(spawn) || c.Equals(goal))
            {
                continue;
            }
            if (!distGoal.TryGetValue(c, out int dist) || dist < 2 || dist > 5)
            {
                continue;
            }
            if (dist < bestDist)
            {
                best = c;
                bestDist = dist;
            }
        }
        return best;
    }

    /// <summary>假主干上离终点最近、且可作为静态障碍封路的格（距离 1–3）。</summary>
    private static Cell PickWallCell(GridModel grid, Branch fake, Cell spawn, Cell goal)
    {
        var empty = new HashSet<Cell>();
        Dictionary<Cell, int> distGoal = grid.BfsDistMap(goal, empty);
        Cell best = Cell.Zero;
        int bestDist = int.MaxValue;
        foreach (Cell c in fake.Representative)
        {
            if (c.Equals(spawn) || c.Equals(goal) || grid.Obstacles.Contains(c) || !grid.IsFloor(c))
            {
                continue;
            }
            if (!distGoal.TryGetValue(c, out int dist) || dist < 1 || dist > 3)
            {
                continue;
            }
            if (dist < bestDist)
            {
                best = c;
                bestDist = dist;
            }
        }
        return best;
    }

    /// <summary>沿真路线等弧长铺花瓣：n = ceil((路线长 - L0) / Gain)，避免面包屑式贴着玩家排花。</summary>
    private static void PlaceEvenFoods(LevelSpec spec, IReadOnlyList<Cell> path, ParamSet param)
    {
        int pathLen = path.Count - 1;
        int needed = (int)Math.Ceiling((pathLen - param.InitialMaxLen) / (double)param.FoodLenGain);
        needed = Math.Clamp(needed, 1, 8);
        var occupied = new HashSet<Cell>(spec.MechCenters) { spec.Spawn, spec.Goal };
        for (int k = 0; k < needed; k++)
        {
            int idx = (int)Math.Round((k + 1) * pathLen / (double)(needed + 1));
            idx = Math.Clamp(idx, 1, pathLen - 1);
            Cell chosen = Cell.Zero;
            for (int shift = 0; shift < pathLen && chosen.Equals(Cell.Zero); shift++)
            {
                foreach (int candidate in new[] { idx - shift, idx + shift })
                {
                    if (candidate < 1 || candidate > pathLen - 1)
                    {
                        continue;
                    }
                    Cell c = path[candidate];
                    if (!occupied.Contains(c))
                    {
                        chosen = c;
                        break;
                    }
                }
            }
            if (chosen.Equals(Cell.Zero))
            {
                continue;
            }
            spec.Foods.Add(chosen);
            occupied.Add(chosen);
        }
    }

    private static GenerationResult Fail(string reason)
    {
        return new GenerationResult { Success = false, Reason = reason };
    }
}
