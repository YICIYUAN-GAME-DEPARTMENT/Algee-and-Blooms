using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace Growth.Algorithms.Search;

/// <summary>
/// 精确 BFS 求解器（由 tools/LevelSolver.gd 内的 GDScript 求解器迁移）。
/// 状态 = (轨迹栈节点, 已吃食物掩码, shielded 掩码, 生长次数 g, 生长周期相位)；
/// 机关用 cover_step 的"开花时刻表"表达；方向顺序与旧版一致（LEFT, RIGHT, UP, DOWN）。
/// 只读 RuleState 的静态数据，不修改它。
/// </summary>
public partial class ExactBfsSolver : RefCounted
{
    private const int INF = 1 << 30;

    private static readonly Cell[] Directions =
    {
        new(-1, 0), new(1, 0), new(0, -1), new(0, 1),
    };

    private static readonly string[] DirChars = { "A", "D", "W", "S" };

    private sealed class SearchState
    {
        public int TrailId;
        public int FoodMask;
        public int ShieldedMask;
        public int G;
        public int Phase;
        public int MaxLen = 3;
        public int Parent = -1;
        public int DirIndex;
    }

    private sealed class Connectivity
    {
        public HashSet<Cell> ReachGoal = new();
        public Dictionary<Cell, int> DistS = new();
    }

