#!/usr/bin/env node
/**
 * L1–L7 场景 → webapp/data/levels.json
 *
 * 只做“场景快照导出 + 一致性校验”，不参与游戏运行。任何结构错误都会非零退出。
 * L1–L5 额外与 tests/fixtures/anchors 的 LevelSpec 快照交叉校验；L6 以当前场景为准。
 */
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '../..');
const OUT_REL = 'webapp/data/levels.json';
const OUT_ABS = path.join(ROOT, OUT_REL);
const CHECK_ONLY = process.argv.includes('--check');

const LEVELS_DIR = 'Scenes/Levels';
const BALANCE_REL = 'Resources/Config/Balance.tres';
const GRASS_REL = 'Art/Tiles/TerrainGrassBlob47.tres';
const WALL_REL = 'Art/Tiles/TerrainWall.tres';
const MECH_REL = 'Art/Tiles/TerrainMech.tres';
const SNEK_REL = 'Art/Tiles/PlayerSnek.tres';
const RULE_REL = 'Scripts/Algorithms/RuleState.cs';
const SFX_SCENE_REL = 'Scenes/Audio/SfxManager.tscn';
const BUTTON_AUDIO_REL = 'Scenes/UI/ButtonAudio.tscn';
const GOAL_SCENE_REL = 'Scenes/Entities/Goal.tscn';
const PROJECT_REL = 'project.godot';

const errors = [];
const warnings = [];

function fail(message) {
  errors.push(message);
}
function warn(message) {
  warnings.push(message);
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
  while ((m = re.exec(source)) !== null) attrs.set(m[1], m[2] !== undefined ? m[2] : m[3]);
  return attrs;
}

function parseSections(text) {
  const re = /^\[([^\][]+)\][ \t]*$/gm;
  const marks = [];
  let m;
  while ((m = re.exec(text)) !== null) marks.push({ header: m[1], start: m.index, bodyStart: re.lastIndex });
  return marks.map((mark, i) => ({
    header: mark.header,
    body: text.slice(mark.bodyStart, i + 1 < marks.length ? marks[i + 1].start : text.length),
  }));
}

function splitHeader(header) {
  const space = header.indexOf(' ');
  return space < 0 ? [header, ''] : [header.slice(0, space), header.slice(space + 1)];
}

function parseProps(body) {
  const props = new Map();
  for (const raw of body.split(/\r?\n/)) {
    const line = raw.trim();
    if (!line || line.startsWith(';')) continue;
    const m = /^([A-Za-z_][\w/]*)\s*=\s*(.*)$/.exec(line);
    if (m) props.set(m[1], m[2]);
  }
  return props;
}

function vec2(value, fallback = [0, 0]) {
  const m = /Vector2\(\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*\)/.exec(value ?? '');
  return m ? [Number(m[1]), Number(m[2])] : fallback;
}
function vec2i(value) {
  const m = /Vector2i\(\s*(-?\d+)\s*,\s*(-?\d+)\s*\)/.exec(value ?? '');
  return m ? [Number(m[1]), Number(m[2])] : null;
}

