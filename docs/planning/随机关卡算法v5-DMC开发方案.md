# 随机关卡算法 v5：DMC 开发方案

> 状态：**规划稿（非权威）**。本文只讨论随机关卡生成、验证与评分的算法方案，不改变任何玩法规则、数值口径或权威文档。
>
> 关联：[随机关卡算法 v4](随机关卡算法v4-整体改造方案.md) · [正式上线待办](正式上线待办.md) · [功能需求文档](../design/功能需求文档.md) · [开发文档](../开发文档.md) · [架构说明](../architecture/架构说明.md)
>
> 当前阶段：算法方向验证期。**精确求解预算暂设为无上限，生成任务总墙钟超过 180s 即中止**；性能优化推迟到算法模式确认之后。本文中的参数范围均为实验域，不是上线数值。

---

## 0. 摘要

当前 route-first 生成器的根本问题是：

- 路线先在静态地形里生成，机关、食物、诱饵只能事后适配；
- 参数域小且耦合，hard/expert 能实现的动态结构非常有限；
- 评分是生成后的标量，不能保证关卡一定带有高难结构；
- 精确求解在复杂动态关卡上容易状态爆炸。

本方案提出 **DMC（Dynamic Maze Compiler，动态迷宫编译器）**：

```text
静态舞台（编织迷宫/走廊图）
  → 动态母题（证明义务）
  → 时间表合成（L0 / Gain / I / cap / 食物 / 机关阶段）
  → 放置（S/E 最后定）
  → 时间展开证书（R1 代理 + R2 精确）
  → 局部修复
  → QD / band-first 选择
```

核心原则：

1. **难度来自时间展开状态，不来自静态死路。** 静态死路只是舞台；真正的难度是步长、身长、机关相位、身体几何共同逼出的证明。
2. **证明分带，分数带内排序。** hard/expert 由动态证明的数量与强度决定，不由分数阈值决定。
3. **物理参数与内容参数解耦。** L0 / Gain / I / cap 是“物理定律”；食物数、机关数、闸门数是“内容密度”，由母题与证明义务决定。
4. **完全信息 → 只做设计公平。** 允许诱饵、关门、死路、陷阱；只查可解、规范解包含母题、无更简单支配解、无意外捷径。
5. **验证期不优化性能。** 先确认算法方向正确，再谈求解器剪枝、预算与缓存。
6. **难度是两根轴。** 结构深度（要想通什么）决定档位；容错率（走错能不能救）只做同档排序与 expert 最低档准入，权重上限 0.30，避免用“一步错就重开”冒充高难。
7. **搜索只回答区间。** 搜索器输出“最佳解 / 下界 / 是否穷尽”；没有 `exhaustive` 不许说“无解”，没有 `gap = 0` 不许说“最优”（§6.9.4）。

---

## 1. 规则基线（以代码为准）

### 1.1 一步移动

权威实现：`Scripts/Algorithms/RuleState.cs` 的 `StepCore` / `ClassifyMove`。

判定顺序：

```text
next = head + dir
1. 越界 → 拒绝
2. next ∈ trail → 截断
3. 非地板 / 障碍 / 机关占格 → 拒绝
4. trail.Count >= maxLen → 拒绝
5. 否则 → 行走
```

- **截断**：删除 `next` 之后的轨迹，`steps += 1`，不触发吃食物。
- **行走**：追加 `next`；若为食物，移除并 `maxLen += food_len_gain`；`steps += 1`。
- 无效移动不计步、不生长、不检查死局。
- 满 `maxLen` 时仍可踩身体截断，只是不能走进空格。

### 1.2 机关生长与身体屏蔽

权威实现：`RuleState.BuildCoverStep` / `Grow` / `RefreshMechCells`，`MechanicShapes.CoverLevel`。

形状：

```text
CoverLevel(d) = max(0, max(2*(|dx|+|dy|) - 2, 2*max(|dx|,|dy|) - 1))
```

覆盖时刻：

```text
coverStep(c) = max(0, CoverLevel(c - center) - initialLevel)
gMax = max_i(cap - initialLevel_i)
```

生长：

- 每次有效移动后，若 `steps % growth_interval == 0` → 生长一次。
- `g = min(g + 1, gMax)`。
- 生长时，若某格 `coverStep <= g` 且该格当前在 `trail` 上，则加入 `shielded`，本次不占格。
- 下一次生长重新扫描：身体离开后补占。
- `spawn / goal` 永远不被机关覆盖。
- 负初始阶段：机关休眠、不占格，只参与生长倒计时；到 0 后开始占格。
- **满级后 `g` 不再增加，但每次生长仍刷新 shielded 并补格。**

### 1.3 胜利与死局

- 胜利优先：`head == goal` 直接胜利。
- 死局检查是**乐观的**：
  - 忽略身体与身长，从 `head` 到 `goal` 无通路；
  - 或身体已满 `maxLen`，且从 `spawn` 出发、忽略身体，在 `maxLen - 1` 步内到不了 `goal` 或任何剩余食物。
- 因此 `CheckDeadlock` 是必要条件，不是可解性证明；**精确求解器才是权威**。

### 1.4 精确求解器

`ExactBfsSolver` 状态：

```text
(trailId, foodMask, shieldedMask, g, phase)
```

- `trailId` 保留完整轨迹；
- `phase` 单独存在，因为 `g` 会封顶但生长周期继续；
- 转移语义与 `RuleState.StepCore` 对齐；
- 限制：食物 ≤31，机关覆盖格 ≤63，超出返回 `UNKNOWN`；
- `RequiredFoodIndex` 支持“必须吃某个食物才算赢”；
- `BoundedRouteEnumerator` 可枚举有界胜利路线数，用于解多重性/漏斗宽度近似。

### 1.5 当前生成器与耦合

当前档位（`RouteFirstGenerator.GetBand`）：

| 档位 | 目标步数 | L0 | I | cap | 机关数 |
|---|---:|---:|---:|---:|---:|
| hard | 20–28 | 3 | 5 | 2 | 2–3 |
| expert | 26–38 | 4 | 5 | 4 | 2–3 |

当前参数采样（`ParamSampler`）：

```text
L0 ∈ [3,7], Gain ∈ [2,4], I ∈ [5,8], cap ∈ [1,4]
initialLevel ∈ [minA, cap]（负值只作倒计时）
RequiredFoods = ceil((targetSteps - L0) / Gain)
```

耦合问题：

1. 食物数量由 L0/Gain/目标步数直接推导；
2. 机关数量由档位固定；
3. 全局代数过滤（`ParamSampler.Feasible`）提前拒绝大量组合；
4. 路线先于动态参数生成，后续只能局部适配。

### 1.6 对 DMC 的硬约束

| 规则事实 | DMC 设计含义 |
|---|---|
| 截断算一步，且满身长可执行 | 截断是时间资源，不是失败 |
| 身体会 shield 机关格 | 身体位置可以进入时间表 |
| 生长每 I 步发生，满级仍补格 | 闸门时机必须用 `coverStep * I` 计算 |
| 死局检查忽略身体 | 不能用 `CheckDeadlock` 证明无解 |
| 精确求解含 trail/shield/g/phase | 动态证明可直接从求解/重放提取 |
| 食物 ≤31、覆盖格 ≤63 | 单关内容密度必须受控 |
| 无等待动作 | 时间只能靠移动/截断推进，时间窗更紧 |

---

## 2. 问题定义与目标

### 2.1 当前失败模式

1. **route-first 结构性失败**：路线在无机关约束的空间里生成，天然是简单路径；机关后置只能造绕行，不能造回头/环路/截断。
2. **参数域结构性失败**：小采样域下，能同时满足长度预算、闸门时机、食物顺序的 hard 结构很少。
3. **评分结构性失败**：分数是事后标量；高分不一定有 hard 结构，低分也可能结构很强。
4. **求解成本失败**：动态关卡状态空间大，精确求解容易 `UNKNOWN`；当前用预算/时间片掩盖，不是算法层面的解决。

