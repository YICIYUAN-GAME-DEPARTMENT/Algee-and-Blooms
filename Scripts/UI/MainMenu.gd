extends Control
## ============================================================================
## MainMenu — 开始界面（场景树布局；脚本只接线）
## ============================================================================

@onready var _start: Button = $MenuFrame/Margin/Content/StartButton
@onready var _random: Button = $MenuFrame/Margin/Content/RandomButton
@onready var _settings: Button = $MenuFrame/Margin/Content/SettingsButton
@onready var _quit: Button = $MenuFrame/Margin/Content/QuitButton
@onready var _settings_overlay: Control = $SettingsOverlay


func _ready() -> void:
	_start.pressed.connect(_on_start)
	_random.pressed.connect(_on_random)
	_settings.pressed.connect(_open_settings)
	_quit.pressed.connect(_on_quit)
	_settings_overlay.connect("closed", _on_settings_closed)
	_start.grab_focus()


func _on_start() -> void:
	GameManager.change_scene(GameManager.level_select_scene)


## 随机关卡：难度 / 种子等选项在结算与暂停界面按需选择。
func _on_random() -> void:
	GameManager.start_generated_mode()


func _open_settings() -> void:
	_settings_overlay.call("open")


func _on_settings_closed() -> void:
	_settings.grab_focus()


func _on_quit() -> void:
	get_tree().quit()
