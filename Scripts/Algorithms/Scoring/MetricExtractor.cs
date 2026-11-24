using System;
using System.Collections.Generic;
using Godot;

namespace Growth.Algorithms.Scoring;

/// <summary>指标提取选项：控制 C 类指标开关与有界搜索预算。</summary>
public sealed class MetricOptions
{
    public bool EnableFalseAffordance = true;
    public bool EnableFunnel = true;
    public bool EnableNaiveStrategies = true;
    public int FunnelNodeBudget = 30_000;
    public int FunnelMaxCount = 2000;
    public int SolveNodeBudget = 400_000;
}

/// <summary>
/// 19 项量化指标 + 归一化 + 两段难度指数（生成器算法规划 §5）。
/// 输入：LevelSpec + 规范路线；C 类指标（M14/M15/M16）走有界搜索，超预算标记 CEstimated。
/// </summary>
public static class MetricExtractor
{
    private static readonly Cell[] Directions =
    {
        new(-1, 0), new(1, 0), new(0, -1), new(0, 1),
    };

    public static MetricVector Extract(LevelSpec spec, string route, MetricOptions options = null)
    {
        options ??= new MetricOptions();
        var vector = new MetricVector { CEstimated = false };
        if (string.IsNullOrEmpty(route))
        {
            return vector;
        }

        var grid = BuildGrid(spec);
        var empty = new HashSet<Cell>();
        Dictionary<Cell, int> distFromSpawn = grid.BfsDistMap(spec.Spawn, empty);
        Dictionary<Cell, int> distFromGoal = grid.BfsDistMap(spec.Goal, empty);
        int shortest = distFromSpawn.TryGetValue(spec.Goal, out int sd) ? sd : 0;

        var st = new RuleState { SuppressDeadlockChecks = true, SuppressPathCheck = true };
        st.Setup(spec);
        st.Start();

        int steps = route.Length;
        int minSlack = st.GetRemainingSteps();
        int forcedTruncations = 0;
        int mechNearSteps = 0;
        int branchSamples = 0;
        double branchingSum = 0;
        int corridorSteps = 0;
        int legalSumLog = 0;
        double entropy = 0;
        int lateStart = steps * 2 / 3;
        bool lateGrowth = false;
        bool routeValid = true;
        var eatenFoods = new HashSet<Cell>();
        var obstacleSet = new HashSet<Cell>(spec.Obstacles);
        var routeCells = new List<Cell>();
        var routeDirs = new List<Cell>();
        var grewSteps = new List<int>();
        var mechCovered = new HashSet<Cell>(st.CoverSteps.Keys);
        int minDistToGoal = int.MaxValue;
        bool overshootActive = false;
        bool overshootReturn = false;
        int overshootPeak = 0;
        int overshootMax = 0;
        int bodyKeyStep = -1;
        int bodyKeyRemaining = 0;
        int bodyKeyOptions = 0;

        for (int i = 0; i < steps; i++)
        {
            Cell head = st.Head;
            minSlack = Math.Min(minSlack, st.GetRemainingSteps());
            routeCells.Add(head);
            int distGoal = distFromGoal.TryGetValue(head, out int dg) ? dg : int.MaxValue;
            if (distGoal != int.MaxValue)
            {
                if (minDistToGoal == int.MaxValue || distGoal < minDistToGoal)
                {
                    minDistToGoal = distGoal;
                }
                if (minDistToGoal != int.MaxValue && distGoal >= minDistToGoal + 3)
                {
                    overshootActive = true;
                    overshootPeak = Math.Max(overshootPeak, distGoal);
                }
                else if (overshootActive && distGoal <= overshootPeak - 2)
                {
                    overshootReturn = true;
                    overshootMax = Math.Max(overshootMax, overshootPeak - minDistToGoal);
                }
            }

            int walkOptions = 0;
            int legal = 0;
            foreach (Cell dir in Directions)
            {
                RuleState.MoveKind kind = st.ClassifyMove(head + dir);
                if (kind == RuleState.MoveKind.Walk)
                {
                    walkOptions++;
                }
                if (kind != RuleState.MoveKind.Rejected)
                {
                    legal++;
                }
            }
            if (legal > 1)
            {
                entropy += Math.Log2(legal);
                legalSumLog++;
            }

            Cell chosenDir = DirCell(route[i]);
            Cell chosen = head + chosenDir;
            routeDirs.Add(chosenDir);
            int remaining = st.GetRemainingSteps();
            if (bodyKeyStep < 0 && walkOptions == 1 && remaining <= 2)
            {
                bodyKeyStep = i + 1;
                bodyKeyRemaining = remaining;
                bodyKeyOptions = walkOptions;
            }
            if (st.ClassifyMove(chosen) == RuleState.MoveKind.Body && walkOptions == 0)
            {
                forcedTruncations++;
            }

            int walkable = 0;
            foreach (Cell n in GridModel.Neighbors(head))
            {
                if (grid.IsFloor(n) && !obstacleSet.Contains(n))
                {
                    walkable++;
                }
            }
            branchingSum += walkable;
            branchSamples++;
            if (walkable <= 1)
            {
                corridorSteps++;
            }

            if (spec.MechCenters.Count > 0)
            {
                foreach (Cell center in spec.MechCenters)
                {
                    if (Chebyshev(head, center) <= 2)
                    {
                        mechNearSteps++;
                        break;
                    }
                }
            }

            RuleState.StepOutcome outcome = st.StepCore(chosenDir);
            if (!outcome.Accepted)
            {
                routeValid = false;
                break;
            }
            if (outcome.AteFood)
            {
                eatenFoods.Add(chosen);
            }
            if (outcome.Grew)
            {
                grewSteps.Add(i + 1);
                if (i >= lateStart)
                {
                    lateGrowth = true;
                }
            }
        }
        minSlack = Math.Min(minSlack, st.GetRemainingSteps());

        vector.RouteCells = routeCells;
        vector.RouteDirs = routeDirs;
        vector.GrewSteps = grewSteps;
        vector.MechCovered = mechCovered;
        vector.EatenFoods = eatenFoods;
        vector.OvershootReturn = overshootReturn;
        vector.OvershootMax = overshootMax;
        vector.BodyKeyStep = bodyKeyStep;
        vector.BodyKeyRemaining = bodyKeyRemaining;
        vector.BodyKeyOptions = bodyKeyOptions;

        int foodEaten = eatenFoods.Count;
        int finalTrail = Math.Max(st.Trail.Count, 1);
        double maxLenReach = spec.InitialMaxLen + spec.FoodLenGain * foodEaten;

        vector.RouteValid = routeValid;
        vector.M01Steps = steps;
        vector.M02MinSlack = minSlack;
        vector.M03EndSlack = st.GetRemainingSteps();
        vector.M04FoodTightness = spec.Foods.Count > 0 ? foodEaten / (double)spec.Foods.Count : 0;
        vector.M05LenBuffer = maxLenReach / finalTrail;
        vector.M06ForcedTruncations = forcedTruncations;
        vector.M07DetourRatio = shortest > 0 ? steps / (double)shortest : 2.0;
        vector.M08CorridorRatio = corridorSteps / (double)Math.Max(1, steps);
        vector.M09Branching = branchSamples > 0 ? branchingSum / branchSamples : 1.0;
        vector.M10Openness = spec.Floor.Count / (double)Math.Max(1, spec.Width * spec.Height);
        vector.M11MechPressure = mechNearSteps / (double)Math.Max(1, steps);
        vector.M12GateLayers = st.GrowthCount;
        vector.M13LateGrowth = lateGrowth;
        vector.M18ChoiceEntropy = entropy;
        vector.M19Size = Math.Max(spec.Width, spec.Height);

        if (options.EnableFalseAffordance)
        {
            (int count, int gap, bool estimated) = ComputeFalseAffordance(
                spec, steps, eatenFoods, distFromSpawn, distFromGoal, options.SolveNodeBudget, st);
            vector.M14FalseAffordance = count;
            vector.M15MisleadGap = gap;
            vector.CEstimated |= estimated;
        }

        if (options.EnableFunnel)
        {
            var enumerator = new Search.BoundedRouteEnumerator();
            Search.BoundedRouteEnumerator.Result funnel = enumerator.Enumerate(
                spec, steps + 2, options.FunnelNodeBudget, options.FunnelMaxCount, st);
            vector.M16FunnelWidth = Math.Max(1, funnel.Count);
            vector.CEstimated |= funnel.Truncated;
        }
        else
        {
            vector.M16FunnelWidth = 1;
            vector.CEstimated = true;
        }

        if (options.EnableNaiveStrategies)
        {
            st.SuppressDeadlockChecks = false;
            st.SuppressPathCheck = false;
            vector.M17NaiveFailRate = ComputeNaiveFailRate(spec, steps, distFromSpawn, distFromGoal, st);
        }

        Normalize(vector);
        return vector;
    }

