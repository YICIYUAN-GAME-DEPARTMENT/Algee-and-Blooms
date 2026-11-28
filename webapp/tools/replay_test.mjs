#!/usr/bin/env node
/**
 * 多关卡回归：
 *   1. 用 webapp/src/rules.js 重放 L1–L5 官方黄金路线（tests/fixtures）。
 *   2. 校验 levels.json 的 47-blob、实体、视图变换与关卡清单。
 *   3. L6/L7 结构检查（官方求解器 UNKNOWN，路线由人工试玩确认）。
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { RuleState, cellKey } from '../src/rules.js';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');
const DATA_REL = 'webapp/data/levels.json';

const DIRS = {
  A: [-1, 0],
  D: [1, 0],
  W: [0, -1],
  S: [0, 1],
};

let assertions = 0;
const failures = [];

function assert(condition, message) {
  assertions += 1;
  if (!condition) failures.push(message);
}

function readJson(rel) {
  return JSON.parse(fs.readFileSync(path.join(ROOT, rel), 'utf8'));
}

function cellLabel(cell) {
  return `(${cell[0]},${cell[1]})`;
}

const BLOB_DIRS = [
  [-1, -1],
  [0, -1],
  [1, -1],
  [-1, 0],
  [1, 0],
  [-1, 1],
  [0, 1],
  [1, 1],
];
const BLOB_CORNER_GATES = [
  [0, 1, 3],
  [2, 1, 4],
  [5, 3, 6],
  [7, 4, 6],
];

function blobMask(set, x, y) {
  let mask = 0;
  for (let i = 0; i < BLOB_DIRS.length; i++) {
    const [dx, dy] = BLOB_DIRS[i];
    if (set.has(cellKey(x + dx, y + dy))) mask |= 1 << i;
  }
  for (const [corner, sideA, sideB] of BLOB_CORNER_GATES) {
    if ((mask >> corner) & 1) {
      if (!((mask >> sideA) & 1) || !((mask >> sideB) & 1)) mask &= ~(1 << corner);
    }
  }
  return mask;
}

// ── 1. 官方 1–5 关黄金路线 ────────────────────────────────────
function testGoldenLevels(levelsById) {
  const expected = readJson('tests/fixtures/levels_expected.json');
  for (const levelExpected of expected.levels) {
    const level = levelsById.get(levelExpected.level_id);
    assert(Boolean(level), `L${levelExpected.level_id}: levels.json 中缺少该关`);
    if (!level) continue;
    const rule = new RuleState(level.spec);
    rule.reset();

    let replayed = '';
    let deadlock = '';
    for (const ch of levelExpected.route) {
      const dir = DIRS[ch];
      assert(Boolean(dir), `L${level.id}: 未知路线字符 ${ch}`);
      if (!dir) break;
      const outcome = rule.step(dir[0], dir[1]);
      replayed += ch;
      assert(outcome.accepted, `L${level.id}: 第 ${replayed.length} 步 ${ch} 被拒绝`);
      if (!outcome.accepted) break;
      if (outcome.deadlockMsg && !outcome.won) deadlock = outcome.deadlockMsg;
      assert(!deadlock, `L${level.id}: 第 ${replayed.length} 步出现死局 ${deadlock}`);
      if (deadlock) break;
    }

    assert(replayed === levelExpected.route, `L${level.id}: 路线重放不一致`);
    assert(rule.steps === levelExpected.steps, `L${level.id}: steps=${rule.steps}，期望 ${levelExpected.steps}`);
    assert(
      rule.head[0] === level.spec.goal[0] && rule.head[1] === level.spec.goal[1],
      `L${level.id}: 终点未到达，head=${cellLabel(rule.head)}`
    );
    assert(rule.finished, `L${level.id}: 未标记 finished`);
    console.log(`  L${level.id} ${level.name}: ${rule.steps} 步 OK`);
  }
}

// ── 2. L6/L7 结构检查（无路线） ───────────────────────────────
function testAdvancedLevels(levelsById) {
  for (const id of [6, 7]) {
    const level = levelsById.get(id);
    assert(Boolean(level), `L${id}: levels.json 中缺少第 ${id} 关`);
    if (!level) continue;
    const rule = new RuleState(level.spec);
    rule.reset();
    assert(rule.gMax > 0, `L${id}: gMax=${rule.gMax}，期望 >0`);
    assert(rule.checkDeadlock() === '', `L${id}: 开局即死局：${rule.checkDeadlock()}`);
    console.log(
      `  L${id} ${level.name}: 结构 OK（${level.spec.width}×${level.spec.height}，食物 ${level.spec.foods.length}，机关 ${level.spec.mech_centers.length}，gMax=${rule.gMax}）`
    );
  }
}

// ── 3. 数据完整性 ─────────────────────────────────────────────
function testDataIntegrity(data) {
  assert(data.schema_version === 2, `schema_version=${data.schema_version}`);
  assert(
    Array.isArray(data.viewport) && data.viewport[0] === 1920 && data.viewport[1] === 1080,
    `viewport=${JSON.stringify(data.viewport)}`
  );
  assert(Array.isArray(data.levels) && data.levels.length === 7, `levels=${data.levels?.length}`);
  const ids = data.levels.map((level) => level.id).sort((a, b) => a - b);
  assert(JSON.stringify(ids) === JSON.stringify([1, 2, 3, 4, 5, 6, 7]), `level ids=${JSON.stringify(ids)}`);

  const maskCols = data.blob.mask_cols;
  assert(maskCols.length === 47, `mask_cols 长度=${maskCols?.length}`);
  assert(maskCols[0] === 0 && maskCols[46] === 255, 'mask_cols 首尾不是 0/255');
  const maskToCol = new Map(maskCols.map((mask, col) => [mask, col]));

  for (const level of data.levels) {
    const label = `L${level.id}`;
    assert(typeof level.name === 'string' && level.name.length > 0, `${label}: 缺少名称`);
    assert(level.view, `${label}: 缺少 view`);
    if (level.view) {
      for (const [key, pair] of Object.entries({
        worldScale: level.view.world?.scale,
        worldPosition: level.view.world?.position,
        cameraPosition: level.view.camera?.position,
        cameraZoom: level.view.camera?.zoom,
        backgroundPosition: level.view.background?.position,
        backgroundScale: level.view.background?.scale,
      })) {
        assert(Array.isArray(pair) && pair.length === 2 && pair.every((n) => Number.isFinite(n)), `${label}: view.${key} 非法`);
      }
      assert(level.view.world.scale[0] > 0 && level.view.world.scale[1] > 0, `${label}: world.scale 非正`);
      assert(level.view.camera.zoom[0] > 0 && level.view.camera.zoom[1] > 0, `${label}: camera.zoom 非正`);
    }

    const spec = level.spec;
    assert(Array.isArray(spec.floor) && spec.floor.length > 0, `${label}: floor 为空`);
    assert(Array.isArray(spec.obstacles), `${label}: obstacles 缺失`);
    assert(spec.spawn && spec.goal, `${label}: 缺少 spawn/goal`);
    assert(Array.isArray(spec.foods), `${label}: foods 缺失`);
    assert(Array.isArray(spec.mech_centers), `${label}: mech_centers 缺失`);
    assert(spec.mech_initial_levels.length === spec.mech_centers.length, `${label}: 机关中心/等级数量不一致`);

    for (const [layerName, layer] of Object.entries(level.layers)) {
      const set = new Set(layer.cells.map((c) => cellKey(c.x, c.y)));
      const specCells = new Set((layerName === 'ground' ? spec.floor : spec.obstacles).map((c) => cellKey(c[0], c[1])));
      assert(set.size === specCells.size, `${label} ${layerName}: 图层 ${set.size} 格，spec ${specCells.size} 格`);
      for (const key of set) assert(specCells.has(key), `${label} ${layerName}: spec 缺少 ${key}`);
      for (const cell of layer.cells) {
        const col = maskToCol.get(blobMask(set, cell.x, cell.y));
        assert(col === cell.col, `${label} ${layerName} (${cell.x},${cell.y}) col=${cell.col}，重算=${col}`);
      }
    }

    assert(level.entities.foods.length === spec.foods.length, `${label}: 食物实体数量不一致`);
    assert(level.entities.mechs.length === spec.mech_centers.length, `${label}: 机关实体数量不一致`);
    const floorSet = new Set(spec.floor.map((c) => cellKey(c[0], c[1])));
    const obstacleSet = new Set(spec.obstacles.map((c) => cellKey(c[0], c[1])));
    const blockingEntities = [
      ['spawn', level.entities.spawn.cell],
      ['goal', level.entities.goal.cell],
      ...level.entities.foods.map((f, i) => [`food${i + 1}`, f.cell]),
    ];
    for (const [name, cell] of blockingEntities) {
      assert(floorSet.has(cellKey(cell[0], cell[1])), `${label} ${name} 不在 Ground 上`);
      assert(!obstacleSet.has(cellKey(cell[0], cell[1])), `${label} ${name} 压在障碍上`);
    }
    for (let i = 0; i < level.entities.mechs.length; i++) {
      const cell = level.entities.mechs[i].cell;
      assert(floorSet.has(cellKey(cell[0], cell[1])), `${label} mech${i + 1} 不在 Ground 上`);
      // 机关允许放在障碍格：覆盖表只按地板计算，运行时核心会盖在墙上。
    }
  }
  assert(typeof data.render.growAnimSec === 'number', 'render.growAnimSec 缺失');
  console.log('  JSON 完整性 OK');
}

function main() {
  const data = readJson(DATA_REL);
  const levelsById = new Map(data.levels.map((level) => [level.id, level]));
  console.log('规则回归：');
  testGoldenLevels(levelsById);
  testAdvancedLevels(levelsById);
  testDataIntegrity(data);

  if (failures.length > 0) {
    console.error(`\n失败 ${failures.length} 项（共 ${assertions} 个断言）：`);
    for (const message of failures) console.error(`  - ${message}`);
    process.exit(1);
  }
  console.log(`\n全部通过：${assertions} 个断言`);
}

main();
