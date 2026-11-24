class_name MechanicShapes
## ============================================================================
## MechanicShapes — 机关生长形状（逻辑权威，与功能需求文档 §4.2.1 一致）
## ----------------------------------------------------------------------------
## 所有偏移均以机关中心格 C 为原点。形状统一写成八边形：
##   r(lv) = floor((lv + 2) / 2)                    曼哈顿半径
##   k(lv) = floor((lv + 1) / 2)                    切比雪夫半径
##   S(lv) = { p : |p.x| + |p.y| ≤ r(lv)
##                且 max(|p.x|, |p.y|) ≤ k(lv) }
## 即"曼哈顿菱形 ∩ 切比雪夫方框"，面积序列 1/5/9/13/21。
##
## 反解出格子 p 首次进入形状的阶段（供 LevelState / 求解器 O(1) 查询）：
##   cover_level(p) = max(0, 2(|x|+|y|) - 2, 2 max(|x|,|y|) - 1)
## ============================================================================

## 返回某阶段所有被占据格相对中心 C 的偏移（含中心本身）
static func cells(level: int) -> Array[Vector2i]:
	var out: Array[Vector2i] = [Vector2i.ZERO]
	var r := radius(level).x
	for x in range(-r, r + 1):
		for y in range(-r, r + 1):
			var d := Vector2i(x, y)
			if d == Vector2i.ZERO:
				continue
			if cover_level(d) <= level:
				out.append(d)
	return out


static func max_level() -> int:
	return 4


## (r, k)：r = 曼哈顿半径，k = 切比雪夫半径；lv 超出 0..max 时钳制
static func radius(level: int) -> Vector2i:
	var lv := clampi(level, 0, max_level())
	return Vector2i(floori((lv + 2) / 2.0), floori((lv + 1) / 2.0))


## 格子 d 第一次进入机关形状的阶段；> max_level() 表示永不覆盖
static func cover_level(d: Vector2i) -> int:
	var d1 := absi(d.x) + absi(d.y)
	var dinf := maxi(absi(d.x), absi(d.y))
	return maxi(0, maxi(2 * d1 - 2, 2 * dinf - 1))
