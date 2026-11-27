using System;
using System.Collections.Generic;
using Growth.Algorithms.Model;

namespace Growth.Algorithms.Generation;

/// <summary>一个候选 S/E 对 + 精确解路线与排序特征。</summary>
public sealed class PairCandidate
{
    public Cell Spawn;
    public Cell Goal;
    /// <summary>已写入 S/E 与花瓣的完整关卡（机关场来自模板）。</summary>
    public LevelSpec Spec;
    public string Route = "";
    public int Steps;
    public double Rank;
    /// <summary>路线上「刚经过就即将被覆盖」的步数（交互压力）。</summary>
    public int Threat;
    /// <summary>路线上「旁边有看起来更近的分支」的步数（欺骗诱因）。</summary>
    public int Temptation;
    /// <summary>规范路线上至少有两个可走方向的步数（明显岔路）。</summary>
    public int ForkSteps;
    /// <summary>规范路线上的截断次数（回路 / 踩回身体）。</summary>
    public int Truncations;
    /// <summary>路线外的诱饵花瓣数（不吃也能通关）。</summary>
    public int DecoyFoods;
    /// <summary>规范路线实际吃到的花瓣数。</summary>
    public int RouteFoods;
}

/// <summary>
/// 机关优先的 S/E 选择（生成算法 v3 §3.3-P2/P3）：
/// 机关场固定后，枚举候选 S/E 对，先沿静态路径放花瓣（含一枚死路花瓣制造回路），
/// 再用精确求解器得到**时间规则下的真实路线**，按其结构特征（岔路 / 截断 / 威胁 / 目标步数）排序。
/// 只调用现有规则与模型，不复制规则。
/// </summary>
public static class PairSelector
{
    // 加载期生成已移到后台线程：不再为 50ms 时间片压缩候选数，恢复枚举上限换更好的 Top-K。
    public const int MaxPairs = 10;
    public const int KeepTop = 3;
    private const int PairSolveBudget = 250_000;

