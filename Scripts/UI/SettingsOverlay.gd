extends Control
## ============================================================================
## SettingsOverlay — 共享设置面板（画面 / 声音 / 键位）
## ----------------------------------------------------------------------------
## 布局全部在 Scene 定义；本脚本只读写 SettingsManager、处理按键捕获。
## 可被主菜单与暂停菜单复用：open() / close()，关闭时发 closed 信号。
## ============================================================================

signal closed

const KEY_ACTIONS := [
	["move_up", "上"],
	["move_down", "下"],
	["move_left", "左"],
	["move_right", "右"],
	["restart", "重开"],
	["pause", "暂停"],
]

@onready var _tabs: TabContainer = $Center/Panel/Content/Tabs
@onready var _fullscreen: CheckButton = $Center/Panel/Content/Tabs/Video/FullscreenRow/FullscreenToggle
@onready var _vsync: CheckButton = $Center/Panel/Content/Tabs/Video/VsyncRow/VsyncToggle
@onready var _master: HSlider = $Center/Panel/Content/Tabs/Audio/MasterRow/MasterSlider
@onready var _master_value: Label = $Center/Panel/Content/Tabs/Audio/MasterRow/MasterValue
@onready var _sfx: HSlider = $Center/Panel/Content/Tabs/Audio/SfxRow/SfxSlider
@onready var _sfx_value: Label = $Center/Panel/Content/Tabs/Audio/SfxRow/SfxValue
@onready var _key_hint: Label = $Center/Panel/Content/Tabs/Keys/KeyHint
@onready var _reset_keys: Button = $Center/Panel/Content/Tabs/Keys/ResetKeys
@onready var _back: Button = $Center/Panel/Content/Back

var _key_buttons: Dictionary = {}
var _listening_action := ""
var _refreshing := false


func _ready() -> void:
	process_mode = Node.PROCESS_MODE_ALWAYS
	visible = false
	_tabs.set_tab_title(0, "画面")
	_tabs.set_tab_title(1, "声音")
	_tabs.set_tab_title(2, "键位")
	_key_buttons = {
		"move_up": $Center/Panel/Content/Tabs/Keys/KeyGrid/KeyMoveUp,
		"move_down": $Center/Panel/Content/Tabs/Keys/KeyGrid/KeyMoveDown,
		"move_left": $Center/Panel/Content/Tabs/Keys/KeyGrid/KeyMoveLeft,
		"move_right": $Center/Panel/Content/Tabs/Keys/KeyGrid/KeyMoveRight,
		"restart": $Center/Panel/Content/Tabs/Keys/KeyGrid/KeyRestart,
		"pause": $Center/Panel/Content/Tabs/Keys/KeyGrid/KeyPause,
	}
	for action: String in _key_buttons.keys():
		var button := _key_buttons[action] as Button
		button.pressed.connect(_on_rebind_pressed.bind(action))
	_fullscreen.toggled.connect(_on_fullscreen_toggled)
	_vsync.toggled.connect(_on_vsync_toggled)
	_master.value_changed.connect(_on_master_changed)
	_sfx.value_changed.connect(_on_sfx_changed)
	_reset_keys.pressed.connect(_on_reset_keys)
	_back.pressed.connect(close)


func open() -> void:
	visible = true
	_listening_action = ""
	_refresh()
	_back.grab_focus()


func close() -> void:
	if not visible:
		return
	_listening_action = ""
	visible = false
	closed.emit()


func _input(event: InputEvent) -> void:
	if not visible:
		return
	if not _listening_action.is_empty():
		if event is InputEventKey and event.pressed and not event.echo:
			_capture_key(event as InputEventKey)
			get_viewport().set_input_as_handled()
		return
	if event.is_action_pressed("ui_cancel") and not event.is_echo():
		close()
		get_viewport().set_input_as_handled()


# ── 画面 / 声音 ───────────────────────────────────────────────
func _refresh() -> void:
	_refreshing = true
	_fullscreen.button_pressed = SettingsManager.fullscreen
	_vsync.button_pressed = SettingsManager.vsync
	_master.value = roundf(SettingsManager.master_volume * 100.0)
	_sfx.value = roundf(SettingsManager.sfx_volume * 100.0)
	_refreshing = false
	_update_volume_labels()
	_key_hint.text = ""
	_refresh_key_labels()


func _on_fullscreen_toggled(pressed: bool) -> void:
	if _refreshing:
		return
	SettingsManager.set_fullscreen(pressed)


func _on_vsync_toggled(pressed: bool) -> void:
	if _refreshing:
		return
	SettingsManager.set_vsync(pressed)


func _on_master_changed(value: float) -> void:
	if _refreshing:
		return
	SettingsManager.set_master_volume(value / 100.0)
	_update_volume_labels()


func _on_sfx_changed(value: float) -> void:
	if _refreshing:
		return
	SettingsManager.set_sfx_volume(value / 100.0)
	_update_volume_labels()


func _update_volume_labels() -> void:
	_master_value.text = "%d%%" % int(roundf(_master.value))
	_sfx_value.text = "%d%%" % int(roundf(_sfx.value))


# ── 键位 ───────────────────────────────────────────────────────
func _refresh_key_labels() -> void:
	for action: String in _key_buttons.keys():
		var button := _key_buttons[action] as Button
		button.text = SettingsManager.key_label(action)


func _on_rebind_pressed(action: String) -> void:
	_listening_action = action
	var button := _key_buttons[action] as Button
	button.text = "按任意键…"
	_key_hint.text = "按下新键，Esc 取消"


func _capture_key(event: InputEventKey) -> void:
	var code := event.physical_keycode if event.physical_keycode != 0 else event.keycode
	if code == KEY_ESCAPE:
		_listening_action = ""
		_key_hint.text = "已取消"
		_refresh_key_labels()
		return
	if code in [KEY_SHIFT, KEY_CTRL, KEY_ALT, KEY_META]:
		return
	if SettingsManager.rebind(_listening_action, code):
		_key_hint.text = "已更新"
	else:
		_key_hint.text = "该键已被其他操作占用"
	_listening_action = ""
	_refresh_key_labels()


func _on_reset_keys() -> void:
	SettingsManager.reset_keybinds()
	_key_hint.text = "已恢复默认键位"
	_listening_action = ""
	_refresh_key_labels()