    private readonly struct StateKey : IEquatable<StateKey>
    {
        private readonly int _trailId;
        private readonly int _foodMask;
        private readonly int _shieldedMask;
        private readonly int _g;
        private readonly int _phase;

        public StateKey(int trailId, int foodMask, int shieldedMask, int g, int phase)
        {
            _trailId = trailId;
            _foodMask = foodMask;
            _shieldedMask = shieldedMask;
            _g = g;
            _phase = phase;
        }

        public bool Equals(StateKey other)
        {
            return _trailId == other._trailId
                && _foodMask == other._foodMask
                && _shieldedMask == other._shieldedMask
                && _g == other._g
                && _phase == other._phase;
        }

        public override bool Equals(object obj) => obj is StateKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = (int)2166136261;
                h = (h ^ _trailId) * 16777619;
                h = (h ^ _foodMask) * 16777619;
                h = (h ^ _shieldedMask) * 16777619;
                h = (h ^ _g) * 16777619;
                h = (h ^ _phase) * 16777619;
                return h;
            }
        }
    }

    private GridModel _grid = new(Cell.Zero, 1, 1);
    private Cell _spawn = Cell.Zero;
    private Cell _goal = Cell.Zero;
    private Dictionary<Cell, int> _coverStep = new();
    private readonly List<Cell> _coverCells = new();
    private readonly Dictionary<Cell, int> _coverIndex = new();
    private readonly List<Cell> _foodCells = new();
    private readonly Dictionary<Cell, int> _foodIndex = new();
    private int _initialMaxLen = 3;
    private int _foodLenGain = 2;
    private int _interval = 6;
    private int _gMax;

    private readonly List<Cell> _tnCell = new();
    private readonly List<int> _tnParent = new();
    private readonly List<int> _tnDepth = new();
    private readonly Dictionary<long, int> _tnMap = new();

    private readonly Dictionary<(int G, int Shielded), Connectivity> _connCache = new();

    /// <summary>求解入口。status: SOLVABLE / UNSOLVABLE / UNKNOWN。</summary>
    public Godot.Collections.Dictionary Solve(RuleState state, int budget)
    {
        Setup(state);
        if (_foodCells.Count > 31)
        {
            return Result("UNKNOWN", 0, "食物超过 31 个，当前位图模型不支持");
        }
        if (_coverCells.Count > 63)
        {
            return Result("UNKNOWN", 0, "机关覆盖格超过 63，当前位图模型不支持");
        }
        if (!NecessaryPass())
        {
            return Result("UNSOLVABLE", 0, "静态闭包失败");
        }

        int root = InitTrail(_spawn);
        var start = new SearchState { TrailId = root, MaxLen = _initialMaxLen };

        // 开局死局检查（与游戏 _ready 末尾一致）
        if (!_spawn.Equals(_goal) && !AliveAfterMove(root, 0, _initialMaxLen, 0, 0))
        {
            return Result("UNSOLVABLE", 0, "开局死局");
        }

        var queue = new List<SearchState> { start };
        var visited = new Dictionary<StateKey, int> { [Key(start)] = 0 };
        int qi = 0;

        while (qi < queue.Count)
        {
            if (queue.Count > budget)
            {
                return Result("UNKNOWN", queue.Count, "超出节点预算");
            }

            SearchState s = queue[qi];
            int sIdx = qi;
            qi++;

            Cell headCell = _tnCell[s.TrailId];
            if (headCell.Equals(_goal))
            {
                return MakeResult(queue, sIdx);
            }

            for (int di = 0; di < Directions.Length; di++)
            {
                Cell d = Directions[di];
                Cell n = new(headCell.X + d.X, headCell.Y + d.Y);
                if (!_grid.InBounds(n))
                {
                    continue;
                }

                int body = FindInTrail(s.TrailId, n);
                int nt;
                int foodMask = s.FoodMask;
                int ml = s.MaxLen;

                if (body >= 0)
                {
                    // 踏入自身身体 -> 截断
                    nt = body;
                }
                else
                {
                    if (Blocked(n, s.G, s.ShieldedMask))
                    {
                        continue;
                    }
                    if (_tnDepth[s.TrailId] >= s.MaxLen)
                    {
                        continue;
                    }
                    nt = Push(s.TrailId, n);
                    if (_foodIndex.TryGetValue(n, out int fi))
                    {
                        int bit = 1 << fi;
                        if ((foodMask & bit) == 0)
                        {
                            foodMask |= bit;
                            ml += _foodLenGain;
                        }
                    }
                }

                int phase = (s.Phase + 1) % _interval;
                int ng = s.G;
                int nShield = s.ShieldedMask;
                if (phase == 0)
                {
                    ng = Math.Min(ng + 1, _gMax);
                    nShield = 0;
                    int cur = nt;
                    while (cur >= 0)
                    {
                        Cell c = _tnCell[cur];
                        if (_coverIndex.TryGetValue(c, out int ci) && _coverStep[c] <= ng)
                        {
                            nShield |= 1 << ci;
                        }
                        cur = _tnParent[cur];
                    }
                }

                Cell nHead = _tnCell[nt];
                bool isWin = nHead.Equals(_goal);
                if (!isWin && !AliveAfterMove(nt, foodMask, ml, ng, nShield))
                {
                    continue;
                }

                var ns = new SearchState
                {
                    TrailId = nt,
                    FoodMask = foodMask,
                    ShieldedMask = nShield,
                    G = ng,
                    Phase = phase,
                    MaxLen = ml,
                    Parent = sIdx,
                    DirIndex = di,
                };

                StateKey key = Key(ns);
                if (!visited.ContainsKey(key))
                {
                    visited[key] = queue.Count;
                    queue.Add(ns);
                }
            }
        }

        return Result("UNSOLVABLE", queue.Count, "状态耗尽");
    }

    // ── 初始化 ────────────────────────────────────────────────
    private void Setup(RuleState state)
    {
        _grid = state.Grid;
        _spawn = state.Spawn;
        _goal = state.Goal;
        _coverStep = new Dictionary<Cell, int>(state.CoverSteps);
        _initialMaxLen = state.InitialMaxLen;
        _foodLenGain = state.FoodLenGain;
        _interval = state.GrowthInterval;
        _gMax = state.GMax;

        _coverCells.Clear();
        _coverIndex.Clear();
        foreach (KeyValuePair<Cell, int> kv in _coverStep)
        {
            if (_grid.Obstacles.Contains(kv.Key))
            {
                continue; // 障碍格永远不可走，不需要进 shielded 位图
            }
            _coverIndex[kv.Key] = _coverCells.Count;
            _coverCells.Add(kv.Key);
        }

        _foodCells.Clear();
        _foodIndex.Clear();
        foreach (Cell f in state.InitialFoodList)
        {
            _foodIndex[f] = _foodCells.Count;
            _foodCells.Add(f);
        }

        _connCache.Clear();
    }

    // ── 轨迹持久化栈 ──────────────────────────────────────────
    private int InitTrail(Cell root)
    {
        _tnCell.Clear();
        _tnParent.Clear();
        _tnDepth.Clear();
        _tnMap.Clear();
        _tnCell.Add(root);
        _tnParent.Add(-1);
        _tnDepth.Add(1);
        return 0;
    }

    private int Push(int parentId, Cell c)
    {
        long key = (long)parentId * 65536 + CellIndex(c);
        if (_tnMap.TryGetValue(key, out int existing))
        {
            return existing;
        }
        int id = _tnCell.Count;
        _tnCell.Add(c);
        _tnParent.Add(parentId);
        _tnDepth.Add(_tnDepth[parentId] + 1);
        _tnMap[key] = id;
        return id;
    }

    private int FindInTrail(int id, Cell c)
    {
        int cur = id;
        while (cur >= 0)
        {
            if (_tnCell[cur].Equals(c))
            {
                return cur;
            }
            cur = _tnParent[cur];
        }
        return -1;
    }

    private int CellIndex(Cell c)
    {
        return (c.Y - _grid.Origin.Y) * _grid.Width + (c.X - _grid.Origin.X);
    }

    // ── 可走性 / 死局 ────────────────────────────────────────
    private bool Blocked(Cell c, int g, int shieldedMask)
    {
        if (!_grid.InBounds(c))
        {
            return true;
        }
        if (!_grid.IsFloor(c))
        {
            return true;
        }
        if (_grid.Obstacles.Contains(c))
        {
            return true;
        }
        if (c.Equals(_spawn) || c.Equals(_goal))
        {
            return false;
        }
        int cs = _coverStep.TryGetValue(c, out int v) ? v : INF;
        if (cs > g)
        {
            return false;
        }
        if (_coverIndex.TryGetValue(c, out int idx) && (shieldedMask & (1 << idx)) != 0)
        {
            return false;
        }
        return true;
    }

    /// <summary>与 RuleState.CheckDeadlock 同口径，但用搜索态自己的障碍集。</summary>
    private bool AliveAfterMove(int trailId, int foodMask, int ml, int g, int shieldedMask)
    {
        Connectivity conn = EnsureConnectivity(g, shieldedMask);
        Cell h = _tnCell[trailId];
        if (!conn.ReachGoal.Contains(h))
        {
            return false;
        }
        if (_tnDepth[trailId] < ml)
        {
            return true;
        }
        int limit = ml - 1;
        if (conn.DistS.TryGetValue(_goal, out int goalDist) && goalDist <= limit)
        {
            return true;
        }
        for (int i = 0; i < _foodCells.Count; i++)
        {
            if ((foodMask & (1 << i)) != 0)
            {
                continue;
            }
            if (conn.DistS.TryGetValue(_foodCells[i], out int foodDist) && foodDist <= limit)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>缓存每个 (g, shieldedMask) 的：能到 E 的格集合、从 S 出发的距离。</summary>
    private Connectivity EnsureConnectivity(int g, int shieldedMask)
    {
        var key = (g, shieldedMask);
        if (_connCache.TryGetValue(key, out Connectivity cached))
        {
            return cached;
        }
        var rec = new Connectivity
        {
            ReachGoal = BfsRegion(_goal, g, shieldedMask),
            DistS = BfsDist(_spawn, g, shieldedMask),
        };
        _connCache[key] = rec;
        return rec;
    }

    private HashSet<Cell> BfsRegion(Cell from, int g, int shieldedMask)
    {
        var seen = new HashSet<Cell> { from };
        var queue = new Queue<Cell>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            Cell cur = queue.Dequeue();
            foreach (Cell n in GridModel.Neighbors(cur))
            {
                if (seen.Contains(n) || Blocked(n, g, shieldedMask))
                {
                    continue;
                }
                seen.Add(n);
                queue.Enqueue(n);
            }
        }
        return seen;
    }

    private Dictionary<Cell, int> BfsDist(Cell from, int g, int shieldedMask)
    {
        var dist = new Dictionary<Cell, int> { [from] = 0 };
        var queue = new Queue<Cell>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            Cell cur = queue.Dequeue();
            foreach (Cell n in GridModel.Neighbors(cur))
            {
                if (dist.ContainsKey(n) || Blocked(n, g, shieldedMask))
                {
                    continue;
                }
                dist[n] = dist[cur] + 1;
                queue.Enqueue(n);
            }
        }
        return dist;
    }

    // ── 静态必要条件 ──────────────────────────────────────────
    /// <summary>最宽松障碍集（只算 cover_step=0 的初始机关格）都吃不到 E -> 必然无解。</summary>
    private bool NecessaryPass()
    {
        var blocked = new HashSet<Cell>();
        foreach (KeyValuePair<Cell, int> kv in _coverStep)
        {
            if (kv.Value <= 0)
            {
                blocked.Add(kv.Key);
            }
        }
        Dictionary<Cell, int> dist = BfsStatic(_spawn, blocked);
        int l = _initialMaxLen;
        var remaining = new List<Cell>(_foodCells);
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = remaining.Count - 1; i >= 0; i--)
            {
                Cell f = remaining[i];
                if (dist.TryGetValue(f, out int fd) && fd <= l - 1)
                {
                    remaining.RemoveAt(i);
                    l += _foodLenGain;
                    changed = true;
                }
            }
        }
        return dist.TryGetValue(_goal, out int goalDist) && goalDist <= l - 1;
    }

    private Dictionary<Cell, int> BfsStatic(Cell from, HashSet<Cell> extra)
    {
        var dist = new Dictionary<Cell, int> { [from] = 0 };
        var queue = new Queue<Cell>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            Cell cur = queue.Dequeue();
            foreach (Cell n in GridModel.Neighbors(cur))
            {
                if (dist.ContainsKey(n) || StaticBlocked(n, extra))
                {
                    continue;
                }
                dist[n] = dist[cur] + 1;
                queue.Enqueue(n);
            }
        }
        return dist;
    }

    private bool StaticBlocked(Cell c, HashSet<Cell> extra)
    {
        if (!_grid.InBounds(c))
        {
            return true;
        }
        if (!_grid.IsFloor(c))
        {
            return true;
        }
        if (_grid.Obstacles.Contains(c))
        {
            return true;
        }
        if (c.Equals(_spawn) || c.Equals(_goal))
        {
            return false;
        }
        return extra.Contains(c);
    }

    // ── 结果 ──────────────────────────────────────────────────
    private StateKey Key(SearchState s)
    {
        return new StateKey(s.TrailId, s.FoodMask, s.ShieldedMask, s.G, s.Phase);
    }

    private Godot.Collections.Dictionary MakeResult(List<SearchState> queue, int idx)
    {
        var dirs = new List<int>();
        int cur = idx;
        while (cur >= 0)
        {
            SearchState s = queue[cur];
            if (s.Parent >= 0)
            {
                dirs.Add(s.DirIndex);
            }
            cur = s.Parent;
        }
        dirs.Reverse();
        var route = new StringBuilder();
        foreach (int di in dirs)
        {
            route.Append(DirChars[di]);
        }
        SearchState last = queue[idx];
        return new Godot.Collections.Dictionary
        {
            { "status", "SOLVABLE" },
            { "steps", dirs.Count },
            { "route", route.ToString() },
            { "states", queue.Count },
            { "max_len", last.MaxLen },
        };
    }

    private static Godot.Collections.Dictionary Result(string status, int states, string reason)
    {
        return new Godot.Collections.Dictionary
        {
            { "status", status },
            { "states", states },
            { "reason", reason },
        };
    }
}
