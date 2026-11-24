using System;
using System.Collections.Generic;
using Godot;

namespace Growth.Algorithms;

/// <summary>
/// 玩法规则唯一实现（由 GDScript LevelState 迁移）。
/// 权威规则：docs/design/功能需求文档.md §4.1–4.5。
/// 只做纯数据计算，不访问节点 / 场景 / 信号；GDScript 侧 LevelState.gd 仅作适配转发。
/// </summary>
public partial class RuleState : RefCounted
{
    private const int INF = 1 << 30;

    private GridModel _grid = new(Cell.Zero, 1, 1);
    private Cell _spawn = Cell.Zero;
    private Cell _goal = Cell.Zero;

    private readonly HashSet<Cell> _initialFoods = new();
    private readonly List<Cell> _initialFoodList = new();
    private readonly HashSet<Cell> _foods = new();
    private readonly HashSet<Cell> _mechCells = new();
    private readonly HashSet<Cell> _shielded = new();
    private readonly Dictionary<Cell, int> _coverStep = new();

    private readonly List<Cell> _mechCenters = new();
    private readonly List<int> _mechInitialLevels = new();
    private readonly List<Cell> _trail = new();

    /// <summary>从 S 出发的距离缓存；key = (生长次数 g, shielded 版本)。机关/挡格不变时可复用。</summary>
    private readonly Dictionary<(int G, int ShieldVersion), Dictionary<Cell, int>> _distCache = new();
    private int _shieldVersion;

    private int _initialMaxLen = 3;
    private int _foodLenGain = 2;
    private int _growthInterval = 6;
    private int _maxMechanismLevel = MechanicShapes.MaxLevel;
    private int _gMax;

    private int _maxLen = 3;
    private int _steps;
    private int _foodEaten;
    private int _g;
    private bool _finished;
    /// <summary>生成器随机行走时关闭"胜负/死局"判定，仅模拟移动与生长。</summary>
    internal bool SuppressDeadlockChecks;
    /// <summary>生成校验时跳过 head→E 连通性 BFS（路线剩余段本身就是通路），只保留身长达上限的检查。</summary>
    internal bool SuppressPathCheck;

    // ── 供同程序集内的求解器读取静态数据 ─────────────────────────
    internal GridModel Grid => _grid;
    internal Cell Spawn => _spawn;
    internal Cell Goal => _goal;
    internal IReadOnlyList<Cell> InitialFoodList => _initialFoodList;
    internal IReadOnlyCollection<Cell> InitialFoods => _initialFoods;
    internal IReadOnlyDictionary<Cell, int> CoverSteps => _coverStep;
    internal int InitialMaxLen => _initialMaxLen;
    internal int FoodLenGain => _foodLenGain;
    internal int GrowthInterval => _growthInterval;
    internal int GMax => _gMax;
    internal IReadOnlySet<Cell> MechCells => _mechCells;
    internal IReadOnlyList<Cell> Trail => _trail;

    public Cell Head => _trail.Count > 0 ? _trail[^1] : _spawn;

    /// <summary>注入关卡静态数据；必须在 Start() 前调用一次。由 GDScript 适配层传入。</summary>
    public void Setup(
        Vector2I origin,
        Vector2I size,
        Godot.Collections.Array floorCells,
        Godot.Collections.Array obstacleCells,
        Vector2I spawn,
        Vector2I goal,
        Godot.Collections.Array foods,
        Godot.Collections.Array mechCenters,
        Godot.Collections.Array mechInitialLevels,
        int maxLen,
        int growthInterval,
        int foodLenGain,
        int mechCap)
    {
        var spec = new LevelSpec
        {
            Origin = new Cell(origin.X, origin.Y),
            Width = size.X,
            Height = size.Y,
            Spawn = new Cell(spawn.X, spawn.Y),
            Goal = new Cell(goal.X, goal.Y),
            InitialMaxLen = maxLen,
            GrowthInterval = growthInterval,
            FoodLenGain = foodLenGain,
            MechanismCap = mechCap,
        };
        foreach (Variant v in floorCells)
        {
            Vector2I c = v.AsVector2I();
            spec.Floor.Add(new Cell(c.X, c.Y));
        }
        foreach (Variant v in obstacleCells)
        {
            Vector2I c = v.AsVector2I();
            spec.Obstacles.Add(new Cell(c.X, c.Y));
        }
        foreach (Variant v in foods)
        {
            Vector2I c = v.AsVector2I();
            spec.Foods.Add(new Cell(c.X, c.Y));
        }
        foreach (Variant v in mechCenters)
        {
            Vector2I c = v.AsVector2I();
            spec.MechCenters.Add(new Cell(c.X, c.Y));
        }
        foreach (Variant v in mechInitialLevels)
        {
            spec.MechInitialLevels.Add(v.AsInt32());
        }
        Setup(spec);
    }

