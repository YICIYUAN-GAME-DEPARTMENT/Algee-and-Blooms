# 第 6 关独立 HTML5 复刻 Demo · 实施计划

## Context（为什么做）

- 现有工程为 Godot 4.7 **.NET/mono** + C# 规则唯一权威，Godot 4.x 不支持 C# Web 导出，且项目已定稿「放弃 Web 导出」；因此不能直接把第 6 关导出为网页。
- 目标：为第 6 关做一个**独立、自包含的 HTML5/Canvas 单关试玩 Demo**，桌面与手机浏览器可玩，便于分享；玩法规则与画面高度还原 PC 版。
- 已与用户确认：走「独立 HTML5 复刻」路线；用途为分享试玩；`webapp/assets/**` 不走 LFS；UI 采用实用版；完成本地验证后尝试用 Trae deployment 部署。
- 非目标：不做提示（官方求解器对第 6 关返回 UNKNOWN）、不做选关、评分区、剧情、PWA/离线、单文件 HTML；不修改 Godot 游戏逻辑与第 6 关场景。

## 关键事实（工作区当前状态）

- 第 6 关：`Scenes/Levels/Level_06_ASampleOfHardLevel.tscn`，`level_id=6`，`level_name=难关示例`，override：`initial_max_len=6`、`food_len_gain=8`、`growth_step_interval=5`；全局 `mechanism_max_level=4`、`grow_anim_sec=0.1`；无剧情。
- 棋盘：origin `(7,4)`、30×16、格 32px；Ground 283 格、Obstacles 64 格；spawn `(9,7)`、goal `(35,13)`；5 食物、6 机关（含 1 个初始 +1 机关，开局占 5 格十字）。
- 规则权威：`Scripts/Algorithms/RuleState.cs`（`Setup/Start/StepCore/ClassifyMove/CheckDeadlock/Grow`），`MechanicShapes.cs` 提供 `CoverLevel`；`LevelState.gd` 只做适配并调用 `SetEndOnDeadlock(false)`。
- 表现权威：`Scripts/Systems/Level.gd`（绘制/动画/输入锁），`Scenes/UI/LevelHUD.tscn` + `Scripts/UI/LevelHUD.gd`（HUD），`Scripts/Autoload/SfxManager.gd`（音效）。
- 回归基线：`tests/fixtures/levels_expected.json` 有 1–5 关黄金路线；第 6 关另有一条经独立模型验证的 60 步可行路线（见下）。
- 工作区有大量未提交改动（含第 6 关场景与 `RuleState.cs`），Demo 以工作区当前版本为准，并用 sha256 记录来源。

## 方案总览

- 纯静态站点，零运行时依赖，ES Modules + Canvas 2D + DOM HUD；`webapp/` 目录自包含、可单独托管。
- 数据不手抄：Node 导出器解析 `.tscn/.tres` 生成 `webapp/data/level_06.json`，导出时做一致性断言，`--check` 防漂移。
- 规则移植纯逻辑模块 `rules.js`，用 1–5 关黄金路线 + 第 6 关 60 步路线做 Node 回归。
- 视觉复刻：三套 47-blob 地形、身体连接件/端点半截瓦、头尾 Sprite、机关三帧生长与核心染色、背景与音效；HUD 用 DOM，世界用 Canvas。
- 移动端：固定 1280×720 舞台等比缩放、虚拟方向键 + 滑动、横屏提示、安全区、音频手势解锁、切后台自动暂停。
- 交付：本地验证通过后，尝试 Trae deployment 发布；若插件不支持静态目录，产出 `webapp/dist/` + 上传说明。

## 目录与文件

```
webapp/
  .gdignore                      # 让 Godot 完全跳过该目录（不导入、不生成 .import）
  package.json                   # 仅 {"private":true,"type":"module"} + scripts，无依赖
  index.html                     # canvas + HUD + 面板 + 方向键 + 加载/横屏提示
  styles.css                     # 舞台缩放、HUD、面板、移动端、safe-area
  src/
    main.js                      # 启动、状态机、step 管线、?debug=1 API
    rules.js                     # RuleState 1:1 移植（纯逻辑，Node 可直接 import）
    render.js                    # Canvas 绘制、动画时钟、47-blob、图片加载
    input.js                     # 键盘 / 滑动 / 虚拟方向键 / 失焦
    ui.js                        # HUD 数值、胜利/失败/暂停面板、按钮
    audio.js                     # WebAudio 解码、音量/变调/复音、手势解锁
  data/
    level_06.json                # 唯一关卡数据（导出产物，不手改）
  assets/
    tiles/{grass,wall,flower,path}.png
    sprites/{algee,charlie,pental,core,goal_flower}.png
    bg.png
    sfx/{step,pick,grow,win,lose,ui_bounce}.wav
    font/ZCOOLKuaiLe-Regular.ttf + OFL.txt
  tools/
    export_level.mjs             # 场景/tres → JSON；--check 模式
    replay_test.mjs              # Node 规则回归
  dist/                          # 部署产物（git 忽略）
```

