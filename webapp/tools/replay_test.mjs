#!/usr/bin/env node
/**
 * 规则回归：
 *   1. 用 webapp/src/rules.js 重放 1–5 关官方黄金路线（tests/fixtures）。
 *   2. 重放第 6 关 60 步路线并校验事件/终局。
 *   3. 校验 level_06.json 的 47-blob 与实体数据。
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { RuleState, cellKey } from '../src/rules.js';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');
const DATA_REL = 'webapp/data/level_06.json';

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

function keyLabel(key) {
  return `(${key})`;
}

// ── 47-blob 计算（与 export_level.mjs / Level.gd 一致）──────────
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
function testGoldenLevels() {
  const expected = readJson('tests/fixtures/levels_expected.json');
  for (const level of expected.levels) {
    const anchorRel = `tests/fixtures/anchors/level_${String(level.level_id).padStart(2, '0')}.json`;
    const anchor = readJson(anchorRel);
    const rule = new RuleState(anchor);
    rule.reset();

    let replayed = '';
    let deadlock = '';
    for (const ch of level.route) {
      const dir = DIRS[ch];
      assert(Boolean(dir), `L${level.level_id}: 未知路线字符 ${ch}`);
      if (!dir) break;
      const outcome = rule.step(dir[0], dir[1]);
      replayed += ch;
      assert(outcome.accepted, `L${level.level_id}: 第 ${replayed.length} 步 ${ch} 被拒绝`);
      if (!outcome.accepted) break;
      if (outcome.deadlockMsg && !outcome.won) deadlock = outcome.deadlockMsg;
      assert(!deadlock, `L${level.level_id}: 第 ${replayed.length} 步出现死局 ${deadlock}`);
      if (deadlock) break;
    }

    assert(replayed === level.route, `L${level.level_id}: 路线重放不一致 ${replayed} ≠ ${level.route}`);
    assert(rule.steps === level.steps, `L${level.level_id}: steps=${rule.steps}，期望 ${level.steps}`);
    assert(
      rule.head[0] === anchor.goal[0] && rule.head[1] === anchor.goal[1],
      `L${level.level_id}: 终点未到达，head=${cellLabel(rule.head)}`
    );
    assert(rule.finished, `L${level.level_id}: 未标记 finished`);
    console.log(`  L${level.level_id} ${level.name}: ${rule.steps} 步 OK`);
  }
}

// ── 2. 第 6 关 60 步路线 ──────────────────────────────────────
const LEVEL6_ROUTE = 'DWDDSDDSDDSSDDDDDWDWDDDWDDDDAAASSSSASAAWAAWSDSDDDDDDDDDDDSDD';
const LEVEL6_EXPECTED = {
  steps: 60,
  foodSteps: [5, 12, 20, 28],
  foodCells: [
    [12, 7],
    [16, 10],
    [22, 8],
    [29, 7],
  ],
  growAdded: [2, 3, 4, 1, 5, 8, 11, 8, 22, 9, 3, 5],
  finalMaxLen: 38,
  finalTrailLen: 35,
  finalG: 12,
  remainingFood: [21, 19],
};

function testLevel6(data) {
  const rule = new RuleState(data.level.spec);
  rule.reset();
  assert(rule.gMax === 12, `L6: gMax=${rule.gMax}，期望 12`);
  assert(rule.growthCountdown === 5, `L6: 开局倒计时=${rule.growthCountdown}，期望 5`);

  const foodSteps = [];
  const foodCells = [];
  const growAdded = [];
  let deadlock = '';
  let won = false;

  for (let i = 0; i < LEVEL6_ROUTE.length; i++) {
    const ch = LEVEL6_ROUTE[i];
    const dir = DIRS[ch];
    const outcome = rule.step(dir[0], dir[1]);
    assert(outcome.accepted, `L6: 第 ${i + 1} 步 ${ch} 被拒绝`);
    if (!outcome.accepted) break;
    if (outcome.ateFood) {
      foodSteps.push(i + 1);
      foodCells.push(outcome.foodCell);
    }
    if (outcome.grew) growAdded.push(outcome.addedMechCells.length);
    if (outcome.won) {
      won = true;
      assert(i + 1 === LEVEL6_ROUTE.length, `L6: 第 ${i + 1} 步提前胜利`);
      break;
    }
    if (outcome.deadlockMsg) {
      deadlock = outcome.deadlockMsg;
      break;
    }
    if (outcome.grew && i + 1 === 20) {
      assert(rule.g === 4, `L6: 第 20 步生长后 g=${rule.g}，期望 4`);
    }
  }

  assert(!deadlock, `L6: 路线中途死局：${deadlock}`);
  assert(won, 'L6: 路线未胜利');
  assert(rule.steps === LEVEL6_EXPECTED.steps, `L6: steps=${rule.steps}，期望 ${LEVEL6_EXPECTED.steps}`);
  assert(
    rule.head[0] === data.level.spec.goal[0] && rule.head[1] === data.level.spec.goal[1],
    `L6: 终点未到达，head=${cellLabel(rule.head)}`
  );
  assert(rule.maxLen === LEVEL6_EXPECTED.finalMaxLen, `L6: maxLen=${rule.maxLen}，期望 ${LEVEL6_EXPECTED.finalMaxLen}`);
  assert(
    rule.trail.length === LEVEL6_EXPECTED.finalTrailLen,
    `L6: trailLen=${rule.trail.length}，期望 ${LEVEL6_EXPECTED.finalTrailLen}`
  );
  assert(rule.g === LEVEL6_EXPECTED.finalG, `L6: g=${rule.g}，期望 ${LEVEL6_EXPECTED.finalG}`);
  assert(
    JSON.stringify(foodSteps) === JSON.stringify(LEVEL6_EXPECTED.foodSteps),
    `L6: 吃食物步=${JSON.stringify(foodSteps)}，期望 ${JSON.stringify(LEVEL6_EXPECTED.foodSteps)}`
  );
  assert(
    JSON.stringify(foodCells) === JSON.stringify(LEVEL6_EXPECTED.foodCells),
    `L6: 吃食物格=${JSON.stringify(foodCells)}，期望 ${JSON.stringify(LEVEL6_EXPECTED.foodCells)}`
  );
  assert(
    JSON.stringify(growAdded) === JSON.stringify(LEVEL6_EXPECTED.growAdded),
    `L6: 生长新增=${JSON.stringify(growAdded)}，期望 ${JSON.stringify(LEVEL6_EXPECTED.growAdded)}`
  );
  assert(rule.foods.size === 1, `L6: 剩余食物 ${rule.foods.size}，期望 1`);
  const remaining = [...rule.foods][0] ?? '';
  assert(
    remaining === cellKey(LEVEL6_EXPECTED.remainingFood[0], LEVEL6_EXPECTED.remainingFood[1]),
    `L6: 剩余食物=${keyLabel(remaining)}，期望 ${cellLabel(LEVEL6_EXPECTED.remainingFood)}`
  );
  console.log(
    `  L6 ${data.level.name}: ${rule.steps} 步 OK，maxLen=${rule.maxLen}，trail=${rule.trail.length}，g=${rule.g}`
  );
}

// ── 3. JSON 完整性 ────────────────────────────────────────────
function testDataIntegrity(data) {
  assert(data.schema_version === 1, `schema_version=${data.schema_version}`);
  assert(data.level.id === 6, `level.id=${data.level.id}`);
  assert(
    data.level.origin[0] === 7 && data.level.origin[1] === 4,
    `origin=${JSON.stringify(data.level.origin)}`
  );
  assert(data.level.width === 30 && data.level.height === 16, `board=${data.level.width}×${data.level.height}`);
  assert(data.level.spec.floor.length === 283, `floor=${data.level.spec.floor.length}`);
  assert(data.level.spec.obstacles.length === 64, `obstacles=${data.level.spec.obstacles.length}`);
  assert(data.level.spec.foods.length === 5, `foods=${data.level.spec.foods.length}`);
  assert(data.level.spec.mech_centers.length === 6, `mechs=${data.level.spec.mech_centers.length}`);
  assert(data.render.growAnimSec === 0.1, `growAnimSec=${data.render.growAnimSec}`);

  const maskCols = data.blob.mask_cols;
  assert(Array.isArray(maskCols) && maskCols.length === 47, `mask_cols 长度=${maskCols?.length}`);
  assert(new Set(maskCols).size === 47, 'mask_cols 存在重复');
  assert(maskCols[0] === 0 && maskCols[46] === 255, 'mask_cols 首尾不是 0/255');
  const maskToCol = new Map(maskCols.map((mask, col) => [mask, col]));

  for (const [layerName, layer] of Object.entries(data.layers)) {
    const set = new Set(layer.cells.map((c) => cellKey(c.x, c.y)));
    for (const cell of layer.cells) {
      const col = maskToCol.get(blobMask(set, cell.x, cell.y));
      assert(col === cell.col, `${layerName} (${cell.x},${cell.y}) col=${cell.col}，重算=${col}`);
      if (col !== cell.col) break;
    }
  }

  const floorSet = new Set(data.level.spec.floor.map((c) => cellKey(c[0], c[1])));
  const obstacleSet = new Set(data.level.spec.obstacles.map((c) => cellKey(c[0], c[1])));
  const allEntities = [
    ['spawn', data.entities.spawn.cell],
    ['goal', data.entities.goal.cell],
    ...data.entities.foods.map((f, i) => [`food${i + 1}`, f.cell]),
    ...data.entities.mechs.map((m, i) => [`mech${i + 1}`, m.cell]),
  ];
  for (const [label, cell] of allEntities) {
    assert(floorSet.has(cellKey(cell[0], cell[1])), `${label} 不在 Ground 上`);
    assert(!obstacleSet.has(cellKey(cell[0], cell[1])), `${label} 压在障碍上`);
  }
  console.log('  JSON 完整性 OK');
}

function main() {
  const data = readJson(DATA_REL);
  console.log('规则回归：');
  testGoldenLevels();
  testLevel6(data);
  testDataIntegrity(data);

  if (failures.length > 0) {
    console.error(`\n失败 ${failures.length} 项（共 ${assertions} 个断言）：`);
    for (const message of failures) console.error(`  - ${message}`);
    process.exit(1);
  }
  console.log(`\n全部通过：${assertions} 个断言`);
}

main();