function decodeTileMapData(value) {
  const m = /^PackedByteArray\("([^"]*)"\)$/.exec((value ?? '').trim());
  if (!m) throw new Error('tile_map_data 不是 PackedByteArray');
  const buf = Buffer.from(m[1], 'base64');
  if (buf.length < 2) throw new Error('tile_map_data 太短');
  const version = buf.readUInt16LE(0);
  if ((buf.length - 2) % 12 !== 0) throw new Error(`tile_map_data 长度异常：${buf.length}`);
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
  const tileRe = /^(\d+):(\d+)\/0\s*=\s*0\s*$/gm;
  let m;
  while ((m = tileRe.exec(text)) !== null) {
    const key = `${m[1]}:${m[2]}`;
    if (!rows.has(key)) rows.set(key, 0);
  }
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
  const peerRe = /^(\d+):(\d+)\/0\/terrains_peering_bit\/([a-z_]+)\s*=\s*0\s*$/gm;
  while ((m = peerRe.exec(text)) !== null) {
    const key = `${m[1]}:${m[2]}`;
    const bit = bits[m[3]];
    if (bit === undefined) continue;
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
  if (maskCols.length !== 47) throw new Error(`${rel}：期望 47 列，实际 ${maskCols.length}`);
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
const cellKey = (x, y) => `${x},${y}`;

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

function levelNumberFromPath(rel) {
  const m = /Level_(\d+)_/.exec(path.basename(rel));
  return m ? Number(m[1]) : 0;
}

// ── 全局配置 ────────────────────────────────────────────────────
const projectText = readText(PROJECT_REL);
function projectNumber(name, fallback) {
  const m = new RegExp(`^${name.replace('.', '\\.')}=(-?\\d+)`, 'm').exec(projectText);
  return m ? Number(m[1]) : fallback;
}
const viewport = [projectNumber('window/size/viewport_width', 1920), projectNumber('window/size/viewport_height', 1080)];

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

const grass = parseTerrain(GRASS_REL);
const wall = parseTerrain(WALL_REL);
const mech = parseTerrain(MECH_REL);
const maskCols = grass.maskCols;
if (JSON.stringify(wall.maskCols) !== JSON.stringify(maskCols)) fail('墙体 47-blob 掩码序与草地不一致');
if (JSON.stringify(mech.maskCols) !== JSON.stringify(maskCols)) fail('机关 47-blob 掩码序与草地不一致');

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

// ── 音频元数据 ──────────────────────────────────────────────────
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

let goalOffset = [0, -8];
for (const section of parseSections(readText(GOAL_SCENE_REL))) {
  const [kind, rest] = splitHeader(section.header);
  if (kind !== 'node') continue;
  const attrs = parseAttrs(rest);
  if (attrs.get('type') === 'Sprite2D') {
    const pos = vec2(parseProps(section.body).get('position'), null);
    if (pos) goalOffset = pos;
  }
}

// ── 单关解析 ────────────────────────────────────────────────────
function parseLevel(sceneRel) {
  const sceneText = readText(sceneRel);
  const sections = parseSections(sceneText);
  const extPaths = new Map();
  for (const section of sections) {
    const [kind2, rest] = splitHeader(section.header);
    if (kind2 !== 'ext_resource') continue;
    const attrs = parseAttrs(rest);
    if (attrs.get('id') && attrs.get('path')) extPaths.set(attrs.get('id'), attrs.get('path'));
  }

  const nodes = [];
  for (const section of sections) {
    const [kind2, rest] = splitHeader(section.header);
    if (kind2 !== 'node') continue;
    const attrs = parseAttrs(rest);
    nodes.push({
      name: attrs.get('name') ?? '',
      parent: attrs.get('parent'),
      type: attrs.get('type'),
      instance: attrs.get('instance') ?? '',
      props: parseProps(section.body),
    });
  }

  const root = nodes.find((n) => n.parent === undefined);
  if (!root) throw new Error(`${sceneRel}: 找不到根节点`);
  const fileNumber = levelNumberFromPath(sceneRel);
  const explicitId = root.props.get('level_id');
  const levelId = explicitId === undefined ? fileNumber : parseNumber(explicitId, fileNumber);
  if (levelId <= 0) throw new Error(`${sceneRel}: 无法确定 level_id`);
  if (fileNumber > 0 && levelId !== fileNumber) warn(`${sceneRel}: level_id=${levelId} 与文件名编号 ${fileNumber} 不一致`);

  const levelName = (root.props.get('level_name') ?? '').replace(/^"|"$/g, '') || `Level ${levelId}`;
  const overrides = {
    initial_max_len: parseNumber(root.props.get('initial_max_len_override')),
    food_len_gain: parseNumber(root.props.get('food_len_gain_override')),
    growth_step_interval: parseNumber(root.props.get('growth_step_interval_override')),
    mechanism_max_level: parseNumber(root.props.get('mechanism_max_level_override')),
  };

  const layerCells = new Map();
  for (const node of nodes) {
    if (node.name !== 'Ground' && node.name !== 'Obstacles') continue;
    const data = node.props.get('tile_map_data');
    if (data === undefined) {
      layerCells.set(node.name, []);
      continue;
    }
    const decoded = decodeTileMapData(data);
    if (decoded.version !== 0) fail(`${sceneRel} ${node.name}: 未知 tile_map_data version=${decoded.version}`);
    layerCells.set(node.name, decoded.cells);
  }
  const groundCells = layerCells.get('Ground') ?? [];
  const obstacleCells = layerCells.get('Obstacles') ?? [];
  if (groundCells.length === 0) fail(`${sceneRel}: Ground 为空`);

  const entities = { spawn: null, goal: null, foods: [], mechs: [] };
  for (const node of nodes) {
    if (!node.instance) continue;
    if (node.parent !== 'World/EntityRoot') continue;
    const instanceMatch = /ExtResource\("([^"]+)"\)/.exec(node.instance);
    if (!instanceMatch) continue;
    const base = path.basename(extPaths.get(instanceMatch[1]) ?? '');
    const cell = vec2i(node.props.get('cell'));
    const position = vec2(node.props.get('position'), null);
    if (!cell) {
      fail(`${sceneRel} ${node.name}: 缺少合法 cell`);
      continue;
    }
    const entity = { name: node.name, cell, position };
    if (base === 'PlayerSpawn.tscn') {
      if (entities.spawn) fail(`${sceneRel}: 多个 PlayerSpawn`);
      entities.spawn = entity;
    } else if (base === 'Goal.tscn') {
      if (entities.goal) fail(`${sceneRel}: 多个 Goal`);
      entities.goal = entity;
    } else if (base === 'Food.tscn') {
      entities.foods.push(entity);
    } else if (base === 'Mechanism.tscn') {
      entity.initialLevel = parseNumber(node.props.get('initial_level'));
      entities.mechs.push(entity);
    }
  }
  if (!entities.spawn) fail(`${sceneRel}: 找不到 PlayerSpawn`);
  if (!entities.goal) fail(`${sceneRel}: 找不到 Goal`);

  const worldNode = nodes.find((n) => n.name === 'World' && n.parent === '.');
  const bgNode = nodes.find((n) => n.name === 'Bg' && n.parent === 'World');
  const camNode = nodes.find((n) => n.name === 'Cam' && n.parent === '.');
  const view = {
    world: {
      position: vec2(worldNode?.props.get('position'), [0, 0]),
      scale: vec2(worldNode?.props.get('scale'), [1, 1]),
    },
    camera: {
      position: vec2(camNode?.props.get('position'), [960, 540]),
      zoom: vec2(camNode?.props.get('zoom'), [1, 1]),
    },
    background: {
      position: vec2(bgNode?.props.get('position'), [640, 360]),
      scale: vec2(bgNode?.props.get('scale'), [1, 1]),
    },
  };
  if (!(view.world.scale[0] > 0 && view.world.scale[1] > 0)) fail(`${sceneRel}: World.scale 非正`);
  if (!(view.camera.zoom[0] > 0 && view.camera.zoom[1] > 0)) fail(`${sceneRel}: Camera.zoom 非正`);

  const allX = [
    ...groundCells.map((c) => c.x),
    ...obstacleCells.map((c) => c.x),
    ...(entities.spawn ? [entities.spawn.cell[0]] : []),
    ...(entities.goal ? [entities.goal.cell[0]] : []),
    ...entities.foods.map((e) => e.cell[0]),
    ...entities.mechs.map((e) => e.cell[0]),
  ];
  const allY = [
    ...groundCells.map((c) => c.y),
    ...obstacleCells.map((c) => c.y),
    ...(entities.spawn ? [entities.spawn.cell[1]] : []),
    ...(entities.goal ? [entities.goal.cell[1]] : []),
    ...entities.foods.map((e) => e.cell[1]),
    ...entities.mechs.map((e) => e.cell[1]),
  ];
  const origin = [Math.min(...allX), Math.min(...allY)];
  const width = Math.max(...allX) - origin[0] + 1;
  const height = Math.max(...allY) - origin[1] + 1;

  const resolve = (overrideValue, globalValue) => (overrideValue > 0 ? overrideValue : globalValue);
  const spec = {
    origin,
    width,
    height,
    floor: groundCells.map((c) => [c.x, c.y]),
    obstacles: obstacleCells.map((c) => [c.x, c.y]),
    spawn: entities.spawn?.cell ?? [0, 0],
    goal: entities.goal?.cell ?? [0, 0],
    foods: entities.foods.map((e) => e.cell),
    mech_centers: entities.mechs.map((e) => e.cell),
    mech_initial_levels: entities.mechs.map((e) => e.initialLevel ?? 0),
    initial_max_len: resolve(overrides.initial_max_len, balance.initial_max_len),
    food_len_gain: resolve(overrides.food_len_gain, balance.food_len_gain),
    growth_interval: resolve(overrides.growth_step_interval, balance.growth_step_interval),
    mechanism_cap: resolve(overrides.mechanism_max_level, balance.mechanism_max_level),
  };

  const groundSet = new Set(groundCells.map((c) => cellKey(c.x, c.y)));
  const obstacleSet = new Set(obstacleCells.map((c) => cellKey(c.x, c.y)));

  // 场景里存的 atlas_x 可能是旧涂法；Level.gd 运行时按格集重铺 47-blob，
  // 所以以“格集重算列号”为导出权威，存储值不一致只告警。
  const resolvedCols = new Map();
  for (const [label, cells, set] of [
    ['Ground', groundCells, groundSet],
    ['Obstacles', obstacleCells, obstacleSet],
  ]) {
    for (const cell of cells) {
      const expectedCol = maskCols.indexOf(blobMask(set, cell.x, cell.y));
      resolvedCols.set(`${label}:${cell.x},${cell.y}`, expectedCol);
      if (expectedCol !== cell.atlasX) {
        warn(`${sceneRel} ${label} (${cell.x},${cell.y}) 存储 atlas_x=${cell.atlasX}，按格集重算=${expectedCol}（运行时重铺）`);
      }
    }
  }

  const entityChecks = [
    ['spawn', entities.spawn],
    ['goal', entities.goal],
    ...entities.foods.map((e, i) => [`food${i + 1}`, e]),
  ];
  for (const [label, entity] of entityChecks) {
    if (!entity) continue;
    if (!groundSet.has(cellKey(entity.cell[0], entity.cell[1]))) fail(`${sceneRel} ${label} 不在 Ground 上`);
    if (obstacleSet.has(cellKey(entity.cell[0], entity.cell[1]))) fail(`${sceneRel} ${label} 压在障碍上`);
    const expectedPos = [entity.cell[0] * 32 + 16, entity.cell[1] * 32 + 16];
    if (!entity.position || entity.position[0] !== expectedPos[0] || entity.position[1] !== expectedPos[1]) {
      fail(`${sceneRel} ${label} position=${entity.position}，期望 ${expectedPos}`);
    }
  }
  for (const mechEntity of entities.mechs) {
    if (!groundSet.has(cellKey(mechEntity.cell[0], mechEntity.cell[1]))) {
      fail(`${sceneRel} 机关 ${mechEntity.name} 不在 Ground 上`);
    }
    if (obstacleSet.has(cellKey(mechEntity.cell[0], mechEntity.cell[1]))) {
      warn(`${sceneRel} 机关 ${mechEntity.name} 位于障碍格 ${mechEntity.cell}（规则允许，核心会盖在墙上）`);
    }
  }

  // 相机取景检查（仅告警）：棋盘应在视口内。
  const [vw, vh] = viewport;
  const locX = [origin[0] * 32, (origin[0] + width) * 32];
  const locY = [origin[1] * 32, (origin[1] + height) * 32];
  const sx = locX.map((p) => (p * view.world.scale[0] + view.world.position[0] - view.camera.position[0]) * view.camera.zoom[0] + vw / 2);
  const sy = locY.map((p) => (p * view.world.scale[1] + view.world.position[1] - view.camera.position[1]) * view.camera.zoom[1] + vh / 2);
  if (Math.min(...sx) < 0 || Math.max(...sx) > vw || Math.min(...sy) < 0 || Math.max(...sy) > vh) {
    warn(`${sceneRel}: 棋盘屏幕范围 x=${sx.map((v) => Math.round(v))} y=${sy.map((v) => Math.round(v))} 超出视口 ${vw}×${vh}`);
  }

  return {
    id: levelId,
    name: levelName,
    scene: sceneRel,
    sceneSha256: sha256(sceneRel),
    overrides,
    spec,
    view,
    layers: {
      ground: {
        atlas: images.grass,
        cols: 47,
        rows: 1,
        cells: [...groundCells]
          .sort((a, b) => a.y - b.y || a.x - b.x)
          .map((c) => ({ x: c.x, y: c.y, col: resolvedCols.get(`Ground:${c.x},${c.y}`) })),
      },
      obstacles: {
        atlas: images.wall,
        cols: 47,
        rows: 1,
        cells: [...obstacleCells]
          .sort((a, b) => a.y - b.y || a.x - b.x)
          .map((c) => ({ x: c.x, y: c.y, col: resolvedCols.get(`Obstacles:${c.x},${c.y}`) })),
      },
    },
    entities: {
      spawn: { cell: entities.spawn?.cell ?? [0, 0] },
      goal: { cell: entities.goal?.cell ?? [0, 0], offset: goalOffset },
      foods: entities.foods.map((e) => ({ cell: e.cell })),
      mechs: entities.mechs.map((e) => ({ cell: e.cell, initial_level: e.initialLevel ?? 0 })),
    },
  };
}

