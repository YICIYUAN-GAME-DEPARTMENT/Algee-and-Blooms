@tool
class_name Mechanism
extends CellEntity
## ============================================================================
## Mechanism — 机关核心（关卡中放置 1 个节点 = 机关核心 C）
## ----------------------------------------------------------------------------
## 只保留核心贴图与初始阶段；占格/生长/死局规则在 LevelState（纯逻辑层）。
## 核心格由 MechanismCells 图层铺生长体瓦，本节点 Core Sprite 用 z_index=1
## 浮在瓦片层之上画核心外观。
## ============================================================================

## 初始阶段（默认 0）；本关最高 cap 由 Level 的 mechanism_max_level_override 解析
@export var initial_level: int = 0


func _ready() -> void:
	# 核心格也铺生长体瓦，Core Sprite 必须浮在 MechanismCells(TileMapLayer) 之上
	z_index = 1
	super._ready()
