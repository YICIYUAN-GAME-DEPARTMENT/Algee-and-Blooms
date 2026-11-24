using System;
using System.Collections.Generic;
using Godot;

namespace Growth.Algorithms.Search;

/// <summary>
/// 有界胜利路线枚举器（M16 funnel_width 使用）。
/// 在真实 RuleState 上 DFS，限制长度与节点预算；返回长度 ≤ maxSteps 的胜利路线数（状态去重，数量级近似）。
/// 不改动 ExactBfsSolver 的 UNKNOWN / 最短解语义，两者互不替代。
/// </summary>
public sealed class BoundedRouteEnumerator
{
    public sealed class Result
    {
        public int Count { get; set; }
        public bool Truncated { get; set; }
        public int Nodes { get; set; }
    }

    private static readonly Cell[] Directions =
    {
        new(-1, 0), new(1, 0), new(0, -1), new(0, 1),
    };

    private readonly struct StateKey : IEquatable<StateKey>
    {
        private readonly ulong _trailHash;
        private readonly ulong _foodMask;
        private readonly ulong _shieldMask;
        private readonly int _g;
        private readonly int _phase;

        public StateKey(ulong trailHash, ulong foodMask, ulong shieldMask, int g, int phase)
        {
            _trailHash = trailHash;
            _foodMask = foodMask;
            _shieldMask = shieldMask;
            _g = g;
            _phase = phase;
        }

        public bool Equals(StateKey other)
        {
            return _trailHash == other._trailHash
                && _foodMask == other._foodMask
                && _shieldMask == other._shieldMask
                && _g == other._g
                && _phase == other._phase;
        }

        public override bool Equals(object obj) => obj is StateKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                h = (h ^ _trailHash) * 1099511628211UL;
                h = (h ^ _foodMask) * 1099511628211UL;
                h = (h ^ _shieldMask) * 1099511628211UL;
                h = (h ^ (uint)_g) * 1099511628211UL;
                h = (h ^ (uint)_phase) * 1099511628211UL;
                return (int)(h ^ (h >> 32));
            }
        }
    }

    private readonly Dictionary<Cell, int> _foodIndex = new();
    private readonly Dictionary<Cell, int> _coverIndex = new();
    private readonly HashSet<StateKey> _visited = new();
    private readonly Dictionary<Cell, int> _distToGoal = new();

    private RuleState _state;
    private Cell _goal;
    private int _interval = 1;
    private int _nodeBudget;
    private int _maxSteps;
    private int _maxCount;
    private int _nodes;
    private int _count;
    private bool _truncated;

    public Result Enumerate(LevelSpec spec, int maxSteps, int nodeBudget, int maxCount = 2000,
        RuleState reusableState = null)
    {
        var result = new Result();
        if (maxSteps < 1)
        {
            return result;
        }

        _state = reusableState ?? new RuleState();
        _state.SuppressDeadlockChecks = true;
        _state.SuppressPathCheck = true;
        _state.ResetToSpec(spec);
        _goal = spec.Goal;
        _interval = Math.Max(spec.GrowthInterval, 1);
        _nodeBudget = Math.Max(nodeBudget, 1);
        _maxSteps = maxSteps;
        _maxCount = Math.Max(maxCount, 1);
        _nodes = 0;
        _count = 0;
        _truncated = false;
        _visited.Clear();

        BuildIndexes(spec);
        BuildDistToGoal(spec);

        Dfs(_state, 0);

        result.Count = _count;
        result.Truncated = _truncated;
        result.Nodes = _nodes;
        return result;
    }

    private void BuildIndexes(LevelSpec spec)
    {
        _foodIndex.Clear();
        foreach (Cell food in spec.Foods)
        {
            if (!_foodIndex.ContainsKey(food))
            {
                _foodIndex[food] = _foodIndex.Count;
            }
        }
        _coverIndex.Clear();
        foreach (KeyValuePair<Cell, int> kv in _state.CoverSteps)
        {
            if (!_coverIndex.ContainsKey(kv.Key))
            {
                _coverIndex[kv.Key] = _coverIndex.Count;
            }
        }
    }

    private void BuildDistToGoal(LevelSpec spec)
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
        _distToGoal.Clear();
        var empty = new HashSet<Cell>();
        foreach (KeyValuePair<Cell, int> kv in grid.BfsDistMap(spec.Goal, empty))
        {
            _distToGoal[kv.Key] = kv.Value;
        }
    }

    private void Dfs(RuleState st, int steps)
    {
        if (_truncated)
        {
            return;
        }
        _nodes++;
        if (_nodes > _nodeBudget)
        {
            _truncated = true;
            return;
        }
        if (st.Head.Equals(_goal))
        {
            _count++;
            if (_count >= _maxCount)
            {
                _truncated = true;
            }
            return;
        }
        if (steps >= _maxSteps)
        {
            return;
        }
        if (_distToGoal.TryGetValue(st.Head, out int toGoal) && steps + toGoal > _maxSteps)
        {
            return;
        }

        var key = new StateKey(
            TrailHash(st.Trail),
            FoodMask(st),
            ShieldMask(st),
            st.GrowthCount,
            st.GetSteps() % _interval);
        if (!_visited.Add(key))
        {
            return;
        }

        foreach (Cell dir in Directions)
        {
            Cell next = new(st.Head.X + dir.X, st.Head.Y + dir.Y);
            if (st.ClassifyMove(next) == RuleState.MoveKind.Rejected)
            {
                continue;
            }
            RuleState.RuleSnapshot snapshot = st.CaptureState();
            st.Step(new Vector2I(dir.X, dir.Y));
            Dfs(st, steps + 1);
            st.RestoreState(snapshot);
            if (_truncated)
            {
                return;
            }
        }
    }

    private ulong FoodMask(RuleState st)
    {
        ulong mask = 0;
        foreach (Cell food in st.RemainingFoods)
        {
            if (_foodIndex.TryGetValue(food, out int index) && index < 64)
            {
                mask |= 1UL << index;
            }
        }
        return mask;
    }

    private ulong ShieldMask(RuleState st)
    {
        ulong mask = 0;
        foreach (Cell cell in st.ShieldedCells)
        {
            if (_coverIndex.TryGetValue(cell, out int index) && index < 64)
            {
                mask |= 1UL << index;
            }
        }
        return mask;
    }

    private static ulong TrailHash(IReadOnlyList<Cell> trail)
    {
        ulong h = 14695981039346656037UL;
        foreach (Cell c in trail)
        {
            h = (h ^ (uint)c.X) * 1099511628211UL;
            h = (h ^ (uint)c.Y) * 1099511628211UL;
        }
        return h;
    }
}
