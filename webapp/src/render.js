/**
 * Canvas 表现层：只读 RuleState，不修改规则状态。
 * 逻辑视口来自 levels.json（1920×1080），格子 32px；按每关 World/Camera 变换绘制。
 */

const CELL = 32;
const INF = 1e9;

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

function parseKey(value) {
  const comma = value.indexOf(',');
  return [Number(value.slice(0, comma)), Number(value.slice(comma + 1))];
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

export function loadImage(src) {
  return new Promise((resolve, reject) => {
    const image = new Image();
    image.onload = () => resolve(image);
    image.onerror = () => reject(new Error(`图片加载失败：${src}`));
    image.src = src;
  });
}

export async function loadImages(imageMap) {
  const entries = Object.entries(imageMap);
  const loaded = await Promise.all(entries.map(async ([name, src]) => [name, await loadImage(src)]));
  return Object.fromEntries(loaded);
}

export class Renderer {
  constructor(canvas, bundle, images) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
    this.bundle = bundle;
    this.images = images;
    this.level = null;
    this.data = null;
    this.freeze = false;
    this.growAnim = null;
    this.walkStart = -INF;
    this.coreCache = new Map();
    this.scale = 1;

    this.maskToCol = new Map(bundle.blob.mask_cols.map((mask, col) => [mask, col]));

    this.groundImage = images.grass;
    this.wallImage = images.wall;
    this.flowerImage = images.flower;
    this.pathImage = images.path;
    this.headImage = images.head;
    this.tailImage = images.tail;
    this.foodImage = images.food;
    this.coreImage = images.core;
    this.goalImage = images.goal;
    this.bgImage = images.bg;
  }

  setLevel(level) {
    this.level = level;
    this.data = {
      ...level,
      render: this.bundle.render,
      blob: this.bundle.blob,
      viewport: this.bundle.viewport,
    };
    this.groundCells = new Map(level.layers.ground.cells.map((c) => [key(c.x, c.y), c.col]));
    this.obstacleCells = new Map(level.layers.obstacles.cells.map((c) => [key(c.x, c.y), c.col]));
    this.groundSet = new Set(this.groundCells.keys());
    this.obstacleSet = new Set(this.obstacleCells.keys());
    this.validateStaticBlobs();
    this.finishGrow();
    this.walkStart = -INF;
  }

  resize(cssWidth, cssHeight, dpr) {
    const scale = Math.min(2, Math.max(1, dpr || 1));
    const width = Math.max(1, Math.round(cssWidth * scale));
    const height = Math.max(1, Math.round(cssHeight * scale));
    if (this.canvas.width !== width || this.canvas.height !== height) {
      this.canvas.width = width;
      this.canvas.height = height;
    }
    this.canvas.style.width = `${cssWidth}px`;
    this.canvas.style.height = `${cssHeight}px`;
    this.scale = width / this.bundle.viewport[0];
  }

  validateStaticBlobs() {
    const check = (label, cells, set) => {
      let mismatches = 0;
      for (const [cellKey, col] of cells) {
        const [x, y] = parseKey(cellKey);
        const expected = this.maskToCol.get(blobMask(set, x, y));
        if (expected !== col) mismatches += 1;
      }
      if (mismatches > 0) console.warn(`${label} 层有 ${mismatches} 格 47-blob 列号与重算不一致`);
    };
    check('Ground', this.groundCells, this.groundSet);
    check('Obstacles', this.obstacleCells, this.obstacleSet);
  }

  notifyStep() {
    this.walkStart = performance.now();
  }

  beginGrow(addedCells, durationMs, pulse = true) {
    if (this.freeze) return;
    const cells = new Set(addedCells.map((cell) => key(cell[0], cell[1])));
    this.growAnim = {
      start: performance.now(),
      duration: Math.max(durationMs, 1),
      cells,
      pulse,
    };
  }

  finishGrow() {
    this.growAnim = null;
  }

  draw(rule, now) {
    const ctx = this.ctx;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
    if (!this.data) return;

    const [vw, vh] = this.bundle.viewport;
    const backing = this.scale ?? 1;
    const { world, camera } = this.data.view;
    const sx = backing * world.scale[0] * camera.zoom[0];
    const sy = backing * world.scale[1] * camera.zoom[1];
    const tx = backing * ((world.position[0] - camera.position[0]) * camera.zoom[0] + vw / 2);
    const ty = backing * ((world.position[1] - camera.position[1]) * camera.zoom[1] + vh / 2);
    ctx.setTransform(sx, 0, 0, sy, tx, ty);
    ctx.imageSmoothingEnabled = false;

    const animNow = this.freeze ? this.walkStart + this.data.render.head.idleSec + 1 : now;
    this.drawBackground();
    this.drawLayer(this.groundCells, this.groundImage, 0);
    this.drawLayer(this.obstacleCells, this.wallImage, 0);

    for (const foodKey of rule.foods) {
      const [x, y] = parseKey(foodKey);
      this.drawCenter(this.foodImage, x, y, 0, 0);
    }
    this.drawGoal();

    const claimed = rule.claimedCells;
    this.drawMechLayer(claimed, animNow);
    this.drawBody(rule.trail);
    this.drawTail(rule.trail);
    this.drawCores(rule, animNow);
    this.drawHead(rule.trail, animNow);
  }

  drawBackground() {
    const bg = this.data.view.background;
    const w = this.bgImage.width * bg.scale[0];
    const h = this.bgImage.height * bg.scale[1];
    this.ctx.drawImage(
      this.bgImage,
      Math.round(bg.position[0] - w / 2),
      Math.round(bg.position[1] - h / 2),
      Math.round(w),
      Math.round(h)
    );
  }

  drawLayer(cells, image, row) {
    const ctx = this.ctx;
    for (const [cellKey, col] of cells) {
      const [x, y] = parseKey(cellKey);
      ctx.drawImage(image, col * CELL, row * CELL, CELL, CELL, x * CELL, y * CELL, CELL, CELL);
    }
  }

  drawCenter(image, x, y, ox = 0, oy = 0, w = image.width, h = image.height) {
    const cx = x * CELL + CELL / 2 + ox;
    const cy = y * CELL + CELL / 2 + oy;
    this.ctx.drawImage(image, Math.round(cx - w / 2), Math.round(cy - h / 2), w, h);
  }

  drawGoal() {
    const [x, y] = this.data.entities.goal.cell;
    const [ox, oy] = this.data.entities.goal.offset ?? [0, 0];
    const w = this.data.render.goal.w;
    const h = this.data.render.goal.h;
    const cx = x * CELL + CELL / 2 + ox;
    const cy = y * CELL + CELL / 2 + oy;
    this.ctx.drawImage(this.goalImage, Math.round(cx - w / 2), Math.round(cy - h / 2), w, h);
  }

  drawMechLayer(claimed, now) {
    const ctx = this.ctx;
    const anim = this.growAnim;
    for (const cellKey of claimed) {
      const [x, y] = parseKey(cellKey);
      const col = this.maskToCol.get(blobMask(claimed, x, y)) ?? 0;
      let row = this.data.render.mech.doneRow;
      if (anim && anim.cells.has(cellKey)) {
        const t = (now - anim.start) / anim.duration;
        if (t < 1 / 3) row = this.data.render.mech.popRows[0];
        else if (t < 2 / 3) row = this.data.render.mech.popRows[1];
        else row = this.data.render.mech.popRows[2];
      }
      ctx.drawImage(this.flowerImage, col * CELL, row * CELL, CELL, CELL, x * CELL, y * CELL, CELL, CELL);
    }
  }

  drawBody(trail) {
    if (trail.length < 2) return;
    const ctx = this.ctx;
    for (let i = 1; i < trail.length - 1; i++) {
      const a = [trail[i][0] - trail[i - 1][0], trail[i][1] - trail[i - 1][1]];
      const b = [trail[i + 1][0] - trail[i][0], trail[i + 1][1] - trail[i][1]];
      let col;
      if (a[0] === b[0] && a[1] === b[1]) {
        col = a[0] !== 0 ? 0 : 1;
      } else {
        const s = [b[0] - a[0], b[1] - a[1]];
        if (s[0] === 1 && s[1] === -1) col = 2;
        else if (s[0] === -1 && s[1] === -1) col = 3;
        else if (s[0] === -1 && s[1] === 1) col = 4;
        else col = 5;
      }
      const [x, y] = trail[i];
      ctx.drawImage(this.pathImage, col * CELL, 0, CELL, CELL, x * CELL, y * CELL, CELL, CELL);
    }

    const tailDir = [trail[1][0] - trail[0][0], trail[1][1] - trail[0][1]];
    this.drawEndpoint(trail[0], tailDir);
    const headDir = [
      trail[trail.length - 1][0] - trail[trail.length - 2][0],
      trail[trail.length - 1][1] - trail[trail.length - 2][1],
    ];
    this.drawEndpoint(trail[trail.length - 1], headDir);
  }

  drawEndpoint(cell, dir) {
    let col = 9;
    if (dir[0] === 1) col = 6;
    else if (dir[0] === -1) col = 7;
    else if (dir[1] === 1) col = 8;
    this.ctx.drawImage(this.pathImage, col * CELL, 0, CELL, CELL, cell[0] * CELL, cell[1] * CELL, CELL, CELL);
  }

  drawTail(trail) {
    if (trail.length === 0) return;
    const out = trail.length > 1 ? [trail[1][0] - trail[0][0], trail[1][1] - trail[0][1]] : [1, 0];
    const angle = Math.atan2(out[1], out[0]);
    const cx = trail[0][0] * CELL + CELL / 2;
    const cy = trail[0][1] * CELL + CELL / 2;
    const ctx = this.ctx;
    ctx.save();
    ctx.translate(cx, cy);
    ctx.rotate(angle);
    ctx.drawImage(this.tailImage, -CELL / 2, -CELL / 2);
    ctx.restore();
  }

  drawCores(rule, now) {
    const anim = this.growAnim;
    let scale = 1;
    if (anim && anim.pulse) {
      const t = (now - anim.start) / anim.duration;
      if (t >= 0 && t < 0.4) scale = 1 + 0.18 * (t / 0.4);
      else if (t >= 0.4 && t < 1) scale = 1.18 - 0.18 * ((t - 0.4) / 0.6);
    }
    const levels = rule.mechLevels;
    for (let i = 0; i < rule.mechCenters.length; i++) {
      const [x, y] = rule.mechCenters[i];
      const level = levels[i] ?? 0;
      const sprite = this.getCoreSprite(level);
      const cx = x * CELL + CELL / 2;
      const cy = y * CELL + CELL / 2;
      const ctx = this.ctx;
      ctx.save();
      ctx.translate(cx, cy);
      ctx.scale(scale, scale);
      ctx.drawImage(sprite, -CELL / 2, -CELL / 2);
      ctx.restore();
    }
  }

  getCoreSprite(level) {
    if (level >= 0) return this.coreImage;
    if (this.coreCache.has(level)) return this.coreCache.get(level);
    const { w, h, blue, minLevel } = this.data.render.core;
    const t = Math.min(1, Math.max(0, (-level) / -minLevel));
    const r = Math.round(255 * (1 - t) + 255 * blue[0] * t);
    const g = Math.round(255 * (1 - t) + 255 * blue[1] * t);
    const b = Math.round(255 * (1 - t) + 255 * blue[2] * t);
    const offscreen = document.createElement('canvas');
    offscreen.width = w;
    offscreen.height = h;
    const octx = offscreen.getContext('2d');
    octx.imageSmoothingEnabled = false;
    octx.drawImage(this.coreImage, 0, 0, w, h);
    octx.globalCompositeOperation = 'multiply';
    octx.fillStyle = `rgb(${r}, ${g}, ${b})`;
    octx.fillRect(0, 0, w, h);
    octx.globalCompositeOperation = 'destination-in';
    octx.drawImage(this.coreImage, 0, 0, w, h);
    this.coreCache.set(level, offscreen);
    return offscreen;
  }

  drawHead(trail, now) {
    if (trail.length === 0) return;
    const face = trail.length > 1
      ? [trail[trail.length - 1][0] - trail[trail.length - 2][0], trail[trail.length - 1][1] - trail[trail.length - 2][1]]
      : [1, 0];
    let row = 0;
    if (face[0] === -1) row = 1;
    else if (face[1] === 1) row = 2;
    else if (face[1] === -1) row = 3;

    const elapsed = now - this.walkStart;
    let col = this.data.render.head.idleCol;
    if (elapsed < this.data.render.head.holdSec) col = 0;
    else if (elapsed < this.data.render.head.idleSec) col = 1;

    const head = trail[trail.length - 1];
    const { frameW, frameH } = this.data.render.head;
    const cx = head[0] * CELL + CELL / 2;
    const cy = head[1] * CELL + CELL / 2 + this.data.render.head.anchorY;
    this.ctx.drawImage(
      this.headImage,
      col * frameW,
      row * frameH,
      frameW,
      frameH,
      Math.round(cx - frameW / 2),
      Math.round(cy - frameH / 2),
      frameW,
      frameH
    );
  }
}
