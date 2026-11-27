#!/usr/bin/env node
/**
 * 第 6 关场景 → webapp/data/level_06.json
 *
 * 只做“场景快照导出 + 一致性校验”，不参与游戏运行。任何断言失败都会非零退出，
 * 避免场景改了但 Web Demo 数据没更新。若确实要改关卡，请同步更新下方 EXPECTED
 * 快照常量后再导出。
 */
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');
const OUT_REL = 'webapp/data/level_06.json';
const OUT_ABS = path.join(ROOT, OUT_REL);
const CHECK_ONLY = process.argv.includes('--check');

const SCENE_REL = 'Scenes/Levels/Level_06_ASampleOfHardLevel.tscn';
const BALANCE_REL = 'Resources/Config/Balance.tres';
const GRASS_REL = 'Art/Tiles/TerrainGrassBlob47.tres';
const WALL_REL = 'Art/Tiles/TerrainWall.tres';
const MECH_REL = 'Art/Tiles/TerrainMech.tres';
const SNEK_REL = 'Art/Tiles/PlayerSnek.tres';
const RULE_REL = 'Scripts/Algorithms/RuleState.cs';
const SFX_SCENE_REL = 'Scenes/Audio/SfxManager.tscn';
const BUTTON_AUDIO_REL = 'Scenes/UI/ButtonAudio.tscn';
const GOAL_SCENE_REL = 'Scenes/Entities/Goal.tscn';

const EXPECTED = {
  levelId: 6,
  levelName: '难关示例',
  origin: [7, 4],
  width: 30,
  height: 16,
  floorCount: 283,
  obstacleCount: 64,
  spawn: [9, 7],
  goal: [35, 13],
  foodCount: 5,
  mechCount: 6,
  initialMaxLen: 6,
  foodLenGain: 8,
  growthInterval: 5,
  mechanismCap: 4,
  gMax: 12,
};

const errors = [];
const notes = [];

function fail(message) {
  errors.push(message);
}

function readText(rel) {
  return fs.readFileSync(path.join(ROOT, rel), 'utf8');
}

function sha256(rel) {
  return crypto.createHash('sha256').update(fs.readFileSync(path.join(ROOT, rel))).digest('hex');
}

function parseAttrs(source) {
  const attrs = new Map();
  const re = /([A-Za-z_][\w]*)\s*=\s*(?:"([^"]*)"|([^\s]+))/g;
  let m;
  while ((m = re.exec(source)) !== null) {
    attrs.set(m[1], m[2] !== undefined ? m[2] : m[3]);
  }
  return attrs;
}

function parseSections(text) {
  const re = /^\[([^\][]+)\][ \t]*$/gm;
  const marks = [];
  let m;
  while ((m = re.exec(text)) !== null) {
    marks.push({ header: m[1], bodyStart: re.lastIndex });
  }
  const sections = [];
  for (let i = 0; i < marks.length; i++) {
    const end = i + 1 < marks.length ? text.lastIndexOf('[', marks[i + 1].bodyStart) : text.length;
    sections.push({
      header: marks[i].header,
      body: text.slice(marks[i].bodyStart, end),
    });
  }
  return sections;
}

function splitHeader(header) {
  const space = header.indexOf(' ');
  if (space < 0) return [header, ''];
  return [header.slice(0, space), header.slice(space + 1)];
}

function parseProps(body) {
  const props = new Map();
  for (const rawLine of body.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line || line.startsWith(';')) continue;
    const m = /^([A-Za-z_][\w/]*)\s*=\s*(.*)$/.exec(line);
    if (m) props.set(m[1], m[2]);
  }
  return props;
}

function vec2i(value) {
  const m = /Vector2i\(\s*(-?\d+)\s*,\s*(-?\d+)\s*\)/.exec(value ?? '');
  return m ? [Number(m[1]), Number(m[2])] : null;
}

function vec2(value) {
  const m = /Vector2\(\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*\)/.exec(value ?? '');
  return m ? [Number(m[1]), Number(m[2])] : null;
}

