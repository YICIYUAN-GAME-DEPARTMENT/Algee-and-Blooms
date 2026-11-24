extends Node
## ============================================================================
## LevelSolver — 离线关卡可解性求解器（headless）
## ----------------------------------------------------------------------------
## 运行：
##   godot --headless --path . res://tools/LevelSolver.tscn
## 输出：每关 SOLVABLE / UNSOLVABLE / UNKNOWN + 最短路线 / 步数 / 状态数
##
## 规则与搜索统一由 C# 提供（Scripts/Algorithms/RuleState + ExactBfsSolver）；
## 本工具只负责加载关卡、打印结果，并用同一 RuleState 重放校验路线。
## ============================================================================

const LEVELS_DIR := "res://Scenes/Levels"
## BFS 节点预算；超出则报 UNKNOWN（不谎报无解）
const MAX_STATES := 800_000


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
	var t0 := Time.get_ticks_msec()
	var result := st.solve_exact(MAX_STATES)
	var elapsed := Time.get_ticks_msec() - t0
	print("  status = %s" % str(result.get("status", "?")))
	print("  states = %d" % int(result.get("states", 0)))
	print("  time   = %d ms" % elapsed)
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


## 用同一份 RuleState 重放路线；每步都检查 accepted / 死局 / 最终到 E
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