### 2.2 目标

- hard/expert 输出**稳定带证书**；
- 参数域可解释、可扫描、可复现；
- 生成步骤每一步都有明确算法原理与失败处理；
- 评分能解释“为什么这关难”，而不只是给一个数；
- 评分分两根轴：**结构深度**（要想通什么）与**容错率/严苛度**（走错能不能救），二者不能互相冒充；
- 验证期允许慢，但必须有 180s 安全阀；
- 不改变玩法规则与线上数值口径。

### 2.3 非目标

- 本阶段不优化求解器性能；
- 本阶段不替换 easy/normal/free 生成器；
- 本阶段不引入外部求解器 / 插件 / 原生扩展；
- 本阶段不做最终数值平衡；
- 本阶段不把规划稿内容写入权威文档。

### 2.4 验证期预算

```text
GenerationBudget = Unlimited
WallClockCutoff = 180s
```

- 超过 180s：中止本轮，返回已找到的最优候选或预证书兜底；
- 记录 `budget_aborted / work_units / max_attempt_ms`；
- 该模式只用于算法方向验证，**不是上线预算**；
- 性能优化放到 Phase D/E，在算法模式确认后进行；
- **180s 只用于“找解”，不用于“证明最优/无解”**：证明类结论走 §6.9.4 的区间契约，超时一律记为 `timeout`，不许降级成结论。

---

## 3. 参数空间重构

### 3.1 三层参数

| 层 | 变量 | 作用 | 新原则 |
|---|---|---|---|
| 物理参数 | `L0 / Gain / I / cap` | 决定题目“物理定律” | 扩大采样域，但跨参数极差受控 |
| 内容参数 | `foodCount / mechCount / gateCount / decoyCount` | 决定结构密度 | 由母题/证明义务决定，不由物理参数推导 |
| 目标参数 | `targetSteps / proofLevel` | 决定档位目标 | 软目标带，不作为食物数量公式 |

### 3.2 验证期参数外框

按当前验证目标，允许：

```text
L0    ∈ [1, 15]
Gain  ∈ [1, 15]
I     ∈ [1, 15]
cap   ∈ [1, 4]        // 规则上限 4
targetSteps ≤ 50
foodCount ∈ [0, 8]
mechCount ∈ [0, 4]
initialLevel ∈ [-10, cap]
```

注意：外框 ≠ 全部可用。以下约束用于筛掉“不合理”的组合。

### 3.3 极差约束

“极差不大”建议同时定义两种：

```text
单参数范围：
  range(L0), range(Gain), range(I) 在批次内受控

跨参数极差：
  crossRange = max(L0, Gain, I) - min(L0, Gain, I)
  初始 spread_limit = 5（扫描时可用 5 / 8 / 12 三档）
```

示例：

| L0 | Gain | I | crossRange | 判定 |
|---:|---:|---:|---:|---|
| 3 | 2 | 5 | 3 | 合理 |
| 8 | 4 | 6 | 4 | 合理 |
| 12 | 4 | 8 | 8 | 需放宽 spread_limit 才可用 |
| 15 | 1 | 15 | 14 | 高风险，需实验验证 |
| 1 | 15 | 1 | 14 | 高风险，通常节奏失衡 |

说明：

- 物理参数可以扩宽，但不要让“初始身长很小、食物增益巨大、生长间隔极端”同时出现；
- `cap` 仍守 1–4 的规则上限；
- `initialLevel` 允许负值，利用休眠期制造时间窗，但会增大求解状态空间。

### 3.4 内容参数解耦

不再使用：

```text
RequiredFoods = ceil((targetSteps - L0) / Gain)
```

作为主推导。改为：

```text
foodCount = 由母题需要（必经食物、顺序食物、诱饵食物）决定
mechCount = 由闸门/母题需要决定
gateCount = 由 Timing / Phase 母题数量决定
decoyCount = 由 Regret / Deception 目标决定
```

长度预算只在**母题级**验证：

```text
对每个必经食物 f_i（按顺序）：
  dist_prefix(f_i) ≤ L_prefix - 1
吃完后：
  dist_to_goal ≤ L_after - 1
```

其中 `dist` 用真实规则重放或 R1 模型估计，最终由 R2 精确求解确认。

### 3.5 母题级可行性

不使用全局公式拒绝参数，而是：

1. 选母题；
2. 为母题选物理参数与内容参数；
3. 用 `TimeExpandedModel`（R1）检查可达性/时机/食物预算；
4. 用 `RuleState` / `ExactBfsSolver`（R2）确认最终候选。

这样 `L0/Gain/I` 的扩宽不会直接放大无效组合，而是被母题结构过滤。

### 3.6 参数扫描与 expressive range

验证期必须做参数扫描，回答：

- 哪些 `(L0, Gain, I, cap)` 区域能稳定产出 HL2+？
- 哪些区域只产出“长但简单”的关卡？
- `foodCount / mechCount` 与 hard+ 产出率的关系；
- `targetSteps` 扩到 50 后，哪些母题仍能在 180s 内完成证书。

指标：

```text
hard+ yield
expert share
UNKNOWN rate
p50 / p95 wall-clock
motif coverage
dominance shortcut rate
solution multiplicity distribution
```

---

## 4. 算法模型调研与选型

| 模型 | 代表工作 | 优点 | 局限 | 在 DMC 中的角色 |
|---|---|---|---|---|
| 构造式 / 图语法 | Dormans & Bakkes 2011；Font et al. 2016 | 结构可控、可保证锁钥顺序 | 顺序依赖强；嵌入易失败 | 静态舞台与母题组合 |
| Generate-and-test | The Witness Challenge（Blow；BorisTheBrave 分析） | 实现简单、可解性检查直接 | 难度控制弱，时难时易 | 只做兜底/对照 |
| 搜索式（GA / MCTS） | Kartal et al. 2016；Bento et al. 2019 | 可直接优化难度/硬度 | 运行成本高、方差大 | 局部修复 / QD，不做全量 GA |
| 约束式（ASP / CP） | Sturgeon-MKIII；Refraction | 可保证 completability 与设计性质 | 建模成本高、求解慢 | 证书与设计公平，不引入外部求解器 |
| 反向生成 | Taylor & Parberry 2011；Sokoban reverse | 可解性由构造保证 | 容易“长而简单” | 兜底/诱饵，不作主引擎 |
| 时间展开 / Spacetime | Priemer et al. 2016；Vandara & Cooper 2026 | 正确表达动态机制 | 状态空间大 | R1 代理 + R2 证书 |
| QD / MAP-Elites | pyribs；QD logic puzzles | 覆盖难度带与多样性 | 需要行为维度设计 | 选择器 |
| WFC / 局部约束 | WaveFunctionCollapse | 局部风格一致、实现简单 | 不保证全局可解/难度 | 仅可选地形装饰，不作主引擎 |

**选型结论：**

- 主引擎：**动态母题 + 编织迷宫种子 + 时间表合成 + 时间展开证书**；
- 选择器：**band-first + QD/MAP-Elites**；
- 修复：**局部搜索 / EPCG 式单点修改**；
- 不采用：全量 GA、外部 ASP/CP 求解器、纯 WFC 生成。

---

## 5. 难题模板：动态母题库

### 5.1 为什么淘汰静态死路模板

完全信息、确定性、回合制下：

- 静态死路在玩家看完地图后只是“最短路/可达性”问题；
- 静态阻断岔路没有证明深度，只是地图装饰；
- 难度来自状态随步数演化：`(pos, trail, L, t, foodMask, phase)`。

因此模板必须描述**动态证明义务**，而不是静态形状。

### 5.2 母题模板结构

每个母题包含：

