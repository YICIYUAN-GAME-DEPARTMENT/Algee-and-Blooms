extends Node
## ============================================================================
## SettingsManager — 画面 / 音量 / 键位设置（Autoload）
## ----------------------------------------------------------------------------
## 文件：user://growth_settings.cfg（ConfigFile）
## 键位只管理玩法动作；默认键位在启动时从 project.godot 的 InputMap 捕获，
## 恢复默认时原样还原（保留方向键 + WASD 的双绑定）。
## ============================================================================

const SETTINGS_PATH := "user://growth_settings.cfg"
const MANAGED_ACTIONS := ["move_up", "move_down", "move_left", "move_right", "restart", "pause"]
const DEFAULT_MASTER := 0.8
const DEFAULT_SFX := 0.8

var fullscreen := false
var vsync := true
var master_volume := DEFAULT_MASTER
var sfx_volume := DEFAULT_SFX

var _default_events: Dictionary = {}


func _ready() -> void:
	_capture_default_inputs()
	_ensure_audio_buses()
	_load_settings()
	_apply_video()
	_apply_audio()


func _capture_default_inputs() -> void:
	for action: String in MANAGED_ACTIONS:
		_default_events[action] = _clone_events(InputMap.action_get_events(action))


func _clone_events(events: Array) -> Array:
	var out: Array = []
	for ev: InputEvent in events:
		out.append(ev.duplicate())
	return out


# ── 画面 ───────────────────────────────────────────────────────
func set_fullscreen(value: bool) -> void:
	fullscreen = value
	_apply_video()
	_save_settings()


func set_vsync(value: bool) -> void:
	vsync = value
	_apply_video()
	_save_settings()


func _apply_video() -> void:
	if DisplayServer.get_name() == "headless":
		return
	var mode := DisplayServer.WINDOW_MODE_FULLSCREEN if fullscreen else DisplayServer.WINDOW_MODE_WINDOWED
	if DisplayServer.window_get_mode() != mode:
		DisplayServer.window_set_mode(mode)
	DisplayServer.window_set_vsync_mode(
		DisplayServer.VSYNC_ENABLED if vsync else DisplayServer.VSYNC_DISABLED)


# ── 声音 ───────────────────────────────────────────────────────
func set_master_volume(value: float) -> void:
	master_volume = clampf(value, 0.0, 1.0)
	_apply_audio()
	_save_settings()


func set_sfx_volume(value: float) -> void:
	sfx_volume = clampf(value, 0.0, 1.0)
	_apply_audio()
	_save_settings()


## 总线布局缺 SFX 时兜底创建（正常由 Resources/Config/AudioBuses.tres 提供）。
func _ensure_audio_buses() -> void:
	if AudioServer.get_bus_index("SFX") >= 0:
		return
	AudioServer.add_bus()
	var index := AudioServer.bus_count - 1
	AudioServer.set_bus_name(index, "SFX")
	AudioServer.set_bus_send(index, "Master")


func _apply_audio() -> void:
	_set_bus_volume("Master", master_volume)
	_set_bus_volume("SFX", sfx_volume)


func _set_bus_volume(bus_name: String, value: float) -> void:
	var index := AudioServer.get_bus_index(bus_name)
	if index < 0:
		return
	AudioServer.set_bus_mute(index, value <= 0.001)
	AudioServer.set_bus_volume_db(index, linear_to_db(maxf(value, 0.0001)))


# ── 键位 ───────────────────────────────────────────────────────
## 当前动作的主键显示名（取第一个键盘事件）。
func key_label(action: String) -> String:
	var code := _first_keycode(action)
	return OS.get_keycode_string(code) if code > 0 else "未绑定"


## 绑定新键；与其它受管动作冲突时返回 false（调用方负责提示）。
func rebind(action: String, keycode: int) -> bool:
	if keycode <= 0:
		return false
	for other: String in MANAGED_ACTIONS:
		if other != action and _action_has_key(other, keycode):
			return false
	_apply_action_key(action, keycode)
	_save_settings()
	return true


func reset_keybinds() -> void:
	for action: String in MANAGED_ACTIONS:
		InputMap.action_erase_events(action)
		var defaults: Array = _default_events.get(action, [])
		for ev: InputEvent in defaults:
			InputMap.action_add_event(action, ev.duplicate())
	_save_settings()


func _apply_action_key(action: String, keycode: int) -> void:
	InputMap.action_erase_events(action)
	var ev := InputEventKey.new()
	ev.physical_keycode = keycode
	InputMap.action_add_event(action, ev)


func _action_has_key(action: String, keycode: int) -> bool:
	for ev: InputEvent in InputMap.action_get_events(action):
		if ev is InputEventKey:
			var key := ev as InputEventKey
			var code := key.physical_keycode if key.physical_keycode != 0 else key.keycode
			if code == keycode:
				return true
	return false


func _first_keycode(action: String) -> int:
	for ev: InputEvent in InputMap.action_get_events(action):
		if ev is InputEventKey:
			var key := ev as InputEventKey
			return key.physical_keycode if key.physical_keycode != 0 else key.keycode
	return 0


# ── 持久化 ─────────────────────────────────────────────────────
func _load_settings() -> void:
	var cfg := ConfigFile.new()
	if cfg.load(SETTINGS_PATH) != OK:
		return
	fullscreen = bool(cfg.get_value("video", "fullscreen", fullscreen))
	vsync = bool(cfg.get_value("video", "vsync", vsync))
	master_volume = clampf(float(cfg.get_value("audio", "master", master_volume)), 0.0, 1.0)
	sfx_volume = clampf(float(cfg.get_value("audio", "sfx", sfx_volume)), 0.0, 1.0)
	for action: String in MANAGED_ACTIONS:
		if cfg.has_section_key("input", action):
			var code := int(cfg.get_value("input", action, 0))
			if code > 0:
				_apply_action_key(action, code)


func _save_settings() -> void:
	var cfg := ConfigFile.new()
	cfg.set_value("video", "fullscreen", fullscreen)
	cfg.set_value("video", "vsync", vsync)
	cfg.set_value("audio", "master", master_volume)
	cfg.set_value("audio", "sfx", sfx_volume)
	for action: String in MANAGED_ACTIONS:
		cfg.set_value("input", action, _first_keycode(action))
	cfg.save(SETTINGS_PATH)