    public static List<PairCandidate> Select(LevelSpec field, TemplateInstance instance,
        int targetSteps, int minSteps, int maxSteps, DeterministicRng rng)
    {
        var results = new List<PairCandidate>();
        if (field == null || instance == null || field.Floor.Count == 0)
        {
            return results;
        }

        HashSet<Cell> initialBlocked = InitialBlockedCells(field);
        GridModel grid = BuildGrid(field, initialBlocked);
        var obstacleSet = new HashSet<Cell>(field.Obstacles);
        var spawnPool = new List<Cell>();
        foreach (Cell c in field.Floor)
        {
            if (obstacleSet.Contains(c) || initialBlocked.Contains(c)
                || c.Equals(field.Spawn) || c.Equals(field.Goal))
            {
                continue;
            }
            spawnPool.Add(c);
        }
        if (spawnPool.Count < 2)
        {
            return results;
        }

        List<Cell> spawnOrder = RouteFirstGenerator.SpawnOrder(spawnPool, rng, field.Tier);
        int spawnLimit = Math.Min(spawnOrder.Count, field.Tier is "hard" or "expert" ? 8 : 6);
        var empty = new HashSet<Cell>();
        var walkableSet = new HashSet<Cell>(spawnPool);
        RouteFirstGenerator.WalkBounds bounds = RouteFirstGenerator.BoundsOf(spawnPool);
        int pairCount = 0;

        // 端点内移优先于交互分：否则高 fork/truncation 的边缘端点会稳定压过内部端点，
        // 让“起点终点都在边缘”的旧观感继续出现。
        bool requireInteriorGoal = field.Tier is "hard" or "expert";
        int interiorPairs = 0;
        int EndpointClass(PairCandidate c)
        {
            int spawnMargin = RouteFirstGenerator.BorderMargin(c.Spawn, bounds);
            int goalMargin = RouteFirstGenerator.BorderMargin(c.Goal, bounds);
            if (requireInteriorGoal)
            {
                return spawnMargin >= 2 && goalMargin >= 2 ? 0 : spawnMargin >= 2 ? 1 : 2;
            }
            return spawnMargin >= 2 ? 0 : 1;
        }

        // 已有 3 个内部端点候选就提前停：再多也只是被 KeepTop 截掉，白白增加求解耗时。
        for (int si = 0; si < spawnLimit && pairCount < MaxPairs && interiorPairs < KeepTop; si++)
        {
            if (GenerationTrace.Expired)
            {
                break;
            }
            Cell spawn = spawnOrder[si];
            Dictionary<Cell, int> dist = grid.BfsDistMap(spawn, empty);
            var goalPool = new List<Cell>();
            foreach (KeyValuePair<Cell, int> kv in dist)
            {
                if (kv.Value >= minSteps && kv.Value <= maxSteps)
                {
                    goalPool.Add(kv.Key);
                }
            }
            if (goalPool.Count == 0)
            {
                continue;
            }
            RouteFirstGenerator.SortGoalPool(goalPool, dist, targetSteps, walkableSet, field.Tier,
                bounds);

            int goalLimit = Math.Min(goalPool.Count, 2);
            for (int gi = 0; gi < goalLimit && pairCount < MaxPairs && interiorPairs < KeepTop; gi++)
            {
                if (GenerationTrace.Expired)
                {
                    break;
                }
                PairCandidate candidate = TryPair(field, grid, spawn, goalPool[gi], instance,
                    targetSteps, minSteps, maxSteps, bounds);
                if (candidate == null)
                {
                    continue;
                }
                results.Add(candidate);
                pairCount++;
                if (EndpointClass(candidate) == 0)
                {
                    interiorPairs++;
                }
            }
        }

        results.Sort((a, b) =>
        {
            int classA = EndpointClass(a);
            int classB = EndpointClass(b);
            if (classA != classB)
            {
                return classA.CompareTo(classB);
            }
            int cmp = b.Rank.CompareTo(a.Rank);
            if (cmp != 0)
            {
                return cmp;
            }
            Cell sa = a.Spawn;
            Cell sb = b.Spawn;
            cmp = sa.Y != sb.Y ? sa.Y.CompareTo(sb.Y) : sa.X.CompareTo(sb.X);
            if (cmp != 0)
            {
                return cmp;
            }
            return a.Goal.Y != b.Goal.Y ? a.Goal.Y.CompareTo(b.Goal.Y) : a.Goal.X.CompareTo(b.Goal.X);
        });
        if (results.Count > KeepTop)
        {
            results.RemoveRange(KeepTop, results.Count - KeepTop);
        }
        return results;
    }

