class_name HintOverlay
extends Node2D
## ============================================================================
## HintOverlay — 局内解法路线提示（纯视觉，不改规则）
## ----------------------------------------------------------------------------
## 预置场景：Outline(Line2D) + Route(Line2D + ShaderMaterial)。
## 脚本只设置点集与显隐；方向串 A/D/W/S 转格中心坐标。
## 规范见 docs/planning/关卡内解法提示规范.md。
## ============================================================================

@onready var _outline: Line2D = $Outline
@onready var _route: Line2D = $Route


func _ready() -> void:
	visible = false


## 显示路线；head 有效且在线路上时，从 head 开始画剩余段
func show_route(route: String, spawn: Vector2i, head: Vector2i = Vector2i(-1, -1)) -> void:
	var cells := _decode_route(route, spawn)
	if head.x >= 0:
		var idx := cells.find(head)
		if idx > 0:
			var sliced: Array[Vector2i] = []
			for i in range(idx, cells.size()):
				sliced.append(cells[i])
			cells = sliced
	if cells.size() < 2:
		hide_route()
		return
	var points := PackedVector2Array()
	for c in cells:
		points.append(GridMetrics.cell_center(c))
	_outline.points = points
	_route.points = points
	visible = true


func hide_route() -> void:
	visible = false


func _decode_route(route: String, spawn: Vector2i) -> Array[Vector2i]:
	var cells: Array[Vector2i] = [spawn]
	var cur := spawn
	for i in route.length():
		match route[i]:
			"A": cur += Vector2i.LEFT
			"D": cur += Vector2i.RIGHT
			"W": cur += Vector2i.UP
			"S": cur += Vector2i.DOWN
			_:
				continue
		cells.append(cur)
	return cells