    /// <summary>由 LevelSpec 注入静态数据（生成器 / 测试与 GDScript 适配层共用）。</summary>
    internal void Setup(LevelSpec spec)
    {
        _grid = new GridModel(spec.Origin, spec.Width, spec.Height);
        foreach (Cell c in spec.Floor)
        {
            _grid.Floor.Add(c);
        }
        foreach (Cell c in spec.Obstacles)
        {
            _grid.Obstacles.Add(c);
        }

        _spawn = spec.Spawn;
        _goal = spec.Goal;

        _initialFoods.Clear();
        _initialFoodList.Clear();
        foreach (Cell c in spec.Foods)
        {
            _initialFoods.Add(c);
            _initialFoodList.Add(c);
        }

        _initialMaxLen = Math.Max(spec.InitialMaxLen, 1);
        _growthInterval = Math.Max(spec.GrowthInterval, 1);
        _foodLenGain = spec.FoodLenGain;
        _maxMechanismLevel = Math.Clamp(spec.MechanismCap, 0, MechanicShapes.MaxLevel);

        _mechCenters.Clear();
        foreach (Cell c in spec.MechCenters)
        {
            _mechCenters.Add(c);
        }

        _mechInitialLevels.Clear();
        _gMax = 0;
        for (int i = 0; i < _mechCenters.Count; i++)
        {
            int a = i < spec.MechInitialLevels.Count ? spec.MechInitialLevels[i] : 0;
            a = Math.Clamp(a, 0, _maxMechanismLevel);
            _mechInitialLevels.Add(a);
            _gMax = Math.Max(_gMax, _maxMechanismLevel - a);
        }

        _distCache.Clear();
        _shieldVersion = 0;
        BuildCoverStep();
    }

    /// <summary>重置到关卡开局（可重复调用）；返回初始占格（供表现层播"范围初建"动画）。</summary>
    public Godot.Collections.Array Start()
    {
        _trail.Clear();
        _trail.Add(_spawn);
        _maxLen = _initialMaxLen;
        _steps = 0;
        _foodEaten = 0;
        _g = 0;
        _shielded.Clear();
        _distCache.Clear();
        _shieldVersion = 0;
        _finished = false;
        _foods.Clear();
        foreach (Cell f in _initialFoods)
        {
            _foods.Add(f);
        }
        RefreshMechCells();
        return ToGodotArray(GetClaimedCellsInternal());
    }

    /// <summary>一次方向输入；无效移动返回 accepted=false 且不计步。返回字段供 GDScript 适配层转换。</summary>
    public Godot.Collections.Dictionary Step(Vector2I dir)
    {
        bool accepted = false;
        bool truncated = false;
        bool ateFood = false;
        Cell foodCell = Cell.Zero;
        bool grew = false;
        bool won = false;
        string deadlockMsg = "";
        List<Cell> addedCells = new();

        if (!_finished)
        {
            Cell next = new Cell(Head.X + dir.X, Head.Y + dir.Y);
            MoveKind kind = ClassifyMove(next);
            if (kind == MoveKind.Body)
            {
                // 情况 B：踏入自身身体 -> 截断到该格
                int bodyIdx = _trail.IndexOf(next);
                _trail.RemoveRange(bodyIdx + 1, _trail.Count - bodyIdx - 1);
                _steps += 1;
                accepted = true;
                truncated = true;
            }
            else if (kind == MoveKind.Walk)
            {
                // 情况 A：可走格且身体未达硬上限
                _trail.Add(next);
                if (_foods.Remove(next))
                {
                    _foodEaten += 1;
                    _maxLen += _foodLenGain;
                    ateFood = true;
                    foodCell = next;
                }
                _steps += 1;
                accepted = true;
            }

            if (accepted)
            {
                if (_steps > 0 && _steps % _growthInterval == 0)
                {
                    grew = true;
                    addedCells = Grow();
                }
                if (Head.Equals(_goal))
                {
                    won = true;
                    _finished = true;
                }
                else if (!SuppressDeadlockChecks)
                {
                    deadlockMsg = CheckDeadlock();
                    if (deadlockMsg.Length > 0)
                    {
                        _finished = true;
                    }
                }
            }
        }

        var res = new Godot.Collections.Dictionary
        {
            { "accepted", accepted },
            { "truncated", truncated },
            { "ate_food", ateFood },
            { "food_cell", new Vector2I(foodCell.X, foodCell.Y) },
            { "grew", grew },
            { "won", won },
            { "deadlock_msg", deadlockMsg },
            { "added_mech_cells", ToGodotArray(addedCells) },
            { "trail", GetTrail() },
            { "max_len", _maxLen },
            { "steps", _steps },
            { "finished", _finished },
        };
        return res;
    }

