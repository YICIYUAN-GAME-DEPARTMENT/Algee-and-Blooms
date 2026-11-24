using System;
using System.Collections.Generic;
using Godot;
using Growth.Algorithms;
using Growth.Algorithms.Generation;
using Growth.Algorithms.Search;

namespace Growth.Tools;

/// <summary>
/// BranchBench — v5 原型：对比障碍模型 + 黏菌枝干结构。
/// 运行：godot --headless --path . res://tools/BranchBench.tscn
/// </summary>
public partial class BranchBench : Node
{
    private readonly RuleState _state = new();
    private readonly ExactBfsSolver _solver = new();
    public override void _Ready()
    {
        AnalyzeLevel5();
        AnalyzeGenerated();
        GetTree().Quit(0);
    }

    private void AnalyzeLevel5()
    {
        LevelSpec spec = LevelSpec.FromJson(
            FileAccess.GetFileAsString("res://tests/fixtures/anchors/level_05.json"));
        var rng = new DeterministicRng(20261125);
        BranchNetwork network = PhysarumBranchModel.Build(spec, rng, 600, 260, 0.55);
        Report("L5", spec, network, Solve(spec));
    }

    private void AnalyzeGenerated()
    {
        var models = new[]
        {
            ObstacleModel.BlueNoise, ObstacleModel.NoiseCluster,
            ObstacleModel.PoissonCluster, ObstacleModel.WallGrowth,
        };
        foreach (ObstacleModel model in models)
        {
            var nn = new List<double>();
            var ent = new List<double>();
            var clusters = new List<int>();
            var branchCounts = new List<int>();
            var ratios = new List<double>();
            var secondGoals = new List<int>();
            int unreachable = 0;
            int solved = 0;
            double states = 0;
            for (long seed = 1; seed <= 6; seed++)
            {
                GenerationResult baseResult = RouteFirstGenerator.GenerateSpec(seed, "expert");
                if (!baseResult.Success)
                {
                    continue;
                }
                LevelSpec baseSpec = baseResult.Spec;
                var rng = new DeterministicRng((ulong)seed * 0x9E3779B97F4A7C15UL + (ulong)model + 77UL);
                List<Cell> obstacles = ObstacleModels.Generate(
                    model, baseSpec.Floor, baseSpec.Width, baseSpec.Height, rng, 0.18);
                var obstacleSet = new HashSet<Cell>(obstacles);
                if (obstacleSet.Contains(baseSpec.Spawn) || obstacleSet.Contains(baseSpec.Goal))
                {
                    continue;
                }
                Cell spawn = baseSpec.Spawn;
                Cell goal = baseSpec.Goal;
                var nav = new LevelSpec
                {
                    Width = baseSpec.Width,
                    Height = baseSpec.Height,
                    Origin = baseSpec.Origin,
                    Floor = baseSpec.Floor,
                    Obstacles = obstacles,
                    Spawn = spawn,
                    Goal = goal,
                    InitialMaxLen = 40,
                    FoodLenGain = 2,
                    GrowthInterval = 6,
                    MechanismCap = 4,
                };
                BranchNetwork network = PhysarumBranchModel.Build(nav, rng, 500, 240, 0.55);
                if (!network.Reachable)
                {
                    unreachable++;
                    continue;
                }

                ObstacleSolve solve = Solve(nav);
                if (solve.Status == "SOLVABLE")
                {
                    solved++;
                    states += solve.States;
                }
                nn.Add(ObstacleMetrics.NearestNeighborCv(obstacles));
                ent.Add(ObstacleMetrics.OrientationEntropy(obstacles));
                clusters.Add(ObstacleMetrics.ClusterCount(obstacles));
                branchCounts.Add(network.Branches.Count);
                Branch top = network.Thickest;
                Branch second = network.Second;
                if (top != null && second != null && top.Thickness > 1e-9)
                {
                    ratios.Add(second.Thickness / top.Thickness);
                    secondGoals.Add(second.MinGoalDist);
                }
                Report($"model={model} seed={seed}", nav, network, solve);
            }
            GD.Print($"[Summary] {model} unreachable={unreachable} solved={solved} " +
                     $"nncv={Mean(nn):F2} ent={Mean(ent):F2} clusters={Mean(clusters):F1} " +
                     $"branches={Mean(branchCounts):F1} ratio2/1={Mean(ratios):F2} " +
                     $"secondGoal={Mean(secondGoals):F1} states={states / Math.Max(1, solved):F0}");
        }
    }

