/**
 * 第 6 关 Web Demo 的规则镜像。
 *
 * ⚠️ 非权威实现：玩法唯一权威仍是 Scripts/Algorithms/RuleState.cs。
 * 本文件只用于独立 HTML5 Demo，方法名与常量尽量与 C# 一一对应，便于逐段对照。
 * 回归：tools/replay_test.mjs 用 1–5 关黄金路线 + 第 6 关 60 步路线校验。
 */

export const INF = 1e9;
export const MAX_MECH_LEVEL = 4;
export const MIN_MECH_LEVEL = -10;

export function cellKey(x, y) {
  return `${x},${y}`;
}

export function keyOf(cell) {
  return cellKey(cell[0], cell[1]);
}

export function parseKey(key) {
  const comma = key.indexOf(',');
  return [Number(key.slice(0, comma)), Number(key.slice(comma + 1))];
}

/** 格子 d 第一次进入机关形状的阶段；> MaxLevel 表示永不覆盖。 */
export function coverLevel(dx, dy) {
  const d1 = Math.abs(dx) + Math.abs(dy);
  const dInf = Math.max(Math.abs(dx), Math.abs(dy));
  return Math.max(0, Math.max(2 * d1 - 2, 2 * dInf - 1));
}

function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

export class RuleState {
  constructor(spec) {
    this.setup(spec);
  }

  setup(spec) {
    this.origin = [...spec.origin];
    this.width = spec.width;
    this.height = spec.height;
    this.floor = new Set((spec.floor ?? []).map((c) => keyOf(c)));
    this.obstacles = new Set((spec.obstacles ?? []).map((c) => keyOf(c)));
    this.spawn = [...spec.spawn];
    this.goal = [...spec.goal];
    this.initialFoods = (spec.foods ?? []).map((c) => [...c]);
    this.mechCenters = (spec.mech_centers ?? []).map((c) => [...c]);
    this.initialMaxLen = Math.max(spec.initial_max_len ?? 1, 1);
    this.growthInterval = Math.max(spec.growth_interval ?? 1, 1);
    this.foodLenGain = spec.food_len_gain ?? 0;
    this.maxMechLevel = clamp(spec.mechanism_cap ?? 0, 0, MAX_MECH_LEVEL);

    this.mechInitialLevels = [];
    this.gMax = 0;
    for (let i = 0; i < this.mechCenters.length; i++) {
      const raw = spec.mech_initial_levels?.[i] ?? 0;
      const level = clamp(raw, MIN_MECH_LEVEL, this.maxMechLevel);
      this.mechInitialLevels.push(level);
      this.gMax = Math.max(this.gMax, this.maxMechLevel - level);
    }

    this.buildCoverStep();

    this.trail = [];
    this.foods = new Set();
    this.maxLen = this.initialMaxLen;
    this.steps = 0;
    this.foodEaten = 0;
    this.g = 0;
    this.finished = false;
    this.endOnDeadlock = false;
    this.shielded = new Set();
    this.mechCells = new Set();
    this.shieldVersion = 0;
    this.distCacheKey = '';
    this.distCache = null;
  }

  setEndOnDeadlock(value) {
    this.endOnDeadlock = Boolean(value);
  }

  // ── 网格判定（GridModel.cs）─────────────────────────────────
  inBounds(cell) {
    return (
      cell[0] >= this.origin[0] &&
      cell[1] >= this.origin[1] &&
      cell[0] < this.origin[0] + this.width &&
      cell[1] < this.origin[1] + this.height
    );
  }

  isFloor(cell) {
    return this.floor.size === 0 || this.floor.has(keyOf(cell));
  }

  isBlocked(cell, mechCells = this.mechCells) {
    if (!this.inBounds(cell)) return true;
    if (!this.isFloor(cell)) return true;
    return this.obstacles.has(keyOf(cell)) || mechCells.has(keyOf(cell));
  }