    internal enum MoveKind
    {
        Rejected,
        Body,
        Walk,
    }

    /// <summary>不改变状态地判定一步的结果类型（生成器随机行走使用）。</summary>
    internal MoveKind ClassifyMove(Cell next)
    {
        if (!_grid.InBounds(next))
        {
            return MoveKind.Rejected;
        }
        if (_trail.Contains(next))
        {
            return MoveKind.Body;
        }
        if (_grid.IsBlocked(next, _mechCells))
        {
            return MoveKind.Rejected;
        }
        if (_trail.Count >= _maxLen)
        {
            return MoveKind.Rejected;
        }
        return MoveKind.Walk;
    }

    /// <summary>当前机关实际占格（= 已到覆盖阶段且未被身体挡住的地板格）。</summary>
    public Godot.Collections.Array GetClaimedCells()
    {
        return ToGodotArray(GetClaimedCellsInternal());
    }

    public Godot.Collections.Array GetTrail()
    {
        return ToGodotArray(_trail);
    }

    /// <summary>死局检查；返回空串表示仍可继续（胜利由调用方优先处理）。</summary>
    public string CheckDeadlock()
    {
        if (_finished || Head.Equals(_goal))
        {
            return "";
        }
        if (!SuppressPathCheck && !_grid.HasPath(Head, _goal, _mechCells))
        {
            return "无路可走… 重开吧";
        }
        if (_trail.Count < _maxLen)
        {
            return "";
        }
        int reach = _maxLen - 1;
        var distKey = (_g, _shieldVersion);
        if (!_distCache.TryGetValue(distKey, out Dictionary<Cell, int> dist))
        {
            dist = _grid.BfsDistMap(_spawn, _mechCells);
            _distCache[distKey] = dist;
        }
        if (dist.TryGetValue(_goal, out int goalDist) && goalDist <= reach)
        {
            return "";
        }
        foreach (Cell f in _foods)
        {
            if (dist.TryGetValue(f, out int foodDist) && foodDist <= reach)
            {
                return "";
            }
        }
        return "管道不够长啊… 摘不到蓝花也捡不到花瓣";
    }

    public int GetRemainingSteps()
    {
        return Math.Max(_maxLen - _trail.Count, 0);
    }

    public int GetGrowthCountdown()
    {
        return _growthInterval - (_steps % _growthInterval);
    }

    public int GetMaxMechLevel()
    {
        int lv = 0;
        foreach (int a in _mechInitialLevels)
        {
            lv = Math.Max(lv, Math.Min(a + _g, _maxMechanismLevel));
        }
        return lv;
    }

    public bool HasMechanisms()
    {
        return _mechCenters.Count > 0;
    }

    public int GetMaxLen()
    {
        return _maxLen;
    }

    public int GetSteps()
    {
        return _steps;
    }

    public bool GetFinished()
    {
        return _finished;
    }

    public int GetFoodLenGain()
    {
        return _foodLenGain;
    }

    public int GetFoodEaten()
    {
        return _foodEaten;
    }

    /// <summary>当前生长次数（封顶 gMax）；供指标 / 枚举器读取。</summary>
    internal int GrowthCount => _g;

    /// <summary>当前剩余食物（已吃掉的不在集合内）。</summary>
    internal IReadOnlyCollection<Cell> RemainingFoods => _foods;

    /// <summary>当前被身体遮挡的机关格（生长时跳过、之后补占）。</summary>
    internal IReadOnlyCollection<Cell> ShieldedCells => _shielded;

    /// <summary>运行态快照（有界枚举 / 差分测试用）；静态数据（地板 / 机关 / 食物）不在快照内。</summary>
    internal sealed class RuleSnapshot
    {
        internal List<Cell> Trail;
        internal HashSet<Cell> Foods;
        internal HashSet<Cell> Shielded;
        internal HashSet<Cell> MechCells;
        internal int ShieldVersion;
        internal int MaxLen;
        internal int Steps;
        internal int FoodEaten;
        internal int G;
        internal bool Finished;
    }