// ── L1–L5 anchor 交叉校验 ───────────────────────────────────────
function sortedKeys(cells) {
  return [...cells].map(([x, y]) => cellKey(x, y)).sort();
}
function normalizedSpec(spec) {
  const pairs = spec.mech_centers
    .map((cell, i) => `${cellKey(cell[0], cell[1])}|${spec.mech_initial_levels[i] ?? 0}`)
    .sort();
  return {
    origin: spec.origin,
    width: spec.width,
    height: spec.height,
    floor: sortedKeys(spec.floor),
    obstacles: sortedKeys(spec.obstacles),
    spawn: spec.spawn,
    goal: spec.goal,
    foods: sortedKeys(spec.foods),
    mechs: pairs,
    initial_max_len: spec.initial_max_len,
    food_len_gain: spec.food_len_gain,
    growth_interval: spec.growth_interval,
    mechanism_cap: spec.mechanism_cap,
  };
}
function anchorSpec(anchor) {
  return normalizedSpec({
    origin: anchor.origin,
    width: anchor.width,
    height: anchor.height,
    floor: anchor.floor,
    obstacles: anchor.obstacles,
    spawn: anchor.spawn,
    goal: anchor.goal,
    foods: anchor.foods,
    mech_centers: anchor.mech_centers,
    mech_initial_levels: anchor.mech_initial_levels,
    initial_max_len: anchor.initial_max_len,
    food_len_gain: anchor.food_len_gain,
    growth_interval: anchor.growth_interval,
    mechanism_cap: anchor.mechanism_cap,
  });
}
function compareLevelWithAnchor(level) {
  const anchorRel = `tests/fixtures/anchors/level_${String(level.id).padStart(2, '0')}.json`;
  if (!fs.existsSync(path.join(ROOT, anchorRel))) return;
  const anchor = JSON.parse(readText(anchorRel));
  const actual = normalizedSpec(level.spec);
  const expected = anchorSpec(anchor);
  for (const key of Object.keys(expected)) {
    if (JSON.stringify(actual[key]) !== JSON.stringify(expected[key])) {
      fail(`${level.scene}: spec.${key} 与 ${anchorRel} 不一致\n  实际=${JSON.stringify(actual[key])}\n  期望=${JSON.stringify(expected[key])}`);
      return;
    }
  }
}