    private static void Normalize(MetricVector v)
    {
        v.StepsNorm = Clamp01((v.M01Steps - 8) / 24.0);
        v.SlackNorm = 1.0 - Math.Min(v.M02MinSlack, 5) / 5.0;
        v.EndSlackNorm = 1.0 - Math.Min(v.M03EndSlack, 4) / 4.0;
        v.TightNorm = Clamp01(v.M04FoodTightness);
        v.BufferNorm = 1.0 - Clamp01((v.M05LenBuffer - 1.0) / 0.5);
        v.TruncNorm = Math.Min(v.M06ForcedTruncations, 3) / 3.0;
        v.DetourNorm = Clamp01((v.M07DetourRatio - 1.0) / 1.0);
        v.CorridorNorm = Clamp01(v.M08CorridorRatio);
        v.BranchNorm = 1.0 - Clamp01((v.M09Branching - 1.0) / 2.5);
        v.OpenNorm = 1.0 - Clamp01((v.M10Openness - 0.30) / 0.60);
        v.MechNorm = Clamp01(v.M11MechPressure);
        v.GateNorm = Math.Min(v.M12GateLayers, 3) / 3.0;
        v.LateNorm = v.M13LateGrowth ? 1.0 : 0.0;
        v.FaNorm = Math.Min(v.M14FalseAffordance, 3) / 3.0;
        v.MisleadNorm = Clamp01(v.M15MisleadGap / 6.0);
        v.FunnelNorm = 1.0 - Clamp01(Math.Log10(Math.Max(1, v.M16FunnelWidth)) / 3.0);
        v.NaiveNorm = Clamp01(v.M17NaiveFailRate);
        v.EntropyNorm = Clamp01(v.M18ChoiceEntropy / (1.5 * Math.Max(1, v.M01Steps)));
        v.SizeNorm = Clamp01((v.M19Size - 10) / 10.0);

        v.DCore = 12 * v.StepsNorm + 9 * v.SlackNorm + 4 * v.EndSlackNorm
            + 6 * v.TightNorm + 5 * v.BufferNorm + 6 * v.TruncNorm
            + 5 * v.DetourNorm + 3 * v.CorridorNorm + 2 * v.BranchNorm + 2 * v.OpenNorm
            + 7 * v.MechNorm + 6 * v.GateNorm + 2 * v.LateNorm
            + 5 * v.SizeNorm;
        v.DRaw = v.DCore
            + 9 * v.FaNorm + 4 * v.MisleadNorm + 5 * v.FunnelNorm
            + 4 * v.NaiveNorm + 4 * v.EntropyNorm;
    }

