class_name RatingStore
extends RefCounted
## ============================================================================
## RatingStore — 关卡人类评分记录（JSONL）
## ----------------------------------------------------------------------------
## 开发期（编辑器 / debug）写 res://data/ratings/；导出构建写 user://ratings/。
## 每行一个 JSON；同一进程共用一个 session 文件。
## 记录 schema 见 docs/planning/过时/评分训练集与标定规范.md（历史）。
## ============================================================================

const SUB_DIR := "ratings"
const SCHEMA_VERSION := 1

static var _session_id := ""
static var _session_file := ""


## 当前评分会话 ID（进程内稳定）
static func session_id() -> String:
	if _session_id.is_empty():
		_session_id = Time.get_datetime_string_from_system().replace(":", "-")
		_session_file = "session_%s.jsonl" % _session_id
	return _session_id


## 返回本次运行应写入的目录（绝对路径）
static func ratings_dir() -> String:
	if OS.has_feature("editor"):
		return ProjectSettings.globalize_path("res://data/%s" % SUB_DIR)
	return ProjectSettings.globalize_path("user://%s" % SUB_DIR)


## 追加一条记录；返回是否写入成功
static func write_record(record: Dictionary) -> bool:
	session_id()
	var dir := ratings_dir()
	if not DirAccess.dir_exists_absolute(dir):
		var mk := DirAccess.make_dir_recursive_absolute(dir)
		if mk != OK:
			push_warning("RatingStore: 无法创建目录 %s (%d)" % [dir, mk])
			return false
	var path := dir.path_join(_session_file)
	var file := FileAccess.open(path, FileAccess.READ_WRITE)
	if file == null:
		# 文件不存在时 READ_WRITE 可能失败，退回 WRITE 创建
		file = FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		push_warning("RatingStore: 无法打开 %s (%s)" % [path, error_string(FileAccess.get_open_error())])
		return false
	file.seek_end()
	# 人类可读：外层缩进换行；spec 里的坐标列表（floor/obstacles/…）保持单行
	file.store_line(_stringify_readable(record))
	file.store_line("")
	file.close()
	return true


## 可读输出：顶层键与 spec 的键各占一行；spec 的值（含长数组）压成单行，便于人眼扫结构。
static func _stringify_readable(record: Dictionary) -> String:
	var lines: PackedStringArray = ["{"]
	var keys: Array = record.keys()
	keys.sort()
	for i in keys.size():
		var k := str(keys[i])
		var tail := "," if i < keys.size() - 1 else ""
		if k == "spec" and record[k] is Dictionary:
			lines.append("\t%s: {" % JSON.stringify(k))
			var spec: Dictionary = record[k]
			var skeys: Array = spec.keys()
			skeys.sort()
			for j in skeys.size():
				var sk := str(skeys[j])
				var stail := "," if j < skeys.size() - 1 else ""
				lines.append("\t\t%s: %s%s" % [JSON.stringify(sk), JSON.stringify(spec[skeys[j]]), stail])
			lines.append("\t}" + tail)
		else:
			lines.append("\t%s: %s%s" % [JSON.stringify(k), JSON.stringify(record[k]), tail])
	lines.append("}")
	return "\n".join(lines)


## 当前会话文件绝对路径（调试 / 导出用）
static func session_path() -> String:
	session_id()
	return ratings_dir().path_join(_session_file)
