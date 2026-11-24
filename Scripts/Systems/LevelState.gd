class_name LevelState
extends RefCounted
## ============================================================================
## LevelState — 关卡纯规则状态（无场景树 / 无动画 / 无 UI）
## ----------------------------------------------------------------------------
## 游戏 Level.gd 与离线 tools/LevelSolver 共用同一套规则：
##   · trail 是尾巴钉死在 S 的简单路径，长度 ≤ max_len
##   · 食物只加 L；每 N 个有效步所有机关同步 +1 阶段
##   · 机关占格用 cover_step（每格第几次生长被覆盖）+ shielded（身体挡格）表达，
##     等价于逐机关 claim_missing，但状态更小，可直接用于搜索。
## 权威规则：[docs/design/功能需求文档.md] §4.1–4.4
## ============================================================================

const INF := 1 << 30

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


var grid: GridSystem = null
var spawn_cell := Vector2i.ZERO
var goal_cell := Vector2i.ZERO

## 初始食物集合 / 当前剩余食物（Vector2i -> true）
var initial_foods: Dictionary = {}
var food_cells: Dictionary = {}

## 格子 -> 第几次生长被机关覆盖（只存会被覆盖的地板格；无键 = 永不覆盖）
var cover_step: Dictionary = {}

var initial_max_len := 3
var food_len_gain := 2
var growth_interval := 6
var max_mechanism_level := 4
## 本关还会发生的生长次数上限（所有机关到 cap 为止）
var g_max := 0

var trail: Array[Vector2i] = []
var max_len := 3
var steps := 0
var food_eaten := 0
## 已完成的生长次数（封顶 g_max；到顶后仍每次刷新 shielded）
var g := 0
## 本应已被覆盖、但因身体留在上面而暂时未占的格
var shielded: Dictionary = {}
var finished := false

var _mech_centers: Array[Vector2i] = []
var _mech_initial_levels: Array[int] = []


## 注入关卡静态数据；必须在 start() 前调用一次
func setup(p_grid: GridSystem, p_spawn: Vector2i, p_goal: Vector2i,
		foods: Array[Vector2i], mech_centers: Array[Vector2i], mech_initial_levels: Array[int],
		p_max_len: int, p_growth_interval: int, p_food_len_gain: int, p_mech_cap: int) -> void:
	grid = p_grid
	spawn_cell = p_spawn
	goal_cell = p_goal
	grid.goal_cell = p_goal

	initial_foods.clear()
	for f in foods:
		initial_foods[f] = true

	initial_max_len = maxi(p_max_len, 1)
	growth_interval = maxi(p_growth_interval, 1)
	food_len_gain = p_food_len_gain
	max_mechanism_level = clampi(p_mech_cap, 0, MechanicShapes.max_level())

	_mech_centers.clear()
	for c: Vector2i in mech_centers:
		_mech_centers.append(c)
	_mech_initial_levels.clear()
	g_max = 0
	for i in _mech_centers.size():
		var a := 0
		if i < mech_initial_levels.size():
			a = mech_initial_levels[i]
		a = clampi(a, 0, max_mechanism_level)
		_mech_initial_levels.append(a)
		g_max = maxi(g_max, max_mechanism_level - a)

	_build_cover_step()


## 重置到关卡开局（可重复调用）；返回初始占格（供表现层播"范围初建"动画）
func start() -> Array[Vector2i]:
	trail = [spawn_cell]
	max_len = initial_max_len
	steps = 0
	food_eaten = 0
	g = 0
	shielded.clear()
	finished = false
	food_cells = initial_foods.duplicate()
	_refresh_mech_cells()
	return get_claimed_cells()


var head: Vector2i:
	get:
		return trail[-1] if not trail.is_empty() else spawn_cell


## 一次方向输入；无效移动返回 accepted=false 且不计步
func step(dir: Vector2i) -> StepResult:
	var res := StepResult.new()
	if finished:
		return res

	var nxt: Vector2i = head + dir
	if not grid.in_bounds(nxt):
		return res

	# 情况 B：踏入自身身体 -> 截断到该格
	var body_idx := trail.find(nxt)
	if body_idx >= 0:
		trail = trail.slice(0, body_idx + 1)
		steps += 1
		res.accepted = true
		res.truncated = true
	else:
		# 情况 A：可走格（障碍/机关占格不可入），且身体未达硬上限
		if not grid.can_step_into(nxt):
			return res
		if trail.size() >= max_len:
			return res
		trail.append(nxt)
		if food_cells.has(nxt):
			food_cells.erase(nxt)
			food_eaten += 1
			max_len += food_len_gain
			res.ate_food = true
			res.food_cell = nxt
		steps += 1
		res.accepted = true

	# 生长与胜利/死局
	if steps > 0 and steps % growth_interval == 0:
		res.grew = true
		res.added_mech_cells = _grow()
	if head == goal_cell:
		res.won = true
		finished = true
		return res
	res.deadlock_msg = check_deadlock()
	if not res.deadlock_msg.is_empty():
		finished = true
	return res