    private static (int Count, int Gap, bool Estimated) ComputeFalseAffordance(
        LevelSpec spec, int steps, HashSet<Cell> eatenFoods,
        Dictionary<Cell, int> distFromSpawn, Dictionary<Cell, int> distFromGoal, int solveBudget,
        RuleState state)
    {
        int count = 0;
        int gap = 0;
        bool estimated = false;
        Search.ExactBfsSolver solver = null;

        foreach (Cell food in spec.Foods)
        {
            if (eatenFoods.Contains(food))
            {
                continue;
            }
            if (!distFromSpawn.TryGetValue(food, out int ds) || !distFromGoal.TryGetValue(food, out int dg))
            {
                continue;
            }
            if (ds + dg > steps - 2)
            {
                continue;
            }

            LevelSpec without = CloneSpec(spec, food);
            solver ??= new Search.ExactBfsSolver();
            state.ResetToSpec(without);
            Godot.Collections.Dictionary result = solver.Solve(state, solveBudget);
            string status = result["status"].AsString();
            if (status == "UNKNOWN")
            {
                estimated = true;
                continue;
            }
            if (status == "SOLVABLE" && result["steps"].AsInt32() <= steps + 2)
            {
                count++;
                gap = Math.Max(gap, steps - (ds + dg));
            }
        }
        state.ResetToSpec(spec);
        return (count, gap, estimated);
    }