    internal RuleSnapshot CaptureState()
    {
        return new RuleSnapshot
        {
            Trail = new List<Cell>(_trail),
            Foods = new HashSet<Cell>(_foods),
            Shielded = new HashSet<Cell>(_shielded),
            MechCells = new HashSet<Cell>(_mechCells),
            ShieldVersion = _shieldVersion,
            MaxLen = _maxLen,
            Steps = _steps,
            FoodEaten = _foodEaten,
            G = _g,
            Finished = _finished,
        };
    }

    internal void RestoreState(RuleSnapshot snapshot)
    {
        _trail.Clear();
        _trail.AddRange(snapshot.Trail);
        _foods.Clear();
        foreach (Cell c in snapshot.Foods)
        {
            _foods.Add(c);
        }
        _shielded.Clear();
        foreach (Cell c in snapshot.Shielded)
        {
            _shielded.Add(c);
        }
        _mechCells.Clear();
        foreach (Cell c in snapshot.MechCells)
        {
            _mechCells.Add(c);
        }
        _shieldVersion = snapshot.ShieldVersion;
        _maxLen = snapshot.MaxLen;
        _steps = snapshot.Steps;
        _foodEaten = snapshot.FoodEaten;
        _g = snapshot.G;
        _finished = snapshot.Finished;
        _distCache.Clear();
    }

    // ── 机关：覆盖时刻表 ─────────────────────────────────────────
    private void BuildCoverStep()
    {
        _coverStep.Clear();
        if (_mechCenters.Count == 0)
        {
            return;
        }
        int endX = _grid.Origin.X + _grid.Width;
        int endY = _grid.Origin.Y + _grid.Height;
        for (int y = _grid.Origin.Y; y < endY; y++)
        {
            for (int x = _grid.Origin.X; x < endX; x++)
            {
                Cell c = new(x, y);
                if (c.Equals(_spawn) || c.Equals(_goal))
                {
                    continue;
                }
                if (!_grid.IsFloor(c))
                {
                    continue;
                }
                int best = INF;
                for (int i = 0; i < _mechCenters.Count; i++)
                {
                    int cs = MechCoverStep(_mechCenters[i], _mechInitialLevels[i], c);
                    if (cs < best)
                    {
                        best = cs;
                    }
                }
                if (best <= _gMax)
                {
                    _coverStep[c] = best;
                }
            }
        }
    }

    private int MechCoverStep(Cell center, int initialLevel, Cell c)
    {
        int lv = MechanicShapes.CoverLevel(c - center);
        if (lv > _maxMechanismLevel)
        {
            return INF;
        }
        return Math.Max(0, lv - initialLevel);
    }

    private int GetCoverStep(Cell c)
    {
        return _coverStep.TryGetValue(c, out int v) ? v : INF;
    }

    private List<Cell> GetClaimedCellsInternal()
    {
        var outCells = new List<Cell>();
        foreach (KeyValuePair<Cell, int> kv in _coverStep)
        {
            if (kv.Value <= _g && !_shielded.Contains(kv.Key))
            {
                outCells.Add(kv.Key);
            }
        }
        return outCells;
    }

    /// <summary>生长一次：阶段 +1（封顶），重算被身体挡住的格；返回本次新增占格。</summary>
    private List<Cell> Grow()
    {
        var before = new HashSet<Cell>(GetClaimedCellsInternal());
        _g = Math.Min(_g + 1, _gMax);
        _shieldVersion++;
        _shielded.Clear();
        foreach (Cell c in _trail)
        {
            if (GetCoverStep(c) <= _g)
            {
                _shielded.Add(c);
            }
        }
        RefreshMechCells();

        var added = new List<Cell>();
        foreach (Cell c in GetClaimedCellsInternal())
        {
            if (!before.Contains(c))
            {
                added.Add(c);
            }
        }
        return added;
    }

    private void RefreshMechCells()
    {
        _mechCells.Clear();
        foreach (Cell c in GetClaimedCellsInternal())
        {
            if (_grid.InBounds(c))
            {
                _mechCells.Add(c);
            }
        }
    }

    private static Godot.Collections.Array ToGodotArray(IEnumerable<Cell> cells)
    {
        var arr = new Godot.Collections.Array();
        foreach (Cell c in cells)
        {
            arr.Add(new Vector2I(c.X, c.Y));
        }
        return arr;
    }
}