  bfsDistMap(from, mechCells = this.mechCells) {
    const dist = new Map();
    if (!this.inBounds(from)) return dist;
    const startKey = keyOf(from);
    dist.set(startKey, 0);
    const queue = [from];
    for (let head = 0; head < queue.length; head++) {
      const cur = queue[head];
      const nd = dist.get(keyOf(cur)) + 1;
      const neighbors = [
        [cur[0] - 1, cur[1]],
        [cur[0] + 1, cur[1]],
        [cur[0], cur[1] - 1],
        [cur[0], cur[1] + 1],
      ];
      for (const next of neighbors) {
        const nextKey = keyOf(next);
        if (dist.has(nextKey) || this.isBlocked(next, mechCells)) continue;
        dist.set(nextKey, nd);
        queue.push(next);
      }
    }
    return dist;
  }

  hasPath(from, to, mechCells = this.mechCells) {
    if (!this.inBounds(from) || !this.inBounds(to)) return false;
    if (this.isBlocked(to, mechCells)) return false;
    if (from[0] === to[0] && from[1] === to[1]) return true;
    const visited = new Set([keyOf(from)]);
    const queue = [from];
    for (let head = 0; head < queue.length; head++) {
      const cur = queue[head];
      const neighbors = [
        [cur[0] - 1, cur[1]],
        [cur[0] + 1, cur[1]],
        [cur[0], cur[1] - 1],
        [cur[0], cur[1] + 1],
      ];
      for (const next of neighbors) {
        if (next[0] === to[0] && next[1] === to[1]) return true;
        const nextKey = keyOf(next);
        if (!visited.has(nextKey) && !this.isBlocked(next, mechCells)) {
          visited.add(nextKey);
          queue.push(next);
        }
      }
    }
    return false;
  }

  // ── 机关覆盖表（RuleState.cs BuildCoverStep）────────────────
  buildCoverStep() {
    this.coverStep = new Map();
    if (this.mechCenters.length === 0) return;
    const endX = this.origin[0] + this.width;
    const endY = this.origin[1] + this.height;
    for (let y = this.origin[1]; y < endY; y++) {
      for (let x = this.origin[0]; x < endX; x++) {
        const cell = [x, y];
        if ((x === this.spawn[0] && y === this.spawn[1]) || (x === this.goal[0] && y === this.goal[1])) {
          continue;
        }
        if (!this.isFloor(cell)) continue;
        let best = INF;
        for (let i = 0; i < this.mechCenters.length; i++) {
          const center = this.mechCenters[i];
          const lv = coverLevel(x - center[0], y - center[1]);
          if (lv > this.maxMechLevel) continue;
          const step = Math.max(0, lv - this.mechInitialLevels[i]);
          if (step < best) best = step;
        }
        if (best <= this.gMax) this.coverStep.set(cellKey(x, y), best);
      }
    }
  }

  getCoverStep(cell) {
    return this.coverStep.get(keyOf(cell)) ?? INF;
  }

  claimedKeySet() {
    const result = new Set();
    for (const [key, step] of this.coverStep) {
      if (step <= this.g && !this.shielded.has(key)) result.add(key);
    }
    return result;
  }

  refreshMechCells() {
    this.mechCells = new Set();
    for (const key of this.claimedKeySet()) {
      const cell = parseKey(key);
      if (this.inBounds(cell)) this.mechCells.add(key);
    }
  }

  // ── 生命周期（RuleState.cs Start / StepCore / Grow）──────────
  reset() {
    this.trail = [[...this.spawn]];
    this.maxLen = this.initialMaxLen;
    this.steps = 0;
    this.foodEaten = 0;
    this.g = 0;
    this.shielded = new Set();
    this.shieldVersion = 0;
    this.finished = false;
    this.distCacheKey = '';
    this.distCache = null;
    this.foods = new Set(this.initialFoods.map((c) => keyOf(c)));
    this.refreshMechCells();
    return this.claimedKeys;
  }

  get head() {
    return this.trail.length > 0 ? this.trail[this.trail.length - 1] : [...this.spawn];
  }

  get claimedCells() {
    return this.claimedKeySet();
  }

  get claimedKeys() {
    return [...this.claimedKeySet()];
  }

