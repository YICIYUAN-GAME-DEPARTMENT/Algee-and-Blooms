using System;
using System.Collections.Generic;

namespace Growth.Algorithms.Model;

/// <summary>
/// R1 时间展开模型（生成算法 v3 §2.2 / §3.3-P2）。
/// 状态 (v, t, k)：v = head 格；t = 已走步数；k = 松弛的已吃花数。
/// 与 RuleState 对齐的口径：coverStep 时刻表、第 t 步移动使用 M(t−1)（先移动、后生长）、
/// spawn / goal 永不被机关覆盖。R1 是必要条件的松弛（不建模身体几何 / 截断 / 身体屏蔽），
/// 因此 R2 ⊆ R1；本类不复制精确规则，也不访问节点 / 场景 / 信号。
/// </summary>
public sealed class TimeExpandedModel
{
    public const int INF = 1 << 30;

    private const int DefaultHorizonMin = 24;
    private const int DefaultHorizonMargin = 8;

    private readonly Cell _origin;
    private readonly int _width;
    private readonly int _height;
    private readonly int _count;

    private readonly HashSet<Cell> _floor;
    private readonly bool[] _walkable;
    private readonly bool[] _isFood;
    private readonly int[] _coverStage;
    private readonly int[] _distSpawn;
    private readonly int[] _distGoal;
    private readonly int[][] _adjacency;
    private bool[][] _mechSet;
    private readonly bool[][][] _reachable;
    private readonly int[][] _latest;
    private readonly bool[][][] _viable;

    /// <summary>时间上限 T（步）。</summary>
    public int Horizon { get; }
    /// <summary>食物总数 F（k 的上界）。</summary>
    public int FoodCount { get; }
    /// <summary>最大生长次数 g_max = max(cap − a_i)。</summary>
    public int GMax { get; private set; }
    public int GrowthInterval { get; }
    public int InitialMaxLen { get; }
    public int FoodLenGain { get; }
    public int SpawnIndex { get; }
    public int GoalIndex { get; }
    public Cell Spawn { get; }
    public Cell Goal { get; }
    public int CellCount => _count;

    /// <summary>默认时间上限：至少 24 步，或目标步数 + 8。</summary>
    public static int DefaultHorizon(LevelSpec spec)
    {
        int target = spec != null ? Math.Max(spec.TargetSteps, 0) : 0;
        return Math.Max(DefaultHorizonMin, target + DefaultHorizonMargin);
    }

    public TimeExpandedModel(LevelSpec spec, int timeHorizon = 0)
    {
        if (spec == null)
        {
            throw new ArgumentNullException(nameof(spec));
        }

        _origin = spec.Origin;
        _width = Math.Max(spec.Width, 1);
        _height = Math.Max(spec.Height, 1);
        _count = _width * _height;
        Horizon = timeHorizon > 0 ? timeHorizon : DefaultHorizon(spec);
        GrowthInterval = Math.Max(spec.GrowthInterval, 1);
        InitialMaxLen = Math.Max(spec.InitialMaxLen, 1);
        FoodLenGain = spec.FoodLenGain;
        Spawn = spec.Spawn;
        Goal = spec.Goal;
        FoodCount = Math.Max(spec.Foods.Count, 0);

        _floor = new HashSet<Cell>(spec.Floor);
        _walkable = new bool[_count];
        _isFood = new bool[_count];
        _coverStage = new int[_count];
        _distSpawn = new int[_count];
        _distGoal = new int[_count];
        _adjacency = new int[_count][];
        Array.Fill(_coverStage, INF);
        Array.Fill(_distSpawn, INF);
        Array.Fill(_distGoal, INF);

        var obstacles = new HashSet<Cell>(spec.Obstacles);
        var foods = new HashSet<Cell>(spec.Foods);
        for (int index = 0; index < _count; index++)
        {
            Cell cell = CellAt(index);
            _walkable[index] = IsFloor(cell) && !obstacles.Contains(cell);
            _isFood[index] = foods.Contains(cell);
        }

        if (!TryIndexOf(Spawn, out int spawnIndex) || !TryIndexOf(Goal, out int goalIndex))
        {
            throw new ArgumentException("Spawn / Goal 必须在关卡范围内", nameof(spec));
        }
        SpawnIndex = spawnIndex;
        GoalIndex = goalIndex;

        BuildCoverStage(spec);
        BuildMechSets();
        BuildAdjacency();
        BuildStaticDistances();
        _reachable = BuildReachable();
        (_latest, _viable) = BuildLatestAndViable();
    }