```text
1. 静态舞台：死路 / 环路 / 瓶颈 / 岔路 / 走廊
2. 动态不变量：L、t、g、trail、食物顺序、shield 状态
3. 证明义务：必须出现什么事件（截断/回头/过闸门/错序失败）
4. 参数包络：L0/Gain/I/cap/foodCount/mechCount 的可行区间
5. 组合规则：串联 / 并联 / 嵌套
6. 容错包络：该母题允许的偏离致命率 / 最小余量（结构相同，容错可以完全不同，见 §5.6）
7. 反例：什么样的实现不算该母题
```

### 5.3 首批母题

#### M1 LengthBoundDeadEnd

- **舞台**：单入口死路 D，端点放必经食物 F。
- **动态不变量**：
  - `dist(S, A) + len(D) ≤ L_at_entry - 1`；
  - 不吃 F 时，剩余路线超出当前 L；
  - 返回必须踩身体截断。
- **证明义务**：
  - 重放出现 `truncated`；
  - 去掉 F 后不可解或超出目标带；
  - 规范解包含 D。
- **适用**：hard 基础母题；可与 Timing 组合成 expert。

#### M2 GrowthRace

- **舞台**：主路/支路的瓶颈格 B，放机关 M。
- **动态不变量**：
  - `coverStep(B) = max(0, CoverLevel(B - center) - initialLevel)`；
  - 覆盖时刻 `t_cover = coverStep(B) * I`；
  - `t_before < t_cover ≤ t_after`，其中 `t_before/t_after` 由规范路线重放得到。
- **证明义务**：
  - 晚到一步/走错分支会被封路；
  - 去掉机关或调休眠后最优代价显著下降。
- **适用**：hard/expert 的时机核心。

#### M3 TrailLock

- **舞台**：单入口环 C 或窄走廊。
- **动态不变量**：
  - 身体填满返回路径；
  - 必须在特定格 k 截断，才能腾出通路；
  - `trail` 几何与 `maxLen` 同时约束。
- **证明义务**：
  - 重放出现截断；
  - 不截断的替代路线不可解或显著更差；
  - 规范解包含 C/k。
- **适用**：expert 核心；可验证“身体即迷宫”。

#### M4 PhaseLockedFork

- **舞台**：岔路 A，两支路各有不同机关相位。
- **动态不变量**：
  - 正确分支只在某相位区间可走；
  - 错误分支不是静态堵死，而是到达后被生长封死；
  - 分支选择与 `t`、`g` 绑定。
- **证明义务**：
  - 规范解在窗口内进入正确分支；
  - 延迟/错支不可解；
  - `Regret` 高。
- **适用**：expert。

#### M5 FoodOrderBudget

- **舞台**：多个食物分布在不同分支/环。
- **动态不变量**：
  - 每个前缀满足 `dist ≤ L_prefix - 1`；
  - 错序导致长度不足或闸门关闭；
  - 食物增益与步长互相制约。
- **证明义务**：
  - 规范解的食物顺序唯一/近似唯一；
  - 至少一个替代顺序失败；
  - `OrderProof` 成立。
- **适用**：expert；可与 M1/M2 组合。

#### M6 BodyShieldHold

- **舞台**：机关覆盖格旁的走廊/环。
- **动态不变量**：
  - 身体占据某格时，机关本次不占；
  - 身体离开后，下一次生长补占；
  - 玩家必须“保持/释放”身体位置来控制闸门。
- **证明义务**：
  - 重放中 shield 状态改变；
  - 释放时机错误会导致封路；
  - 规范解依赖 shield。
- **适用**：expert+；本作独有动态母题。

#### M7 SacrificeTruncation

- **舞台**：窄口/自锁走廊。
- **动态不变量**：
  - 身体过长会堵住唯一通路；
  - 必须主动截断缩短身体才能通过；
  - 截断消耗一步，可能触发机关生长。
- **证明义务**：
  - 重放出现“为了通过而截断”；
  - 不截断不可解；
  - 截断后长度预算仍成立。
- **适用**：hard/expert。

### 5.4 组合规则

```text
串联：S → M1 → M2 → E         （难度叠加，长度预算累加）
并联：A 点分叉，一支真、一支诱饵 （Regret/Deception）
嵌套：环中死路、闸门中食物        （Timing + Length + Truncation）
```

组合约束：

- 物理参数在组合后仍满足极差约束；
- 每个母题至少保留一个证明义务；
- 组合数量决定 band：
  - hard：1 个强母题；
  - expert：2 个强母题，或 1 强 + 高 proof depth；
  - expert+：3 个强母题。

### 5.5 反模式

- `StaticBlockedFork`：从开局就堵死，只算舞台/诱饵；
- 纯静态死路：没有长度/时机/截断证明；
- 长走廊：`Forcedness` 高但 `InfoRequirement` 低；
- 纯诱饵：只有诱饵，没有动态证明；
- 唯一被迫路径：`Forcedness` 接近 1，但玩家没有决策。

### 5.6 容错包络（严苛度）

同一结构可以做出“宽容”和“零容错”两个版本。第 6 关的实验说明了这一点：

- 旧版（走一步回路越过机关未生长区）：删掉任一机关，最优解几乎不变（`GateNecessity≈0`），偏离后多半还能救回；
- 新版（必须绕岛一圈再截断）：结构相近，但多数偏离直接不可恢复。

所以母题除了证明义务，还要声明**容错包络**：

```text
TolBudget = { fatal_rate_range, slack_min, recovery_depth_min }
```

| 母题 | 容错敏感点 | 宽容形态 | 零容错形态 |
|---|---|---|---|
| M1 LengthBoundDeadEnd | 进入死路时的长度余量 | 余量 ≥1（可多绕一步） | 余量 0（必须精确） |
| M2 GrowthRace | 通过闸门的步序窗 | 窗宽 ≥3（可犹豫一次） | 窗宽 1（晚一步封路） |
| M3 TrailLock | 截断点位置 | 多个可用截断点 | 截断点唯一 |
| M4 PhaseLockedFork | 进入分支的相位 | 相位窗宽 ≥2 | 相位窗宽 1 |
| M5 FoodOrderBudget | 食物顺序 | 两种顺序都可解 | 顺序唯一 |
| M6 BodyShieldHold | 保持/释放时机 | 可晚一次生长释放 | 时机唯一 |
| M7 SacrificeTruncation | 截断时机 | 早截断可恢复 | 晚截断即死 |

判定原则：

- 容错包络写入证书（§8.2 `tolerance` 字段），**不写进玩法**；
- hard 允许宽容（`FatalRate` 初始带 `[0.15, 0.60]`）；
- expert 要求至少一个母题落在“零容错”区，且整体 `FatalRate` 初始带 `[0.35, 0.85]`（超过 0.85 视为“一步错全盘废”，体感是惩罚不是难度）；
- 生成关必须受上下限约束；手工关不受此限。

---

## 6. 生成器分步算法原理

### 6.0 流水线

```text
Step 0  参数采样（物理/内容/目标）
Step 1  静态舞台（编织迷宫）
Step 2  母题挖掘/种子
Step 3  时间表合成
Step 4  放置（S/E 最后）
Step 5  证书（R1 + R2）
Step 6  修复
Step 7  选择（band-first + QD）
```

### 6.1 Step 0：参数采样

**输入**：tier、seed、实验参数域。

**算法**：

1. 在 `L0/Gain/I ∈ [1,15]` 外框内采样；
2. 施加 `crossRange ≤ spread_limit`；
3. `cap ∈ [1,4]`，`initialLevel ∈ [-10,cap]`；
4. `targetSteps ≤ 50`；
5. 内容参数先给候选范围，不直接由物理参数推导。

**输出**：`DmcParamProposal`。

**失败**：参数组合无法支撑任何母题 → 重采样。

### 6.2 Step 1：静态舞台

**输入**：尺寸、可走格预算、参数。

**算法**：