## 当前机关实际占格（= 已到覆盖阶段且未被身体挡住的地板格）
func get_claimed_cells() -> Array[Vector2i]:
	var out: Array[Vector2i] = []
	for c: Vector2i in cover_step:
		if int(cover_step[c]) <= g and not shielded.has(c):
			out.append(c)
	return out


## 死局检查（与旧 Level._check_deadlock 同口径）：
## 无通路 -> 失败；身长达上限后，E 与所有剩余食物都超出 S 的 L 半径 -> 失败。
## 返回空串表示仍可继续；胜利由调用方优先处理。
func check_deadlock() -> String:
	if finished or head == goal_cell:
		return ""
	if not grid.has_path(head, goal_cell):
		return "无路可走… 重开吧"
	if trail.size() < max_len:
		return ""
	var reach := max_len - 1
	var dist := grid.bfs_dist_map(spawn_cell)
	if dist.has(goal_cell) and int(dist[goal_cell]) <= reach:
		return ""
	for f: Vector2i in food_cells:
		if dist.has(f) and int(dist[f]) <= reach:
			return ""
	return "管道不够长啊… 摘不到蓝花也捡不到花瓣"


func get_remaining_steps() -> int:
	return maxi(max_len - trail.size(), 0)


func get_growth_countdown() -> int:
	return growth_interval - (steps % growth_interval)


func get_max_mech_level() -> int:
	var lv := 0
	for a in _mech_initial_levels:
		lv = maxi(lv, mini(a + g, max_mechanism_level))
	return lv


func has_mechanisms() -> bool:
	return not _mech_centers.is_empty()


func get_cover_step(c: Vector2i) -> int:
	return int(cover_step.get(c, INF))


# ── 机关：覆盖时刻表 ───────────────────────────────────────────
## 预计算每个地板格的"第几次生长被覆盖"，O(格子数 × 机关数)
func _build_cover_step() -> void:
	cover_step.clear()
	if grid == null or _mech_centers.is_empty():
		return
	var origin := grid.origin
	var end := origin + grid.size
	for y in range(origin.y, end.y):
		for x in range(origin.x, end.x):
			var c := Vector2i(x, y)
			if c == spawn_cell or c == goal_cell:
				continue
			if not _is_floor(c):
				continue
			var best := INF
			for i in _mech_centers.size():
				var cs := _mech_cover_step(_mech_centers[i], _mech_initial_levels[i], c)
				if cs < best:
					best = cs
			if best <= g_max:
				cover_step[c] = best


func _mech_cover_step(center: Vector2i, initial_level: int, c: Vector2i) -> int:
	var lv := MechanicShapes.cover_level(c - center)
	if lv > max_mechanism_level:
		return INF
	return maxi(0, lv - initial_level)


func _is_floor(c: Vector2i) -> bool:
	if grid == null or not grid.in_bounds(c):
		return false
	return grid.floor_cells.is_empty() or grid.floor_cells.has(c)


## 生长一次：阶段 +1（封顶），重算本帧被身体挡住的格，刷新 grid.mech_cells。
## 返回"本次新增占格"（表现层出场动画用）。
func _grow() -> Array[Vector2i]:
	var before := get_claimed_cells()
	g = mini(g + 1, g_max)
	shielded.clear()
	for c: Vector2i in trail:
		if get_cover_step(c) <= g:
			shielded[c] = true
	_refresh_mech_cells()
	return _diff_cells(before, get_claimed_cells())


func _refresh_mech_cells() -> void:
	grid.mech_cells.clear()
	for c in get_claimed_cells():
		if grid.in_bounds(c):
			grid.mech_cells[c] = true


func _diff_cells(before: Array[Vector2i], after: Array[Vector2i]) -> Array[Vector2i]:
	var seen := {}
	for c in before:
		seen[c] = true
	var out: Array[Vector2i] = []
	for c in after:
		if not seen.has(c):
			out.append(c)
	return out
