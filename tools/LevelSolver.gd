extends Node
## ============================================================================
## LevelSolver — 离线关卡可解性求解器（headless）
## ----------------------------------------------------------------------------
## 运行：
##   godot --headless --path . res://tools/LevelSolver.tscn
## 输出：每关 SOLVABLE / UNSOLVABLE / UNKNOWN + 最短路线 / 步数 / 状态数
##
## 状态 = (trail, 已吃食物, shielded, 生长次数 g, 生长周期相位)。
## 机关用 LevelState.cover_step 的"开花时刻表"表达；BFS 与游戏共用同一套
## LevelState 规则，找到解后会再用 LevelState 逐步重放校验。
## ============================================================================

const LEVELS_DIR := "res://Scenes/Levels"
## BFS 节点预算；超出则报 UNKNOWN（不谎报无解）
const MAX_STATES := 800_000


## BFS 节点；parent 是队列下标，用于回溯路线
class SearchState:
	var trail_id := 0
	var food_mask := 0
	var shielded_mask := 0
	var g := 0
	var phase := 0
	var max_len := 3
	var parent := -1
	var dir := Vector2i.ZERO


class Solver:
	const INF := 1 << 30
	const DIRS := [Vector2i.LEFT, Vector2i.RIGHT, Vector2i.UP, Vector2i.DOWN]
	const DIR_CHARS := {
		Vector2i.LEFT: "A",
		Vector2i.RIGHT: "D",
		Vector2i.UP: "W",
		Vector2i.DOWN: "S",
	}

	var floor_cells: Dictionary
	var obstacles: Dictionary
	var spawn := Vector2i.ZERO
	var goal := Vector2i.ZERO
	var cover_step: Dictionary
	var cover_cells: Array[Vector2i] = []
	var cover_index: Dictionary = {}
	var food_cells: Array[Vector2i] = []
	var food_index: Dictionary = {}
	var initial_max_len := 3
	var food_len_gain := 2
	var interval := 6
	var g_max := 0
	var board_origin := Vector2i.ZERO
	var board_size := Vector2i.ZERO

	# 轨迹持久化栈：节点 = (父节点, 格)，相同轨迹共享 ID
	var tn_cell: Array[Vector2i] = []
	var tn_parent: Array[int] = []
	var tn_depth: Array[int] = []
	var tn_map: Dictionary = {}

	# 每个 (g, shielded_mask) 的可达性缓存：head 能否到 E / 从 S 的最短距离
	var conn_cache: Dictionary = {}


	func solve(st: LevelState, budget: int) -> Dictionary:
		_setup(st)
		if cover_cells.size() > 63:
			return {"status": "UNKNOWN", "states": 0, "reason": "机关覆盖格超过 63，当前位图模型不支持"}
		if not _necessary_pass():
			return {"status": "UNSOLVABLE", "states": 0, "reason": "静态闭包失败"}

		var root := _init_trail(spawn)
		var start := SearchState.new()
		start.trail_id = root
		start.max_len = initial_max_len

		# 开局死局检查（与游戏 _ready 末尾一致）
		if spawn != goal and not _alive_after_move(root, 0, initial_max_len, 0, 0):
			return {"status": "UNSOLVABLE", "states": 0, "reason": "开局死局"}

		var queue: Array[SearchState] = [start]
		var visited := {}
		visited[_key(start)] = 0
		var qi := 0

		while qi < queue.size():
			if queue.size() > budget:
				return {"status": "UNKNOWN", "states": queue.size(), "reason": "超出节点预算"}

			var s: SearchState = queue[qi]
			var s_idx := qi
			qi += 1

			var head_cell: Vector2i = tn_cell[s.trail_id]
			if head_cell == goal:
				return _make_result(queue, s_idx)

			for d in DIRS:
				var n: Vector2i = head_cell + d
				if not _in_bounds(n):
					continue

				var body := _find_in_trail(s.trail_id, n)
				var nt := -1
				var food_mask := s.food_mask
				var ml := s.max_len

				if body >= 0:
					# 踏入自身身体 -> 截断
					nt = body
				else:
					if _blocked(n, s.g, s.shielded_mask):
						continue
					if int(tn_depth[s.trail_id]) >= s.max_len:
						continue
					nt = _push(s.trail_id, n)
					if food_index.has(n):
						var bit := 1 << int(food_index[n])
						if (food_mask & bit) == 0:
							food_mask |= bit
							ml += food_len_gain

				var phase := (s.phase + 1) % interval
				var ng := s.g
				var nshield := s.shielded_mask
				if phase == 0:
					ng = mini(ng + 1, g_max)
					nshield = 0
					var cur := nt
					while cur >= 0:
						var c: Vector2i = tn_cell[cur]
						if cover_index.has(c) and int(cover_step[c]) <= ng:
							nshield |= 1 << int(cover_index[c])
						cur = tn_parent[cur]

				var nhead: Vector2i = tn_cell[nt]
				var is_win := nhead == goal
				if not is_win and not _alive_after_move(nt, food_mask, ml, ng, nshield):
					continue

				var ns := SearchState.new()
				ns.trail_id = nt
				ns.food_mask = food_mask
				ns.shielded_mask = nshield
				ns.g = ng
				ns.phase = phase
				ns.max_len = ml
				ns.parent = s_idx
				ns.dir = d

				var key := _key(ns)
				if not visited.has(key):
					visited[key] = queue.size()
					queue.append(ns)

		return {"status": "UNSOLVABLE", "states": queue.size(), "reason": "状态耗尽"}


	# ── 初始化 ────────────────────────────────────────────────
	func _setup(st: LevelState) -> void:
		floor_cells = st.grid.floor_cells
		obstacles = st.grid.obstacles
		spawn = st.spawn_cell
		goal = st.goal_cell
		cover_step = st.cover_step
		initial_max_len = st.initial_max_len
		food_len_gain = st.food_len_gain
		interval = st.growth_interval
		g_max = st.g_max
		board_origin = st.grid.origin
		board_size = st.grid.size
		tn_cell.clear()
		tn_parent.clear()
		tn_depth.clear()
		tn_map.clear()
		conn_cache.clear()

		cover_cells.clear()
		cover_index.clear()
		var ci := 0
		for c: Vector2i in cover_step:
			if obstacles.has(c):
				continue  # 障碍格永远不可走，不需要进 shielded 位图
			cover_cells.append(c)
			cover_index[c] = ci
			ci += 1
		if cover_cells.size() > 63:
			push_warning("LevelSolver: cover cells=%d，shielded 位图会超出 64 位" % cover_cells.size())

		food_cells.clear()
		food_index.clear()
		var fi := 0
		for c: Vector2i in st.initial_foods:
			food_cells.append(c)
			food_index[c] = fi
			fi += 1


	# ── 轨迹持久化栈 ──────────────────────────────────────────
	func _init_trail(root: Vector2i) -> int:
		tn_cell = [root]
		tn_parent = [-1]
		tn_depth = [1]
		tn_map.clear()
		return 0


	func _push(parent_id: int, c: Vector2i) -> int:
		var key := parent_id * 65536 + _cell_index(c)
		if tn_map.has(key):
			return int(tn_map[key])
		var id := tn_cell.size()
		tn_cell.append(c)
		tn_parent.append(parent_id)
		tn_depth.append(int(tn_depth[parent_id]) + 1)
		tn_map[key] = id
		return id


	func _find_in_trail(id: int, c: Vector2i) -> int:
		var cur := id
		while cur >= 0:
			if tn_cell[cur] == c:
				return cur
			cur = int(tn_parent[cur])
		return -1


	func _cell_index(c: Vector2i) -> int:
		return (c.y - board_origin.y) * board_size.x + (c.x - board_origin.x)


	# ── 可走性 / 死局 ────────────────────────────────────────
	func _in_bounds(c: Vector2i) -> bool:
		return c.x >= board_origin.x and c.y >= board_origin.y \
			and c.x < board_origin.x + board_size.x and c.y < board_origin.y + board_size.y


	func _blocked(c: Vector2i, g: int, shielded_mask: int) -> bool:
		if not _in_bounds(c):
			return true
		if not floor_cells.is_empty() and not floor_cells.has(c):
			return true
		if obstacles.has(c):
			return true
		if c == spawn or c == goal:
			return false
		var cs := int(cover_step.get(c, INF))
		if cs > g:
			return false
		var idx := int(cover_index.get(c, -1))
		if idx >= 0 and (shielded_mask & (1 << idx)) != 0:
			return false
		return true


	## 与 LevelState.check_deadlock 同口径，但用搜索态自己的障碍集。
	func _alive_after_move(trail_id: int, food_mask: int, ml: int, g: int, shielded_mask: int) -> bool:
		var conn := _ensure_connectivity(g, shielded_mask)
		var reach: Dictionary = conn["reach_goal"]
		var h: Vector2i = tn_cell[trail_id]
		if not reach.has(h):
			return false
		if int(tn_depth[trail_id]) < ml:
			return true
		var dist_s: Dictionary = conn["dist_s"]
		var limit := ml - 1
		if dist_s.has(goal) and int(dist_s[goal]) <= limit:
			return true
		for f in food_cells:
			if (food_mask & (1 << int(food_index[f]))) != 0:
				continue
			if dist_s.has(f) and int(dist_s[f]) <= limit:
				return true
		return false


	## 缓存每个 (g, shielded_mask) 的：能到 E 的格集合、从 S 出发的距离
	func _ensure_connectivity(g: int, shielded_mask: int) -> Dictionary:
		var key := "%d|%d" % [g, shielded_mask]
		if conn_cache.has(key):
			return conn_cache[key]
		var rec := {
			"reach_goal": _bfs_region(goal, g, shielded_mask),
			"dist_s": _bfs_dist(spawn, g, shielded_mask),
		}
		conn_cache[key] = rec
		return rec


	func _bfs_region(from: Vector2i, g: int, shielded_mask: int) -> Dictionary:
		var seen := {from: true}
		var q: Array[Vector2i] = [from]
		var i := 0
		while i < q.size():
			var cur: Vector2i = q[i]
			i += 1
			for d in DIRS:
				var n: Vector2i = cur + d
				if seen.has(n) or _blocked(n, g, shielded_mask):
					continue
				seen[n] = true
				q.append(n)
		return seen


	func _bfs_dist(from: Vector2i, g: int, shielded_mask: int) -> Dictionary:
		var dist := {from: 0}
		var q: Array[Vector2i] = [from]
		var i := 0
		while i < q.size():
			var cur: Vector2i = q[i]
			i += 1
			for d in DIRS:
				var n: Vector2i = cur + d
				if dist.has(n) or _blocked(n, g, shielded_mask):
					continue
				dist[n] = int(dist[cur]) + 1
				q.append(n)
		return dist


	# ── 静态必要条件 ──────────────────────────────────────────
	## 最宽松障碍集（只算 cover_step=0 的初始机关格）都吃不到 E -> 必然无解。
	func _necessary_pass() -> bool:
		var blocked := {}
		for c: Vector2i in cover_step:
			if int(cover_step[c]) <= 0:
				blocked[c] = true
		var dist := _bfs_static(spawn, blocked)
		var l := initial_max_len
		var remaining := food_cells.duplicate()
		var changed := true
		while changed:
			changed = false
			for f in remaining.duplicate():
				if dist.has(f) and int(dist[f]) <= l - 1:
					remaining.erase(f)
					l += food_len_gain
					changed = true
		return dist.has(goal) and int(dist[goal]) <= l - 1


	func _bfs_static(from: Vector2i, extra: Dictionary) -> Dictionary:
		var dist := {from: 0}
		var q: Array[Vector2i] = [from]
		var i := 0
		while i < q.size():
			var cur: Vector2i = q[i]
			i += 1
			for d in DIRS:
				var n: Vector2i = cur + d
				if dist.has(n) or _static_blocked(n, extra):
					continue
				dist[n] = int(dist[cur]) + 1
				q.append(n)
		return dist


	func _static_blocked(c: Vector2i, extra: Dictionary) -> bool:
		if not _in_bounds(c):
			return true
		if not floor_cells.is_empty() and not floor_cells.has(c):
			return true
		if obstacles.has(c):
			return true
		if c == spawn or c == goal:
			return false
		return extra.has(c)


	# ── 结果 ──────────────────────────────────────────────────
	func _key(s: SearchState) -> String:
		return "%d|%d|%d|%d|%d" % [s.trail_id, s.food_mask, s.shielded_mask, s.g, s.phase]


	func _make_result(queue: Array, idx: int) -> Dictionary:
		var dirs: Array[Vector2i] = []
		var cur := idx
		while cur >= 0:
			var s: SearchState = queue[cur]
			if s.parent >= 0:
				dirs.append(s.dir)
			cur = s.parent
		dirs.reverse()
		var route := ""
		for d in dirs:
			route += DIR_CHARS[d]
		var last: SearchState = queue[idx]
		return {
			"status": "SOLVABLE",
			"steps": dirs.size(),
			"route": route,
			"states": queue.size(),
			"max_len": last.max_len,
		}