- 32×32 画布；可走区约 60–180 格；
- 粗网格编织迷宫：
  - 随机 DFS/Prim 生成完美迷宫；
  - braid 概率拆死端加环；
  - 走廊宽 1–2；
  - 可选小房间；
- 建立拓扑图：
  - 节点 = 交点/死端/瓶颈；
  - 边 = 走廊，带长度/宽度；
  - Tarjan 求割点/桥；
  - 双连通分量求环；
  - BFS 求距离矩阵。

**输出**：`DmcScaffold`（floor/obstacles/topology graph）。

**失败**：可走区不足/环太少/死路太少 → 重生成。

### 6.3 Step 2：母题挖掘/种子

**输入**：`DmcScaffold`、目标 tier。

**算法**：

- 挖掘：
  - 死路深度 ≥ d_min；
  - 单入口环长 ≥ C_min；
  - 桥/瓶颈格；
  - 岔路两支长度差；
  - 食物候选点。
- 匹配母题模板；
- 若稀有母题不足，做局部种子：
  - 在选定交点旁雕一条死路/环；
  - 保护约束：除入口外不允许新连接；
  - 重新验证拓扑。

**输出**：`List<DmcMotif>`。

**失败**：没有可用母题 → 换 scaffold。

### 6.4 Step 3：时间表合成

**输入**：母题、物理参数、目标步数。

**变量**：

```text
L0, Gain, I, cap, initialLevel[m],
foodOrder[], S, E, mechanism centers
```

**约束**：

```text
coverStep(c) = max(0, CoverLevel(c - center) - initialLevel)
gMax = max(cap - initialLevel)
t_cover(c) = coverStep(c) * I
长度预算：L_prefix = L0 + Σ Gain
前缀可达：dist_prefix ≤ L_prefix - 1
最终可达：dist_goal ≤ L_final - 1
闸门窗口：t_before < t_cover ≤ t_after
```

**算法**：

- 用 `GridModel.BfsDistMap` 做乐观长度检查；
- 用 `TimeExpandedModel`（R1）检查 t/g/食物计数可达性；
- 对闸门相位做小范围枚举（`initialLevel ±1..±3`）；
- 选择满足母题不变量且目标步数在带内的组合。

**输出**：`DmcSchedule`。

**失败**：长度/时机矛盾 → 回退参数或换母题。

### 6.5 Step 4：放置

**输入**：scaffold、motifs、schedule。

**规则**：

- S/E 最后定：由母题端点和拓扑决定；
- 食物：只放在母题指定节点；
- 机关中心：只放地板格，优先瓶颈/桥；
- 机关不能覆盖 spawn/goal；
- 必需食物不能在预期吃之前被机关覆盖；
- `initialLevel` 写入 `MechInitialLevels`；
- 输出标准 `LevelSpec`。

**输出**：`LevelSpec` + 规范路线占位。

### 6.6 Step 5：证书

**R1 代理**：

- `TimeExpandedModel`：可达性、时机、食物预算；
- 不建模身体几何/截断，只做快速筛。

**R2 精确**：

- `RuleState.Setup(spec); Start(); ExactBfsSolver.Solve(...)`；
- 得到规范路线；
- 用 `RuleState` 重放，记录：
  - `truncated` 事件；
  - `ate_food` 顺序；
  - `grew` 时刻；
  - 闸门格通过步数；
  - `trail.Count / maxLen`；
- 若最短路不包含母题关键格 → 母题被绕过 → 拒绝/修复；
- 去掉必需食物/机关，重新求解，验证母题必要性；
- `BoundedRouteEnumerator` 统计解多重性；
- `DesignFairnessGate` 只做 dominance / canonical 检查；
- 容错分析（§7.6）：T2 代理默认开启；T1 精确只对吸引力最高的 Top-K 决策点。

**分带**（两条轴联合判定，见 §7.6）：

| 等级 | 条件 |
|---|---|
| HL2 hard | ≥1 动态证明 + 规范解包含母题 + 无支配捷径 |
| HL3 expert | ≥2 动态证明，或 1 强证明 + 高 Regret/Info，或 1 强证明 + `D_harsh ≥ 60` |
| HL4 expert+ | ≥3 动态证明，或 2 强 + 高 proof depth |

同一档内再按 `D_struct + 0.30·D_harsh` 排序；`D_harsh` 不能单独提档。

**UNKNOWN 处理**：

- 验证期允许放宽预算；
- 仍 UNKNOWN：不进入 hard+ 输出；
- 记录 `dmc_unknown`，用于后续性能分析。

### 6.7 Step 6：修复

| 失败 | 修复 |
|---|---|
| 捷径绕过母题 | 在捷径格加墙/机关 |
| 必需食物非必需 | 降低 L0、移深食物、加闸门 |
| 闸门早/晚 1 步 | 调 `initialLevel` 或移机关中心 |
| 身体未锁住返回路 | 调环长/走廊宽/S/E |
| 食物顺序不唯一 | 加前置食物/缩短 L0 |
| 解太多 | 加约束/减少食物 |
| UNKNOWN | 缩小 scaffold/减少机关/换母题 |

修复只做局部修改，最多 N 次；失败即丢弃候选。

### 6.8 Step 7：选择

- **band-first**：HL3/HL4 优先，HL2 保底；
- 同 band 内按分数/目标步数排序；
- QD 行为维度：
  - 母题组合；
  - `targetSteps` 桶；
  - 机关数；
  - 环路数；
  - 走廊开放度；
- 配额：hard 约 60%，expert 30–40%，expert+ 少量；
- 兜底：预证书 hard 模板/缓存，不输出 easy/normal。

### 6.9 有界搜索与结论契约

**问题**：状态含完整 `trail`，直接穷举是 4^60 量级；而纯贪心 / 束搜索会系统性丢弃“先远离终点再回撤截断”的解（本项目第 6 关就是这样被误判为“无解”的）。所以搜索器必须同时改**状态表示**和**结论形式**。

#### 6.9.1 分层：宏图 + 身体摘要 + 时间量

把拓扑图上 degree-2 的走廊压成带长度的**宏边**，只在交汇点 / 死端 / 食物 / 闸门 / 截断点保留宏节点。搜索在宏层进行：

```text
MacroState = (macroNode, 身体摘要, foodMask, g, phase)
身体摘要   = { 被身体占用的宏边, 尾部在第几条宏边/距端点长度 }
```

- 开阔地板、死路内部对“身体几何”不敏感，不应进入状态身份；
- 回撤解在宏层是“同一条宏边来回”，不会因 `h` 变大而被剪；
- 命中后展开成格子级路线，仍用 `RuleState` 重放校验（展开失败则该候选作废）。

#### 6.9.2 支配剪枝（sound 条件）

只在下列条件**全部一致**时使用后缀支配：

```text
head、g、phase、foodMask、shielded 相同
且 A 的 trail 是 B 的「后缀」且 |A| < |B|
⇒ A 支配 B
```

理由：保留更近的身体意味着可用的截断点更近、占用更少，B 能做的动作 A 都能做。**方向搞反（前缀支配）会错杀**，必须写正反例单测。

#### 6.9.3 地标顺序启发（只用于找解）

食物、闸门、必经截断点构成 landmark，并带顺序约束（例如“f1 必须在 g=1 前吃到”）。用 LM-count（剩余未达成地标数）做 satisficing 引导——**不可采纳，不得用于证明最优**。

#### 6.9.4 anytime 上下界与 SearchReport

搜索器不许只返回“有/无”，必须返回区间：

```text
SearchReport {
  best_route, best_upper,     // 已找到的最好解（上界）
  lower_bound,                // OPEN 最小 f（下界）
  gap = best_upper - lower_bound,
  exhaustive: bool,           // 是否真正穷尽
  reason: "frontier_pruned" | "state_exhausted" | "timeout" | "node_cap"
}
```

