import { RuleState } from './rules.js';
import { Renderer, loadImages } from './render.js';
import { AudioEngine } from './audio.js';
import { UI } from './ui.js';
import { createInput } from './input.js';

const PARAMS = new URLSearchParams(window.location.search);
const DEBUG = PARAMS.get('debug') === '1';
const FREEZE = PARAMS.get('freeze') === '1';
const REPLAY = PARAMS.get('replay') === '1';

const DIRS = {
  up: [0, -1],
  down: [0, 1],
  left: [-1, 0],
  right: [1, 0],
};
const CHAR_DIRS = { A: 'left', D: 'right', W: 'up', S: 'down' };
const LEVEL6_ROUTE = 'DWDDSDDSDDSSDDDDDWDWDDDWDDDDAAASSSSASAAWAAWSDSDDDDDDDDDDDSDD';

async function boot() {
  const canvas = document.getElementById('game');
  const dpad = document.getElementById('dpad');

  const response = await fetch('data/level_06.json', { cache: 'no-cache' });
  if (!response.ok) throw new Error(`关卡数据加载失败：HTTP ${response.status}`);
  const data = await response.json();
  const images = await loadImages(data.assets.images);

  const audio = new AudioEngine(data.assets.sfx);
  audio.preload().catch((error) => console.warn('音效预加载失败：', error));

  const rule = new RuleState(data.level.spec);
  const renderer = new Renderer(canvas, data, images);
  renderer.freeze = FREEZE;

  let paused = false;
  let won = false;
  let failed = false;
  let inputLocked = false;
  let growTimer = null;
  let actionToken = 0;
  const growMs = Math.max(data.render.growAnimSec * 1000, 1);

  const ui = new UI(document, {
    onButton: () => {
      audio.unlock();
      audio.play('uiBounce');
    },
    onRestart: () => restart(true),
    onTogglePause: () => togglePause(),
    onResume: () => resume(),
    onToggleMute: () => toggleMute(),
    onToggleFullscreen: () => toggleFullscreen(),
    onShare: () => share(),
  });

  function isBlocked() {
    return paused || won || inputLocked || ui.isModalOpen();
  }

  function updateHud() {
    ui.setStats({
      length: rule.trail.length,
      maxLen: rule.maxLen,
      petalGain: rule.foodLenGain,
      growthCountdown: rule.growthCountdown,
      growthVisible: rule.hasMechanisms,
    });
  }

  function step(dirName) {
    if (isBlocked()) return;
    const dir = DIRS[dirName];
    if (!dir) return;

    const outcome = rule.step(dir[0], dir[1]);
    if (!outcome.accepted) return;

    audio.play('step');
    if (outcome.ateFood) audio.play('pick');
    renderer.notifyStep();
    updateHud();

    if (outcome.grew) {
      inputLocked = true;
      renderer.beginGrow(outcome.addedMechCells, growMs, true);
      audio.play('grow');
      const token = ++actionToken;
      if (growTimer) window.clearTimeout(growTimer);
      growTimer = window.setTimeout(() => {
        growTimer = null;
        if (token !== actionToken) return;
        inputLocked = false;
        if (!outcome.won && !failed && outcome.deadlockMsg) fail();
      }, growMs);
    }

    if (outcome.won && !failed) {
      win();
    } else if (!outcome.grew && outcome.deadlockMsg) {
      fail();
    }
  }

  function win() {
    if (won || failed) return;
    won = true;
    audio.play('win');
    ui.showWin(rule.steps);
  }

  function fail() {
    if (won || failed) return;
    failed = true;
    audio.play('lose');
    ui.showFail('本关已失败，按 R 或点「重开」重试本关卡');
  }

  function restart(force = false) {
    if (!force && ui.isModalOpen()) return;
    actionToken += 1;
    if (growTimer) {
      window.clearTimeout(growTimer);
      growTimer = null;
    }
    inputLocked = false;
    paused = false;
    won = false;
    failed = false;

    const claimed = rule.reset();
    renderer.finishGrow();
    renderer.walkStart = -Infinity;
    if (!FREEZE) renderer.beginGrow(claimed, growMs, false);

    ui.hideWin();
    ui.hidePause();
    ui.hideFail();
    updateHud();
  }

  function togglePause() {
    if (won || ui.isRotateOpen()) return;
    if (paused) {
      resume();
    } else {
      paused = true;
      ui.showPause();
    }
  }

  function resume() {
    paused = false;
    ui.hidePause();
  }

  function toggleMute() {
    audio.setMuted(!audio.muted);
    ui.setMuted(audio.muted);
  }

  function toggleFullscreen() {
    if (!document.fullscreenEnabled) return;
    if (document.fullscreenElement) {
      document.exitFullscreen().catch(() => {});
    } else {
      document.documentElement.requestFullscreen().catch(() => {});
    }
  }

  async function share() {
    const url = window.location.origin + window.location.pathname;
    const text = `我在《阿吉与花海》第 6 关走了 ${rule.steps} 步，来试试：`;
    if (navigator.share) {
      try {
        await navigator.share({ title: '阿吉与花海 · 第 6 关', text, url });
        return;
      } catch (error) {
        if (error?.name === 'AbortError') return;
      }
    }
    try {
      await navigator.clipboard.writeText(`${text}${url}`);
      ui.setShareFeedback('链接已复制');
      window.setTimeout(() => ui.setShareFeedback('分享'), 1600);
    } catch {
      window.prompt('复制链接分享', url);
    }
  }

  function fit() {
    const scale = Math.min(window.innerWidth / 1280, window.innerHeight / 720);
    const width = Math.max(1, Math.floor(1280 * scale));
    const height = Math.max(1, Math.floor(720 * scale));
    renderer.resize(width, height, window.devicePixelRatio || 1);
  }

  function snapshot() {
    return {
      steps: rule.steps,
      finished: rule.finished,
      g: rule.g,
      maxLen: rule.maxLen,
      trailLen: rule.trail.length,
      head: rule.head,
      foods: [...rule.foods],
      claimed: rule.claimedKeys.length,
      failed,
      won,
      paused,
      locked: inputLocked,
      mechLevels: rule.mechLevels,
    };
  }

  async function replayRoute() {
    for (const ch of LEVEL6_ROUTE) {
      step(CHAR_DIRS[ch]);
      await new Promise((resolve) => window.setTimeout(resolve, 130));
    }
    console.log('replay finished', snapshot());
  }

  const claimed = rule.reset();
  updateHud();
  ui.setMuted(audio.muted);
  if (!FREEZE) renderer.beginGrow(claimed, growMs, false);

  fit();
  window.addEventListener('resize', fit);

  createInput({
    canvas,
    dpad,
    onStep: step,
    onRestart: () => restart(false),
    onTogglePause: togglePause,
    onToggleMute: toggleMute,
    onGesture: () => audio.unlock(),
    isBlocked,
  });

  document.addEventListener('visibilitychange', () => {
    if (!document.hidden) return;
    if (growTimer) {
      window.clearTimeout(growTimer);
      growTimer = null;
    }
    inputLocked = false;
    renderer.finishGrow();
    if (!won && !paused && !ui.isRotateOpen()) {
      paused = true;
      ui.showPause();
    }
  });

  if (DEBUG) {
    window.__demo = {
      step: (dirName) => {
        step(dirName);
        return snapshot();
      },
      restart: () => restart(true),
      snapshot,
      replay: replayRoute,
      wait: (ms) => new Promise((resolve) => window.setTimeout(resolve, ms)),
    };
  }

  function loop(now) {
    renderer.draw(rule, now);
    window.requestAnimationFrame(loop);
  }
  window.requestAnimationFrame(loop);

  ui.hideLoading();
  if (DEBUG && REPLAY) replayRoute().catch((error) => console.error(error));
}

boot().catch((error) => {
  console.error(error);
  const loading = document.getElementById('loading');
  if (loading) {
    loading.hidden = false;
    loading.textContent = '加载失败，请刷新重试';
  }
});
