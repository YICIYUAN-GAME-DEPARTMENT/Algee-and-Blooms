class_name LevelState
extends RefCounted
## ============================================================================
## LevelState — 规则适配层（GDScript 侧唯一入口）
## ----------------------------------------------------------------------------
## 玩法规则唯一实现在 C# `Scripts/Algorithms/RuleState.cs`；本文件只做：
##   · 把关卡静态数据转发给 C# RuleState
##   · 每步后同步 trail / max_len / steps / finished 等字段
##   · 把 C# 返回的字典转换为原有 StepResult，保持 Level.gd 调用不变
## 权威规则：[docs/design/功能需求文档.md] §4.1–4.5
## ============================================================================

const RULE_STATE_SCRIPT := "res://Scripts/Algorithms/RuleState.cs"
const EXACT_SOLVER_SCRIPT := "res://Scripts/Algorithms/Search/ExactBfsSolver.cs"


## 一次有效行动的规则结果；表现层据此播动画/发信号/弹结算
class StepResult:
	var accepted := false
	var truncated := false
	var ate_food := false
	var food_cell := Vector2i.ZERO
	var grew := false
	var won := false
	var deadlock_msg := ""
	var added_mech_cells: Array[Vector2i] = []


var spawn_cell := Vector2i.ZERO
var goal_cell := Vector2i.ZERO
var trail: Array[Vector2i] = []
var max_len := 3
var steps := 0
var food_len_gain := 2
var finished := false

var _core: RefCounted = null
var _solver: RefCounted = null


func _ensure_core() -> void:
	if _core == null:
		_core = load(RULE_STATE_SCRIPT).new()
		# 表现层：判负只提示、不锁死后续移动（求解/生成/测试仍用默认的"死局即终结"）
		_core.call("SetEndOnDeadlock", false)


## 注入关卡静态数据；必须在 start() 前调用一次
func setup(p_grid: GridSystem, p_spawn: Vector2i, p_goal: Vector2i,
		foods: Array[Vector2i], mech_centers: Array[Vector2i], mech_initial_levels: Array[int],
		p_max_len: int, p_growth_interval: int, p_food_len_gain: int, p_mech_cap: int) -> void:
	_ensure_core()
	spawn_cell = p_spawn
	goal_cell = p_goal
	_core.call("Setup", p_grid.origin, p_grid.size,
			p_grid.floor_cells.keys(), p_grid.obstacles.keys(),
			p_spawn, p_goal, foods, mech_centers, mech_initial_levels,
			p_max_len, p_growth_interval, p_food_len_gain, p_mech_cap)
	food_len_gain = int(_core.call("GetFoodLenGain"))


## 重置到关卡开局（可重复调用）；返回初始占格（供表现层播"范围初建"动画）
func start() -> Array[Vector2i]:
	_ensure_core()
	var claimed: Array = _core.call("Start")
	_sync_from_core()
	var out: Array[Vector2i] = []
	out.assign(claimed)
	return out


var head: Vector2i:
	get:
		return trail[-1] if not trail.is_empty() else spawn_cell


## 一次方向输入；无效移动返回 accepted=false 且不计步
func step(dir: Vector2i) -> StepResult:
	_ensure_core()
	var res := StepResult.new()
	var raw: Dictionary = _core.call("Step", dir)
	_sync_from_core()
	res.accepted = bool(raw.get("accepted", false))
	res.truncated = bool(raw.get("truncated", false))
	res.ate_food = bool(raw.get("ate_food", false))
	res.food_cell = raw.get("food_cell", Vector2i.ZERO)
	res.grew = bool(raw.get("grew", false))
	res.won = bool(raw.get("won", false))
	res.deadlock_msg = str(raw.get("deadlock_msg", ""))
	var added: Array = raw.get("added_mech_cells", [])
	res.added_mech_cells.assign(added)
	return res


## 当前机关实际占格（= 已到覆盖阶段且未被身体挡住的地板格）
func get_claimed_cells() -> Array[Vector2i]:
	_ensure_core()
	var claimed: Array = _core.call("GetClaimedCells")
	var out: Array[Vector2i] = []
	out.assign(claimed)
	return out


## 死局检查；返回空串表示仍可继续（胜利由调用方优先处理）
func check_deadlock() -> String:
	_ensure_core()
	return str(_core.call("CheckDeadlock"))


func get_remaining_steps() -> int:
	_ensure_core()
	return int(_core.call("GetRemainingSteps"))


func get_growth_countdown() -> int:
	_ensure_core()
	return int(_core.call("GetGrowthCountdown"))


func get_max_mech_level() -> int:
	_ensure_core()
	return int(_core.call("GetMaxMechLevel"))


## 各机关当前阶段（按中心顺序；负值表示尚未激活）
func get_mech_levels() -> Array[int]:
	_ensure_core()
	var levels: Array = _core.call("GetMechLevels")
	var out: Array[int] = []
	out.assign(levels)
	return out


func has_mechanisms() -> bool:
	_ensure_core()
	return bool(_core.call("HasMechanisms"))


## 精确 BFS（工具 / 后续提示使用）；委托给 C# ExactBfsSolver
func solve_exact(max_states: int) -> Dictionary:
	_ensure_core()
	if _solver == null:
		_solver = load(EXACT_SOLVER_SCRIPT).new()
	return _solver.call("Solve", _core, max_states)


func _sync_from_core() -> void:
	trail.assign(_core.call("GetTrail"))
	max_len = int(_core.call("GetMaxLen"))
	steps = int(_core.call("GetSteps"))
	finished = bool(_core.call("GetFinished"))