纪律（写死）：

| 想要说的结论 | 必须满足 |
|---|---|
| “本关无解” | `exhaustive = true` 且 `reason = state_exhausted` |
| “规范解最优” | `gap = 0` |
| “规范解近似最优” | `gap ≤ 2` |
| 其它情况 | 只能说“未找到 ≤ N 的解”，并附 `reason` |

`beam` 的“前沿空”只能记为 `frontier_pruned`，**永远不能当作无解证据**。

#### 6.9.5 兜底：保底解

生成阶段可先用“覆盖全图的环（Hamiltonian cycle）”构造一条保底解，保证任何地形都先有解，再在其上挖陷阱 / 加闸门制造难度。保底解只用于**可行性**，不参与难度评分（它长而简单）。

#### 6.9.6 与生成器的分工

| 阶段 | 需要什么 | 用什么 |
|---|---|---|
| 生成候选 | 有解 | 见证路线 + `RuleState` 重放（O(步数)） |
| 判难度下界 | 长度 / 食物 / 闸门下界 | 松弛模型（§7.6 的 R1，忽略身体与身长） |
| 判规范解质量 | 与最优的差距 | anytime 搜索固定预算，取 `gap` |
| 判“无解” | 穷尽证明 | 仅在宏图搜索 `exhaustive = true` 时成立 |

**生成阶段永远不需要证明“没有更短解”**，只需要 `gap ≤ 2`；这把公平门从“精确最优”降为“有界搜索 + 区间判定”。

---

## 7. 评分算法原理

### 7.1 证书优先，分数其次

```text
Band = f(certificate)     // hard / expert / expert+
Score = g(dynamic metrics) // 仅用于同 band 排序
```

不再用单一分数阈值决定 hard/expert。

### 7.2 动态指标

| 指标 | 含义 | 难度方向 |
|---|---|---|
| `FunnelWidth` | 可行状态漏斗宽度 | 越窄越难 |
| `Forcedness` | 单合法步占比 | 高但不过 1 |
| `Regret` | 错误决策的代价 | 越高越难 |
| `InfoRequirement` | 证明正确解所需信息 | 越高越难 |
| `TimingTightness` | 闸门/生长余量 | 越紧越难 |
| `TruncationTightness` | 强制截断/返回余量 | 越紧越难 |
| `SolutionMultiplicity` | 胜利解数量 | 越低越难，但不能为 0/1 的被迫走廊 |
| `DominanceGap` | 规范解与最优解差 | 越小越好 |
| `ConstraintSlack` | 长度/食物余量 | 越接近 0 越难 |

**严苛度指标**（第二根轴，只做排序与 expert 准入）：

| 指标 | 含义 | 方向 |
|---|---|---|
| `SlackMin` | 沿 π* 的最小长度余量 `L_t − |trail_t|` | 越小越紧 |
| `FatalRate` | 合理偏离中导致不可恢复的比例 | 越高越严苛 |
| `RecoveryDepth` | 连续犯错仍能取胜的深度（≤3 即可） | 越低越严苛 |
| `Punisher` | 机关覆盖格落在“偏离后合理路径”上的比例 | 越高越严苛（补 `GateNecessity` 只看最优解的盲区） |
| `Fragility` | `L0/Gain/I` 各 ±1 后不可解的扰动占比 | 越高越脆 |
| `DeathAttribution` | 判负时能指出越界的步序与原因 | 覆盖率为 1 才合格 |

其中 `FatalRate` 只统计**合理偏离**：排除立即回头、排除静态距离 +≥2 的明显坏步；权重可用 `PhysarumBranchModel` / `BranchPosteriorScorer` 的吸引力先验（与 v4 §8.6 的“流量先验只做权重”一致）。

### 7.3 分数公式

两根轴分开算，再加权合成。第一根轴沿用 v4 的 noisy-OR：

```text
// 结构深度：决定档位主体
sources = [
  w_route  · RouteTightness,
  w_forced · Forcedness,
  w_info   · InfoRequirement,
  w_regret · RegretNorm,
  w_time   · TimingTightness,
  w_trunc  · TruncationTightness,
  w_mech   · MechanismTightness
]
D_struct = 100 · (1 − Π_k (1 − s_k))

// 严苛度：只在同档内排序，权重上限 0.30
D_harsh = 100 · ( 0.35·(1 − SlackNorm)
                + 0.35·FatalRate
                + 0.20·(1 − RecoveryNorm)
                + 0.10·Fragility )

D_total = clamp(D_struct + 0.30 · D_harsh, bandFloor, 95)
```

其中 `MechanismTightness = Deception + CounterfactualGap + (GateNecessity ∨ Punisher)`（§7.5）；纯装饰机关取 0。

备选（更易解释）：

```text
D_base  = max(s_k)
D_bonus = β · count(s_k > θ)
D_struct = min(100, 100·(D_base + D_bonus))
```

约束：

- `D_harsh` 不能单独提档；它只能把同一 band 内的关卡排序，以及满足 expert 最低档的准入条件（§6.6）；
- `D_struct` 与 `D_harsh` 必须双写进证书与结算诊断，便于盲评校准时分清“想得深”和“错不起”。

### 7.4 反退化

- 长走廊 `Forcedness` 高但 `InfoRequirement` 低 → 不判 hard；
- 唯一被迫路径 → 不判 expert；
- 解太多 → 降难度；
- 静态死路/阻断岔路不计入证明；
- 机关装饰但非必要 → 不计入 `MechanismTightness`；
- 容错低但结构浅（`D_harsh` 高、`D_struct` 低）→ 只是“烦”，不判 hard/expert；
- `FatalRate > 0.85` → 降档或拒绝：一步错全盘废不算难度；
- 生成关 `Fragility > 0.34` → 拒绝（`L0/Gain/I` 各 ±1 至少 4/6 仍可解），避免“看着能过、实际一刀切”；
- 机关只在偏离集上起作用（`Punisher > 0`、`GateNecessity = 0`）→ 计入 `MechanismTightness` 的一半，不算“装饰”。

### 7.5 设计公平

只查：

- 可解；
- 规范解包含目标母题；
- 规范解接近最优；
- 不存在明显更短的支配解；
- 不存在意外捷径。

允许：

- 诱饵；
- 关门；
- 死路；
- 陷阱；
- 第二条可行解（但会计入 `SolutionMultiplicity`）。

不查：

- cue coverage；
- reaction window；
- 玩家是否能“看懂”；
- `NoUnfairTrap` 之类的玩家公平性。

两个修正：

1. **机关必要性不是单项硬门**：`GateNecessity` 只测“删掉机关最优解会不会变短”，测不到“机关在偏离路径上惩罚”。改为 `GateNecessity OR Punisher ≥ 阈值` 才算机关必要，否则会误杀“惩罚偏离型”机关。
2. **死亡必须可归因**（`DeathAttribution`）：判负时能指出“是哪一步越过了不可逆边界、哪条约束不再满足”，例如“第 12 步吃完 food 后未截断，导致第 34 步在 (28,11) 处长度必然不足”。这是诊断要求，不是玩家公平性检查；生成关覆盖率必须为 1，否则“走错就要重开”会退化成背板。

### 7.6 容错率与严苛度（计算口径）

**余量**（沿 π*）：

```text
Slen(t)  = L_t − |trail_t|              // 还能伸多少格
Sgate(c) = t_cover(c) − t_pass(c)      // 通过后还能晚几步
Sfood(f) = t_cover(f) − t_eat(f)       // 被覆盖前多少步吃到
SlackMin = min_t Slen(t)
```

**容错**：

```text
A_t      = 决策点 t 的「合理偏离」集合（排除立即回头、排除静态距离 +≥2）
fatal(a) = 1  若偏离态不存在取胜路线，或恢复代价 > 剩余余量
FatalRate = Σ_t w_t · mean_{a∈A_t} fatal(a) / Σ_t w_t
Tolerance = 1 − FatalRate
RecoveryCost(a) = 最短取胜步数(偏离态) − 剩余规范步数
RecoveryDepth  = 递归「错一步后仍可取最优」的最大次数（截断到 3）
```

