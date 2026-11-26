# AGENTS.md — AGENT 接入索引

> 本文件是**所有 AGENT 工具进入本仓库的入口与索引**：说明项目是什么、去哪读规则、有哪些技能、章节号对哪个文件，并汇总角色分工与纪律摘要。
> 规则正文以 [docs/开发文档.md](docs/开发文档.md)、[docs/design/功能需求文档.md](docs/design/功能需求文档.md)、[docs/agents/rules/](docs/agents/rules/) 与各技能正文为准；本文件与它们冲突时，以它们为准。

适用范围：任何在本仓库工作的编码 agent（Trae / Claude Code / Codex / Cursor 等）。

---

## 0. 全局原则

1. **双权威源：**
   - 工程 / 架构 / 数据 / 命名 → [docs/开发文档.md](docs/开发文档.md)（工程规范·权威）。
   - 玩法 / 功能 / 数值 / 验收 → [docs/design/功能需求文档.md](docs/design/功能需求文档.md)（产品需求·权威）。
   - 改规范 = 改对应权威文档 + 走 PR；引用其章节的技能需同步。
2. **玩法定稿：** 玩法/规则已冻结（见功能需求文档 §4）。**涉及玩法/数值调整必须先与人类对齐**，确认后同步更新功能需求文档与 `Balance.tres` / 关卡场景，禁止在代码里"顺手"改规则。
3. **不破坏扩展点：** Autoload 公共接口（见 [架构说明](docs/architecture/架构说明.md) §2）勿擅改；新增内容走 [开发文档](docs/开发文档.md) §2.3 扩展点。
4. **最简可用、避免过度工程：** 原型/上线过渡期优先最小改动；占位资源允许，但须按开发文档 §3.7 命名并登记 §3.9。
5. **GDScript 表现层 + C# 规则与算法（单一权威，已确认）：** 引擎接入 / 场景 / 动画 / UI / 输入 / 存档仍用 GDScript；**玩法规则与搜索 / 生成 / 评分统一由 C# 实现**（`Scripts/Algorithms/`，普通类 + `RefCounted`，不建独立类库 / CLI），`LevelState.gd` 退化为适配层，`Level.gd` 通过它调用 C# 规则。项目使用 Godot **.NET/mono** 构建，**放弃 Web 导出**；不引入其他第三方插件 / 原生扩展，除非人类确认并同步权威文档。测试全部在引擎内 headless 运行（`tests/core/` C# 断言场景；当前未引入 GUT 等外部测试框架）。2026-11-24 确认：规则单一权威在 C#，不再保留 GDScript 规则副本。
6. **本文档用途：** 本作是 2D 网格解谜（无卡牌 / 敌人 / 技能类玩法）；角色职责与索引均与仓库现状一致。

---

## 1. 项目速览

| 项 | 内容 |
| -- | ---- |
| 项目 | 《阿吉与花海》（Algee & Blooms），2D 俯视网格解谜，PC 单机；2026 GameJam · 主题 **Grow** |
| 一句话玩法 | 尾部钉在起点、身体像管道铺到哪探到哪；每走 N 步花丛（机关）集体扩张堵路，赶在合拢前踩到蓝花（终点） |
| 核心体验 | 扩张可达范围 ↔ 花丛随步数收紧；每一步都是路线规划与折返截断的取舍 |
| 引擎 / 语言 | Godot **4.7 stable**（.NET/mono，导出与 CI）+ **GDScript**（表现层）/ **C#**（规则与算法，单一权威，项目内 `Scripts/Algorithms/`）；不支持 Web 导出 |
| 画面 / 平台 | 1280×720，`canvas_items` 拉伸，Forward+（Windows 渲染设备 d3d12）；目标 Windows 桌面 |
| 架构 | 5 个 Autoload（GameManager / EventManager / SaveManager / ButtonAudio / SfxManager）+ `Level.gd`（表现）/ `LevelState.gd`（薄适配）/ `Scripts/Algorithms/`（C# 规则 + 搜索 + 生成，单一权威）/ `GridSystem`（场景网格数据）+ 实体预制 + 关卡场景直编 |
| 测试 | 全部引擎内 headless：`tests/core/`（C# 断言场景）+ `tests/fixtures/`（黄金路线 / 锚点 / 基准快照） |
| 项目阶段 | GameJam 已完成，进入正式上线准备；已确认 GDScript 表现层 + C# 规则单一权威并同步权威文档；里程碑见 [正式上线待办](docs/planning/正式上线待办.md)（**规划稿，非权威**） |
| 权威设计文档 | [docs/开发文档.md](docs/开发文档.md)（工程 / 架构 / 数据 / 命名，权威）<br>[docs/design/功能需求文档.md](docs/design/功能需求文档.md)（玩法 / 数值 / 验收，权威） |
| 实现说明 | [docs/architecture/架构说明.md](docs/architecture/架构说明.md)（随代码同步的实现细节） |