    private static double ComputeNaiveFailRate(LevelSpec spec, int steps,
        Dictionary<Cell, int> distFromSpawn, Dictionary<Cell, int> distFromGoal, RuleState state)
    {
        int limit = steps + 5;
        int fail = 0;
        if (!RunGreedy(state, spec, limit, (st, goal) => ChooseTowardGoal(st, distFromGoal)))
        {
            fail++;
        }
        if (!RunGreedy(state, spec, limit, (st, goal) => ChooseFoodFirst(st, goal, distFromGoal)))
        {
            fail++;
        }
        if (!RunGreedy(state, spec, limit, (st, goal) => ChooseStaticShortest(st, distFromSpawn)))
        {
            fail++;
        }
        return fail / 3.0;
    }

    private static bool RunGreedy(RuleState st, LevelSpec spec, int stepLimit,
        Func<RuleState, Cell, Cell> choose)
    {
        st.ResetToSpec(spec);
        for (int i = 0; i < stepLimit; i++)
        {
            if (st.Head.Equals(spec.Goal))
            {
                return true;
            }
            Cell dir = choose(st, spec.Goal);
            if (dir.Equals(Cell.Zero))
            {
                return false;
            }
            RuleState.StepOutcome outcome = st.StepCore(dir);
            if (!outcome.Accepted)
            {
                return false;
            }
            if (outcome.Won)
            {
                return true;
            }
            if (outcome.DeadlockMsg.Length > 0)
            {
                return false;
            }
        }
        return st.Head.Equals(spec.Goal);
    }

    private static Cell ChooseTowardGoal(RuleState st, Dictionary<Cell, int> distGoal)
    {
        Cell head = st.Head;
        for (int pass = 0; pass < 2; pass++)
        {
            Cell bestDir = Cell.Zero;
            int bestDist = int.MaxValue;
            foreach (Cell dir in Directions)
            {
                Cell next = head + dir;
                RuleState.MoveKind kind = st.ClassifyMove(next);
                bool accept = pass == 0 ? kind == RuleState.MoveKind.Walk : kind == RuleState.MoveKind.Body;
                if (!accept || !distGoal.TryGetValue(next, out int d))
                {
                    continue;
                }
                if (d < bestDist)
                {
                    bestDist = d;
                    bestDir = dir;
                }
            }
            if (!bestDir.Equals(Cell.Zero))
            {
                return bestDir;
            }
        }
        return Cell.Zero;
    }

    private static Cell ChooseFoodFirst(RuleState st, Cell goal, Dictionary<Cell, int> distGoal)
    {
        Cell head = st.Head;
        var empty = new HashSet<Cell>();
        var dist = st.Grid.BfsDistMap(head, empty);
        Cell target = Cell.Zero;
        int best = int.MaxValue;
        foreach (Cell food in st.RemainingFoods)
        {
            if (dist.TryGetValue(food, out int d) && d < best)
            {
                best = d;
                target = food;
            }
        }
        if (target.Equals(Cell.Zero))
        {
            return ChooseTowardGoal(st, distGoal);
        }

        Cell bestDir = Cell.Zero;
        int bestDist = best;
        foreach (Cell dir in Directions)
        {
            Cell next = head + dir;
            if (st.ClassifyMove(next) != RuleState.MoveKind.Walk)
            {
                continue;
            }
            if (dist.TryGetValue(next, out int d) && d < bestDist)
            {
                bestDist = d;
                bestDir = dir;
            }
        }
        return bestDir;
    }

