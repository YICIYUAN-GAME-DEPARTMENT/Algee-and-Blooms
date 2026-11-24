extends Node
## ============================================================================
## GameManager  —  全局游戏状态与场景流转 (Autoload)
## ----------------------------------------------------------------------------
## 职责：持有顶层状态机、当前关卡信息、场景切换；玩法细节下沉到关卡。
## 数值配置：balance（Balance.tres）——实现层唯一读取处，脚本勿再硬编码。
## ============================================================================

const BALANCE_PATH := "res://Resources/Config/Balance.tres"
## 关卡目录（与 LevelSelect 一致；LevelTemplate 不算可玩关）
const LEVELS_DIR := "res://Scenes/Levels"

enum State { BOOT, MENU, PLAYING }

var state: State = State.BOOT

## 全局数值配置（只读使用）
var balance: Balance = null

## 当前运行的关卡（由 LevelSelect 写入；用于重开/结算返回选关）
var current_level_scene := ""
## 选关界面路径（结算"回选关"用）
var level_select_scene := "res://Scenes/UI/LevelSelect.tscn"
## 主菜单路径
var main_menu_scene := "res://Scenes/UI/MainMenu.tscn"

# ── 随机关卡（S7）─────────────────────────────────────────────
## 生成模式：关卡由 LevelSpec 运行时重建，不读关卡场景里的手编内容。
const GENERATED_LEVEL_SCENE := "res://Scenes/Levels/LevelTemplate.tscn"
const GENERATION_SERVICE_SCRIPT := "res://Scripts/Algorithms/Generation/GenerationService.cs"

var generated_mode := false
var generated_seed := 0
var generated_tier := "normal"
var generated_spec_json := ""
var generated_route := ""
var generated_score := 0
var generated_score_tier := ""
var generated_steps := 0
var generated_tags: Array = []
var generated_index := 0
var generated_base_tier := "normal"
var generated_obstacle_model := "poisson"
var generated_generator_model := "route_first"

var _generation: RefCounted = null


func _ready() -> void:
	_load_balance()
	state = State.MENU
	if "--generated-smoke" in OS.get_cmdline_user_args():
		call_deferred("_run_generated_smoke")


func _load_balance() -> void:
	balance = load(BALANCE_PATH) as Balance
	if balance == null:
		push_error("GameManager: 无法加载 Balance.tres (%s)" % BALANCE_PATH)
	# Balance.tres 为静态配置，运行时禁止修改实例


# ── 关卡入口 ───────────────────────────────────────────────────
## LevelSelect 调用：进入指定关卡场景文件
func start_level(scene_path: String) -> void:
	current_level_scene = scene_path
	state = State.PLAYING
	change_scene(scene_path)


func restart_level() -> void:
	# 经选关进入时重开指定关卡；若直接 F6 运行某关卡场景（无记录），
	# 回退为重载当前场景自身，保证编辑器调试时按钮也可用。
	var target := current_level_scene
	if target == "":
		var cs := get_tree().current_scene
		if cs != null and cs.scene_file_path != "":
			target = cs.scene_file_path
	if target != "":
		change_scene(target)


# ── 随机关卡（S7）─────────────────────────────────────────────
## 主菜单入口：按所选难度/障碍模型/生成模型起步，接力继续沿难度阶梯升档。
func start_generated_mode(tier: String = "normal", model: String = "", generator: String = "") -> void:
	generated_base_tier = tier if tier in ["easy", "normal", "hard", "expert"] else "normal"
	if not model.is_empty():
		generated_obstacle_model = model
	if not generator.is_empty():
		generated_generator_model = generator
	generated_index = 0
	start_generated_level(_random_seed(), generated_base_tier)