    private static Cell PickWalkable(Cell preferred, IReadOnlyList<Cell> floor, HashSet<Cell> obstacles)
    {
        if (!obstacles.Contains(preferred) && Contains(floor, preferred))
        {
            return preferred;
        }
        foreach (Cell c in floor)
        {
            if (!obstacles.Contains(c))
            {
                return c;
            }
        }
        return preferred;
    }

    private static bool Contains(IReadOnlyList<Cell> floor, Cell c)
    {
        foreach (Cell f in floor)
        {
            if (f.Equals(c))
            {
                return true;
            }
        }
        return false;
    }

    private ObstacleSolve Solve(LevelSpec spec)
    {
        _state.Setup(spec);
        _state.Start();
        Godot.Collections.Dictionary result = _solver.Solve(_state, 1_200_000);
        return new ObstacleSolve
        {
            Status = result["status"].AsString(),
            Steps = result.TryGetValue("steps", out Variant steps) ? steps.AsInt32() : -1,
            States = result.TryGetValue("states", out Variant states) ? states.AsInt32() : 0,
            Route = result.TryGetValue("route", out Variant route) ? route.AsString() : "",
        };
    }

    private static void Report(string tag, LevelSpec spec, BranchNetwork network, ObstacleSolve solve)
    {
        Branch top = network.Thickest;
        Branch second = network.Second;
        double ratio = top != null && second != null && top.Thickness > 1e-9
            ? second.Thickness / top.Thickness : 0;
        var exactCells = RouteCells(spec, solve.Route);
        string exactKey = solve.Route.Length >= 3 ? solve.Route.Substring(solve.Route.Length - 3) : solve.Route;
        double exactTop = Overlap(top?.Representative, exactCells);
        double exactSecond = Overlap(second?.Representative, exactCells);
        bool topTrue = top != null && top.Signature == exactKey;
        bool secondTrue = second != null && second.Signature == exactKey;
        GD.Print($"[Branch] {tag} obstacles={spec.Obstacles.Count} " +
                 $"nncv={ObstacleMetrics.NearestNeighborCv(spec.Obstacles):F2} " +
                 $"ent={ObstacleMetrics.OrientationEntropy(spec.Obstacles):F2} " +
                 $"clusters={ObstacleMetrics.ClusterCount(spec.Obstacles)} " +
                 $"branches={network.Branches.Count} top={top?.Thickness ?? 0:F1} " +
                 $"second={second?.Thickness ?? 0:F1} ratio={ratio:F2} " +
                 $"keys={top?.Signature ?? "-"}/{second?.Signature ?? "-"}/{exactKey} " +
                 $"topTrue={topTrue} secondTrue={secondTrue} " +
                 $"exactTop={exactTop:F2} exactSecond={exactSecond:F2} " +
                 $"walkers={network.SuccessfulWalkers}/{network.Walkers} " +
                 $"solve={solve.Status}/{solve.Steps}/{solve.States}");
    }

    private static List<Cell> RouteCells(LevelSpec spec, string route)
    {
        var cells = new List<Cell> { spec.Spawn };
        Cell cur = spec.Spawn;
        foreach (char ch in route)
        {
            switch (ch)
            {
                case 'A': cur = new Cell(cur.X - 1, cur.Y); break;
                case 'D': cur = new Cell(cur.X + 1, cur.Y); break;
                case 'W': cur = new Cell(cur.X, cur.Y - 1); break;
                case 'S': cur = new Cell(cur.X, cur.Y + 1); break;
                default: return cells;
            }
            cells.Add(cur);
        }
        return cells;
    }

    private static double Overlap(List<Cell> route, List<Cell> exactCells)
    {
        if (route == null || route.Count == 0 || exactCells.Count == 0)
        {
            return 0;
        }
        var set = new HashSet<Cell>(route);
        int hit = 0;
        foreach (Cell c in exactCells)
        {
            if (set.Contains(c))
            {
                hit++;
            }
        }
        return hit / (double)exactCells.Count;
    }

    private static double Mean(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        double sum = 0;
        foreach (double v in values)
        {
            sum += v;
        }
        return sum / values.Count;
    }

    private static double Mean(List<int> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }
        double sum = 0;
        foreach (int v in values)
        {
            sum += v;
        }
        return sum / values.Count;
    }

    private struct ObstacleSolve
    {
        public string Status;
        public int Steps;
        public int States;
        public string Route;
    }
}