仓库级改动：

- `.gitattributes`：新增 `webapp/assets/** -filter -text` 例外（用户已确认），避免 3MB 级素材进入 LFS，保证静态托管可直接取文件。
- `.gitignore`：追加 `webapp/dist/`。
- `AGENTS.md` §4.4：新增 `webapp/` 一行（新代码模块同步，属文档同步，不涉及玩法权威）。
- 不修改 `project.godot`、任何关卡场景、C#/GDScript 逻辑。

## 模块设计

### 1. `src/rules.js`（唯一玩法逻辑，非权威镜像）

接口与 C# 对齐：

```js
export function coverLevel(dx, dy);
export class RuleState {
  constructor(spec);       // origin,width,height,floor,obstacles,spawn,goal,foods,
                           // mechCenters,mechInitialLevels,initialMaxLen,
                           // growthInterval,foodLenGain,mechanismCap
  setEndOnDeadlock(v);     // Demo 固定 false，与 LevelState.gd 一致
  reset();                 // 等价 Start()，返回初始 claimed 格
  step(dx, dy);            // → StepOutcome
  checkDeadlock();         // "" | "无路可走… 重开吧" | "管道不够长啊… 摘不到蓝花也捡不到花瓣"
  get head/trail/foods/claimedCells/maxLen/steps/finished/g/growthCountdown/mechLevels();
}
```

`StepOutcome = { accepted, truncated, ateFood, foodCell, grew, won, deadlockMsg, addedMechCells }`。

必须逐条对齐的语义：

1. `ClassifyMove` 顺序：越界 → 身体（截断）→ 非地板/障碍/机关格 → `trail.length >= maxLen` → 可走。
2. 截断：`steps+1`、不吃食物；之后照常判断生长/胜利/死局。
3. 吃食物：先 push、再 `maxLen += gain`、再 `steps++`。
4. 仅 accepted 后：`steps % interval === 0` 先 `Grow()`，再判 `head === goal`，再 `checkDeadlock()`。
5. `Grow()`：`g=min(g+1,gMax)`；`shielded` 只在 `Start/Grow` 重算；`added = after - before`；`mechCells` 缓存到下一次 Grow（不是每步重算）。
6. 覆盖表：只对地板格且排除 spawn/goal；`coverLevel(dx,dy)=max(0,max(2(|dx|+|dy|)-2,2max(|dx|,|dy|)-1))`；`mecCoverStep=max(0,lv-initial)`。
7. 死局：先 `head→goal` 连通；再仅在 `trail.length === maxLen` 时，用从 spawn 出发、忽略身体/身长、只避 bounds/非地板/障碍/机关格的 BFS，判断 `maxLen-1` 步内能否到终点或任一剩余食物。
8. `won` 后 `finished=true`；死局不锁移动（`endOnDeadlock=false`），由表现层显示失败且抑制胜利。

### 2. `src/render.js`

```js
export async function loadImages(imageMap);
export class Renderer {
  constructor(canvas, levelData, images);
  resize(cssW, cssH, dpr);
  draw(rule, nowMs);
  beginGrow(addedCells, durationMs);   // 机关三帧 + 核心脉冲
  resetAnimations();
}
```

- 逻辑坐标固定 1280×720；backing store 按 `css*dpr`（上限约 2560×1440），`setTransform` + `imageSmoothingEnabled=false`。
- 绘制顺序：BG → Ground → Obstacles → 食物/终点（Spawn 不画）→ MechanismCells → 身体 → Tail → Core → Head。
- 47-blob：位序 `TL,T,TR,L,R,BL,B,BR`；角位折叠 `TL需T+L / TR需T+R / BL需L+B / BR需R+B`；内嵌固定 `maskCols[47]` 查表。地面/墙体用导出 col，并在启动时用格集重算校验；机关层每次 claimed 变化重算。
- 机关：完成帧 row2；新增格按 row0→row1→row2，各 `growAnimSec/3`；核心 0.1s 内 1→1.18→1 脉冲。
- 核心负等级染色：离屏 canvas 画核心，`multiply` 填 `white.lerp(rgb(0.2,0.38,0.95), -lv/10)`，再 `destination-in` 恢复 alpha；按等级缓存。
- 身体：中间格 `a=cur-prev`、`b=next-cur`；`a===b` 横/竖列 0/1，否则按 `b-a` 取 2/3/4/5；首尾格端点列 E/W/S/N = 6/7/8/9。
- 头：3 列×4 行、帧 32×64；行 E/W/S/N=0/1/2/3；每次有效步 col0→0.18s→col1→0.35s 回 col2；绘制中心 `(cx, cy-16)`。
- 尾：32×32 以格中心为原点，按 `atan2(out.y,out.x)` 旋转；n=1 默认朝右。
- 每步瞬移，无补间；背景 `BG.png` 铺 (0,0,1280,720)。