    private static PairCandidate TryPair(LevelSpec field, GridModel sharedGrid, Cell spawn, Cell goal,
        TemplateInstance instance, int targetSteps, int minSteps, int maxSteps,
        RouteFirstGenerator.WalkBounds bounds)
    {
        // 每对使用独立网格：死路雕刻产生的障碍不能泄漏到其他候选。
        GridModel grid = CloneGrid(sharedGrid);
        List<Cell> path = RouteFirstGenerator.BfsPath(grid, spawn, goal);
        if (path == null)
        {
            return null;
        }
        int pathSteps = path.Count - 1;
        if (pathSteps < minSteps || pathSteps > maxSteps)
        {
            return null;
        }

        LevelSpec probe = CloneField(field);
        probe.Spawn = spawn;
        probe.Goal = goal;
        PlaceMechanismsOnRoute(probe, path, grid);
        var decoys = new List<Cell>();
        if (instance.AllowsFoods)
        {
            int decoyTarget = path.Count >= 10 ? 2 : 1;
            if (!PlaceFoods(probe, path, grid, decoyTarget, out decoys))
            {
                return null;
            }
        }
        else
        {
            probe.Foods.Clear();
        }
        // 用精确求解器得到时间规则下的真实路线（会自然产生绕行 / 截断 / 回路）。
        RuleState solveState = RouteFirstGenerator.GetSolveState();
        solveState.Setup(probe);
        solveState.Start();
        Godot.Collections.Dictionary solved = RouteFirstGenerator.GetSolver().Solve(solveState, PairSolveBudget);
        if (solved["status"].AsString() != "SOLVABLE")
        {
            return null;
        }
        string route = solved["route"].AsString();
        if (route.Length < minSteps)
        {
            return null;
        }

        // 重放统计结构特征。
        var replay = new RuleState();
        replay.Setup(probe);
        replay.Start();
        int truncations = 0;
        int forkSteps = 0;
        var trailOrder = new List<Cell> { replay.Head };
        for (int i = 0; i < route.Length; i++)
        {
            Cell head = replay.Head;
            int walkOptions = 0;
            foreach (Cell d in Directions)
            {
                if (replay.ClassifyMove(head + d) == RuleState.MoveKind.Walk)
                {
                    walkOptions++;
                }
            }
            if (walkOptions >= 2)
            {
                forkSteps++;
            }
            RuleState.StepOutcome outcome = replay.StepCore(DirForChar(route[i]));
            if (!outcome.Accepted)
            {
                return null;
            }
            if (outcome.Truncated)
            {
                truncations++;
            }
            trailOrder.Add(replay.Head);
        }

        int horizon = Math.Max(route.Length + 12, targetSteps + 12);
        var model = new TimeExpandedModel(probe, horizon);
        var empty = new HashSet<Cell>();
        Dictionary<Cell, int> distGoal = grid.BfsDistMap(goal, empty);
        int interval = Math.Max(probe.GrowthInterval, 1);

        // 诱饵 = 路线外花瓣；路线实际吃到的花瓣单独统计，用于结构验收。
        var routeSet = new HashSet<Cell>(trailOrder);
        int routeFoods = 0;
        foreach (Cell f in probe.Foods)
        {
            if (routeSet.Contains(f))
            {
                routeFoods++;
            }
        }
        int decoyFoods = 0;
        foreach (Cell d in decoys)
        {
            if (!routeSet.Contains(d))
            {
                decoyFoods++;
            }
        }

        int threat = 0;
        for (int i = 1; i < trailOrder.Count; i++)
        {
            int coverTime = model.CoverTimeOf(trailOrder[i]);
            if (coverTime < TimeExpandedModel.INF && coverTime >= i && coverTime <= i + 2 * interval)
            {
                threat++;
            }
        }

        int temptation = 0;
        for (int i = 1; i < trailOrder.Count; i++)
        {
            Cell head = trailOrder[i - 1];
            Cell next = trailOrder[i];
            if (!distGoal.TryGetValue(next, out int nextDist))
            {
                continue;
            }
            foreach (Cell n in GridModel.Neighbors(head))
            {
                if (n.Equals(next) || !grid.IsFloor(n) || grid.Obstacles.Contains(n))
                {
                    continue;
                }
                if (model.IsBlockedAt(n, i - 1))
                {
                    continue;
                }
                if (distGoal.TryGetValue(n, out int dist) && dist < nextDist)
                {
                    temptation++;
                }
            }
        }

        double fit = 1.0 - Math.Min(1.0,
            Math.Abs(route.Length - targetSteps) / (double)Math.Max(4, maxSteps - minSteps + 1));
        // 端点位于可走区内部（多方向进入）时小幅加分：边缘端点容易把谜题压成单路。
        int spawnMargin = RouteFirstGenerator.BorderMargin(spawn, bounds);
        int goalMargin = RouteFirstGenerator.BorderMargin(goal, bounds);
        double interiorBonus = spawnMargin >= 2 && goalMargin >= 2
            ? 0.08
            : spawnMargin >= 2 ? 0.04 : 0.0;
        // 结构项优先：岔路 / 截断（回路）比"贴着机关"更能体现交互。
        double rank = 0.28 * fit
            + 0.15 * Math.Min(threat, 4) / 4.0
            + 0.25 * Math.Min(forkSteps, 4) / 4.0
            + 0.20 * Math.Min(truncations, 2) / 2.0
            + 0.05 * Math.Min(decoyFoods, 2) / 2.0
            + 0.07 * Math.Clamp(instance.Prior, 0.0, 1.0)
            + interiorBonus;

        return new PairCandidate
        {
            Spawn = spawn,
            Goal = goal,
            Spec = probe,
            Route = route,
            Steps = route.Length,
            Rank = rank,
            Threat = threat,
            Temptation = temptation,
            ForkSteps = forkSteps,
            Truncations = truncations,
            DecoyFoods = decoyFoods,
            RouteFoods = routeFoods,
        };
    }