**机关威胁**（补 `GateNecessity` 的盲区）：

```text
Punisher(m) = |{a ∈ fatal 偏离 : m 的覆盖格落在 a 之后的合理路径上}| / |{a ∈ fatal 偏离}|
```

**脆性**：

```text
Fragility = 1 − (#{ L0±1, Gain±1, I±1 仍可解 } / 6)
```

**两档计算成本**：

| 档 | 做法 | 适用 |
|---|---|---|
| T1 精确 | 每个偏离态跑一次有界精确求解；只对吸引力最高的 3–5 个决策点 | 离线 / 批次 / 人工审计 |
| T2 代理 | `TimeExpandedModel`（R1）+ 贪心补全 + `CheckDeadlock`；采样 8 个决策点 | 运行时 / 生成筛 |

两档输出同一套字段；T2 标记 `tolerance_estimated = 1`。

**使用位置**：

- `FatalRate` → `D_harsh`；
- `Punisher` → `MechanismTightness`（与 `GateNecessity` 取并）；
- `Fragility` → 生成门（§7.4）；
- 盲评时把 `D_struct` 与 `D_harsh` 分开提问（“要想多深” / “错一步多疼”），用来验证两根轴是否真的独立。

### 7.7 校准

- 锚点：L1–L5 + 现有 hard/expert 批次；
- 盲评 0–100 + 成对比较；
- 目标：Spearman ≥0.5 先，≥0.7 后；
- 分数只用于带内排序，不承担档位判定；
- 校准样本与流程沿用 v4 §12 / 历史评分训练集方法。

---

## 8. 数据契约与接口

### 8.1 LevelSpec

保持现有规则字段不变：

```text
floor / obstacles / spawn / goal / foods / mech_centers /
mech_initial_levels / initial_max_len / food_len_gain /
growth_interval / mechanism_cap
```

新增只读诊断字段（可选）：

```text
dmc_band, dmc_motifs, dmc_proofs, dmc_params, dmc_certificate_id
dmc_d_struct, dmc_d_harsh, dmc_fatal_rate, dmc_fragility, dmc_punisher
dmc_gap, dmc_exhaustive
```

写入 `Tags` / `TagEvidence` 或 `LevelSpec` 扩展字段；不参与规则。

### 8.2 DmcCertificate

```text
band: HL2 | HL3 | HL4
solvable, replay_ok
canonical_includes_motif
length_proof, truncation_proof, timing_proof,
trail_proof, order_proof, shield_proof
dominance_gap, solution_multiplicity
funnel_width, forcedness, regret,
timing_tightness, truncation_tightness
// 第二根轴（§7.6）
slack_min, fatal_rate, recovery_depth,
punisher, fragility, tolerance_estimated,
d_struct, d_harsh
tolerance: { fatal_rate_range, slack_min, recovery_depth_min }   // 母题容错包络（§5.6）
death_attribution[]                                              // 判负归因（§7.5）
// 搜索结论契约（§6.9.4）
search: { best_upper, lower_bound, gap, exhaustive, reason }
failures[]
```

### 8.3 GenerationResult

增加：

```text
DmcBand, DmcProofs, DmcMotifs, DmcParams
```

`GenerationJob` 选择时先按 band，再按目标步数/质量。

### 8.4 版本

```text
generator_version += 1
schema_version 不变（规则字段不变）
cache key 包含 dmc + generator_version
```

---

## 9. 集成方案

| 位置 | 改动 |
|---|---|
| `GeneratorModel` | 增加 `Dmc` |
| `GenerationService.SetGeneratorModel` | 支持 `"dmc"` |
| `GenerationJob.StepAttempt` | 增加 DMC 分支 |
| `GenerationJob.Finish` | DMC 候选使用 DmcCertificate，不重复跑全量公平门 |
| `GenerationPortfolio` | hard/expert 的 structured → DMC |
| `GenerationBudget` | 验证期 `Unlimited` + 180s 熔断；上线另定 |
| `GeneratorBench` | 增加 `--generator dmc` 与 DMC 指标 |
| `Level.gd` | 原则上不改；可选显示 DMC 标签 |

easy/normal/free 保持现有 route-first / template / physarum 逻辑，不受影响。

---

## 10. 测试与验收

### 10.1 单元

- 截断 / shield / 满级刷新 / 负阶段 / 食物覆盖 / 胜负优先级；
- 拓扑割点/桥/环/死路；
- 母题正反例；
- 时间表边界；
- 证书正反例；
- 容错指标正反例：`SlackMin` / `FatalRate` / `RecoveryDepth` / `Punisher` / `Fragility`，含“机关不影响最优解但抬高 `FatalRate`”的用例；
- `RuleState` 与 `ExactBfsSolver` 差分一致性。

### 10.2 集成/批次

- 每关规范路线重放并胜利；
- 同 seed 同 spec / 同证书；
- 30 → 100 → 500 seeds；
- 指标：hard+ yield、expert share、UNKNOWN、p50/p95、母题覆盖、dominance、`FatalRate`、`Punisher`、`Fragility`。
- `--generated-smoke` 4 关。

### 10.3 验证期验收（初值）

```text
hard+ 输出带证书        100%
expert 占 hard+         ≥40%
可解 / 可重放           100%
dominance shortcut      0
UNKNOWN 进入输出         0
p95 wall-clock          ≤180s（验证期）
母题覆盖                ≥3 类
FatalRate（hard）       ∈ [0.15, 0.60]
FatalRate（expert）     ∈ [0.35, 0.85]
Fragility（生成关）      ≤ 0.34
DeathAttribution        覆盖率 100%
D_struct / D_harsh      双写并在批次报告中分列
search_gap（规范解）     ≤ 2（无 gap=0 时禁止自称最优）
“无解”结论              exhaustive=true 才允许（§6.9.4）
盲评 Spearman           ≥0.5 先，≥0.7 后
```

性能目标（上线）另定；验证期只保证安全阀与可观测。

---

## 11. 实施阶段

### Phase S — 搜索器与结论契约（先做，低风险）

对应 §6.9，不依赖新算法，只改搜索器与报告口径：

1. `SearchReport { best_upper, lower_bound, gap, exhaustive, reason }`，全仓禁止“未穷尽即无解”的措辞；
2. 现有 A\* 加“trail 后缀支配”（同 head/g/phase/food/shield）；
3. 束搜索增开“回撤通道”（额外保留 `len(trail) << depth` 的状态），仅用于找解；
4. `LevelAudit` 工具输出改为区间格式，作为后续所有关卡结论的唯一口径。

**Exit**：同 seed 同结果；对任一候选关卡，报告能区分 `frontier_pruned / state_exhausted / timeout`；L1–L5 黄金路线不回归。

### Phase A — 规则/证书基线

- `DmcReplay`、`DmcCertificate`、`DmcGraph`；
- 在 L1–L5 与 30 个现有生成关上验证证书；
- 补规则单测。

**Exit**：证书与精确求解一致；黄金路线不回归。

### Phase B — 拓扑 + 两个母题（hard）

- 编织迷宫 + 拓扑图；
- M1 LengthBoundDeadEnd、M2 GrowthRace；
- 时间表合成 + 放置 + 证书。

**Exit**：30 seeds；hard+ 输出 100% 带 HL2；尝试成功率 ≥70%；0 不可解。

### Phase C — 动态母题扩展（expert）

- M3 TrailLock、M4 PhaseLockedFork、M6 BodyShieldHold；
- 局部种子与修复；
- 参数扫描。

**Exit**：expert ≥30%；hard+ ≥90%；母题覆盖 ≥3 类。