### 3. `src/input.js`

- 键盘：方向键/WASD，忽略 `event.repeat`；`R` 重开、`Esc/P` 暂停、`M` 静音；方向键 `preventDefault`。
- 滑动：只绑 canvas 的 `pointerdown/pointerup`，阈值 24–28 CSS px，取主轴方向。
- 虚拟方向键：`pointerdown` 触发一步，不绑定 click，不做长按连走。
- `window.blur` 清按键；`visibilitychange` 由 `main.js` 自动暂停并强制结束未完成生长。

### 4. `src/ui.js` + `index.html`

- 顶栏三项：`身长 x/y`、`花瓣效益 +8`、`距生长 N`；按钮：重开、暂停、音效、全屏。
- 失败横幅：`本关已失败，按 R 或点「重开」重试本关卡`；失败后仍可走格，但不弹胜利。
- 胜利面板：`胜利！ 步数 N` +「再玩一次」「分享」；无下一关/选关/评分。
- 暂停面板：继续、重开、音效；打开时屏蔽方向输入。
- 面板打开时 R 被忽略（与原版 `is_modal_open` 一致）；所有按钮点击播放按钮音。
- HUD 用 DOM，舞台 1280×720 等比缩放居中；手机横屏优先，竖屏显示可关闭的「请横屏」提示；`100dvh` + `env(safe-area-inset-*)`、`touch-action:none`、`overscroll-behavior:none`。

### 5. `src/audio.js`

- WebAudio：`fetch + decodeAudioData`；首次 `pointerdown/keydown` 创建/`resume` 并播 1 帧静音；静音状态存 `localStorage`。
- 音量：`gain = 10^(db/20)`；参数取导出 JSON：step(-14dB, poly3, pitch 0.95–1.06)、pick(-10dB, poly2)、grow(-11dB, poly2, 0.97–1.03)、win(-9dB)、lose(-9dB)、uiBounce(-20.9dB, pitch 1.37, poly4)。
- grow 与 win 同一步允许重叠（与原版一致）。

### 6. `src/main.js`

- 状态机：`loading → playing ⇄ paused → won | failed`；`failed` 不阻塞移动但抑制胜利。
- step 管线严格按 `Level.gd._step`：rules.step → 拒绝直接返回（无音效）→ 走格音/吃食物音 → 头帧/身体/HUD 同步 → grew 则锁 100ms 播机关动画与 grow 音 → won 立即播 win 并弹面板 → 未生长且 deadlockMsg 非空则失败横幅 + lose 音。
- `?debug=1` 暴露 `window.__demo = { step(name), restart(), snapshot() }`；`?freeze=1` 冻结动画时钟便于截图。

## 数据导出（`tools/export_level.mjs`）

解析内容：

- `Level_06_*.tscn`：根节点导出字段；Ground/Obstacles 的 `tile_map_data`（前 2 字节 uint16 version，之后每 12 字节小端 6×int16：x,y,source,atlas_x,atlas_y,alternative）；实体 `cell` 与 `initial_level`。
- `TerrainGrassBlob47.tres`（并用 Wall/Mech 交叉校验）→ 47 列 peering bit → `maskCols`。
- `Balance.tres` + 场景 override → 生效数值（L0=6、gain=8、interval=5、cap=4、growAnimSec=0.1）。
- 实体预制与图集：Goal 偏移 `(0,-8)`、身体 10 列、头 3×4 帧、音频元数据、图片尺寸。

导出断言（任一失败非零退出）：

- Ground 283 格、Obstacles 64 格、origin `(7,4)`、30×16；逐格 `atlas_x` 与重算 mask 查表 0 差异；三套地形 maskCols 完全一致；机关图高 3×32。
- spawn/goal 各 1；food 5、mechanism 6；实体 `position == cell*32+16`；实体格在地板内且非障碍。
- `gMax = max(cap - initial) = 12`；写入 `source_sha256`（场景、RuleState.cs、Balance、地形 tres）。
- `--check`：重新生成与 `data/level_06.json` 比较，不一致打印摘要并非零退出。

JSON 结构要点：`level.spec`（与 LevelSpec 同形）、`blob.maskCols/cornerGates`、`layers.ground/obstacles.cells[{x,y,col}]`、`render`（机关帧、头身尾图集、动画参数）、`assets.images/sfx/font`。

## 验证

### Node（必跑）

```bash
node webapp/tools/export_level.mjs --check
node webapp/tools/replay_test.mjs
```

`replay_test.mjs` 断言：