  get mechLevels() {
    return this.mechInitialLevels.map((level) => Math.min(level + this.g, this.maxMechLevel));
  }

  get maxMechLevelNow() {
    if (this.mechInitialLevels.length === 0) return 0;
    return Math.max(...this.mechLevels);
  }

  get hasMechanisms() {
    return this.mechCenters.length > 0;
  }

  get growthCountdown() {
    return this.growthInterval - (this.steps % this.growthInterval);
  }

  get remainingSteps() {
    return Math.max(this.maxLen - this.trail.length, 0);
  }

  classifyMove(next) {
    if (!this.inBounds(next)) return 'rejected';
    if (this.trail.some((c) => c[0] === next[0] && c[1] === next[1])) return 'body';
    if (this.isBlocked(next)) return 'rejected';
    if (this.trail.length >= this.maxLen) return 'rejected';
    return 'walk';
  }

  step(dx, dy) {
    const outcome = {
      accepted: false,
      truncated: false,
      ateFood: false,
      foodCell: null,
      grew: false,
      won: false,
      deadlockMsg: '',
      addedMechCells: [],
    };
    if (this.finished) return outcome;

    const next = [this.head[0] + dx, this.head[1] + dy];
    const kind = this.classifyMove(next);

    if (kind === 'body') {
      const bodyIndex = this.trail.findIndex((c) => c[0] === next[0] && c[1] === next[1]);
      this.trail = this.trail.slice(0, bodyIndex + 1);
      this.steps += 1;
      outcome.accepted = true;
      outcome.truncated = true;
    } else if (kind === 'walk') {
      this.trail.push(next);
      if (this.foods.delete(keyOf(next))) {
        this.foodEaten += 1;
        this.maxLen += this.foodLenGain;
        outcome.ateFood = true;
        outcome.foodCell = next;
      }
      this.steps += 1;
      outcome.accepted = true;
    }

    if (outcome.accepted) {
      if (this.steps > 0 && this.steps % this.growthInterval === 0) {
        outcome.grew = true;
        outcome.addedMechCells = this.grow();
      }
      if (this.head[0] === this.goal[0] && this.head[1] === this.goal[1]) {
        outcome.won = true;
        this.finished = true;
      } else {
        outcome.deadlockMsg = this.checkDeadlock();
        if (outcome.deadlockMsg && this.endOnDeadlock) this.finished = true;
      }
    }
    return outcome;
  }

  grow() {
    const before = this.claimedKeySet();
    this.g = Math.min(this.g + 1, this.gMax);
    this.shieldVersion += 1;
    this.shielded = new Set();
    for (const cell of this.trail) {
      if (this.getCoverStep(cell) <= this.g) this.shielded.add(keyOf(cell));
    }
    this.refreshMechCells();

    const added = [];
    for (const key of this.claimedKeySet()) {
      if (!before.has(key)) added.push(parseKey(key));
    }
    return added;
  }

  checkDeadlock() {
    if (this.finished || (this.head[0] === this.goal[0] && this.head[1] === this.goal[1])) {
      return '';
    }
    if (!this.hasPath(this.head, this.goal, this.mechCells)) {
      return '无路可走… 重开吧';
    }
    if (this.trail.length < this.maxLen) return '';
    const reach = this.maxLen - 1;
    const dist = this.bfsDistCached();
    const goalDist = dist.get(keyOf(this.goal));
    if (goalDist !== undefined && goalDist <= reach) return '';
    for (const foodKey of this.foods) {
      const foodDist = dist.get(foodKey);
      if (foodDist !== undefined && foodDist <= reach) return '';
    }
    return '管道不够长啊… 摘不到蓝花也捡不到花瓣';
  }

  bfsDistCached() {
    const cacheKey = `${this.g}/${this.shieldVersion}`;
    if (this.distCacheKey === cacheKey && this.distCache) return this.distCache;
    this.distCacheKey = cacheKey;
    this.distCache = this.bfsDistMap(this.spawn, this.mechCells);
    return this.distCache;
  }
}
