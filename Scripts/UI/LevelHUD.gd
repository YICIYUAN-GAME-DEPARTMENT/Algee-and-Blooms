class_name LevelHUD
extends CanvasLayer
## ============================================================================
## LevelHUD — 局内顶栏 + 结算覆盖（全部场景树布局；脚本只更新数值/显隐）
## ============================================================================

signal hint_toggled
signal rating_submitted(score: int, confidence: String, notes: String)

@onready var _remaining_steps_value: Label = $Root/TopArea/StatsPanel/StatsRow/RemainingBlock/RemainingValue
@onready var _length_value: Label = $Root/TopArea/StatsPanel/StatsRow/LengthBlock/LengthValue
@onready var _petal_value: Label = $Root/TopArea/StatsPanel/StatsRow/PetalBlock/PetalValue
@onready var _petal_block: VBoxContainer = $Root/TopArea/StatsPanel/StatsRow/PetalBlock
@onready var _grow_block: VBoxContainer = $Root/TopArea/StatsPanel/StatsRow/GrowBlock
@onready var _grow_value: Label = $Root/TopArea/StatsPanel/StatsRow/GrowBlock/GrowValue
@onready var _mech_block: VBoxContainer = $Root/TopArea/StatsPanel/StatsRow/MechBlock
@onready var _mech_value: Label = $Root/TopArea/StatsPanel/StatsRow/MechBlock/MechValue
@onready var _level_title: Label = $Root/TopArea/LevelTitle
@onready var _overlay: Control = $Root/Overlay
@onready var _overlay_text: Label = $Root/Overlay/Center/Panel/Content/Text
@onready var _restart: Button = $Root/TopArea/ActionsPanel/Actions/RestartBtn
@onready var _back: Button = $Root/TopArea/ActionsPanel/Actions/BackBtn
@onready var _seed_btn: Button = $Root/SeedBtn
@onready var _difficulty_label: Label = $Root/DifficultyLabel
@onready var _tag_list: Label = $Root/TagList
@onready var _overlay_restart: Button = $Root/Overlay/Center/Panel/Content/RestartBtn
@onready var _overlay_back: Button = $Root/Overlay/Center/Panel/Content/BackBtn
@onready var _next: Button = $Root/Overlay/Center/Panel/Content/NextBtn
@onready var _pause_overlay: Control = $Root/PauseOverlay
@onready var _pause_continue: Button = $Root/PauseOverlay/Center/Panel/Content/ContinueButton
@onready var _pause_restart: Button = $Root/PauseOverlay/Center/Panel/Content/RestartButton
@onready var _pause_exit: Button = $Root/PauseOverlay/Center/Panel/Content/ExitButton
@onready var _hint_btn: Button = $Root/TopArea/ActionsPanel/Actions/HintBtn
@onready var _rating_panel: VBoxContainer = $Root/Overlay/Center/Panel/Content/RatingPanel
@onready var _rating_option: OptionButton = $Root/Overlay/Center/Panel/Content/RatingPanel/RatingOption
@onready var _rating_note: LineEdit = $Root/Overlay/Center/Panel/Content/RatingPanel/RatingNote
@onready var _rating_buttons: HBoxContainer = $Root/Overlay/Center/Panel/Content/RatingPanel/RatingButtons
@onready var _rating_confirm: Button = $Root/Overlay/Center/Panel/Content/RatingPanel/RatingButtons/RatingConfirm
@onready var _rating_skip: Button = $Root/Overlay/Center/Panel/Content/RatingPanel/RatingButtons/RatingSkip
@onready var _rating_result: Label = $Root/Overlay/Center/Panel/Content/RatingPanel/RatingResult
@onready var _next_config: VBoxContainer = $Root/Overlay/Center/Panel/Content/NextConfig
@onready var _next_tier_option: OptionButton = $Root/Overlay/Center/Panel/Content/NextConfig/TierRow/NextTierOption
@onready var _next_seed_input: LineEdit = $Root/Overlay/Center/Panel/Content/NextConfig/SeedRow/NextSeedInput
@onready var _pause_next: Button = $Root/PauseOverlay/Center/Panel/Content/NextButton
@onready var _pause_settings: Button = $Root/PauseOverlay/Center/Panel/Content/SettingsButton
@onready var _pause_config: VBoxContainer = $Root/PauseOverlay/Center/Panel/Content/PauseNextConfig
@onready var _pause_tier_option: OptionButton = $Root/PauseOverlay/Center/Panel/Content/PauseNextConfig/TierRow/PauseTierOption
@onready var _pause_seed_input: LineEdit = $Root/PauseOverlay/Center/Panel/Content/PauseNextConfig/SeedRow/PauseSeedInput
@onready var _pause_btn: Button = $Root/TopArea/ActionsPanel/Actions/PauseBtn
@onready var _settings: Control = $SettingsOverlay