# ── 工具入口 ─────────────────────────────────────────────────
func _ready() -> void:
	var paths := _list_levels()
	if paths.is_empty():
		push_error("LevelSolver: 未找到 Level_*.tscn")
		get_tree().quit(1)
		return

	var totals := {"SOLVABLE": 0, "UNSOLVABLE": 0, "UNKNOWN": 0, "MODEL_ERROR": 0}
	for path in paths:
		var r := await _solve_one(path)
		if r.is_empty():
			totals["MODEL_ERROR"] = int(totals["MODEL_ERROR"]) + 1
			continue
		var status := str(r.get("status", "UNKNOWN"))
		totals[status] = int(totals.get(status, 0)) + 1

	print("")
	print("=== 汇总 SOLVABLE=%d UNSOLVABLE=%d UNKNOWN=%d MODEL_ERROR=%d ===" % [
		totals["SOLVABLE"], totals["UNSOLVABLE"], totals["UNKNOWN"], totals["MODEL_ERROR"]
	])
	var bad := int(totals["UNSOLVABLE"]) + int(totals["UNKNOWN"]) + int(totals["MODEL_ERROR"])
	get_tree().quit(0 if bad == 0 else 2)


func _solve_one(path: String) -> Dictionary:
	var ps := load(path) as PackedScene
	if ps == null:
		push_error("LevelSolver: 无法加载 %s" % path)
		return {}
	var level := ps.instantiate()
	if level == null:
		return {}

	# 关掉剧情，避免 headless 求解时等待/播放演出
	level.set("story_start", "")
	level.set("story_win", "")
	level.set("story_fail", "")
	add_child(level)
	await get_tree().process_frame

	var st: LevelState = level.get("state")
	if st == null or st.trail.is_empty():
		push_error("LevelSolver: %s 未构建 LevelState" % path)
		level.queue_free()
		await get_tree().process_frame
		return {}

	print("[Level %d] %s" % [int(level.get("level_id")), str(level.get("level_name"))])
	var solver := Solver.new()
	var result := solver.solve(st, MAX_STATES)
	print("  status = %s" % str(result.get("status", "?")))
	print("  states = %d" % int(result.get("states", 0)))
	if result["status"] == "SOLVABLE":
		print("  steps  = %d" % int(result.get("steps", 0)))
		print("  route  = %s" % str(result.get("route", "")))
		var ok := _verify(st, str(result.get("route", "")))
		print("  verify = %s" % ("OK" if ok else "FAILED"))
		if not ok:
			result["status"] = "MODEL_ERROR"
	else:
		print("  reason = %s" % str(result.get("reason", "")))

	level.queue_free()
	await get_tree().process_frame
	return result


