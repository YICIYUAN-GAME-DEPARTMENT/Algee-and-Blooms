/**
 * WebAudio 音效：音量/复音/变调参数来自 levels.json 的 assets.sfx。
 * 首次用户手势后 resume；静音状态存 localStorage。
 */

const MUTE_KEY = 'algee_blooms_demo_muted';

export class AudioEngine {
  constructor(meta) {
    this.meta = new Map(meta.map((item) => [item.name, item]));
    this.buffers = new Map();
    this.active = new Map();
    this.ctx = null;
    this.unlocked = false;
    try {
      this.muted = localStorage.getItem(MUTE_KEY) === '1';
    } catch {
      this.muted = false;
    }
  }

  async preload() {
    const Ctx = window.AudioContext || window.webkitAudioContext;
    if (!Ctx) return;
    this.ctx = new Ctx();
    await Promise.all(
      [...this.meta.values()].map(async (item) => {
        try {
          const response = await fetch(item.src);
          if (!response.ok) throw new Error(`HTTP ${response.status}`);
          const buffer = await response.arrayBuffer();
          const decoded = await this.ctx.decodeAudioData(buffer);
          this.buffers.set(item.name, decoded);
        } catch (error) {
          console.warn(`音效加载失败 ${item.name}:`, error);
        }
      })
    );
  }

  unlock() {
    if (!this.ctx) return;
    if (this.ctx.state === 'suspended') {
      this.ctx.resume().catch(() => {});
    }
    if (!this.unlocked) {
      const silent = this.ctx.createBuffer(1, 1, 22050);
      const source = this.ctx.createBufferSource();
      source.buffer = silent;
      source.connect(this.ctx.destination);
      source.start(0);
      this.unlocked = true;
    }
  }

  play(name, override = {}) {
    if (this.muted || !this.ctx || !this.buffers.has(name)) return;
    const meta = this.meta.get(name);
    if (!meta) return;
    const active = this.active.get(name) ?? 0;
    if (active >= meta.polyphony) return;

    const source = this.ctx.createBufferSource();
    source.buffer = this.buffers.get(name);
    const [minPitch, maxPitch] = meta.pitch ?? [1, 1];
    source.playbackRate.value = override.pitch ?? minPitch + Math.random() * (maxPitch - minPitch);

    const gain = this.ctx.createGain();
    const db = override.db ?? meta.db;
    gain.gain.value = 10 ** (db / 20);
    source.connect(gain).connect(this.ctx.destination);

    this.active.set(name, active + 1);
    source.onended = () => this.active.set(name, Math.max(0, (this.active.get(name) ?? 1) - 1));
    source.start(0);
  }

  setMuted(muted) {
    this.muted = Boolean(muted);
    try {
      localStorage.setItem(MUTE_KEY, this.muted ? '1' : '0');
    } catch {
      // localStorage 不可用时静默降级
    }
  }
}