## 剧情演出期间由 Level 置位：忽略 Esc（交给剧情层做"跳过"），防误开暂停菜单
var dialogue_active := false

const TAG_LABELS := {
	"false_shortcut": "假捷径",
	"gate_sequence": "机关时序",
	"forced_truncation": "强制截断",
	"overshoot_return": "诱导回折",
	"mirror_fork": "镜像岔路",
	"mutual_exclusion": "互斥诱饵",
	"body_key": "身长钥匙",
	"last_gasp": "最后一刻",
	"tight_budget": "长度紧张",
	"no_mechanism": "无机关",
	"dense_maze": "密集迷宫",
}

const TIER_LABELS := {
	"easy": "简单",
	"normal": "普通",
	"hard": "困难",
	"expert": "专家",
}

## 下一关难度选择（顺序与 GameManager 的档位一致）
const TIER_CHOICES := ["easy", "normal", "hard", "expert"]
const TIER_CHOICE_LABELS := ["简单", "普通", "困难", "专家"]

var _generated_seed := 0
var _rating_algorithm_score := 0
var _rating_generation_mode := ""
var _rating_template_id := ""


func _ready() -> void:
	_restart.pressed.connect(_restart_level)
	_back.pressed.connect(_exit_level)
	_seed_btn.pressed.connect(_copy_generated_seed)
	_seed_btn.visible = false
	_difficulty_label.visible = false
	_tag_list.visible = false
	_overlay_restart.pressed.connect(_restart_level)
	_overlay_back.pressed.connect(_exit_level)
	_next.pressed.connect(_next_level_from_result)
	_pause_continue.pressed.connect(_close_pause_menu)
	_pause_restart.pressed.connect(_restart_level)
	_pause_next.pressed.connect(_next_level_from_pause)
	_pause_settings.pressed.connect(_open_settings)
	_pause_exit.pressed.connect(_exit_level)
	_pause_btn.pressed.connect(_open_pause_menu)
	_settings.connect("closed", _on_settings_closed)
	_hint_btn.pressed.connect(_on_hint_pressed)
	_rating_option.item_selected.connect(_on_rating_selected)
	_rating_confirm.pressed.connect(_on_rating_confirm)
	_rating_skip.pressed.connect(_on_rating_skip)
	for i in TIER_CHOICES.size():
		_next_tier_option.add_item(TIER_CHOICE_LABELS[i])
		_pause_tier_option.add_item(TIER_CHOICE_LABELS[i])
	_next_config.visible = false
	_pause_config.visible = false
	_setup_rating_options()
	hide_result()
	_close_pause_menu()


func _input(event: InputEvent) -> void:
	if _settings.visible or get_viewport().is_input_handled():
		return
	if dialogue_active:
		return
	if event.is_action_pressed("pause") and not event.is_echo() and not _overlay.visible:
		if _pause_overlay.visible:
			_close_pause_menu()
		else:
			_open_pause_menu()
		get_viewport().set_input_as_handled()


## 结算 / 暂停 / 设置任一面板打开时，关卡层应忽略 R 等快捷操作。
func is_modal_open() -> bool:
	return _overlay.visible or _pause_overlay.visible or _settings.visible


func update_remaining_steps(remaining_steps: int) -> void:
	_remaining_steps_value.text = str(remaining_steps)


## 花瓣提供的步长：每片食物给最大身长增加的格数（Level 解析 override/全局后传入）
func set_petal_gain(gain: int) -> void:
	_petal_value.text = str(gain)


## 本关没有花瓣时隐藏该信息块（避免显示无意义的全局占位值）
func set_petal_visible(visible_now: bool) -> void:
	_petal_block.visible = visible_now