    // ── 公开查询 ────────────────────────────────────────────────

    /// <summary>已走 t 步后的生长次数：g(t) = min(⌊t / I⌋, g_max)。</summary>
    public int GrowthCount(int t)
    {
        return Math.Min(Math.Max(t, 0) / GrowthInterval, GMax);
    }

    /// <summary>第 t 步移动后 / 第 t 步生长后的机关集合是否覆盖该格。</summary>
    public bool IsMechAt(int growthStage, int index)
    {
        return growthStage >= 0 && growthStage < _mechSet.Length
            && index >= 0 && index < _count && _mechSet[growthStage][index];
    }

    /// <summary>第 t 步移动时的可走判定（使用 M(t) 之外的地形 + M(t) 之后的机关集合）。</summary>
    public bool IsBlockedAt(Cell c, int t)
    {
        if (!TryIndexOf(c, out int index))
        {
            return true;
        }
        return !_walkable[index] || IsMechAt(GrowthCount(t), index);
    }

    public bool TryIndexOf(Cell c, out int index)
    {
        int x = c.X - _origin.X;
        int y = c.Y - _origin.Y;
        if (x < 0 || y < 0 || x >= _width || y >= _height)
        {
            index = -1;
            return false;
        }
        index = y * _width + x;
        return true;
    }

    public Cell CellAt(int index)
    {
        return new Cell(_origin.X + index % _width, _origin.Y + index / _width);
    }

    public bool IsFloor(Cell c)
    {
        return _floor.Count == 0 || _floor.Contains(c);
    }

    public bool IsWalkableAt(int index)
    {
        return index >= 0 && index < _count && _walkable[index];
    }

    /// <summary>可走邻居（Neumann 边界；图扩散与分支枚举共用）。</summary>
    public int[] WalkableNeighbors(int index)
    {
        return index >= 0 && index < _count ? _adjacency[index] : Array.Empty<int>();
    }

    /// <summary>首次覆盖阶段；<see cref="INF"/> = 不覆盖。</summary>
    public int CoverStageAt(int index)
    {
        return index >= 0 && index < _count ? _coverStage[index] : INF;
    }

    public int CoverStageOf(Cell c)
    {
        return TryIndexOf(c, out int index) ? _coverStage[index] : INF;
    }

    /// <summary>首次覆盖时刻（步）；INF = 不覆盖。</summary>
    public int CoverTimeOf(Cell c)
    {
        int stage = CoverStageOf(c);
        return stage >= INF ? INF : stage * GrowthInterval;
    }

    public int StaticDistanceFromSpawn(Cell c)
    {
        return TryIndexOf(c, out int index) ? _distSpawn[index] : INF;
    }

    public int StaticDistanceFromGoal(Cell c)
    {
        return TryIndexOf(c, out int index) ? _distGoal[index] : INF;
    }

    /// <summary>身长上限 L(k) = L0 + ΔL·k。</summary>
    public int LengthAt(int k)
    {
        return InitialMaxLen + FoodLenGain * Math.Clamp(k, 0, FoodCount);
    }

    public bool IsReachable(int k, int t, Cell c)
    {
        return TryIndexOf(c, out int index) && IsReachable(k, t, index);
    }

    /// <summary>时间展开可达（忽略身长 / 身体）；k 为松弛的已吃花数。</summary>
    public bool IsReachable(int k, int t, int index)
    {
        if (index < 0 || index >= _count || t < 0 || t > Horizon)
        {
            return false;
        }
        return _reachable[Math.Clamp(k, 0, FoodCount)][t][index];
    }