    /// <summary>
    /// 把模板机关改成"贴着规范路线"放置，由构造保证机关参与玩法：
    ///   · 路线旁有空格 → 放在相邻格，机关升级时覆盖路线格（按预计到达步数反推初始阶段）；
    ///   · 单格走廊没有旁格 → 放在路线格上并设为负阶段（开场让路，生长后封路）。
    /// 同时优先选择走廊/瓶颈锚点，避免玩家轻易从旁边绕开机关。
    /// </summary>
    private static void PlaceMechanismsOnRoute(LevelSpec spec, IReadOnlyList<Cell> path, GridModel grid)
    {
        int count = Math.Min(spec.MechCenters.Count, spec.MechInitialLevels.Count);
        if (count == 0 || path.Count < 6)
        {
            return;
        }
        var pathSet = new HashSet<Cell>(path);
        int interval = Math.Max(spec.GrowthInterval, 1);
        int cap = Math.Clamp(spec.MechanismCap, 0, MechanicShapes.MaxLevel);
        int maxLevel = Math.Max(cap - 1, MechanicShapes.MinLevel);
        var used = new HashSet<Cell>();
        var centers = new List<Cell>();
        var levels = new List<int>();
        var originalCenters = new List<Cell>(spec.MechCenters);
        var originalLevels = new List<int>(spec.MechInitialLevels);
        // 只把最多 2 个机关贴到路线上：保证交互，又不过度封死路线导致模板命中率暴跌。
        int onRoute = Math.Min(count, 2);

        for (int i = 0; i < count; i++)
        {
            if (i >= onRoute)
            {
                centers.Add(originalCenters[i]);
                levels.Add(originalLevels[i]);
                continue;
            }
            int target = (int)Math.Round((i + 1) / (double)(onRoute + 1) * (path.Count - 1));
            int anchorIdx = PickBottleneckIndex(path, target, grid);
            Cell anchor = path[anchorIdx];
            Cell chosen = Cell.Zero;
            int coverStage = 0; // 路线格本身在 stage 0 被覆盖；旁格在 stage 1

            foreach (Cell n in NonPathNeighbors(anchor, grid, pathSet))
            {
                if (n.Equals(spec.Spawn) || n.Equals(spec.Goal) || used.Contains(n))
                {
                    continue;
                }
                chosen = n;
                coverStage = 1;
                break;
            }
            if (chosen.Equals(Cell.Zero))
            {
                chosen = anchor;
                coverStage = 0;
            }
            if (used.Contains(chosen))
            {
                centers.Add(originalCenters[i]);
                levels.Add(originalLevels[i]);
                continue;
            }
            used.Add(chosen);
            centers.Add(chosen);

            int arriveGrowth = anchorIdx / interval;
            // 比"玩家到达"晚一个生长周期关闭，保证可通行但必须规划节奏。
            int level = coverStage - arriveGrowth - 1;
            if (coverStage == 1)
            {
                // 旁格必须在开场不被覆盖，否则原路线立即被封死。
                level = Math.Min(level, 0);
            }
            else
            {
                // 路线核心开场保持休眠，之后生长封路。
                level = Math.Min(level, -1);
            }
            levels.Add(Math.Clamp(level, MechanicShapes.MinLevel, maxLevel));
        }

        if (centers.Count == 0)
        {
            return;
        }
        spec.MechCenters.Clear();
        spec.MechCenters.AddRange(centers);
        spec.MechInitialLevels.Clear();
        spec.MechInitialLevels.AddRange(levels);
        spec.ParamSignature = ParamSampler.BuildSignature(spec.InitialMaxLen, spec.FoodLenGain,
            spec.GrowthInterval, spec.MechanismCap, spec.MechInitialLevels);
    }