## 生成并进入一张随机关卡；失败时最多向前重试 3 个种子，仍失败则回主菜单。
## reset_progress=true 表示新一轮（主菜单指定种子），会重置接力进度与基准难度。
func start_generated_level(seed_value: int, tier: String = "", reset_progress: bool = false,
		model: String = "", generator: String = "") -> void:
	generated_mode = true
	generated_seed = seed_value
	if not model.is_empty():
		generated_obstacle_model = model
	if not generator.is_empty():
		generated_generator_model = generator
	if not tier.is_empty():
		generated_tier = tier
	if reset_progress:
		generated_index = 0
		generated_base_tier = generated_tier
	_ensure_generation_service()

	var data := _request_generated(generated_seed, generated_tier)
	var apply_floor := not reset_progress
	if not _generated_acceptable(data, generated_tier, apply_floor):
		for offset in range(1, 6):
			var retry_seed := seed_value + offset
			var retry := _request_generated(retry_seed, generated_tier)
			if _generated_acceptable(retry, generated_tier, apply_floor):
				generated_seed = retry_seed
				data = retry
				break
	if not _generated_acceptable(data, generated_tier, apply_floor):
		push_error("GameManager: 随机关卡生成失败（seed %d / %s）" % [seed_value, generated_tier])
		generated_mode = false
		back_to_main_menu()
		return

	generated_spec_json = str(data.get("spec_json", ""))
	generated_route = str(data.get("route", ""))
	generated_score = int(data.get("score", 0))
	generated_score_tier = str(data.get("score_tier", ""))
	if generated_score_tier.is_empty():
		generated_score_tier = _score_tier(generated_score)
	generated_steps = int(data.get("steps", 0))
	generated_obstacle_model = str(data.get("obstacle_model", generated_obstacle_model))
	generated_generator_model = str(data.get("generator_model", generated_generator_model))
	generated_tags = data.get("tags", [])
	if typeof(generated_tags) != TYPE_ARRAY:
		generated_tags = []
	print("[Generated] seed=%d tier=%s model=%s/%s score=%d(%s) steps=%d" % [
		generated_seed, generated_tier, generated_generator_model, generated_obstacle_model,
		generated_score, generated_score_tier, generated_steps])
	current_level_scene = GENERATED_LEVEL_SCENE
	state = State.PLAYING
	change_scene(GENERATED_LEVEL_SCENE)


func _ensure_generation_service() -> void:
	if _generation != null:
		return
	_generation = load(GENERATION_SERVICE_SCRIPT).new()
	_generation.call("SetupCache", "", 50)


func _request_generated(seed_value: int, tier: String) -> Dictionary:
	_generation.call("SetObstacleModel", generated_obstacle_model)
	_generation.call("SetGeneratorModel", generated_generator_model)
	var json := str(_generation.call("GenerateNow", seed_value, tier, "loading"))
	if json.is_empty():
		return {}
	var parsed: Variant = JSON.parse_string(json)
	return parsed if typeof(parsed) == TYPE_DICTIONARY else {}


## 障碍模型的中文名（HUD 显示用）。
func generated_model_label() -> String:
	return {
		"blue_noise": "蓝噪声",
		"noise_cluster": "噪声簇",
		"wall_growth": "贴墙生长",
	}.get(generated_obstacle_model, "Poisson 簇")


## 生成模型的中文名（HUD 显示用）。
func generated_generator_label() -> String:
	return "黏菌枝干" if generated_generator_model == "physarum" else "经典路线"


## 原始分刻度 → 难度档（与 C# BranchScoreSchemes 保持一致）。
func _score_tier(score: int) -> String:
	if score < 35:
		return "Easy"
	return "Normal" if score < 48 else "Hard" if score < 60 else "Expert"


func generated_score_tier_label() -> String:
	return generated_score_tier if not generated_score_tier.is_empty() else _score_tier(generated_score)


func _generated_ok(data: Dictionary) -> bool:
	return not data.is_empty() and bool(data.get("success", false)) \
			and not str(data.get("spec_json", "")).is_empty()


## 随机接力时按档位设置分数下限，把左尾低分图挡掉，让分布集中在 Hard 附近。
func _score_floor(tier: String) -> int:
	return {"normal": 30, "hard": 45, "expert": 48}.get(tier, 0)


func _generated_acceptable(data: Dictionary, tier: String, apply_floor: bool) -> bool:
	if not _generated_ok(data):
		return false
	if not apply_floor:
		return true
	var score := int(data.get("score", 0))
	var floor_score := _score_floor(tier)
	if score < floor_score:
		print("[Generated] rejected seed=%d score=%d < floor=%d (%s)" % [
			int(data.get("seed", 0)), score, floor_score, tier])
		return false
	return true


func _random_seed() -> int:
	return int(Time.get_ticks_usec() % 1_000_000_000) + 1


## 接力难度曲线：以起始档为基准，每 3 关升一档（最高 expert）。
func _tier_for_generated_index(index: int) -> String:
	var order := ["easy", "normal", "hard", "expert"]
	var base := order.find(generated_base_tier)
	if base < 0:
		base = 1
	var step := 0
	if index >= 13:
		step = 3
	elif index >= 8:
		step = 2
	elif index >= 3:
		step = 1
	return order[mini(base + step, 3)]