## 当前身长 / 身长上限
func set_length(current: int, maximum: int) -> void:
	_length_value.text = "%d/%d" % [current, maximum]


## 花丛当前最高阶段 / 最高上限（负阶段按 0 显示）
func set_mech_status(current_level: int, cap: int) -> void:
	_mech_value.text = "%d/%d" % [maxi(current_level, 0), cap]


## 花丛（机关）距下次生长的剩余有效步数；每步后由 Level 传入
func set_growth_countdown(steps_left: int) -> void:
	_grow_value.text = str(steps_left)


## 本关无机关时隐藏生长倒计时与花丛成长
func set_growth_visible(visible_now: bool) -> void:
	_grow_block.visible = visible_now
	_mech_block.visible = visible_now


## 顶栏关卡标题：第 N 关 · 关卡名（N = 场景 Level 根节点 level_id）
func set_level_title(title: String) -> void:
	_level_title.text = title


## 随机关卡：显示"复制种子"按钮并记录当前种子。
func set_generated_seed(seed_value: int) -> void:
	_generated_seed = seed_value
	_seed_btn.visible = seed_value > 0
	_seed_btn.text = "复制种子"


## 随机关卡：显示实测难度分（原始刻度 + 难度档）、真实最短步数、生成模型与障碍模型。
## mode = template / fallback_physarum / fallback：降级结果用暖色提示，避免通关后才发现。
## requested_tier = 玩家请求的档位；与实测档不一致时明确显示"请求X"。
func set_difficulty(score: int, steps: int, model: String = "", generator: String = "",
		tier: String = "", mode: String = "", requested_tier: String = "") -> void:
	var text := "难度 %d" % score
	if not tier.is_empty():
		text += " (%s)" % tier
	text += " · %d 步" % steps
	if not generator.is_empty():
		text += " · " + generator
	if not model.is_empty():
		text += " / " + model
	if not requested_tier.is_empty() and requested_tier != tier.to_lower():
		text += " · 请求%s" % TIER_LABELS.get(requested_tier, requested_tier)
	_difficulty_label.text = text
	_difficulty_label.visible = score > 0
	if mode.begins_with("fallback"):
		_difficulty_label.add_theme_color_override("font_color", Color(1.0, 0.78, 0.45, 1.0))
	else:
		_difficulty_label.remove_theme_color_override("font_color")


## 随机关卡：左上角纵向列出事后标签（每行一个）。
func set_tags(tags: Array) -> void:
	var lines: Array[String] = []
	for tag in tags:
		var key := str(tag)
		lines.append(TAG_LABELS.get(key, key))
	_tag_list.text = "\n".join(lines)
	_tag_list.visible = not lines.is_empty()


func _copy_generated_seed() -> void:
	if _generated_seed <= 0:
		return
	DisplayServer.clipboard_set(str(_generated_seed))
	_seed_btn.text = "已复制"
	await get_tree().create_timer(1.2).timeout
	if is_instance_valid(_seed_btn):
		_seed_btn.text = "复制种子"


## 结算面板：victory=true 时显示"下一关"按钮；失败隐藏。
## 若是最后一关，"下一关"同样可用——GameManager 会返回选关界面。
func show_result(text: String, victory := false) -> void:
	_close_pause_menu()
	_overlay_text.text = text
	_next.visible = victory
	if victory:
		_refresh_next_config()
	else:
		_next_config.visible = false
	_reset_rating_panel()
	_overlay.visible = true
	if victory:
		_next.grab_focus()
	else:
		_overlay_restart.grab_focus()


func hide_result() -> void:
	_overlay.visible = false
	_rating_panel.visible = false


func set_hint_available(value: bool) -> void:
	_hint_btn.disabled = not value
	if not value:
		_hint_btn.text = "提示"


func set_hint_active(value: bool) -> void:
	_hint_btn.text = "隐藏提示" if value else "提示"


func set_rating_context(algorithm_score: int, generation_mode: String, template_id: String) -> void:
	_rating_algorithm_score = algorithm_score
	_rating_generation_mode = generation_mode
	_rating_template_id = template_id


