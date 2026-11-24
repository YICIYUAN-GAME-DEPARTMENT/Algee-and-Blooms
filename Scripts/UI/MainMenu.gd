extends Control
## ============================================================================
## MainMenu — 开始界面（场景树布局；脚本只接线）
## ============================================================================

@onready var _start: Button = $MenuFrame/Margin/Content/StartButton
@onready var _random: Button = $MenuFrame/Margin/Content/RandomButton
@onready var _seed_input: LineEdit = $MenuFrame/Margin/Content/SeedRow/SeedInput
@onready var _seed_start: Button = $MenuFrame/Margin/Content/SeedRow/SeedStartButton
@onready var _tier_option: OptionButton = $MenuFrame/Margin/Content/TierRow/TierOption
@onready var _model_option: OptionButton = $MenuFrame/Margin/Content/ModelRow/ModelOption
@onready var _generator_option: OptionButton = $MenuFrame/Margin/Content/ModelRow/GeneratorOption
@onready var _settings: Button = $MenuFrame/Margin/Content/SettingsButton
@onready var _quit: Button = $MenuFrame/Margin/Content/QuitButton
@onready var _settings_overlay: Control = $SettingsOverlay
@onready var _fullscreen: Button = $SettingsOverlay/Center/Panel/Content/FullscreenButton
@onready var _settings_back: Button = $SettingsOverlay/Center/Panel/Content/BackButton


const TIER_VALUES := ["easy", "normal", "hard", "expert"]
const MODEL_LABELS := ["蓝噪声", "噪声簇", "Poisson 簇", "贴墙生长"]
const MODEL_VALUES := ["blue_noise", "noise_cluster", "poisson", "wall_growth"]
const GENERATOR_LABELS := ["经典路线", "黏菌枝干"]
const GENERATOR_VALUES := ["route_first", "physarum"]
const TIER_LABELS := ["简单", "普通", "困难", "专家"]


func _ready() -> void:
	for i in TIER_LABELS.size():
		_tier_option.add_item(TIER_LABELS[i])
	_tier_option.select(2)
	for i in MODEL_LABELS.size():
		_model_option.add_item(MODEL_LABELS[i])
	_model_option.select(2)
	for i in GENERATOR_LABELS.size():
		_generator_option.add_item(GENERATOR_LABELS[i])
	_generator_option.select(1)
	_start.pressed.connect(_on_start)
	_random.pressed.connect(_on_random)
	_seed_start.pressed.connect(_on_seed_start)
	_settings.pressed.connect(_open_settings)
	_quit.pressed.connect(_on_quit)
	_fullscreen.pressed.connect(_toggle_fullscreen)
	_settings_back.pressed.connect(_close_settings)
	_refresh_fullscreen_text()
	_start.grab_focus()


func _selected_tier() -> String:
	var index := _tier_option.selected
	return TIER_VALUES[index] if index >= 0 and index < TIER_VALUES.size() else "normal"


func _selected_model() -> String:
	var index := _model_option.selected
	return MODEL_VALUES[index] if index >= 0 and index < MODEL_VALUES.size() else "poisson"


func _selected_generator() -> String:
	var index := _generator_option.selected
	return GENERATOR_VALUES[index] if index >= 0 and index < GENERATOR_VALUES.size() else "route_first"


func _unhandled_input(event: InputEvent) -> void:
	if _settings_overlay.visible and event.is_action_pressed("ui_cancel") and not event.is_echo():
		_close_settings()
		get_viewport().set_input_as_handled()


func _on_start() -> void:
	GameManager.change_scene(GameManager.level_select_scene)


func _on_random() -> void:
	GameManager.start_generated_mode(_selected_tier(), _selected_model(), _selected_generator())


## 按输入种子进入随机关卡（复现同一关）；非法输入回退为 1。
func _on_seed_start() -> void:
	var text := _seed_input.text.strip_edges()
	if not text.is_valid_int():
		_seed_input.placeholder_text = "请输入整数种子"
		_seed_input.text = ""
		return
	var seed_value := int(text)
	GameManager.start_generated_level(maxi(seed_value, 1), _selected_tier(), true,
		_selected_model(), _selected_generator())


func _open_settings() -> void:
	_settings_overlay.visible = true
	_refresh_fullscreen_text()
	_fullscreen.grab_focus()


func _close_settings() -> void:
	_settings_overlay.visible = false
	_settings.grab_focus()


func _toggle_fullscreen() -> void:
	var mode := DisplayServer.window_get_mode()
	var fullscreen := mode == DisplayServer.WINDOW_MODE_FULLSCREEN \
			or mode == DisplayServer.WINDOW_MODE_EXCLUSIVE_FULLSCREEN
	DisplayServer.window_set_mode(
		DisplayServer.WINDOW_MODE_WINDOWED if fullscreen else DisplayServer.WINDOW_MODE_FULLSCREEN
	)
	_refresh_fullscreen_text()


func _refresh_fullscreen_text() -> void:
	var mode := DisplayServer.window_get_mode()
	var fullscreen := mode == DisplayServer.WINDOW_MODE_FULLSCREEN \
			or mode == DisplayServer.WINDOW_MODE_EXCLUSIVE_FULLSCREEN
	_fullscreen.text = "全屏: 开" if fullscreen else "全屏: 关"


func _on_quit() -> void:
	get_tree().quit()