### 1.1 个人开发分工

| 角色 | 负责 |
| ---- | ---- |
| 用户 | 玩法与数值设计、美术、音频、关卡编排、测试验收、git 提交 |
| AGENT（深度参与） | 全部 GDScript / C# 实现、场景 / 资源 / 配置搭建、headless 工具与求解器、调试与性能排查、文档同步、调研与方案提案 |
| AGENT（辅助） | 设计评审、技术选型对比、风险提示、任务拆分 |

推论：脚本类任务默认由 AGENT 主导落地；设计意图以用户为准，AGENT **不得自行增删机制或改数值**，有疑问先问。

---

## 2. 先读哪里（按任务选）

| 你要做的事 | 先读 |
| ---------- | ---- |
| 任何功能 / UI / 脚本 / 场景开发之前 | [功能需求文档](docs/design/功能需求文档.md) §3–§6（规则 / 界面 / 关卡）+ [开发文档](docs/开发文档.md) §2 / §4（架构 / 场景） |
| 任何编码任务之前 | [coding-rules.md](docs/agents/rules/coding-rules.md)（alwaysApply） |
| 写 / 改 `.gd` | coding-rules + 技能 [script-dev](docs/agents/skills/script-dev.md) |
| 建 / 改场景、摆放实体、UI 布局 | [开发文档](docs/开发文档.md) §4 + 技能 [scene-dev](docs/agents/skills/scene-dev.md) |
| 设计新关卡 / 涂地板与障碍 | [关卡制作指南](docs/development/关卡制作指南.md) + 功能需求 §6 + 技能 scene-dev |
| 涉及玩法机制（走格 / 身长 / 截断 / 机关生长 / 食物 / 胜负 / BFS） | [功能需求文档](docs/design/功能需求文档.md) §4 + [开发文档](docs/开发文档.md) §5.3 + 技能 [game-design-context](docs/agents/skills/game-design-context.md) |
| 调数值 / 调平衡 | 功能需求 §7（唯一权威清单）+ [开发文档](docs/开发文档.md) §5.5 + `Resources/Config/Balance.tres` |
| 导入美术 / 音频、LFS、资产登记 | [开发文档](docs/开发文档.md) §3 + 技能 [resource-mgmt](docs/agents/skills/resource-mgmt.md) |
| 引入外部代码 / 插件 / 素材 / 字体，或方案不确定 | 技能 [research-first](docs/agents/skills/research-first.md)（先调研 + 许可证判定，再确认） |
| 跑 `tools/`、headless 生成 / 迁移 / 验证、瓦片贴图 | 技能 [godot-tooling](docs/agents/skills/godot-tooling.md)（**先读，防坑**） |
| 跨模块 / 架构改动、不确定"该怎么做" | 技能 [architect](docs/agents/skills/architect.md) + [架构说明](docs/architecture/架构说明.md) + 开发文档 §2.3 扩展点 |
| C# 规则 / 算法 / 求解器 / 生成器 / 评分 | [随机关卡算法 v4](docs/planning/随机关卡算法v4-整体改造方案.md) + [正式上线待办](docs/planning/正式上线待办.md) + [架构说明](docs/architecture/架构说明.md) §1.1 + [coding-rules.md](docs/agents/rules/coding-rules.md) |
| PR 前回归 / 疑难 bug / 导出验证 | 功能需求 §8 验收（C-通用 / C-Lnn）+ 技能 [qa](docs/agents/skills/qa.md) |
| 写提交信息草稿 | [collaboration-rules.md](docs/agents/rules/collaboration-rules.md) §2 + 开发文档 §7 |
| 正式上线 / 生成器 / 求解器等规划 | [正式上线待办](docs/planning/正式上线待办.md) + [随机关卡算法 v4](docs/planning/随机关卡算法v4-整体改造方案.md)（规划稿；权威口径仍以两本主文档为准） |
| 定位某条规则的设计原文 | 本文件 §4「文档索引映射」 |

