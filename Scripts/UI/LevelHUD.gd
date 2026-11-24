class_name LevelHUD
extends CanvasLayer
## ============================================================================
## LevelHUD — 局内顶栏 + 结算覆盖（全部场景树布局；脚本只更新数值/显隐）
## ============================================================================

@onready var _remaining_steps_value: Label = $Root/TopArea/HBoxContainer/RemainingCard/RemainingValue
@onready var _petal_value: Label = $Root/TopArea/HBoxContainer/PetalCard/PetalValue
@onready var _grow_label: Label = $Root/TopArea/HBoxContainer/GrowLabel
@onready var _grow_card: PanelContainer = $Root/TopArea/HBoxContainer/GrowCard
@onready var _grow_value: Label = $Root/TopArea/HBoxContainer/GrowCard/GrowValue
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

var _generated_seed := 0


func _ready() -> void:
	_restart.pressed.connect(_restart_level)
	_back.pressed.connect(_exit_level)
	_seed_btn.pressed.connect(_copy_generated_seed)
	_seed_btn.visible = false
	_difficulty_label.visible = false
	_tag_list.visible = false
	_overlay_restart.pressed.connect(_restart_level)
	_overlay_back.pressed.connect(_exit_level)
	_next.pressed.connect(_next_level)
	_pause_continue.pressed.connect(_close_pause_menu)
	_pause_restart.pressed.connect(_restart_level)
	_pause_exit.pressed.connect(_exit_level)
	hide_result()
	_close_pause_menu()


func _input(event: InputEvent) -> void:
	if dialogue_active:
		return
	if event.is_action_pressed("ui_cancel") and not event.is_echo() and not _overlay.visible:
		if _pause_overlay.visible:
			_close_pause_menu()
		else:
			_open_pause_menu()
		get_viewport().set_input_as_handled()


func update_remaining_steps(remaining_steps: int) -> void:
	_remaining_steps_value.text = str(remaining_steps)


## 花瓣提供的步长：每片食物给最大身长增加的格数（Level 解析 override/全局后传入）
func set_petal_gain(gain: int) -> void:
	_petal_value.text = str(gain)


## 花丛（机关）距下次生长的剩余有效步数；每步后由 Level 传入
func set_growth_countdown(steps_left: int) -> void:
	_grow_value.text = str(steps_left)


## 本关无机关时隐藏整个计数项
func set_growth_visible(visible_now: bool) -> void:
	_grow_label.visible = visible_now
	_grow_card.visible = visible_now


## 顶栏关卡标题：第 N 关 · 关卡名（N = 场景 Level 根节点 level_id）
func set_level_title(title: String) -> void:
	_level_title.text = title


## 随机关卡：显示"复制种子"按钮并记录当前种子。
func set_generated_seed(seed_value: int) -> void:
	_generated_seed = seed_value
	_seed_btn.visible = seed_value > 0
	_seed_btn.text = "复制种子"


## 随机关卡：显示实测难度分（原始刻度 + 难度档）、真实最短步数、生成模型与障碍模型。
func set_difficulty(score: int, steps: int, model: String = "", generator: String = "",
		tier: String = "") -> void:
	var text := "难度 %d" % score
	if not tier.is_empty():
		text += " (%s)" % tier
	text += " · %d 步" % steps
	if not generator.is_empty():
		text += " · " + generator
	if not model.is_empty():
		text += " / " + model
	_difficulty_label.text = text
	_difficulty_label.visible = score > 0


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
	_overlay.visible = true
	if victory:
		_next.grab_focus()
	else:
		_overlay_restart.grab_focus()


func hide_result() -> void:
	_overlay.visible = false


func set_dialogue_active(value: bool) -> void:
	dialogue_active = value


func _open_pause_menu() -> void:
	_pause_overlay.visible = true
	get_tree().paused = true
	_pause_continue.grab_focus()


func _close_pause_menu() -> void:
	get_tree().paused = false
	_pause_overlay.visible = false


func _restart_level() -> void:
	_close_pause_menu()
	GameManager.restart_level()


func _exit_level() -> void:
	_close_pause_menu()
	GameManager.back_to_level_select()


## "下一关"：进入关卡列表中当前关的下一位；已是最后一关则回选关界面
func _next_level() -> void:
	_close_pause_menu()
	GameManager.start_next_level()


func _exit_tree() -> void:
	get_tree().paused = false
