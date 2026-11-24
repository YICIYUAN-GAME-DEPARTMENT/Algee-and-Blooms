extends Node
## ============================================================================
## LevelSpecDump — 把手工关卡场景导出为 LevelSpec JSON（headless）
## ----------------------------------------------------------------------------
## 运行：
##   godot --headless --path . res://tools/LevelSpecDump.tscn
## 输出：
##   res://tests/fixtures/anchors/level_<id>.json
## 用途：评分锚点快照（S1）与生成器 spec 往返校验；不参与游戏运行。
## 字段与 C# LevelSpec 同形（snake_case，Cell = [x, y]）。
## ============================================================================

const LEVELS_DIR := "res://Scenes/Levels"
const OUT_DIR := "res://tests/fixtures/anchors"

var _failed := 0


func _ready() -> void:
	var err := DirAccess.make_dir_recursive_absolute(OUT_DIR)
	if err != OK and err != ERR_ALREADY_EXISTS:
		push_error("LevelSpecDump: 无法创建输出目录 %s (err=%d)" % [OUT_DIR, err])
		get_tree().quit(1)
		return

	var paths := _list_levels()
	if paths.is_empty():
		push_error("LevelSpecDump: 未找到 Level_*.tscn")
		get_tree().quit(1)
		return

	for path in paths:
		await _dump_one(path)

	print("")
	print("=== LevelSpecDump 完成：%d 关，失败 %d ===" % [paths.size(), _failed])
	get_tree().quit(0 if _failed == 0 else 1)


func _dump_one(path: String) -> void:
	var ps := load(path) as PackedScene
	if ps == null:
		push_error("LevelSpecDump: 无法加载 %s" % path)
		_failed += 1
		return
	var level := ps.instantiate()
	# 关掉剧情，避免 headless 下等待演出
	level.set("story_start", "")
	level.set("story_win", "")
	level.set("story_fail", "")
	add_child(level)
	await get_tree().process_frame

	var grid: GridSystem = level.get("grid")
	var st: LevelState = level.get("state")
	if grid == null or st == null or st.trail.is_empty():
		push_error("LevelSpecDump: %s 未构建网格或规则状态" % path)
		_failed += 1
		level.queue_free()
		await get_tree().process_frame
		return

	var level_id := int(level.get("level_id"))
	var foods: Array[Vector2i] = []
	for node in level.find_children("*", "Food", true, false):
		foods.append((node as Food).cell)
	var mech_centers: Array[Vector2i] = []
	var mech_levels: Array[int] = []
	for node in level.find_children("*", "Mechanism", true, false):
		var mech := node as Mechanism
		mech_centers.append(mech.cell)
		mech_levels.append(mech.initial_level)

	var l0 := _resolve(int(level.get("initial_max_len_override")),
			int(GameManager.balance.initial_max_len))
	var gain := _resolve(int(level.get("food_len_gain_override")),
			int(GameManager.balance.food_len_gain))
	var interval := _resolve(int(level.get("growth_step_interval_override")),
			int(GameManager.balance.growth_step_interval))
	var cap := _resolve(int(level.get("mechanism_max_level_override")),
			int(GameManager.balance.mechanism_max_level))

	var spec := {
		"schema_version": 1,
		"generator_version": 0,
		"seed": 0,
		"level_id": level_id,
		"name": str(level.get("level_name")),
		"origin": [grid.origin.x, grid.origin.y],
		"width": grid.size.x,
		"height": grid.size.y,
		"floor": _cells_json(_sorted_cells(grid.floor_cells.keys())),
		"obstacles": _cells_json(_sorted_cells(grid.obstacles.keys())),
		"spawn": [st.spawn_cell.x, st.spawn_cell.y],
		"goal": [st.goal_cell.x, st.goal_cell.y],
		"foods": _cells_json(_sorted_cells(foods)),
		"mech_centers": _cells_json(mech_centers),
		"mech_initial_levels": mech_levels,
		"initial_max_len": l0,
		"food_len_gain": gain,
		"growth_interval": interval,
		"mechanism_cap": cap,
	}

	var out_path := OUT_DIR.path_join("level_%02d.json" % level_id)
	var f := FileAccess.open(out_path, FileAccess.WRITE)
	if f == null:
		push_error("LevelSpecDump: 无法写入 %s (err=%d)" % [out_path, FileAccess.get_open_error()])
		_failed += 1
	else:
		f.store_string(JSON.stringify(spec, "  ", false) + "\n")
		f.close()
		print("[Dump] level_%02d: floor=%d obstacles=%d foods=%d mechs=%d size=%dx%d" % [
			level_id, grid.floor_cells.size(), grid.obstacles.size(), foods.size(),
			mech_centers.size(), grid.size.x, grid.size.y,
		])

	level.queue_free()
	await get_tree().process_frame


## override 为 0 时沿用全局 Balance（与 Level.gd 同口径）
func _resolve(override_val: int, global_val: int) -> int:
	return global_val if override_val <= 0 else override_val


func _sorted_cells(cells: Array) -> Array[Vector2i]:
	var out: Array[Vector2i] = []
	for c in cells:
		out.append(c)
	out.sort_custom(func(a: Vector2i, b: Vector2i) -> bool:
		if a.y == b.y:
			return a.x < b.x
		return a.y < b.y)
	return out


func _cells_json(cells: Array[Vector2i]) -> Array:
	var out: Array = []
	for c in cells:
		out.append([c.x, c.y])
	return out


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