    /// <summary>在目标索引附近找可走度更低（更像走廊/瓶颈）的路线格。</summary>
    private static int PickBottleneckIndex(IReadOnlyList<Cell> path, int target, GridModel grid)
    {
        target = Math.Clamp(target, 2, path.Count - 2);
        int best = target;
        int bestDegree = WalkableDegree(grid, path[target]);
        for (int radius = 1; radius <= 4; radius++)
        {
            for (int sign = -1; sign <= 1; sign += 2)
            {
                int j = target + radius * sign;
                if (j < 2 || j >= path.Count - 1)
                {
                    continue;
                }
                int degree = WalkableDegree(grid, path[j]);
                if (degree < bestDegree)
                {
                    bestDegree = degree;
                    best = j;
                }
            }
            if (bestDegree <= 2)
            {
                break;
            }
        }
        return best;
    }

    private static int WalkableDegree(GridModel grid, Cell cell)
    {
        int degree = 0;
        foreach (Cell n in GridModel.Neighbors(cell))
        {
            if (grid.IsFloor(n) && !grid.Obstacles.Contains(n))
            {
                degree++;
            }
        }
        return degree;
    }

    private static IEnumerable<Cell> NonPathNeighbors(Cell cell, GridModel grid, HashSet<Cell> pathSet)
    {
        foreach (Cell n in GridModel.Neighbors(cell))
        {
            if (pathSet.Contains(n) || !grid.IsFloor(n) || grid.Obstacles.Contains(n))
            {
                continue;
            }
            yield return n;
        }
    }

    /// <summary>
    /// 花瓣放置（结构优先）：
    ///   1. 雕刻 1–2 个死路/岔口，放入**路线外诱饵花瓣**（不吃也能通关，但会诱导绕路）；
    ///   2. 必修花瓣按 L0 / Gain 沿路线在即将耗尽处放置；
    ///   3. 绝不为塞下花瓣而抬高 L0（旧逻辑会抬到「路线长 − 花瓣增益」）。
    /// 返回 false 表示当前参数/路线放不下必修花瓣。
    /// </summary>
    private static bool PlaceFoods(LevelSpec spec, IReadOnlyList<Cell> path, GridModel grid,
        int decoyTarget, out List<Cell> decoys)
    {
        decoys = new List<Cell>();
        var pathSet = new HashSet<Cell>(path);
        int gain = Math.Max(spec.FoodLenGain, 1);
        int required = (int)Math.Ceiling((path.Count - 1 + 2 - spec.InitialMaxLen) / (double)gain);
        required = Math.Max(required, 1);

        // 1) 诱饵：优先雕刻死路，其次用路线旁任意空格（不额外封路）。
        //    每片诱饵沿路线均匀铺开（2 片 = 1/3 与 2/3）；先排除路线前 1/4，
        //    只有实在找不到时才回退到起点附近。
        int minDecoyIndex = Math.Max(1, spec.Tier == "easy" ? path.Count / 5 : path.Count / 4);
        minDecoyIndex = Math.Min(minDecoyIndex, path.Count - 2);
        int carveTries = decoyTarget + 1;
        for (int i = 0; i < carveTries && decoys.Count < decoyTarget; i++)
        {
            int preferred = PreferredDecoyIndex(path.Count, decoys.Count, decoyTarget);
            if (TryCarveDeadEnd(spec, path, grid, preferred, minDecoyIndex, out Cell dead, out _)
                || TryCarveDeadEnd(spec, path, grid, preferred, 1, out dead, out _))
            {
                if (!pathSet.Contains(dead) && !decoys.Contains(dead)
                    && !spec.MechCenters.Contains(dead))
                {
                    decoys.Add(dead);
                }
            }
        }
        while (decoys.Count < decoyTarget)
        {
            int preferred = PreferredDecoyIndex(path.Count, decoys.Count, decoyTarget);
            List<Cell> decoyCells = decoys;
            Cell best = Cell.Zero;
            int bestScore = int.MaxValue;
            void Scan(int fromIndex)
            {
                for (int j = fromIndex; j < path.Count - 1; j++)
                {
                    Cell p = path[j];
                    foreach (Cell n in GridModel.Neighbors(p))
                    {
                        if (!grid.IsFloor(n) || grid.Obstacles.Contains(n)
                            || n.Equals(spec.Spawn) || n.Equals(spec.Goal)
                            || pathSet.Contains(n) || decoyCells.Contains(n)
                            || spec.MechCenters.Contains(n))
                        {
                            continue;
                        }
                        int score = Math.Abs(j - preferred) * 16
                            + Math.Abs(n.X - p.X) * 2 + Math.Abs(n.Y - p.Y);
                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = n;
                        }
                    }
                }
            }
            Scan(minDecoyIndex);
            if (bestScore == int.MaxValue)
            {
                Scan(1);
            }
            if (bestScore == int.MaxValue)
            {
                break;
            }
            decoys.Add(best);
        }
        if (decoys.Count == 0)
        {
            return false;
        }