---

## 3. 角色与技能

技能 = 「按需加载的任务说明」，真源在 [docs/agents/skills/](docs/agents/skills/)；角色 = 这类工作由谁主导。

| 角色 | 职责 | 何时用 | 技能 |
| ---- | ---- | ------ | ---- |
| 玩法设计上下文 | 设计支柱 / 术语 / 阅读路由；不维护易变数值 | 涉及玩法机制、关卡、HUD、数值 | [game-design-context.md](docs/agents/skills/game-design-context.md) |
| 资料调研与许可证 | 外部代码 / 插件 / 素材 / 方案调研与许可证判定 | 引入外部依赖、替换素材、方案不确定 | [research-first.md](docs/agents/skills/research-first.md) |
| 项目架构师 | 高层规划、任务拆分、架构一致性审查、风险评估 | 新功能起步、跨模块改动、不确定"该怎么做" | [architect.md](docs/agents/skills/architect.md) |
| 场景开发 | `.tscn` 节点树、预制体、UI 布局；核心是关卡场景直编 | 主菜单 / 选关 / HUD / 关卡 / 实体预制 | [scene-dev.md](docs/agents/skills/scene-dev.md) |
| 脚本开发 | GDScript 逻辑：规则 / 实体 / UI / 信号 / Autoload | 实现功能（FR / C-xx）、修 bug、写系统 | [script-dev.md](docs/agents/skills/script-dev.md) |
| 资源管理 | 美术 / 音频导入、`Balance.tres`、LFS、资产登记 | 导入资源、调数值、大资源入库 | [resource-mgmt.md](docs/agents/skills/resource-mgmt.md)（配合全局 `godot-resource`） |
| 测试与质量 | 按验收标准回归、性能、规范审查、导出验证 | PR 前、里程碑前、疑难 bug | [qa.md](docs/agents/skills/qa.md) |
| 工具链（横切） | headless 生成 / 迁移 / 验证、瓦片与场景注入防坑 | 涉及 `tools/`、瓦片贴图、场景脚本注入 | [godot-tooling.md](docs/agents/skills/godot-tooling.md) |
| 规则与算法（C#） | 玩法规则唯一实现、搜索、生成、评分；`LevelState.gd` 仅作适配 | 求解器 / 生成器 / 规则变更 / 性能 | —（先读 [正式上线待办](docs/planning/正式上线待办.md) 与 [架构说明](docs/architecture/架构说明.md) §1.1） |

> 全局技能中可能已有同名 / 他项目技能：`game-design-context` 可能来自卡牌 / 合成台项目，`godot-csharp` 不适用于本作。在本仓库工作时**优先使用本项目 `docs/agents/skills/` 下的同名技能**；本项目没有对应技能时再参考全局技能。
> 参考项目的 `agent-orchestrator` / `godot-gdscript` / `godot-resource` 已分别由全局 `agent-orchestration`、本项目 `script-dev` + `coding-rules`、`resource-mgmt` + `godot-tooling` 覆盖，不重复建同义技能。

### 3.1 协作流程

```
人类提需求 ─→ 架构师拆任务 ─┬→ 场景开发（.tscn，含关卡直编）
                           ├→ 脚本开发（.gd）
                           ├→ 资源管理（.tres / LFS）
                           └→ QA 验证 ─→ PR
```

