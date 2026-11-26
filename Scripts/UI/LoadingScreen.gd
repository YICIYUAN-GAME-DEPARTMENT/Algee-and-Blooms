extends Control
## ============================================================================
## LoadingScreen — 随机关卡生成加载屏
## 分帧驱动 GenerationService（每帧 ~8ms），显示进度条 / 当前阶段 / 尝试次数，
## 同时提供一个「点亮花瓣」小游戏打发等待时间。
## 布局全部在场景树定义，脚本只更新数值 / 文本 / 颜色。
## ============================================================================

const POLL_BUDGET_MS := 4.0
const MINI_INTERVAL := 0.85
## 小游戏点亮项使用机关核心贴图（与关卡内机关一致）
const CORE_TEXTURE := preload("res://Art/Sprites/core.png")

@onready var _status: Label = $Center/Panel/Content/Status
@onready var _progress: ProgressBar = $Center/Panel/Content/Progress
@onready var _percent: Label = $Center/Panel/Content/Percent
@onready var _mini_score: Label = $Center/Panel/Content/MiniScore
@onready var _back: Button = $Center/Panel/Content/Buttons/BackButton
@onready var _mini_timer: Timer = $MiniTimer
@onready var _petals: Array[Button] = [
	$Center/Panel/Content/MiniGrid/Petal0,
	$Center/Panel/Content/MiniGrid/Petal1,
	$Center/Panel/Content/MiniGrid/Petal2,
	$Center/Panel/Content/MiniGrid/Petal3,
	$Center/Panel/Content/MiniGrid/Petal4,
	$Center/Panel/Content/MiniGrid/Petal5,
	$Center/Panel/Content/MiniGrid/Petal6,
	$Center/Panel/Content/MiniGrid/Petal7,
	$Center/Panel/Content/MiniGrid/Petal8,
]

var _active := -1
var _score := 0
var _done := false


func _ready() -> void:
	get_tree().paused = false
	_back.pressed.connect(_on_back)
	_mini_timer.wait_time = MINI_INTERVAL
	_mini_timer.timeout.connect(_on_mini_tick)
	for i in _petals.size():
		_petals[i].pressed.connect(_on_petal_pressed.bind(i))
		_petals[i].text = "·"
		_petals[i].icon = null
		_petals[i].add_theme_color_override("font_color", Color(0.68, 0.75, 0.66, 0.7))
	_mini_timer.start()
	_start_flow()


func _exit_tree() -> void:
	get_tree().paused = false


func _on_back() -> void:
	if _done:
		return
	_done = true
	GameManager.generation_service().call("Cancel")
	GameManager.back_to_main_menu()


## 主流程：生成在后台线程执行，这里每帧只轮询进度；成功后交给 GameManager 切关卡。
func _start_flow() -> void:
	var ctx := GameManager.loading_context()
	var service := GameManager.generation_service()
	var retries := int(ctx.get("retries", 0))
	var tier := str(ctx.get("tier", "normal"))
	var apply_floor := bool(ctx.get("apply_floor", false))
	var base_seed := int(ctx.get("seed", 0))
	service.call("SetLevelIndex", int(ctx.get("index", 0)))
	service.call("SetObstacleModel", str(ctx.get("obstacle_model", "poisson")))
	service.call("SetGeneratorModel", str(ctx.get("generator_model", "template")))

	for retry in range(retries + 1):
		if _done:
			return
		var seed_value := base_seed + retry
		_status.text = "正在生成花海…" if retry == 0 else "换一个种子重试（%d/%d）…" % [retry, retries]
		var finished := bool(service.call("Begin", seed_value, tier, "loading"))
		while not finished:
			await get_tree().process_frame
			if _done:
				return
			finished = bool(service.call("Advance", POLL_BUDGET_MS))
			_refresh_progress(str(service.call("ProgressJson")))
		_refresh_progress(str(service.call("ProgressJson")))

		var parsed: Variant = JSON.parse_string(str(service.call("ResultJson")))
		if typeof(parsed) == TYPE_DICTIONARY:
			var data: Dictionary = parsed
			if GameManager.generated_data_acceptable(data, tier, apply_floor):
				_done = true
				GameManager.apply_generated_result(data, seed_value)
				return
		_progress.value = 0.0
		_percent.text = "0%"

	GameManager.fail_generated("加载阶段未生成可用关卡（seed %d / %s）" % [base_seed, tier])


func _refresh_progress(json: String) -> void:
	if json.is_empty():
		return
	var parsed: Variant = JSON.parse_string(json)
	if typeof(parsed) != TYPE_DICTIONARY:
		return
	var data: Dictionary = parsed
	var percent := clampf(float(data.get("percent", 0.0)), 0.0, 1.0)
	_progress.value = percent * 100.0
	_percent.text = "%d%%" % int(round(percent * 100.0))
	var phase := str(data.get("phase", ""))
	if phase.is_empty():
		return
	if bool(data.get("from_cache", false)):
		_status.text = "缓存命中 · 即刻出发"
	else:
		_status.text = "%s · 第 %d 次尝试" % [phase, int(data.get("attempts", 0))]


# ── 小游戏：点亮花瓣 ────────────────────────────────────────────

func _on_mini_tick() -> void:
	if _done:
		return
	var next := randi() % _petals.size()
	if next == _active:
		next = (next + 1) % _petals.size()
	_set_active(next)


func _set_active(index: int) -> void:
	if _active >= 0 and _active < _petals.size():
		_petals[_active].text = "·"
		_petals[_active].icon = null
		_petals[_active].add_theme_color_override("font_color", Color(0.68, 0.75, 0.66, 0.7))
	_active = index
	if _active >= 0 and _active < _petals.size():
		_petals[_active].text = ""
		_petals[_active].icon = CORE_TEXTURE
		_petals[_active].expand_icon = true


func _on_petal_pressed(index: int) -> void:
	if _done or index != _active:
		return
	_score += 1
	_mini_score.text = "顺手点花：%d 朵" % _score
	_set_active(-1)
	_mini_timer.start()