        // 2) 必修花瓣：在"再走一步就断"的位置补长度，保证路线本身可走完。
        spec.Foods.Clear();
        int maxLen = spec.InitialMaxLen;
        int trail = 1;
        int placed = 0;
        for (int i = 1; i < path.Count; i++)
        {
            Cell next = path[i];
            if (trail >= maxLen)
            {
                return false;
            }
            if (maxLen - trail == 1 && placed < required
                && !next.Equals(spec.Spawn) && !next.Equals(spec.Goal)
                && !decoys.Contains(next) && !spec.MechCenters.Contains(next))
            {
                spec.Foods.Add(next);
                placed++;
            }
            trail++;
            if (spec.Foods.Contains(next))
            {
                maxLen += gain;
            }
        }
        if (placed < required || trail > maxLen)
        {
            return false;
        }

        // 3) 诱饵放到路线外：玩家不吃也能通关，但会被诱惑绕路。
        foreach (Cell d in decoys)
        {
            if (!spec.Foods.Contains(d))
            {
                spec.Foods.Add(d);
            }
        }
        return true;
    }

    /// <summary>第 placed 片诱饵的目标路线位置：沿路线按 (placed+1)/(target+1) 均分。</summary>
    private static int PreferredDecoyIndex(int pathCount, int placed, int target)
    {
        if (pathCount <= 3)
        {
            return 1;
        }
        int segments = Math.Max(1, target + 1);
        int index = (int)Math.Round((pathCount - 1) * (placed + 1) / (double)segments);
        return Math.Clamp(index, 1, pathCount - 2);
    }

    /// <summary>
    /// 在路径旁"雕刻"一个死路：选路径格 p 的一个非路径邻居 n，把 n 的其余邻居涂成静态障碍，
    /// 使 n 度为 1。这样必须走进 n 再原路踩身体返回（产生截断/回路）。
    /// preferredIndex 让死路落在路线的指定位置附近（诱饵均摊，避免全挤在起点）；
    /// minIndex 排除路线前段（起点附近），找不到时才由调用方回退到 1。
    /// 只改当前候选的 spec.Obstacles 与局部 grid，不影响其他候选。
    /// </summary>
    private static bool TryCarveDeadEnd(LevelSpec spec, IReadOnlyList<Cell> path, GridModel grid,
        int preferredIndex, int minIndex, out Cell cell, out int pathIndex)
    {
        var pathSet = new HashSet<Cell>(path);
        Cell bestCell = Cell.Zero;
        int bestIndex = -1;
        List<Cell> bestWalls = null;
        int bestScore = int.MaxValue;
        int fromIndex = Math.Clamp(minIndex, 1, Math.Max(1, path.Count - 2));

        // 第一遍：把路线旁已有地板的其它邻居封成墙。
        for (int j = fromIndex; j < path.Count - 1; j++)
        {
            Cell p = path[j];
            foreach (Cell n in GridModel.Neighbors(p))
            {
                if (pathSet.Contains(n) || grid.Obstacles.Contains(n) || !grid.IsFloor(n)
                    || n.Equals(spec.Spawn) || n.Equals(spec.Goal))
                {
                    continue;
                }
                var others = new List<Cell>();
                bool safe = true;
                foreach (Cell m in GridModel.Neighbors(n))
                {
                    if (m.Equals(p))
                    {
                        continue;
                    }
                    if (pathSet.Contains(m) || m.Equals(spec.Spawn) || m.Equals(spec.Goal))
                    {
                        safe = false;
                        break;
                    }
                    others.Add(m);
                }
                if (!safe)
                {
                    continue;
                }
                // 少封路优先：直接利用既有凹室（无需封邻居）能保留地图环路，
                // 其次才考虑需要封 1–2 个邻居的宽格；两项都服从“沿路线均摊”。
                int walls = 0;
                foreach (Cell m in others)
                {
                    if (grid.IsFloor(m))
                    {
                        walls++;
                    }
                }
                int score = walls * 64 + Math.Abs(j - preferredIndex) * 8
                    + Math.Abs(n.X - p.X) * 2 + Math.Abs(n.Y - p.Y);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestCell = n;
                    bestIndex = j;
                    bestWalls = others;
                }
            }
        }
        if (bestIndex >= 0)
        {
            foreach (Cell m in bestWalls)
            {
                if (!grid.IsFloor(m))
                {
                    continue;
                }
                grid.Obstacles.Add(m);
                if (!spec.Obstacles.Contains(m))
                {
                    spec.Obstacles.Add(m);
                }
            }
            cell = bestCell;
            pathIndex = bestIndex;
            return true;
        }

        // 第二遍：路线被墙夹住时，把相邻的非可走格挖成单格死路（新增地板 + 封住其余出口）。
        bestScore = int.MaxValue;
        bestWalls = null;
        for (int j = fromIndex; j < path.Count - 1; j++)
        {
            Cell p = path[j];
            foreach (Cell n in GridModel.Neighbors(p))
            {
                if (pathSet.Contains(n) || n.Equals(spec.Spawn) || n.Equals(spec.Goal)
                    || spec.MechCenters.Contains(n) || !grid.InBounds(n))
                {
                    continue;
                }
                if (grid.IsFloor(n) && !grid.Obstacles.Contains(n))
                {
                    continue; // 已由第一遍处理
                }
                bool safe = true;
                foreach (Cell m in GridModel.Neighbors(n))
                {
                    if (!m.Equals(p) && pathSet.Contains(m))
                    {
                        safe = false;
                        break;
                    }
                }
                if (!safe)
                {
                    continue;
                }
                var walls = new List<Cell>();
                foreach (Cell m in GridModel.Neighbors(n))
                {
                    if (!m.Equals(p) && grid.IsFloor(m))
                    {
                        walls.Add(m);
                    }
                }
                int score = walls.Count * 64 + Math.Abs(j - preferredIndex) * 8
                    + Math.Abs(n.X - p.X) * 2 + Math.Abs(n.Y - p.Y);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestCell = n;
                    bestIndex = j;
                    bestWalls = walls;
                }
            }
        }
        if (bestIndex >= 0)
        {
            // 把 n 变成地板，并封住除 p 以外的可走邻居。
            grid.Obstacles.Remove(bestCell);
            grid.Floor.Add(bestCell);
            if (!spec.Floor.Contains(bestCell))
            {
                spec.Floor.Add(bestCell);
            }
            spec.Obstacles.Remove(bestCell);
            foreach (Cell m in bestWalls)
            {
                grid.Obstacles.Add(m);
                if (!spec.Obstacles.Contains(m))
                {
                    spec.Obstacles.Add(m);
                }
            }
            cell = bestCell;
            pathIndex = bestIndex;
            return true;
        }

        cell = Cell.Zero;
        pathIndex = -1;
        return false;
    }

    private static GridModel CloneGrid(GridModel grid)
    {
        var copy = new GridModel(grid.Origin, grid.Width, grid.Height);
        foreach (Cell c in grid.Floor)
        {
            copy.Floor.Add(c);
        }
        foreach (Cell c in grid.Obstacles)
        {
            copy.Obstacles.Add(c);
        }
        return copy;
    }

    /// <summary>开局即被机关占据的格（stage 0），选 S/E 与静态 BFS 时视为障碍。</summary>
    private static HashSet<Cell> InitialBlockedCells(LevelSpec field)
    {
        var blocked = new HashSet<Cell>();
        int cap = Math.Clamp(field.MechanismCap, 0, MechanicShapes.MaxLevel);
        var cones = new List<CoverageCone>(field.MechCenters.Count);
        for (int i = 0; i < field.MechCenters.Count; i++)
        {
            int initial = i < field.MechInitialLevels.Count ? field.MechInitialLevels[i] : 0;
            cones.Add(new CoverageCone(field.MechCenters[i],
                Math.Clamp(initial, MechanicShapes.MinLevel, cap), cap));
        }
        foreach (KeyValuePair<Cell, int> kv in CoverageCone.Union(cones))
        {
            if (kv.Value == 0 && !kv.Key.Equals(field.Spawn) && !kv.Key.Equals(field.Goal))
            {
                blocked.Add(kv.Key);
            }
        }
        return blocked;
    }

    private static GridModel BuildGrid(LevelSpec field, HashSet<Cell> initialBlocked)
    {
        var grid = new GridModel(field.Origin, field.Width, field.Height);
        foreach (Cell c in field.Floor)
        {
            grid.Floor.Add(c);
        }
        foreach (Cell c in field.Obstacles)
        {
            grid.Obstacles.Add(c);
        }
        foreach (Cell c in initialBlocked)
        {
            grid.Obstacles.Add(c);
        }
        return grid;
    }

    private static LevelSpec CloneField(LevelSpec field)
    {
        var copy = new LevelSpec
        {
            Seed = field.Seed,
            LevelId = field.LevelId,
            Name = field.Name,
            Tier = field.Tier,
            Origin = field.Origin,
            Width = field.Width,
            Height = field.Height,
            Spawn = field.Spawn,
            Goal = field.Goal,
            InitialMaxLen = field.InitialMaxLen,
            FoodLenGain = field.FoodLenGain,
            GrowthInterval = field.GrowthInterval,
            MechanismCap = field.MechanismCap,
            TargetSteps = field.TargetSteps,
            ParamSignature = field.ParamSignature,
        };
        foreach (Cell c in field.Floor)
        {
            copy.Floor.Add(c);
        }
        foreach (Cell c in field.Obstacles)
        {
            copy.Obstacles.Add(c);
        }
        foreach (Cell c in field.MechCenters)
        {
            copy.MechCenters.Add(c);
        }
        foreach (int level in field.MechInitialLevels)
        {
            copy.MechInitialLevels.Add(level);
        }
        return copy;
    }

    private static readonly Cell[] Directions =
    {
        new(-1, 0), new(1, 0), new(0, -1), new(0, 1),
    };

    private static Cell DirForChar(char ch)
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
}