- **任务拆分粒度：** 一个 PR 一个职责（场景 / 脚本 / 资源尽量不混；关卡 `.tscn` 单独 PR）。
- **冲突预防：** 改动同一 `.tscn` 前在 PR 描述声明，串行处理。
- **接力约定：** 脚本侧需要的新信号，先在 `EventManager` 加好（PR1），玩法再接（PR2）。
- **外部依赖：** 涉及第三方代码 / 插件 / 素材 / 新方案时，先按 [research-first](docs/agents/skills/research-first.md) 调研并取得人类确认，再拆任务。

---

## 4. 文档索引映射（章节号 → 文件）

> **索引原则：** 本文件只做长期索引与纪律摘要，**不写当前版本号、任务状态、资产进度、测试数值、关卡清单**；这些以权威文档、开发文档 §3.9 资产清单与规划稿为准。路径相对仓库根。

### 4.1 《开发文档》（工程权威，`docs/开发文档.md`）

| 章节 | 内容 |
| ---- | ---- |
| §1 | 项目概述：目标 / 技术栈 / 里程碑 |
| §2 | 架构设计：分层 / 通信原则 / **2.3 扩展点** |
| §3 | 资源与命名：导入 / 命名 / 目录职责 / 音频 / 字体 / 占位 / **3.8 LFS** / **3.9 资产清单 A-xx** |
| §4 | 场景与节点组织：通用约定 / 场景文件 / 流转 / **4.4 关卡场景结构** / 4.5 输入锁 / 4.6 剧情覆盖层 |
| §5 | 数据与数值：5.2 关卡导出字段 / 5.3 机关生长 / 5.4 存档 / **5.5 Balance** / 5.6 QA 基准 |
| §6 | 玩法功能（指向功能需求） |
| §7 | 提交与协作 |

### 4.2 《功能需求文档》（玩法权威，`docs/design/功能需求文档.md`）

| 章节 | 内容 |
| ---- | ---- |
| §1 | 产品概述：类型 / 内容规模 / 一句话概念 / 核心张力 |
| §2 | 术语与符号（trail / L / mech_cells / 截断 / 死局…） |
| §3 | 核心玩法概述：核心循环 / 玩家决策模型 |
| §4 | **游戏规则（权威）**：4.1 主角与移动 / 4.2 机关与生长 / 4.3 食物 / 4.4 死局判定（BFS）/ 4.5 胜负与流程 |
| §5 | 界面与操作：布局 / 场景流 / 输入映射 / HUD / 反馈 / 5.6 剧情演出 |
| §6 | 关卡与内容：清单 / 关卡文件（场景直编）/ **6.3 关卡校验（发布前强制）** |
| §7 | **数值配置（唯一权威清单）** |
| §8 | **验收标准**：C-通用（C-01…C-19）/ C-Lnn（逐关） |
| §9 | 非功能需求 |
| §10 | 变更记录 |

### 4.3 其他文档

| 位置 | 内容 |
| ---- | ---- |
| [docs/architecture/架构说明.md](docs/architecture/架构说明.md) | 分层职责 / **Autoload 公共接口（§2，勿擅改）** / 事件总线（§3）/ 数据 / 场景编排 / 输入 / 不使用物理 |
| [docs/design/玩法说明.md](docs/design/玩法说明.md) | 设计推导与决策记录（追溯用，**非权威**；与功能需求冲突时以功能需求为准） |
| [docs/development/环境搭建指南.md](docs/development/环境搭建指南.md) | 新成员环境配置、Godot / Git LFS 安装 |
| [docs/development/关卡制作指南.md](docs/development/关卡制作指南.md) | 设计者：新建 / 摆放 / 校验关卡 |
| [docs/planning/正式上线待办.md](docs/planning/正式上线待办.md) | 正式上线路线、算法核心与 P0/P1/P2 待办（**规划稿，非权威**） |
| [docs/planning/随机关卡算法v4-整体改造方案.md](docs/planning/随机关卡算法v4-整体改造方案.md) | 随机关卡生成 / 评分 / 验证整体改造：双引擎（结构图 + 噪波负型）、设计公平门、节点抽象、纯路线高难（**规划稿，非权威**） |
| [docs/planning/过时/](docs/planning/过时/) | v3 生成 / 评分 / 自由模式 / 开发索引 / 评分训练集 / 状态机等历史规划稿（**已过时**） |
| [docs/agents/rules/coding-rules.md](docs/agents/rules/coding-rules.md) | 编码规范（GDScript / 信号 / 网格判定 / 禁止项） |
| [docs/agents/rules/collaboration-rules.md](docs/agents/rules/collaboration-rules.md) | 分支 / 提交 / PR / LFS / `.tscn` 冲突 |
| [docs/agents/skills/](docs/agents/skills/) | 8 个技能（见 §3） |