    private static Cell ChooseStaticShortest(RuleState st, Dictionary<Cell, int> distSpawn)
    {
        Cell head = st.Head;
        if (!distSpawn.TryGetValue(head, out int cur))
        {
            return Cell.Zero;
        }
        foreach (Cell dir in Directions)
        {
            Cell next = head + dir;
            if (st.ClassifyMove(next) != RuleState.MoveKind.Walk)
            {
                continue;
            }
            if (distSpawn.TryGetValue(next, out int d) && d == cur - 1)
            {
                return dir;
            }
        }
        return Cell.Zero;
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

    private static LevelSpec CloneSpec(LevelSpec spec, Cell removeFood)
    {
        var copy = new LevelSpec
        {
            SchemaVersion = spec.SchemaVersion,
            GeneratorVersion = spec.GeneratorVersion,
            Seed = spec.Seed,
            LevelId = spec.LevelId,
            Name = spec.Name,
            Origin = spec.Origin,
            Width = spec.Width,
            Height = spec.Height,
            Spawn = spec.Spawn,
            Goal = spec.Goal,
            InitialMaxLen = spec.InitialMaxLen,
            FoodLenGain = spec.FoodLenGain,
            GrowthInterval = spec.GrowthInterval,
            MechanismCap = spec.MechanismCap,
        };
        foreach (Cell c in spec.Floor)
        {
            copy.Floor.Add(c);
        }
        foreach (Cell c in spec.Obstacles)
        {
            copy.Obstacles.Add(c);
        }
        foreach (Cell c in spec.Foods)
        {
            if (!c.Equals(removeFood))
            {
                copy.Foods.Add(c);
            }
        }
        foreach (Cell c in spec.MechCenters)
        {
            copy.MechCenters.Add(c);
        }
        foreach (int level in spec.MechInitialLevels)
        {
            copy.MechInitialLevels.Add(level);
        }
        return copy;
    }

    private static Cell DirCell(char ch)
    {
        return ch switch
        {
            'A' => new Cell(-1, 0),
            'D' => new Cell(1, 0),
            'W' => new Cell(0, -1),
            'S' => new Cell(0, 1),
            _ => Cell.Zero,
        };
    }

    private static int Chebyshev(Cell a, Cell b)
    {
        return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private static double Clamp01(double value)
    {
        return Math.Clamp(value, 0.0, 1.0);
    }
}

/// <summary>19 项原始指标 + 归一化值 + 两段难度指数。</summary>
public sealed class MetricVector
{
    public bool RouteValid;
    internal List<Cell> RouteCells = new();
    internal List<Cell> RouteDirs = new();
    internal List<int> GrewSteps = new();
    internal HashSet<Cell> MechCovered = new();
    internal HashSet<Cell> EatenFoods = new();
    internal bool OvershootReturn;
    internal int OvershootMax;
    internal int BodyKeyStep = -1;
    internal int BodyKeyRemaining;
    internal int BodyKeyOptions;
    public int M01Steps;
    public int M02MinSlack;
    public int M03EndSlack;
    public double M04FoodTightness;
    public double M05LenBuffer;
    public int M06ForcedTruncations;
    public double M07DetourRatio;
    public double M08CorridorRatio;
    public double M09Branching;
    public double M10Openness;
    public double M11MechPressure;
    public int M12GateLayers;
    public bool M13LateGrowth;
    public int M14FalseAffordance;
    public int M15MisleadGap;
    public int M16FunnelWidth;
    public double M17NaiveFailRate;
    public double M18ChoiceEntropy;
    public int M19Size;

    public double StepsNorm;
    public double SlackNorm;
    public double EndSlackNorm;
    public double TightNorm;
    public double BufferNorm;
    public double TruncNorm;
    public double DetourNorm;
    public double CorridorNorm;
    public double BranchNorm;
    public double OpenNorm;
    public double MechNorm;
    public double GateNorm;
    public double LateNorm;
    public double FaNorm;
    public double MisleadNorm;
    public double FunnelNorm;
    public double NaiveNorm;
    public double EntropyNorm;
    public double SizeNorm;

    public double DCore;
    public double DRaw;
    public bool CEstimated;

    public Dictionary<string, double> ToDictionary()
    {
        return new Dictionary<string, double>
        {
            ["steps"] = M01Steps,
            ["min_slack"] = M02MinSlack,
            ["end_slack"] = M03EndSlack,
            ["food_tightness"] = M04FoodTightness,
            ["len_buffer"] = M05LenBuffer,
            ["forced_truncations"] = M06ForcedTruncations,
            ["detour_ratio"] = M07DetourRatio,
            ["corridor_ratio"] = M08CorridorRatio,
            ["branching"] = M09Branching,
            ["openness"] = M10Openness,
            ["mech_pressure"] = M11MechPressure,
            ["gate_layers"] = M12GateLayers,
            ["late_growth"] = M13LateGrowth ? 1 : 0,
            ["false_affordance"] = M14FalseAffordance,
            ["mislead_gap"] = M15MisleadGap,
            ["funnel_width"] = M16FunnelWidth,
            ["naive_fail_rate"] = M17NaiveFailRate,
            ["choice_entropy"] = M18ChoiceEntropy,
            ["size"] = M19Size,
            ["d_core"] = DCore,
            ["d_raw"] = DRaw,
            ["c_estimated"] = CEstimated ? 1 : 0,
        };
    }
}
