@tool
class_name Mechanism
extends CellEntity
## ============================================================================
## Mechanism — 机关核心（关卡中放置 1 个节点 = 机关核心 C）
## ----------------------------------------------------------------------------
## 只保留核心贴图与初始阶段；占格/生长/死局规则在 LevelState（纯逻辑层）。
## 核心格由 MechanismCells 图层铺生长体瓦，本节点 Core Sprite 用 z_index=1
## 浮在瓦片层之上画核心外观。
## 阶段允许负值（最低 -10）：负值不扩张、不占格，只把核心染色成蓝色，越负越深。
## ============================================================================

## 初始阶段（默认 0；可为负，最低 -10）；本关最高 cap 由 Level 的
## mechanism_max_level_override 解析
@export var initial_level: int = 0

const MIN_LEVEL := -10

@onready var _core: Sprite2D = $Core


func _ready() -> void:
	# 核心格也铺生长体瓦，Core Sprite 必须浮在 MechanismCells(TileMapLayer) 之上
	z_index = 1
	super._ready()
	set_current_level(initial_level)


## 同步当前阶段外观：≥0 原色；负值染蓝，越负越深（-10 最深）。
func set_current_level(level: int) -> void:
	if _core == null:
		return
	if level >= 0:
		_core.modulate = Color.WHITE
		return
	var t := clampf(float(-level) / float(-MIN_LEVEL), 0.0, 1.0)
	_core.modulate = Color.WHITE.lerp(Color(0.2, 0.38, 0.95, 1.0), t)
