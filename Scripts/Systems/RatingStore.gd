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
	file.store_line(JSON.stringify(record))
	file.close()
	return true


## 当前会话文件绝对路径（调试 / 导出用）
static func session_path() -> String:
	session_id()
	return ratings_dir().path_join(_session_file)