func _run_generated_smoke() -> void:
	# 可选参数：--generator=physarum、--model=wall_growth、--tier=expert
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with("--generator="):
			generated_generator_model = arg.trim_prefix("--generator=")
		elif arg.begins_with("--model="):
			generated_obstacle_model = arg.trim_prefix("--model=")
		elif arg.begins_with("--tier="):
			generated_tier = arg.trim_prefix("--tier=")
	start_generated_level(20261125, generated_tier, true,
		generated_obstacle_model, generated_generator_model)
	for index in range(4):
		await get_tree().process_frame
		await get_tree().process_frame
		var scene := get_tree().current_scene
		var level_state: Variant = scene.get("state") if scene != null else null
		if level_state == null:
			push_error("[Smoke] 第 %d 关没有 LevelState" % index)
			get_tree().quit(1)
			return
		var accepted_all := true
		for ch in generated_route:
			var dir := Vector2i.ZERO
			match ch:
				"A": dir = Vector2i.LEFT
				"D": dir = Vector2i.RIGHT
				"W": dir = Vector2i.UP
				"S": dir = Vector2i.DOWN
			var res: Variant = level_state.call("step", dir)
			if not bool(res.accepted):
				accepted_all = false
				break
		var won: bool = accepted_all and level_state.get("head") == level_state.get("goal_cell")
		print("[Smoke] index=%d seed=%d model=%s/%s score=%d(%s) route_steps=%d accepted=%s won=%s" % [
			index, generated_seed, generated_generator_model, generated_obstacle_model,
			generated_score, generated_score_tier, generated_route.length(), accepted_all, won])
		if not won:
			push_error("[Smoke] 随机关卡规范路线未通关（seed=%d）" % generated_seed)
			get_tree().quit(1)
			return
		if index < 3:
			start_next_level()
	print("[Smoke] done")
	get_tree().quit(0)


func back_to_level_select() -> void:
	generated_mode = false
	generated_spec_json = ""
	state = State.MENU
	change_scene(level_select_scene)


func back_to_main_menu() -> void:
	generated_mode = false
	generated_spec_json = ""
	state = State.MENU
	change_scene(main_menu_scene)


# ── 关卡列表 / 下一关 ─────────────────────────────────────────
## 扫描 Scenes/Levels 下的可玩关卡，按 level_id 升序。每项 = {id, name, path}
## （LevelSelect 列表、"下一关"跳转共用同一来源）。
func list_levels() -> Array[Dictionary]:
	var out: Array[Dictionary] = []
	var dir := DirAccess.open(LEVELS_DIR)
	if dir == null:
		return out
	dir.list_dir_begin()
	var fname := dir.get_next()
	while fname != "":
		if not dir.current_is_dir():
			# 导出后 PCK 内的场景以 "<名>.tscn.remap" 条目列出（编辑器为原始名），
			# 统一剥掉 .remap 再过滤，保证导出包里也能扫到关卡。
			var scene_name := fname.trim_suffix(".remap")
			if scene_name.begins_with("Level_") and scene_name.ends_with(".tscn") \
					and scene_name != "LevelTemplate.tscn":
				var path := LEVELS_DIR.path_join(scene_name)
				var ps := load(path) as PackedScene
				if ps != null:
					var root := ps.instantiate()
					var id := int(root.get("level_id")) if root.get("level_id") != null else 0
					var display_name := str(root.get("level_name")) if root.get("level_name") != null else ""
					root.free()
					if display_name.is_empty():
						display_name = scene_name.get_basename().trim_prefix("Level_")
					out.append({"id": id, "name": display_name, "path": path})
		fname = dir.get_next()
	dir.list_dir_end()
	out.sort_custom(func(a, b): return a.id < b.id)
	return out


## "下一关"：生成模式沿 seed+1 接力并按局数升档；手编关卡走列表下一位。
func start_next_level() -> void:
	if generated_mode:
		generated_index += 1
		start_generated_level(generated_seed + 1, _tier_for_generated_index(generated_index))
		return
	var levels := list_levels()
	if levels.is_empty():
		back_to_level_select()
		return
	var cur := ""
	var cs := get_tree().current_scene
	if cs != null:
		cur = cs.scene_file_path
	var idx := -1
	for i in levels.size():
		if levels[i].path == cur:
			idx = i
			break
	if idx >= 0 and idx < levels.size() - 1:
		start_level(levels[idx + 1].path)
	else:
		back_to_level_select()


# ── 场景流转 ───────────────────────────────────────────────────
func change_scene(target_path: String) -> void:
	get_tree().change_scene_to_file(target_path)