### 4.4 代码与内容模块

| 模块 | 位置 | 职责 |
| ---- | ---- | ---- |
| Autoload | `Scripts/Autoload/` + `Scenes/UI/ButtonAudio.tscn` / `Scenes/Audio/SfxManager.tscn` | 顶层状态 / 信号总线 / 存档 / 按钮音 / 玩法音效 |
| 规则适配层 | `Scripts/Systems/LevelState.gd` | 转发 C# `RuleState` 并同步 trail / max_len / steps 等表现字段；规则唯一实现在 `Scripts/Algorithms/`，**改规则先改 C#** |
| 表现层 | `Scripts/Systems/Level.gd` | 场景节点 / 动画 / HUD / 剧情 / 输入；只做表现同步，不另写一套规则 |
| 网格数据 | `Scripts/Systems/GridSystem.gd` | 可走判定 / BFS（无物理碰撞） |
| 剧情层 | `Scripts/Systems/DialogueOverlay.gd` + `Scenes/Dialogue/` | AVG 覆盖层（运行时懒实例） |
| 实体 | `Scripts/Entities/` + `Scenes/Entities/` | CellEntity 基类 + PlayerSpawn / Food / Goal / Mechanism |
| 界面 | `Scripts/UI/` + `Scenes/UI/` | MainMenu / LevelSelect / LevelHUD / JuicyButton / ButtonAudio |
| 数据类 | `Scripts/Data/` | Balance / MechanicShapes / GridMetrics |
| 资源 | `Resources/Config/` / `Resources/Dialogue/` | `Balance.tres`（唯一数值 .tres）/ 剧情 JSON |
| 关卡 | `Scenes/Levels/Level_<nn>_<名>.tscn` | **每关一个场景，直编**；`LevelTemplate.tscn` 为模板 |
| 工具 | `tools/` | headless 生成 / 迁移 / 校验 / 求解器（`LevelSolver`），不参与游戏运行 |
| 算法与规则 | `Scripts/Algorithms/`（C#） | 规则唯一实现 / 网格模型 / 搜索 / 生成 / 评分；不访问节点 / 场景 / 信号 |
| 测试 | `tests/` | 引擎内 headless：`tests/core/` C# 断言场景 + `tests/fixtures/` 快照；未引入 GUT 等外部框架 |
| 美术 / 音频 | `Art/` / `Audio/` / `UI/` | 正式资源 + 占位；登记见开发文档 §3.9 |

---

## 5. 硬性纪律（摘要）

> 以下是 `rules/` 与技能中最容易踩线的条款摘要；**完整条款以原文为准**。

### 5.1 玩法定稿与数值

- 玩法/规则已冻结：任何规则或数值调整，**先停下问人类** → 更新功能需求文档 + `Balance.tres` / 关卡覆盖 → 再改代码。禁止"顺手改规则"。
- 全局数值唯一来源 `Resources/Config/Balance.tres`；单关只允许 Level 根节点 4 个 `*_override`（0=沿用全局）；`grow_anim_sec` 仅全局；脚本禁硬编码玩法数值（验收 C-14）。
- 关卡布局 = 场景直编：可走区 = `Ground` 实际涂格；障碍 = `Obstacles` 涂格（**非实体**）；机关 / 食物 / 终点 / 出生点 = 实体节点 `cell`。**禁止在脚本里硬编码关卡布局**。

### 5.2 架构与代码