### Phase D — 集成 + QD + 性能基线

- `GeneratorModel.Dmc` 接入 Service/Job/Portfolio/Bench；
- band-first + QD 选择；
- 记录 180s 熔断统计与最慢阶段。

**Exit**：100 seeds/档；hard+ ≥95%；expert ≥40%；0 dominance；同 seed 同结果。

### Phase E — 500 seeds + 盲评校准

- 500 seeds/档；
- 盲评与体感校准；
- 决定是否进入性能优化阶段。

**Exit**：验收阈值达成；算法方向确认；再排性能优化。

### Phase M — 宏图折叠（与 Phase C 并行，解决指数爆炸）

- 拓扑主干（§6.2 已有）→ 宏图：degree-2 走廊压成宏边，1 格瓶颈 / 贴边截断点 / 闸门保留为宏节点；
- `MacroState = (macroNode, 身体摘要, foodMask, g, phase)`；
- 宏层搜索命中后展开为格子级路线，`RuleState` 重放校验，失败则丢弃。

**Exit**：同一关卡在宏层可 `exhaustive = true`（第 6 关这类 283 格地图）；耗时比格子级下降一个数量级；展开后重放 100% 通过。

---

## 12. 风险与开放问题

| 风险 | 对策 |
|---|---|
| 精确求解爆炸 | 180s 熔断；R1 先筛；Top-K；缩小 scaffold；后期优化 |
| 参数极值导致节奏失衡 | crossRange 约束；参数扫描；母题级可行性 |
| 母题同质化 | QD、组合、非结构区域随机化 |
| 证书与体感不一致 | 盲评校准；分数只带内排序 |
| 母题被捷径绕过 | 规范解包含母题检查 + 封捷径修复 |
| 数值域调整越权 | 实验域仅用于验证；上线前与用户确认并同步权威文档/Balance |
| 3min 模式被误用 | 明确标注验证期；上线预算另定 |
| 容错过低（一步错就重开） | `FatalRate` 设上限 0.85；`DeathAttribution` 必须可归因；盲评分轴提问 |
| 参数刀锋（L0±1 即无解） | `Fragility` 门；生成关 ≤0.34；手工关仅作示例 |
| 严苛度冒充难度 | `D_harsh` 权重 ≤0.30 且不能单独提档（§6.6 / §7.3） |
| 偏离开销过大（T1 全量偏离求解） | 只对吸引力最高的 3–5 个决策点做 T1；其余走 T2 代理 |
| 搜索误判“无解”（§6.9.4） | 结论必须带 `exhaustive` / `gap`；`frontier_pruned` 一律记为“未找到” |
| 贪心 / 束搜索丢“回撤型”解 | 宏图分层（§6.9.1）+ 回撤通道；回撤解在宏层不计入 h 惩罚 |
| 支配剪枝错杀 | 仅用“后缀支配”，且 head/g/phase/food/shield 全等；正反例单测 |
| 宏折叠丢掉窄格约束 | 1 格瓶颈 / 贴边截断点 / 闸门必须保留为宏节点 |

开放问题：

1. `spread_limit` 初始取 5 还是 8？
2. `targetSteps` 上限 50 是否所有档位都适用，还是仅 hard+？
3. 食物数量上限 8 是否足够支撑 50 步路线？
4. `cap` 是否参与物理极差，还是作为独立内容强度？
5. 现有 hard/expert 目标步数带是否保留？
6. 评分是否双写 v4 分数一段时间？
7. `FatalRate` 的“合理偏离”过滤阈值（静态距离 +1 还是 +2）由哪一档开始收紧？
8. `Fragility` 的扰动集合是否加入 `cap` 与 `initialLevel`？
9. 宏图折叠粒度：按 degree-2 折叠，还是再合并短于 k 的走廊？
10. anytime 预算：规范解 gap 判定给多少 ms（200 / 500 / 1000）？

---

## 附录 A：公式

```text
CoverLevel(d) = max(0, max(2*(|dx|+|dy|)-2, 2*max(|dx|,|dy|)-1))
coverStep(c) = max(0, CoverLevel(c-center) - initialLevel)
gMax = max_i(cap - initialLevel_i)
t_cover(c) = coverStep(c) * growth_interval
L_prefix = L0 + Σ Gain
growth at t = k * growth_interval
```

---

## 附录 B：研究资料与源码库

### B.1 谜题生成 / 难度

- De Kegel & Haahr, *Procedural Puzzle Generation: A Survey*: https://doi.org/10.1109/TG.2019.2917792
- Taylor & Parberry, *Procedural Generation of Sokoban Levels*: https://ianparberry.com/techreports/LARC-2011-01.pdf
- Kartal, Sohre, Guy, *Data-Driven Sokoban Puzzle Generation with MCTS*: https://ojs.aaai.org/index.php/AIIDE/article/view/12859
- Bento, Pereira, Lelis, *Procedural Generation of Initial States of Sokoban*: https://arxiv.org/abs/1907.02548
- Sturtevant et al., *The Unexpected Consequence of Incremental Design Changes*: https://cdn.aaai.org/ojs/7421/7421-52-10727-1-2-20200923.pdf
- Shen & Sturtevant, *Generalized Entropy and Solution Information for Measuring Puzzle Difficulty*: https://ojs.aaai.org/index.php/AIIDE/article/view/31872
- Earle et al., *ScriptDoctor*: https://arxiv.org/abs/2506.06524 · 源码：https://github.com/smearle/script-doctor

### B.2 结构 / 约束 / 时空

- Dormans & Bakkes, *Generating Missions and Spaces for Adaptable Play Experiences*: http://sander.landofsand.com/publications/Dormans_Bakkes_-_Generating_Missions_and_Spaces_for_Adaptable_Play_Experiences.pdf
- Font et al., *Constrained Level Generation through Grammar-Based Evolutionary Algorithms*: http://julian.togelius.com/Font2016Constrained.pdf
- Venco & Lanzi, *An Agent-Based Approach for Procedural Puzzle Generation in Graph-Based Maps*: https://ieee-cog.org/2021/assets/papers/paper_170.pdf
- Cooper, *Sturgeon-MKIII*: https://dl.acm.org/doi/10.1145/3582437.3587205 · 源码：https://github.com/crowdgames/sturgeon-pub
- Smith et al., *A Case Study of Expressively Constrainable Level Design Automation Tools for a Puzzle Game*: https://dl.acm.org/doi/10.1145/2282338.2282370
- Vandara & Cooper, *Spacetime Constraints for Continuous Gameplay*: https://dl.acm.org/doi/10.1145/3815598.3815715 · 源码：https://github.com/crowdgames/continuous-spacetime/tree/pcg2026
- Priemer et al., *Using Graph Transformation for Puzzle Game Level Generation and Validation*: https://doi.org/10.1007/978-3-319-40530-8_14

### B.3 动态迷宫 / 身体机制

- Sturtevant et al., *A Demonstration of Anhinga: A Mixed-Initiative EPCG Tool for Snakebird*: https://dl.acm.org/doi/10.5555/3505464.3505513 · 源码：https://github.com/nathansttt/hog2/
- BorisTheBrave, *Procedural Puzzle Generation in The Witness*: https://www.boristhebrave.com/2026/11/13/procedural-puzzle-generation-in-the-witness/
- DoctorLai, *sokoban-web*（reverse-pull 生成器）: https://github.com/DoctorLai/sokoban-web

### B.4 QD / 参数空间 / 多样性

- *Level Generation with Constrained Expressive Range*: https://dl.acm.org/doi/10.1145/3723498.3723845
- *Compressing and Comparing the Generative Spaces of Procedural Content Generators*: https://arxiv.org/abs/2205.15133
- *Interactive Constrained MAP-Elites*: https://arxiv.org/abs/2003.03377
- *Procedurally Puzzling: QD-Generated Logic Grid Puzzles*: https://ojs.aaai.org/index.php/AIIDE/article/view/31873
- pyribs 文档：https://pyribs.org/ · 源码：https://github.com/icaros-usc/pyribs