function decodeTileMapData(value) {
  const m = /^PackedByteArray\("([^"]*)"\)$/.exec((value ?? '').trim());
  if (!m) throw new Error('tile_map_data 不是 PackedByteArray');
  const buf = Buffer.from(m[1], 'base64');
  if (buf.length < 2) throw new Error('tile_map_data 太短');
  const version = buf.readUInt16LE(0);
  if ((buf.length - 2) % 12 !== 0) {
    throw new Error(`tile_map_data 长度异常：${buf.length}（应为 2 + 12N）`);
  }
  const cells = [];
  for (let offset = 2; offset + 12 <= buf.length; offset += 12) {
    cells.push({
      x: buf.readInt16LE(offset),
      y: buf.readInt16LE(offset + 2),
      source: buf.readInt16LE(offset + 4),
      atlasX: buf.readInt16LE(offset + 6),
      atlasY: buf.readInt16LE(offset + 8),
      alternative: buf.readInt16LE(offset + 10),
    });
  }
  return { version, cells };
}

function parseTerrain(rel) {
  const text = readText(rel);
  const rows = new Map();
  // 先登记所有瓦片：没有 peering bit 的瓦（掩码 0）只有 `x:y/0 = 0` 一行。
  const tileRe = /^(\d+):(\d+)\/0\s*=\s*0\s*$/gm;
  let tileMatch;
  while ((tileMatch = tileRe.exec(text)) !== null) {
    rows.set(`${tileMatch[1]}:${tileMatch[2]}`, rows.get(`${tileMatch[1]}:${tileMatch[2]}`) ?? 0);
  }
  const re = /^(\d+):(\d+)\/0\/terrains_peering_bit\/([a-z_]+)\s*=\s*0\s*$/gm;
  const bits = {
    top_left_corner: 0,
    top_side: 1,
    top_right_corner: 2,
    left_side: 3,
    right_side: 4,
    bottom_left_corner: 5,
    bottom_side: 6,
    bottom_right_corner: 7,
  };
  let m;
  while ((m = re.exec(text)) !== null) {
    const x = Number(m[1]);
    const y = Number(m[2]);
    const bit = bits[m[3]];
    if (bit === undefined) continue;
    const key = `${x}:${y}`;
    rows.set(key, (rows.get(key) ?? 0) | (1 << bit));
  }
  const xs = [...rows.keys()].map((k) => Number(k.split(':')[0]));
  const ys = [...rows.keys()].map((k) => Number(k.split(':')[1]));
  const maxX = Math.max(...xs);
  const maxY = Math.max(...ys);
  const maskCols = [];
  for (let x = 0; x <= maxX; x++) {
    const mask = rows.get(`${x}:0`);
    if (mask === undefined) throw new Error(`${rel} 缺少 (${x},0) 的 peering 数据`);
    maskCols.push(mask);
  }
  for (let y = 1; y <= maxY; y++) {
    for (let x = 0; x <= maxX; x++) {
      if (rows.get(`${x}:${y}`) !== maskCols[x]) {
        throw new Error(`${rel} 第 ${y} 行 (${x},${y}) 的掩码与第 0 行不一致`);
      }
    }
  }
  if (maskCols.length !== 47) {
    throw new Error(`${rel}：期望 47 列，实际 ${maskCols.length}`);
  }
  return { maskCols, rows: maxY + 1 };
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

function key(x, y) {
  return `${x},${y}`;
}

function blobMask(set, x, y) {
  let mask = 0;
  for (let i = 0; i < BLOB_DIRS.length; i++) {
    const [dx, dy] = BLOB_DIRS[i];
    if (set.has(key(x + dx, y + dy))) mask |= 1 << i;
  }
  for (const [corner, sideA, sideB] of BLOB_CORNER_GATES) {
    if ((mask >> corner) & 1) {
      if (!((mask >> sideA) & 1) || !((mask >> sideB) & 1)) mask &= ~(1 << corner);
    }
  }
  return mask;
}

function pngSize(rel) {
  const buf = fs.readFileSync(path.join(ROOT, rel));
  if (buf.toString('ascii', 1, 4) !== 'PNG') throw new Error(`${rel} 不是 PNG`);
  return [buf.readUInt32BE(16), buf.readUInt32BE(20)];
}

function parseNumber(value, fallback = 0) {
  if (value === undefined || value === null) return fallback;
  const n = Number(value);
  return Number.isFinite(n) ? n : fallback;
}

function main() {
  // ── 场景解析 ────────────────────────────────────────────────
  const sceneText = readText(SCENE_REL);
  const sections = parseSections(sceneText);
  const extPaths = new Map();
  for (const section of sections) {
    const [kind, rest] = splitHeader(section.header);
    if (kind !== 'ext_resource') continue;
    const attrs = parseAttrs(rest);
    if (attrs.get('id') && attrs.get('path')) extPaths.set(attrs.get('id'), attrs.get('path'));
  }

  const nodeSections = [];
  for (const section of sections) {
    const [kind, rest] = splitHeader(section.header);
    if (kind !== 'node') continue;
    const attrs = parseAttrs(rest);
    nodeSections.push({
      name: attrs.get('name') ?? '',
      parent: attrs.get('parent'),
      instance: attrs.get('instance') ?? '',
      props: parseProps(section.body),
    });
  }

  const rootNode = nodeSections.find((n) => n.parent === undefined);
  if (!rootNode) throw new Error('找不到场景根节点');
  const rootProps = rootNode.props;

  const levelId = parseNumber(rootProps.get('level_id'));
  const levelName = (rootProps.get('level_name') ?? '').replace(/^"|"$/g, '');
  const overrides = {
    initial_max_len: parseNumber(rootProps.get('initial_max_len_override')),
    food_len_gain: parseNumber(rootProps.get('food_len_gain_override')),
    growth_step_interval: parseNumber(rootProps.get('growth_step_interval_override')),
    mechanism_max_level: parseNumber(rootProps.get('mechanism_max_level_override')),
  };

  const layerNodes = new Map();
  for (const node of nodeSections) {
    if (node.name === 'Ground' || node.name === 'Obstacles') {
      const decoded = decodeTileMapData(node.props.get('tile_map_data'));
      if (decoded.version !== 0) fail(`${node.name}: 未知 tile_map_data version=${decoded.version}`);
      layerNodes.set(node.name, decoded.cells);
    }
  }
  if (!layerNodes.has('Ground')) throw new Error('找不到 Ground 层');
  if (!layerNodes.has('Obstacles')) throw new Error('找不到 Obstacles 层');

  // ── 实体解析 ────────────────────────────────────────────────
  const entities = { spawn: null, goal: null, foods: [], mechs: [] };
  for (const node of nodeSections) {
    if (!node.instance) continue;
    if (node.parent !== 'World/EntityRoot') continue;
    const instanceMatch = /ExtResource\("([^"]+)"\)/.exec(node.instance);
    if (!instanceMatch) continue;
    const extPath = extPaths.get(instanceMatch[1]) ?? '';
    const base = path.basename(extPath);
    const cell = vec2i(node.props.get('cell'));
    const pos = vec2(node.props.get('position'));
    if (!cell) {
      fail(`${node.name}: 缺少合法 cell（${node.props.get('cell')}）`);
      continue;
    }
    const entity = { name: node.name, cell, position: pos };
    if (base === 'PlayerSpawn.tscn') {
      if (entities.spawn) fail('找到多个 PlayerSpawn');
      entities.spawn = entity;
    } else if (base === 'Goal.tscn') {
      if (entities.goal) fail('找到多个 Goal');
      entities.goal = entity;
    } else if (base === 'Food.tscn') {
      entities.foods.push(entity);
    } else if (base === 'Mechanism.tscn') {
      entity.initialLevel = parseNumber(node.props.get('initial_level'));
      entities.mechs.push(entity);
    }
  }
  if (!entities.spawn) throw new Error('找不到 PlayerSpawn');
  if (!entities.goal) throw new Error('找不到 Goal');

  // ── 数值 ────────────────────────────────────────────────────
  const balanceText = readText(BALANCE_REL);
  function balanceValue(name) {
    const m = new RegExp(`^${name}\\s*=\\s*(-?[\\d.]+)\\s*$`, 'm').exec(balanceText);
    if (!m) throw new Error(`Balance.tres 缺少 ${name}`);
    return Number(m[1]);
  }
  const balance = {
    initial_max_len: balanceValue('initial_max_len'),
    food_len_gain: balanceValue('food_len_gain'),
    mechanism_max_level: balanceValue('mechanism_max_level'),
    growth_step_interval: balanceValue('growth_step_interval'),
    grow_anim_sec: balanceValue('grow_anim_sec'),
  };
  function resolve(overrideValue, globalValue) {
    return overrideValue > 0 ? overrideValue : globalValue;
  }
  const effective = {
    initialMaxLen: resolve(overrides.initial_max_len, balance.initial_max_len),
    foodLenGain: resolve(overrides.food_len_gain, balance.food_len_gain),
    growthInterval: resolve(overrides.growth_step_interval, balance.growth_step_interval),
    mechanismCap: resolve(overrides.mechanism_max_level, balance.mechanism_max_level),
  };

  // ── 地形表 ──────────────────────────────────────────────────
  const grass = parseTerrain(GRASS_REL);
  const wall = parseTerrain(WALL_REL);
  const mech = parseTerrain(MECH_REL);
  const maskCols = grass.maskCols;
  const maskColsJson = JSON.stringify(maskCols);
  if (JSON.stringify(wall.maskCols) !== maskColsJson) fail('墙体 47-blob 掩码序与草地不一致');
  if (JSON.stringify(mech.maskCols) !== maskColsJson) fail('机关 47-blob 掩码序与草地不一致');

  // ── 关卡数据 ────────────────────────────────────────────────
  const groundCells = layerNodes.get('Ground');
  const obstacleCells = layerNodes.get('Obstacles');
  const groundSet = new Set(groundCells.map((c) => key(c.x, c.y)));
  const obstacleSet = new Set(obstacleCells.map((c) => key(c.x, c.y)));

  const allX = [
    ...groundCells.map((c) => c.x),
    ...obstacleCells.map((c) => c.x),
    entities.spawn.cell[0],
    entities.goal.cell[0],
    ...entities.foods.map((e) => e.cell[0]),
    ...entities.mechs.map((e) => e.cell[0]),
  ];
  const allY = [
    ...groundCells.map((c) => c.y),
    ...obstacleCells.map((c) => c.y),
    entities.spawn.cell[1],
    entities.goal.cell[1],
    ...entities.foods.map((e) => e.cell[1]),
    ...entities.mechs.map((e) => e.cell[1]),
  ];
  const origin = [Math.min(...allX), Math.min(...allY)];
  const width = Math.max(...allX) - origin[0] + 1;
  const height = Math.max(...allY) - origin[1] + 1;

  // ── 一致性断言 ──────────────────────────────────────────────
  if (levelId !== EXPECTED.levelId) fail(`level_id=${levelId}，期望 ${EXPECTED.levelId}`);
  if (levelName !== EXPECTED.levelName) fail(`level_name="${levelName}"，期望 "${EXPECTED.levelName}"`);
  if (origin[0] !== EXPECTED.origin[0] || origin[1] !== EXPECTED.origin[1]) {
    fail(`origin=${origin}，期望 ${EXPECTED.origin}`);
  }
  if (width !== EXPECTED.width || height !== EXPECTED.height) {
    fail(`棋盘 ${width}×${height}，期望 ${EXPECTED.width}×${EXPECTED.height}`);
  }
  if (groundCells.length !== EXPECTED.floorCount) {
    fail(`Ground ${groundCells.length} 格，期望 ${EXPECTED.floorCount}`);
  }
  if (obstacleCells.length !== EXPECTED.obstacleCount) {
    fail(`Obstacles ${obstacleCells.length} 格，期望 ${EXPECTED.obstacleCount}`);
  }
  if (entities.foods.length !== EXPECTED.foodCount) {
    fail(`食物 ${entities.foods.length} 个，期望 ${EXPECTED.foodCount}`);
  }
  if (entities.mechs.length !== EXPECTED.mechCount) {
    fail(`机关 ${entities.mechs.length} 个，期望 ${EXPECTED.mechCount}`);
  }
  if (effective.initialMaxLen !== EXPECTED.initialMaxLen) {
    fail(`initial_max_len=${effective.initialMaxLen}，期望 ${EXPECTED.initialMaxLen}`);
  }
  if (effective.foodLenGain !== EXPECTED.foodLenGain) {
    fail(`food_len_gain=${effective.foodLenGain}，期望 ${EXPECTED.foodLenGain}`);
  }
  if (effective.growthInterval !== EXPECTED.growthInterval) {
    fail(`growth_interval=${effective.growthInterval}，期望 ${EXPECTED.growthInterval}`);
  }
  if (effective.mechanismCap !== EXPECTED.mechanismCap) {
    fail(`mechanism_cap=${effective.mechanismCap}，期望 ${EXPECTED.mechanismCap}`);
  }
  const gMax = Math.max(
    0,
    ...entities.mechs.map((mechEntity) =>
      Math.max(0, effective.mechanismCap - Math.max(-10, Math.min(effective.mechanismCap, mechEntity.initialLevel)))
    )
  );
  if (gMax !== EXPECTED.gMax) fail(`gMax=${gMax}，期望 ${EXPECTED.gMax}`);

  function validateLayer(name, cells, set) {
    for (const cell of cells) {
      if (cell.source !== 0) fail(`${name} (${cell.x},${cell.y}) source=${cell.source}`);
      if (cell.atlasY !== 0) fail(`${name} (${cell.x},${cell.y}) atlas_y=${cell.atlasY}`);
      const expectedCol = maskCols.indexOf(blobMask(set, cell.x, cell.y));
      if (expectedCol !== cell.atlasX) {
        fail(`${name} (${cell.x},${cell.y}) atlas_x=${cell.atlasX}，重算=${expectedCol}`);
      }
      if (cell.atlasX < 0 || cell.atlasX > 46) {
        fail(`${name} (${cell.x},${cell.y}) atlas_x 越界：${cell.atlasX}`);
      }
    }
  }
  validateLayer('Ground', groundCells, groundSet);
  validateLayer('Obstacles', obstacleCells, obstacleSet);

  for (const [label, entity] of [['spawn', entities.spawn], ['goal', entities.goal]]) {
    if (!groundSet.has(key(entity.cell[0], entity.cell[1]))) fail(`${label} 不在 Ground 上`);
    if (obstacleSet.has(key(entity.cell[0], entity.cell[1]))) fail(`${label} 压在障碍上`);
    const expectedPos = [entity.cell[0] * 32 + 16, entity.cell[1] * 32 + 16];
    if (!entity.position || entity.position[0] !== expectedPos[0] || entity.position[1] !== expectedPos[1]) {
      fail(`${label} position=${entity.position}，期望 ${expectedPos}`);
    }
  }
  for (const food of entities.foods) {
    if (!groundSet.has(key(food.cell[0], food.cell[1]))) fail(`食物 ${food.name} 不在 Ground 上`);
    if (obstacleSet.has(key(food.cell[0], food.cell[1]))) fail(`食物 ${food.name} 压在障碍上`);
  }
  for (const mechEntity of entities.mechs) {
    if (!groundSet.has(key(mechEntity.cell[0], mechEntity.cell[1]))) fail(`机关 ${mechEntity.name} 不在 Ground 上`);
    if (obstacleSet.has(key(mechEntity.cell[0], mechEntity.cell[1]))) fail(`机关 ${mechEntity.name} 压在障碍上`);
  }

  // ── 图集尺寸 ────────────────────────────────────────────────
  const images = {
    bg: 'assets/bg.png',
    grass: 'assets/tiles/grass.png',
    wall: 'assets/tiles/wall.png',
    flower: 'assets/tiles/flower.png',
    path: 'assets/tiles/path.png',
    head: 'assets/sprites/algee.png',
    tail: 'assets/sprites/charlie.png',
    food: 'assets/sprites/pental.png',
    core: 'assets/sprites/core.png',
    goal: 'assets/sprites/goal_flower.png',
  };
  const imageSources = {
    bg: 'Art/Backdrops/BG.png',
    grass: 'Art/Tiles/TILE_Grass_Blob47_01.png',
    wall: 'Art/Tiles/TILE_Wall_Blob47__01.png',
    flower: 'Art/Tiles/TILE_Flower_Blob47_Animated_01.png',
    path: 'Art/Tiles/path.png',
    head: 'Art/Sprites/Algee-all.png',
    tail: 'Art/Sprites/Charlie-Default.png',
    food: 'Art/Sprites/pental.png',
    core: 'Art/Sprites/core.png',
    goal: 'Art/Sprites/Goal-Flower.png',
  };
  const imageDims = {
    bg: [1280, 720],
    grass: [1504, 32],
    wall: [1504, 32],
    flower: [1504, 96],
    path: [320, 32],
    head: [96, 256],
    tail: [32, 32],
    food: [32, 32],
    core: [32, 32],
    goal: [36, 45],
  };
  for (const [name, rel] of Object.entries(imageSources)) {
    const [w, h] = pngSize(rel);
    if (w !== imageDims[name][0] || h !== imageDims[name][1]) {
      fail(`${rel} 尺寸 ${w}×${h}，期望 ${imageDims[name][0]}×${imageDims[name][1]}`);
    }
  }

  // ── 音频元数据 ──────────────────────────────────────────────
  const sfxSections = new Map();
  for (const section of parseSections(readText(SFX_SCENE_REL))) {
    const [kind, rest] = splitHeader(section.header);
    if (kind !== 'node') continue;
    const attrs = parseAttrs(rest);
    sfxSections.set(attrs.get('name'), parseProps(section.body));
  }
  const pitch = {
    step: [0.95, 1.06],
    pick: [1, 1],
    grow: [0.97, 1.03],
    win: [1, 1],
    lose: [1, 1],
    uiBounce: [1.37, 1.37],
  };
  function sfxMeta(name, nodeName, src) {
    const props = sfxSections.get(nodeName) ?? new Map();
    return {
      name,
      src,
      db: parseNumber(props.get('volume_db'), 0),
      polyphony: Math.max(1, parseNumber(props.get('max_polyphony'), 1)),
      pitch: pitch[name],
    };
  }
  const buttonProps = parseProps(
    parseSections(readText(BUTTON_AUDIO_REL))
      .filter((s) => splitHeader(s.header)[0] === 'node')
      .map((s) => s.body)
      .join('\n')
  );
  const sfx = [
    sfxMeta('step', 'Step', 'assets/sfx/step.wav'),
    sfxMeta('pick', 'Pick', 'assets/sfx/pick.wav'),
    sfxMeta('grow', 'Grow', 'assets/sfx/grow.wav'),
    sfxMeta('win', 'Win', 'assets/sfx/win.wav'),
    sfxMeta('lose', 'Lose', 'assets/sfx/lose.wav'),
    {
      name: 'uiBounce',
      src: 'assets/sfx/ui_bounce.wav',
      db: parseNumber(buttonProps.get('volume_db'), -20.948),
      polyphony: Math.max(1, parseNumber(buttonProps.get('max_polyphony'), 4)),
      pitch: pitch.uiBounce,
    },
  ];

  // ── Goal 预制偏移 ───────────────────────────────────────────
  let goalOffset = [0, -8];
  for (const section of parseSections(readText(GOAL_SCENE_REL))) {
    const [kind, rest] = splitHeader(section.header);
    if (kind !== 'node') continue;
    const attrs = parseAttrs(rest);
    if (attrs.get('type') === 'Sprite2D') {
      const props = parseProps(section.body);
      const pos = vec2(props.get('position'));
      if (pos) goalOffset = pos;
    }
  }

  // ── 输出 ────────────────────────────────────────────────────
  const sortedCells = (cells) =>
    [...cells]
      .sort((a, b) => a.y - b.y || a.x - b.x)
      .map((c) => ({ x: c.x, y: c.y, col: c.atlasX }));

  const data = {
    schema_version: 1,
    source: {
      scene: SCENE_REL,
      scene_sha256: sha256(SCENE_REL),
      rule: RULE_REL,
      rule_sha256: sha256(RULE_REL),
      balance: BALANCE_REL,
      balance_sha256: sha256(BALANCE_REL),
      terrain_tileset: GRASS_REL,
      terrain_sha256: sha256(GRASS_REL),
      wall_tileset: WALL_REL,
      wall_sha256: sha256(WALL_REL),
      mech_tileset: MECH_REL,
      mech_sha256: sha256(MECH_REL),
      snek_tileset: SNEK_REL,
      snek_sha256: sha256(SNEK_REL),
    },
    level: {
      id: levelId,
      name: levelName,
      origin,
      width,
      height,
      cell_px: 32,
      overrides,
      spec: {
        origin,
        width,
        height,
        floor: sortedCells(groundCells).map((c) => [c.x, c.y]),
        obstacles: sortedCells(obstacleCells).map((c) => [c.x, c.y]),
        spawn: entities.spawn.cell,
        goal: entities.goal.cell,
        foods: entities.foods.map((e) => e.cell),
        mech_centers: entities.mechs.map((e) => e.cell),
        mech_initial_levels: entities.mechs.map((e) => e.initialLevel),
        initial_max_len: effective.initialMaxLen,
        food_len_gain: effective.foodLenGain,
        growth_interval: effective.growthInterval,
        mechanism_cap: effective.mechanismCap,
      },
    },
    blob: {
      bit_order: ['TL', 'T', 'TR', 'L', 'R', 'BL', 'B', 'BR'],
      corner_gates: BLOB_CORNER_GATES,
      mask_cols: maskCols,
    },
    layers: {
      ground: { atlas: images.grass, cols: 47, rows: 1, cells: sortedCells(groundCells) },
      obstacles: { atlas: images.wall, cols: 47, rows: 1, cells: sortedCells(obstacleCells) },
    },
    entities: {
      spawn: { cell: entities.spawn.cell },
      goal: { cell: entities.goal.cell, offset: goalOffset },
      foods: entities.foods.map((e) => ({ cell: e.cell })),
      mechs: entities.mechs.map((e) => ({ cell: e.cell, initial_level: e.initialLevel })),
    },
    render: {
      growAnimSec: balance.grow_anim_sec,
      mech: { atlas: images.flower, rows: 3, doneRow: 2, popRows: [0, 1, 2] },
      body: { atlas: images.path, cols: 10 },
      head: {
        atlas: images.head,
        cols: 3,
        rows: 4,
        frameW: 32,
        frameH: 64,
        anchorY: -16,
        holdSec: 0.18,
        idleSec: 0.35,
        idleCol: 2,
      },
      tail: { atlas: images.tail, w: 32, h: 32 },
      goal: { atlas: images.goal, w: 36, h: 45 },
      food: { atlas: images.food, w: 32, h: 32 },
      core: {
        atlas: images.core,
        w: 32,
        h: 32,
        blue: [0.2, 0.38, 0.95],
        minLevel: -10,
        pulseScale: 1.18,
      },
    },
    assets: {
      images,
      font: {
        family: 'ZCOOL KuaiLe',
        src: 'assets/font/ZCOOLKuaiLe-Regular.ttf',
        license: 'assets/font/OFL.txt',
      },
      sfx,
    },
  };

  if (errors.length) {
    console.error(`导出失败（${errors.length} 项不一致）：`);
    for (const message of errors) console.error(`  - ${message}`);
    process.exit(1);
  }

  const json = `${JSON.stringify(data, null, 2)}\n`;
  if (CHECK_ONLY) {
    if (!fs.existsSync(OUT_ABS)) {
      console.error(`缺少 ${OUT_REL}，请先运行 node tools/export_level.mjs`);
      process.exit(1);
    }
    const current = fs.readFileSync(OUT_ABS, 'utf8');
    if (current !== json) {
      console.error(`${OUT_REL} 与当前场景不一致，请重新运行 node tools/export_level.mjs`);
      const a = current.split('\n');
      const b = json.split('\n');
      const index = a.findIndex((line, i) => line !== b[i]);
      console.error(`首个差异行 ${index + 1}:`);
      console.error(`  现有: ${a[index] ?? '<EOF>'}`);
      console.error(`  生成: ${b[index] ?? '<EOF>'}`);
      process.exit(1);
    }
    console.log(`OK: ${OUT_REL} 与场景一致`);
    return;
  }

  fs.writeFileSync(OUT_ABS, json);
  console.log(`已写入 ${OUT_REL}`);
  console.log(
    `  棋盘 ${width}×${height} @ (${origin[0]},${origin[1]})，地板 ${groundCells.length}，障碍 ${obstacleCells.length}`
  );
  console.log(
    `  食物 ${entities.foods.length}，机关 ${entities.mechs.length}，gMax=${gMax}，L0=${effective.initialMaxLen}，interval=${effective.growthInterval}`
  );
  for (const note of notes) console.log(`  ${note}`);
}

try {
  main();
} catch (error) {
  console.error(error instanceof Error ? error.stack ?? error.message : String(error));
  process.exit(1);
}