## 用同一份 LevelState 重放路线；每步都检查 accepted / 死局 / 最终到 E
func _verify(st: LevelState, route: String) -> bool:
	st.start()
	for ch in route:
		var d := _dir_for_char(ch)
		if d == Vector2i.ZERO:
			return false
		var res := st.step(d)
		if not res.accepted:
			return false
		if not res.deadlock_msg.is_empty() and not res.won:
			return false
	return st.head == st.goal_cell


func _dir_for_char(ch: String) -> Vector2i:
	match ch:
		"A": return Vector2i.LEFT
		"D": return Vector2i.RIGHT
		"W": return Vector2i.UP
		"S": return Vector2i.DOWN
	return Vector2i.ZERO


func _list_levels() -> Array[String]:
	var out: Array[String] = []
	var dir := DirAccess.open(LEVELS_DIR)
	if dir == null:
		return out
	dir.list_dir_begin()
	var fname := dir.get_next()
	while fname != "":
		if not dir.current_is_dir():
			var scene_name := fname.trim_suffix(".remap")
			if scene_name.begins_with("Level_") and scene_name.ends_with(".tscn") \
					and scene_name != "LevelTemplate.tscn":
				out.append(LEVELS_DIR.path_join(scene_name))
		fname = dir.get_next()
	dir.list_dir_end()
	out.sort()
	return out