func set_rating_recorded(score: int, algorithm_score: int, generation_mode: String) -> void:
	_rating_result.visible = true
	if score < 0:
		_rating_result.text = "已跳过"
	elif algorithm_score > 0:
		_rating_result.text = "已记录：你的 %d / 算法 %d / %s" % [score, algorithm_score, generation_mode]
	else:
		_rating_result.text = "已记录：你的 %d" % score


func _on_hint_pressed() -> void:
	hint_toggled.emit()


func _setup_rating_options() -> void:
	_rating_option.clear()
	_rating_option.add_item("请选择")
	for v in range(0, 101, 10):
		_rating_option.add_item(str(v))
	_rating_option.selected = 0
	_rating_confirm.disabled = true


func _reset_rating_panel() -> void:
	_rating_panel.visible = true
	_rating_option.visible = true
	_rating_note.visible = true
	_rating_buttons.visible = true
	_rating_result.visible = false
	_rating_option.disabled = false
	_rating_note.editable = true
	_rating_confirm.disabled = true
	_rating_skip.disabled = false
	_rating_option.selected = 0
	_rating_note.text = ""


func _on_rating_selected(index: int) -> void:
	_rating_confirm.disabled = index <= 0


func _on_rating_confirm() -> void:
	var idx := _rating_option.selected
	if idx <= 0:
		return
	var score := (idx - 1) * 10
	_disable_rating_controls()
	_rating_result.visible = true
	_rating_result.text = "已记录：你的 %d" % score
	rating_submitted.emit(score, "medium", _rating_note.text)


func _on_rating_skip() -> void:
	_disable_rating_controls()
	_rating_result.visible = true
	_rating_result.text = "已跳过"
	rating_submitted.emit(-1, "low", "skipped")


func _disable_rating_controls() -> void:
	_rating_option.disabled = true
	_rating_note.editable = false
	_rating_confirm.disabled = true
	_rating_skip.disabled = true


func set_dialogue_active(value: bool) -> void:
	dialogue_active = value


func _open_pause_menu() -> void:
	_refresh_next_config()
	_pause_overlay.visible = true
	get_tree().paused = true
	_pause_continue.grab_focus()


func _open_settings() -> void:
	_settings.call("open")


func _on_settings_closed() -> void:
	_pause_continue.grab_focus()


func _close_pause_menu() -> void:
	get_tree().paused = false
	_pause_overlay.visible = false
	# 关闭菜单后释放按钮焦点，避免方向键继续在按钮间切换而不是移动玩家。
	var vp := get_viewport()
	if vp != null:
		vp.gui_release_focus()


func _restart_level() -> void:
	_close_pause_menu()
	GameManager.restart_level()


func _exit_level() -> void:
	_close_pause_menu()
	GameManager.back_to_level_select()


## 结算面板"下一关"：读取结算页的难度 / 种子选择（手编关卡忽略）
func _next_level_from_result() -> void:
	_start_next(_selected_tier(_next_tier_option), _seed_value(_next_seed_input))


## 暂停菜单"下一关"：读取暂停页的难度 / 种子选择（手编关卡忽略）
func _next_level_from_pause() -> void:
	_start_next(_selected_tier(_pause_tier_option), _seed_value(_pause_seed_input))


func _start_next(tier: String, seed_value: int) -> void:
	_close_pause_menu()
	GameManager.start_next_level_with(tier, seed_value)


## 下一关选项：随机关卡显示并默认当前档位；手编关卡隐藏（按列表下一位）。
func _refresh_next_config() -> void:
	var tier_index := TIER_CHOICES.find(GameManager.generated_tier)
	if tier_index < 0:
		tier_index = 1
	_next_tier_option.select(tier_index)
	_pause_tier_option.select(tier_index)
	_next_seed_input.text = ""
	_pause_seed_input.text = ""
	_next_config.visible = GameManager.generated_mode
	_pause_config.visible = GameManager.generated_mode


func _selected_tier(option: OptionButton) -> String:
	var index := option.selected
	return TIER_CHOICES[index] if index >= 0 and index < TIER_CHOICES.size() else ""


## 种子输入：空 / 非整数 / 非正数 = 随机（0）
func _seed_value(input: LineEdit) -> int:
	var text := input.text.strip_edges()
	if not text.is_valid_int():
		return 0
	return maxi(int(text), 0)


func _exit_tree() -> void:
	get_tree().paused = false