1. 用 `tests/fixtures/anchors/level_01..05.json` + `levels_expected.json` 重放 1–5 关，`won/steps/route` 逐字一致、全程无死局。
2. 用 `data/level_06.json` 的 spec 重放 60 步路线：
   `DWDDSDDSDDSSDDDDDWDWDDDWDDDDAAASSSSASAAWAAWSDSDDDDDDDDDDDSDD`
   - 第 5/12/20/28 步吃食物；第 20 步 g=4；第 60 步到终点且 g=12。
   - 12 次生长新增格数 `[2,3,4,1,5,8,11,8,22,9,3,5]`。
   - 终局 `steps=60, won=true, maxLen=38, trailLen=35`，剩余食物仅 `(21,19)`。
3. JSON 完整性（mask 表 47 项、逐格 col、实体合法性、gMax=12）。

### 浏览器

```bash
cd webapp
python -m http.server 8080
```

- 桌面：console 无 error、Network 无 404；开局 HUD `6/6`、`+8`、倒计时 5；开局 5 格机关出场。
- 用 `?debug=1` 重放 60 步（每步等待 >110ms），`snapshot()` 对上 Node 终局；胜利文案「胜利！ 步数 60」。
- 撞墙/撞花/越界不计步；走身体触发截断计 1 步；失败后走到终点不弹胜利；面板打开时 R 无效。
- 移动仿真（844×390 横屏、390×844 竖屏）：方向键/滑动各只走一步；生长锁期间输入无效；竖屏提示可关闭且无滚动；首次触摸后音效可播；切后台回来为暂停态。
- `?freeze=1` 截图与 Godot 同状态开局截图人工并排比对（结构/坐标一致，不做像素级自动 diff）。
- 确认 Godot 编辑器不扫描 `webapp/`（无新增 `.import`）。

## 实施顺序与验收

1. 脚手架与隔离：`webapp/`、`.gdignore`、`index.html`、`styles.css`、`package.json`、空模块。验收：本地服务无 404/console error。
2. 导出器 + 数据 + 素材复制：`export_level.mjs`、`level_06.json`、素材与 OFL。验收：断言全过、`--check` 通过。
3. `rules.js` 移植。验收：1–5 关黄金路线全部一致。
4. 第 6 关 60 步回归。验收：全部断言通过；故意注入错误规则时测试能失败（自检一次）。
5. 静态渲染：背景/地面/墙体/食物/终点 + blob 校验。验收：`?freeze=1` 与 Godot 开局比对一致。
6. 玩家与机关动态：身体/端点/头尾/机关层/三帧/脉冲/负等级染色。验收：60 步重放视觉正常，第 5 步生长动画正确。
7. HUD/面板/状态机：三项数值、失败横幅、胜利面板、暂停、重开。验收：HUD 与 rules 一致；失败抑制胜利；R 在面板打开时无效。
8. 输入与移动端：键盘/滑动/方向键、失焦、可见性暂停、横屏提示。验收：每种输入只一步；生长锁屏蔽；手机横屏可完整通关。
9. 音频/字体/全屏/分享：参数与解锁。验收：首次手势后音效可辨、静音持久化；iPhone 隐藏全屏按钮；分享降级可用。
10. 最终验证与部署：跑 `--check` + `replay_test` + 浏览器 60 步快照；尝试 Trae deployment（失败则产出 `webapp/dist/` 与上传说明）。输出改动文件、待测试点、提交信息草稿（不执行 git commit）。

## 风险与应对

| 风险 | 应对 |
| --- | --- |
| JS 规则与 C# 漂移 | 文件头标注「非权威，仅 Demo」；1:1 命名；1–5 关黄金路线 + 60 步回归；JSON 记录 `rule_sha256` |
| 工作区未提交改动导致快照漂移 | 导出记录 scene/rule/balance/tres sha256；提交后重跑 `--check` 与测试 |
| LFS 与静态托管 | `webapp/assets/** -filter -text` 例外，普通文件提交（已确认） |
| iOS Safari 限制 | 手势解锁音频；隐藏全屏按钮；`dvh` 兜底；分享用 Web Share/剪贴板 |
| Canvas 清晰度 | DPR backing store + 最近邻；允许 32px 像素格轻微不均匀 |
| 字体/BG 体积 | `font-display: swap` + 系统字体回退；BG 保持原图；后续可选 fontTools 子集化 |
| 后台计时 | `visibilitychange` 强制完成生长并进入暂停，避免卡锁 |
| 触屏双触发 | 只用 pointer 事件，不绑 click；按钮点击后 blur |

## 交付格式

完成后按 AGENTS §6 输出：`改动文件` → `改动内容`（关联章节）→ `待测试点`（场景/步骤/预期）→ `提交信息草稿`；AGENT 不执行 `git commit` / `git push`。