- 分层依赖：Autoload ← 关卡 ← 实体 ← 场景；禁止反向依赖。
- 规则只在 C# `RuleState`（`Scripts/Algorithms/`）；`LevelState.gd` 仅作适配转发；`Level.gd` 只做表现同步；`GridSystem` 只管场景网格数据；`tools/LevelSolver` 调用 C# 求解器，不得复制规则。
- 规则与算法层：C# 普通类 / `RefCounted`（供 GDScript 持有），不得访问节点 / 场景 / 信号；纯数据进出（LevelSpec / Solution / Score）；C# 返回的路线仍须经 `RuleState` 重放校验；算法失败必须可降级，不得崩溃游戏。
- 跨系统通信走 `EventManager`；新增信号追加到文件尾 `## Gameplay` 区块，**不删改仍被 emit 的签名**，并同步架构说明 §3。
- 场景流转走 `GameManager.change_scene()` / `start_level()`；禁止裸 `change_scene`。
- 输入一律走 InputMap（`move_*` / `restart` / `ui_*`），禁止裸查键码；本作**不使用物理碰撞**，走格 / 死局全走数据判定。
- 命名、类型标注、节点缓存、信号写法按 [coding-rules.md](docs/agents/rules/coding-rules.md)；`push_warning/push_error` 不得被静默吞掉。
- **脚本不建 UI**：UI / 贴图 / 布局一律在编辑器场景树定义；脚本不 `_draw`、不 `new` 控件；动态内容（身体 / 机关占格）只同步到既有 TileMap 层与节点。
- 新增实体继承 `CellEntity`，编辑器拖动吸附逻辑集中在基类；headless 注入节点时 `owner` 必须在 `add_child` 之后设置。
- 地板 / 墙 / 机关三套地形为 **47-blob**（corners+sides，列序 = ascending canonical blob），替换美术必须保持列序；重铺一律确定性直写，**禁止** `set_cells_terrain_connect` 批量外扩。

### 5.3 场景与资源

- `.tscn` 冲突**禁止手动文本合并**，用 Godot 编辑器 GUI 解决；改动同一场景前先声明、串行处理。
- 大资源走 Git LFS（`.gitattributes` 按扩展名追踪）；`.gd/.tscn/.tres/.cfg/.import` **永不入 LFS**。
- 新增美术 / 音频登记开发文档 §3.9（A-xx）；`asset` / `art` 提交注明编号。
- 引入外部代码 / 插件 / 美术 / 音频 / 字体前，先按 [research-first](docs/agents/skills/research-first.md) 核对许可证并取得人类确认；外部素材走 LFS 并登记 §3.9。
- 第三方依赖：Godot .NET/mono 运行时随引擎/导出模板；当前未引入 GUT 等测试插件；不引入 NuGet 包 / 插件，除非核对许可证并登记。
- 占位资源按 §3.7 命名；正式替换后删除占位并更新登记。

### 5.4 测试与验收

- 回归依据功能需求 §8（C-通用 / C-Lnn）与 §7 数值；发布前跑 §6.3 关卡校验。
- 自动化测试全部在引擎内 headless 运行：C# 规则 / 求解器 / 生成器用 `tests/core/` 断言场景（退出码判定），快照存 `tests/fixtures/`；当前未引入 GUT 等外部测试框架，也不使用离线差分工具。
- 规则迁移 / 大改必须保留「迁移前黄金路线快照」：5 关 `status/steps/route` 一致才算通过。
- 玩法 / 手感 / 关卡体验验收由用户完成；AGENT 可按 qa.md 做规范检查、启动冒烟与 headless 工具验证（`--import`、`--check-only`、`tools/LevelSolver.tscn`），但**不代替用户判定验收通过**。
- Output 面板的 warning / error 必须为 0；关卡可解性 / 最短路线以 `tools/LevelSolver` 的 SOLVABLE / UNSOLVABLE / UNKNOWN 输出为准。
- 存档 / LFS 完整性 / 导出检查按 [qa.md](docs/agents/skills/qa.md) 执行；headless 命令与引擎路径见 [godot-tooling.md](docs/agents/skills/godot-tooling.md) §1。

### 5.5 提交与协作