### B.5 迷宫 / 其他参考

- Jamis Buck, *Maze Generation: Recursive Backtracking*（含 braided maze 说明）: https://weblog.jamisbuck.org/2010/12/27/maze-generation-recursive-backtracking
- Mazes 源码参考：https://github.com/edabot/mazes
- WaveFunctionCollapse 源码：https://github.com/mxgmn/WaveFunctionCollapse
- PuzzleScript 源码：https://github.com/increpare/PuzzleScript

### B.6 搜索加速（§6.9）

- Junghanns & Schaeffer, *Sokoban: Enhancing General Single-Agent Search Methods Using Domain Knowledge*（macro move / tunnel macro / deadlock table）: http://ai.cs.unibas.ch/research/reading_group/junghanns-schaeffer-aij2001.pdf
- Sokoban 求解器支配剪枝（keeper dominance）示例: http://raw.githubusercontent.com/mkornreich/sokoban/main/README.md
- Richter, Helmert & Westphal, *Landmarks*（LAMA 的 LM-count）: https://rakaposhi.eas.asu.edu/cse571/landmarks-tutorial.pdf
- Karpas & Domshlak, *Cost-Optimal Planning with Landmarks*（可采纳变体）: https://ijcai.org/proceedings/2009/0288.pdf
- *Anytime Heuristic Search*（上下界与 anytime WA*）: https://m.aaai.org/Papers/JAIR/Vol28/JAIR-2808.pdf
- AlphaSnake（蛇类问题的 NP-hard 性质与 Hamiltonian cycle 保底策略）: https://arxiv.org/pdf/2211.09622v1
- Hamiltonian Agent（蛇类保底解实现参考）: https://deepwiki.com/linyiLYi/snake-ai/7.3-hamiltonian-agent

---

## 附录 C：当前代码索引

| 模块 | 位置 |
|---|---|
| 规则唯一实现 | `Scripts/Algorithms/RuleState.cs` |
| 机关形状 | `Scripts/Algorithms/MechanicShapes.cs` |
| 网格/BFS | `Scripts/Algorithms/GridModel.cs` |
| 精确求解 | `Scripts/Algorithms/Search/ExactBfsSolver.cs` |
| 有界路线枚举 | `Scripts/Algorithms/Search/BoundedRouteEnumerator.cs` |
| 时间展开 R1 | `Scripts/Algorithms/Model/TimeExpandedModel.cs` |
| 生成任务/选择 | `Scripts/Algorithms/Generation/GenerationJob.cs` |
| 参数采样 | `Scripts/Algorithms/Generation/ParamSampler.cs` |
| route-first | `Scripts/Algorithms/Generation/RouteFirstGenerator.cs` |
| Portfolio | `Scripts/Algorithms/Generation/GenerationPortfolio.cs` |
| 设计公平门 | `Scripts/Algorithms/Model/DesignFairnessGate.cs` |
| 运行时入口 | `Scripts/Algorithms/Generation/GenerationService.cs` |
| 测试 | `tests/core/CoreTests.cs` |

---

## 附录 D：待校准参数

| 参数 | 初始值 | 扫描范围 | 说明 |
|---|---:|---:|---|
| `spread_limit` | 5 | 5 / 8 / 12 | 跨参数极差 |
| `targetSteps` 上限 | 50 | 38 / 50 | hard+ 目标 |
| `L0` | 3–8 | 1–15 | 初始身长 |
| `Gain` | 2–4 | 1–15 | 食物增益 |
| `I` | 5–8 | 1–15 | 生长间隔 |
| `cap` | 1–4 | 1–4 | 规则上限 |
| `foodCount` | 1–8 | 0–8 | 内容密度 |
| `mechCount` | 0–4 | 0–4 | 内容密度 |
| 180s 熔断 | 180s | 120 / 180 / 300 | 验证期安全阀 |
| `FatalRate`（hard） | 0.15–0.60 | 0.10–0.70 | 严苛度带 |
| `FatalRate`（expert） | 0.35–0.85 | 0.25–0.90 | 严苛度带 |
| `Fragility` 上限 | 0.34 | 0.20 / 0.34 / 0.50 | 生成关门 |
| `D_harsh` 权重 | 0.30 | 0.20 / 0.30 / 0.40 | 合成上限 |

---

## 附录 E：第 6 关审计锚点（实测）

> 用途：作为 `D_struct / D_harsh` 的校准锚点与 `LevelAudit` 工具的基线用例。数值为一次性离线审计结果（Python 版规则复刻，已用 L1–L5 黄金快照校准：9/12/19/17/19 全部一致）。

**关卡**：`Level_06_ASampleOfHardLevel`（`L0=6, Gain=8, I=5, cap=4`，6 食物、6 机关）

| 项 | 结果 |
|---|---|
| 最短解 | 44 步（A\*，可采纳一致启发式，4.2M 节点） |
| 事件 | 4 次吃食、4 次截断、8 次生长 |
| 纯几何下界（忽略机关/身长） | 32 步 |
| 只加机关（身长无限） | 34 步 |
| 只加身长（无机关） | 40 步 |
| 食物必要性 | 4/6 必需；(23,19)、(19,15) 为纯诱饵 |
| 机关必要性（`GateNecessity`） | 1/6（仅开局机关 (11,8) 影响最优解） |
| 禁截断求解 | 不可解（90 万节点穷尽）→ 截断为结构必需 |
| 分支度（沿 π*） | 平均 3.47，最小 2，无“唯一合法步” |
| 参数脆性 | `L0=5` 无解；`L0=7` → 42；`Gain=9` → 42；`I=6` → 44 |

**结论**：这是一关“结构成立但容错被稀释”的样本——`D_struct` 中等（1 个 LengthBoundDeadEnd 族 + 1 个 GrowthRace），`D_harsh` 因 `L0` 刀锋与开局限时取食而偏高；`Punisher` 低（5/6 机关对偏离无威胁）。正好用于标定“hard 高档 / expert 最低档”的门槛。

> 注：上表为旧版审计。该关后续迭代过两次（食物位置与机关阶段调整），锚点数值需在版本冻结后重跑。

---

## 附录 F：误判复盘 —— 第 6 关“无解”结论是怎么错的

> 用途：把一次真实误判固化成纪律，避免重演。对应 §6.9.4 的结论契约。

| 环节 | 当时做了什么 | 错在哪 |
|---|---|---|
| 搜索方法 | 束搜索，评分 = `depth + h(几何/相位) + 长度缺口 + 食物绕路`，每层保留前 K | 正解需要“先远离终点再原路回撤截断”，回撤时 `h` 单调变大，整族状态被剪 |
| 结论 | 见“深度 65 前沿空”，判为无解 | “前沿空”= beam 池被筛空（`frontier_pruned`），不是状态空间耗尽 |
| 推理 | 由一条“不回撤”路线族推出恒等式「终点前身长 = 39 > L = 38」 | 该恒等式只对不回撤族成立；正解靠回撤把身长压到 20 出头 |
| 证据处理 | 先怀疑关卡，后看工作区里的通关记录 | 应先用已有证据证伪自己的模型 |
| 数据读取 | 记录里 `spec.foods` 只剩 `(21,19)`，一度以为关卡只有一片食物 | 实为快照 bug（记录“剩余食物”），已修 |

**固化的三条纪律**：

1. 搜索失败只能说“未找到 ≤ N 的解”，并附 `reason`；`exhaustive = false` 时禁止“无解”。
2. 任何“最优/最短”结论必须 `gap = 0`；否则只能说“近似最优（gap ≤ k）”。
3. 贪心 / 束搜索不能作为难度或可解性的判据；结构结论必须来自宏图（§6.9.1）或松弛下界。