    /// <summary>
    /// 从 (v, k) 出发仍能到达终点的最晚步数；-1 表示时间上限内不可达。
    /// 允许后续继续吃花（k' ≥ k），因此取各层 Latest 的上界；这是 R1 的松弛口径。
    /// </summary>
    public int LatestTime(int k, int index)
    {
        if (index < 0 || index >= _count)
        {
            return -1;
        }
        return _latest[Math.Clamp(k, 0, FoodCount)][index];
    }

    public int LatestTime(int k, Cell c)
    {
        return TryIndexOf(c, out int index) ? LatestTime(k, index) : -1;
    }

    /// <summary>slack = Latest − t；负数表示不可行（含终点不可达的哨兵）。</summary>
    public int Slack(int k, int t, Cell c)
    {
        int latest = LatestTime(k, c);
        return latest >= 0 ? latest - t : -1 - t;
    }

    public bool IsViable(int k, int t, Cell c)
    {
        return TryIndexOf(c, out int index) && IsViable(k, t, index);
    }

    /// <summary>R1 可行核：(v,t,k) 既时间展开可达、又能在时间上限内到达终点。</summary>
    public bool IsViable(int k, int t, int index)
    {
        if (index < 0 || index >= _count || t < 0 || t > Horizon)
        {
            return false;
        }
        return _viable[Math.Clamp(k, 0, FoodCount)][t][index];
    }

    // ── 构建 ────────────────────────────────────────────────────

    private void BuildCoverStage(LevelSpec spec)
    {
        int cap = Math.Clamp(spec.MechanismCap, 0, MechanicShapes.MaxLevel);
        var cones = new List<CoverageCone>(spec.MechCenters.Count);
        GMax = 0;
        for (int i = 0; i < spec.MechCenters.Count; i++)
        {
            int initial = i < spec.MechInitialLevels.Count ? spec.MechInitialLevels[i] : 0;
            int clamped = Math.Clamp(initial, MechanicShapes.MinLevel, cap);
            GMax = Math.Max(GMax, cap - clamped);
            cones.Add(new CoverageCone(spec.MechCenters[i], clamped, cap));
        }

        foreach (KeyValuePair<Cell, int> kv in CoverageCone.Union(cones))
        {
            if (kv.Value > GMax)
            {
                continue;
            }
            if (kv.Key.Equals(Spawn) || kv.Key.Equals(Goal))
            {
                continue;
            }
            if (!TryIndexOf(kv.Key, out int index) || !IsFloor(kv.Key))
            {
                continue;
            }
            _coverStage[index] = kv.Value;
        }
    }

    private void BuildMechSets()
    {
        _mechSet = new bool[GMax + 1][];
        for (int g = 0; g <= GMax; g++)
        {
            var layer = new bool[_count];
            for (int index = 0; index < _count; index++)
            {
                layer[index] = _coverStage[index] <= g;
            }
            _mechSet[g] = layer;
        }
    }

    private void BuildAdjacency()
    {
        for (int index = 0; index < _count; index++)
        {
            if (!_walkable[index])
            {
                _adjacency[index] = Array.Empty<int>();
                continue;
            }
            Cell cell = CellAt(index);
            var neighbors = new List<int>(4);
            foreach (Cell n in GridModel.Neighbors(cell))
            {
                if (TryIndexOf(n, out int ni) && _walkable[ni])
                {
                    neighbors.Add(ni);
                }
            }
            _adjacency[index] = neighbors.ToArray();
        }
    }

    private void BuildStaticDistances()
    {
        BfsDistances(Spawn, _distSpawn);
        BfsDistances(Goal, _distGoal);
    }