- 分支：`main`（受保护）/ `feat/*` / `fix/*` / `art/*` / `docs/*`；提交前缀：`feat` `fix` `art` `asset` `docs` `refactor` `chore`。
- **AGENT 不执行 `git commit` / `git push`**：职责到「产出改动 + 提交信息草稿」为止，由用户审查测试后提交。
- 一个 PR 一个职责；关卡 `.tscn` 改动单独 PR，避免多人同时编辑同一关卡。

### 5.6 文档同步

- 开发文档 / 功能需求文档是权威：**规则性修改须人类确认**（改规范 = 改权威文档 + 走 PR）；不要自行把实现细节写成新规则。
- 纯同步更新可直接做并注明出处：架构说明的 Autoload / 信号表、开发文档 §3.9 资产清单、§5.6 QA 基准（仅在新增资产 / 信号 / 数值时动）。
- 规划稿中的建议，未经人类确认不得写入权威文档，也不得直接按规划稿改架构。

---

## 6. 每次交付的输出格式

实现 / 修改 / 修复类任务完成后，按固定格式输出：

`改动文件` → `改动内容`（关联开发文档 / 功能需求章节）→ `待测试点`（可操作、可验证：场景 / 步骤 / 预期）→ `提交信息草稿`；涉及外部素材时附 `资料依据与许可证`。随后等待用户审查、测试并自行提交。

纯讨论 / 文档任务不强制该格式。

---

## 7. 本文件的维护

- 本文件随仓库分发；不写易变信息（版本号、当前分支、关卡进度、资产状态、具体数值），这些以权威文档与开发文档 §3.9 为准。
- 新增 / 删除技能或规则文档 → 同步 §3 与 §2。
- 新增 / 拆分权威文档章节 → 同步 §4 的章节映射。
- 新增代码模块或目录 → 同步 §4.4。
- 换机器后：安装 Godot 4.7 与 Git LFS，路径与步骤见 [环境搭建指南](docs/development/环境搭建指南.md)。

---

## 8. 变更记录

| 日期 | 摘要 | 作者 |
|------|------|------|
| 2026-09-04 | 初版，定义 5 类智能体角色与协作流程 | 架构组 |
| 2026-09-04 | 玩法冻结：确立双权威源（开发文档 + 功能需求文档）；删除卡牌/敌人等无关描述；角色职责对齐网格解谜 | 架构组 |
| 2026-09-05 | 项目整理：必读文档登记 skills/godot-tooling.md（headless 工具链防坑） | 架构组 |
| 2026-11-24 | 参照 GODOT 参考项目 AGENTS 索引体例重构：项目速览 / 按任务路由 / 角色技能表 / 文档章节索引 / 代码模块 / 硬性纪律 / 交付格式 / 维护约定；同步 LevelState、LevelSolver、5 个 Autoload 与正式上线规划稿 | 架构组 |
| 2026-11-24 | 补充技能：game-design-context（玩法上下文）与 research-first（外部资料 / 许可证）；同步任务路由、技能表与外部依赖纪律 | 架构组 |
| 2026-11-24 | 算法核心改用 C#：独立类库 + Godot 桥接；切换 .NET/mono、放弃 Web 导出；替换 native/ 为 algorithms/ | 架构组 |
| 2026-11-24 | 技术栈同步（正式上线）：确认 GDScript 引擎层 + Rust 算法核心、GUT 9.7.1 测试；补 native/ 模块、路由、角色与纪律 | 架构组 |
| 2026-11-24 | 架构再定稿：规则单一权威迁入 C#（项目内 `Scripts/Algorithms/`），`LevelState.gd` 退化为适配层；取消独立 `.Core`/`.Cli` 与 xUnit，测试全部改为引擎内 headless（C# 断言场景 + GUT） | 架构组 |
| 2026-11-25 | 文档同步现状：仓库未引入 GUT（无 `addons/gut/`），测试统一为 `tests/core/` C# 断言场景 + `tests/fixtures/`；修正 §4.4 规则层表述（`LevelState.gd` 为适配层，规则在 C#）；生成器样本关移至 `WIP/` | 架构组 |
| 2026-11-26 | 新增随机关卡算法 v4 整体改造方案；v3 生成 / 评分 / 自由模式 / 训练集 / 状态机等规划稿移入 `docs/planning/过时/`；同步规划稿索引 | 架构组 |