// ── 主流程 ──────────────────────────────────────────────────────
function main() {
  const levelFiles = fs
    .readdirSync(path.join(ROOT, LEVELS_DIR))
    .filter((name) => /^Level_\d+_.*\.tscn$/.test(name))
    .map((name) => `${LEVELS_DIR}/${name}`)
    .sort();

  const levels = [];
  for (const rel of levelFiles) {
    try {
      levels.push(parseLevel(rel));
    } catch (error) {
      fail(error instanceof Error ? error.message : String(error));
    }
  }
  levels.sort((a, b) => a.id - b.id);

  const ids = new Set();
  for (const level of levels) {
    if (ids.has(level.id)) fail(`重复 level_id=${level.id}`);
    ids.add(level.id);
    if (level.id >= 1 && level.id <= 5) compareLevelWithAnchor(level);
  }
  if (levels.length === 0) fail('没有找到任何关卡');
  for (let expectedId = 1; expectedId <= levels.length; expectedId++) {
    if (!ids.has(expectedId)) fail(`缺少 level_id=${expectedId}`);
  }

  const data = {
    schema_version: 2,
    viewport,
    source: {
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
      levels: levels.map((level) => ({ id: level.id, scene: level.scene, scene_sha256: level.sceneSha256 })),
    },
    blob: {
      bit_order: ['TL', 'T', 'TR', 'L', 'R', 'BL', 'B', 'BR'],
      corner_gates: BLOB_CORNER_GATES,
      mask_cols: maskCols,
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
    levels: levels.map(({ sceneSha256, ...rest }) => rest),
  };

  if (errors.length) {
    console.error(`导出失败（${errors.length} 项错误）：`);
    for (const message of errors) console.error(`  - ${message}`);
    process.exit(1);
  }
  for (const message of warnings) console.warn(`WARN: ${message}`);

  const json = `${JSON.stringify(data, null, 2)}\n`;
  if (CHECK_ONLY) {
    if (!fs.existsSync(OUT_ABS)) {
      console.error(`缺少 ${OUT_REL}，请先运行 node tools/export_levels.mjs`);
      process.exit(1);
    }
    if (fs.readFileSync(OUT_ABS, 'utf8') !== json) {
      console.error(`${OUT_REL} 与当前场景不一致，请重新运行 node tools/export_levels.mjs`);
      process.exit(1);
    }
    console.log(`OK: ${OUT_REL} 与 ${levels.length} 个关卡场景一致`);
    return;
  }
  fs.writeFileSync(OUT_ABS, json);
  console.log(`已写入 ${OUT_REL}`);
  for (const level of levels) {
    console.log(
      `  L${level.id} ${level.name}: ${level.spec.width}×${level.spec.height}，地板 ${level.spec.floor.length}，障碍 ${level.spec.obstacles.length}，食物 ${level.spec.foods.length}，机关 ${level.spec.mech_centers.length}`
    );
  }
}

try {
  main();
} catch (error) {
  console.error(error instanceof Error ? error.stack ?? error.message : String(error));
  process.exit(1);
}