    private void BfsDistances(Cell from, int[] target)
    {
        if (!TryIndexOf(from, out int start) || !_walkable[start])
        {
            return;
        }
        target[start] = 0;
        var queue = new Queue<int>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            foreach (int next in _adjacency[cur])
            {
                if (target[next] != INF)
                {
                    continue;
                }
                target[next] = target[cur] + 1;
                queue.Enqueue(next);
            }
        }
    }

    private bool[][][] BuildReachable()
    {
        var reachable = new bool[FoodCount + 1][][];
        for (int k = 0; k <= FoodCount; k++)
        {
            reachable[k] = new bool[Horizon + 1][];
            for (int t = 0; t <= Horizon; t++)
            {
                reachable[k][t] = new bool[_count];
            }
        }
        reachable[0][0][SpawnIndex] = true;

        for (int t = 0; t < Horizon; t++)
        {
            bool[] mech = _mechSet[GrowthCount(t)];
            for (int k = 0; k <= FoodCount; k++)
            {
                bool[] layer = reachable[k][t];
                for (int index = 0; index < _count; index++)
                {
                    if (!layer[index])
                    {
                        continue;
                    }
                    foreach (int next in _adjacency[index])
                    {
                        if (mech[next])
                        {
                            continue;
                        }
                        int nk = k + (_isFood[next] ? 1 : 0);
                        if (nk > FoodCount)
                        {
                            nk = FoodCount;
                        }
                        reachable[nk][t + 1][next] = true;
                    }
                }
            }
        }
        return reachable;
    }

    private (int[][] Latest, bool[][][] Viable) BuildLatestAndViable()
    {
        var latest = new int[FoodCount + 1][];
        var viable = new bool[FoodCount + 1][][];

        for (int k = 0; k <= FoodCount; k++)
        {
            int limit = LengthAt(k) - 1;
            var allowed = new bool[_count];
            for (int index = 0; index < _count; index++)
            {
                allowed[index] = _walkable[index] && _distSpawn[index] <= limit;
            }

            var can = new bool[Horizon + 1][];
            for (int t = 0; t <= Horizon; t++)
            {
                can[t] = new bool[_count];
            }
            can[Horizon][GoalIndex] = allowed[GoalIndex];

            for (int t = Horizon - 1; t >= 0; t--)
            {
                bool[] mech = _mechSet[GrowthCount(t)];
                bool[] next = can[t + 1];
                bool[] cur = can[t];
                for (int index = 0; index < _count; index++)
                {
                    if (!allowed[index])
                    {
                        continue;
                    }
                    if (index == GoalIndex)
                    {
                        cur[index] = true;
                        continue;
                    }
                    foreach (int neighbor in _adjacency[index])
                    {
                        if (mech[neighbor])
                        {
                            continue;
                        }
                        if (next[neighbor])
                        {
                            cur[index] = true;
                            break;
                        }
                    }
                }
            }

            var latestRow = new int[_count];
            Array.Fill(latestRow, -1);
            for (int index = 0; index < _count; index++)
            {
                for (int t = Horizon; t >= 0; t--)
                {
                    if (can[t][index])
                    {
                        latestRow[index] = t;
                        break;
                    }
                }
            }
            latest[k] = latestRow;
        }

        // 允许后续吃花扩大身长：Latest 取 k' ≥ k 的上界，使可行核成为单调松弛。
        for (int k = FoodCount - 1; k >= 0; k--)
        {
            int[] lower = latest[k];
            int[] upper = latest[k + 1];
            for (int index = 0; index < _count; index++)
            {
                lower[index] = Math.Max(lower[index], upper[index]);
            }
        }

        for (int k = 0; k <= FoodCount; k++)
        {
            int[] latestRow = latest[k];
            viable[k] = new bool[Horizon + 1][];
            for (int t = 0; t <= Horizon; t++)
            {
                var layer = new bool[_count];
                bool[] reach = _reachable[k][t];
                for (int index = 0; index < _count; index++)
                {
                    layer[index] = reach[index] && latestRow[index] >= t;
                }
                viable[k][t] = layer;
            }
        }

        return (latest, viable);
    }
}
