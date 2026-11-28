import { RuleState } from './rules.js';
import { Renderer, loadImages } from './render.js';
import { AudioEngine } from './audio.js';
import { UI } from './ui.js';
import { createInput } from './input.js';

const PARAMS = new URLSearchParams(window.location.search);
const DEBUG = PARAMS.get('debug') === '1';
const FREEZE = PARAMS.get('freeze') === '1';
const BEST_KEY = 'algee_blooms_best_steps';

const DIRS = {
  up: [0, -1],
  down: [0, 1],
  left: [-1, 0],
  right: [1, 0],
};

function loadBestSteps() {
  try {
    const parsed = JSON.parse(localStorage.getItem(BEST_KEY) ?? '{}');
    return parsed && typeof parsed === 'object' ? parsed : {};
  } catch {
    return {};
  }
}

function saveBestSteps(best) {
  try {
    localStorage.setItem(BEST_KEY, JSON.stringify(best));
  } catch {
    // 存储不可用时静默降级
  }
}

async function boot() {
  const canvas = document.getElementById('game');
  const dpad = document.getElementById('dpad');

  const response = await fetch('data/levels.json', { cache: 'no-cache' });
  if (!response.ok) throw new Error(`关卡数据加载失败：HTTP ${response.status}`);
  const bundle = await response.json();
  const images = await loadImages(bundle.assets.images);

  const audio = new AudioEngine(bundle.assets.sfx);
  audio.preload().catch((error) => console.warn('音效预加载失败：', error));

  const renderer = new Renderer(canvas, bundle, images);
  renderer.freeze = FREEZE;
  const levels = [...bundle.levels].sort((a, b) => a.id - b.id);
  const levelsById = new Map(levels.map((level) => [level.id, level]));
  let bestSteps = loadBestSteps();

  let rule = null;
  let currentLevel = null;
  let mode = 'menu';
  let paused = false;
  let inputLocked = false;
  let failed = false;
  let growTimer = null;
  let actionToken = 0;
  const growMs = Math.max(bundle.render.growAnimSec * 1000, 1);

  const ui = new UI(document, {
    onButton: () => {
      audio.unlock();
      audio.play('uiBounce');
    },
    onSelectLevel: (id) => startLevel(id),
    onNext: () => nextLevel(),
    onMenu: () => showMenu(),
    onRestart: () => restart(true),
    onTogglePause: () => togglePause(),
    onResume: () => resume(),
    onToggleMute: () => toggleMute(),
    onToggleFullscreen: () => toggleFullscreen(),
    onShare: () => share(),
  });

  ui.setLevels(levels);
  ui.setMuted(audio.muted);

  function isBlocked() {
    return mode !== 'playing' || paused || inputLocked || ui.isModalOpen();
  }

  function updateHud() {
    if (!rule || !currentLevel) return;
    ui.setStats({
      length: rule.trail.length,
      maxLen: rule.maxLen,
      petalGain: rule.foodLenGain,
      petalVisible: currentLevel.spec.foods.length > 0,
      growthCountdown: rule.growthCountdown,
      growthVisible: currentLevel.spec.mech_centers.length > 0,
    });
  }

  function setLevelParam(id) {
    const url = new URL(window.location.href);
    if (id) url.searchParams.set('level', String(id));
    else url.searchParams.delete('level');
    window.history.replaceState(null, '', url);
  }

  function startLevel(id, updateUrl = true) {
    const level = levelsById.get(Number(id));
    if (!level) return;
    actionToken += 1;
    if (growTimer) {
      window.clearTimeout(growTimer);
      growTimer = null;
    }
    currentLevel = level;
    rule = new RuleState(level.spec);
    const claimed = rule.reset();
    renderer.setLevel(level);
    renderer.freeze = FREEZE;
    if (!FREEZE) renderer.beginGrow(claimed, growMs, false);

    mode = 'playing';
    paused = false;
    inputLocked = false;
    failed = false;

    ui.hideLevelSelect();
    ui.hideWin();
    ui.hidePause();
    ui.hideFail();
    ui.setLevelTitle(`第 ${level.id} 关 · ${level.name}`);
    updateHud();
    if (updateUrl) setLevelParam(level.id);
  }

  function showMenu() {
    actionToken += 1;
    if (growTimer) {
      window.clearTimeout(growTimer);
      growTimer = null;
    }
    mode = 'menu';
    paused = false;
    inputLocked = false;
    failed = false;
    renderer.finishGrow();
    ui.showLevelSelect(bestSteps);
    setLevelParam(null);
  }

  function nextLevel() {
    if (!currentLevel) {
      showMenu();
      return;
    }
    const next = levels.find((level) => level.id === currentLevel.id + 1);
    if (next) startLevel(next.id);
    else showMenu();
  }

  function step(dirName) {
    if (isBlocked() || !rule) return;
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
    if (mode !== 'playing' || failed || !currentLevel) return;
    mode = 'won';
    audio.play('win');
    const previous = bestSteps[currentLevel.id];
    if (!previous || rule.steps < previous) {
      bestSteps[currentLevel.id] = rule.steps;
      saveBestSteps(bestSteps);
    }
    const next = levels.find((level) => level.id === currentLevel.id + 1);
    ui.showWin(rule.steps, next?.name ?? '');
  }

  function fail() {
    if (mode !== 'playing' || failed) return;
    failed = true;
    audio.play('lose');
    ui.showFail('本关已失败，按 R 或点「重开」重试本关卡');
  }

  function restart(force = false) {
    if (!currentLevel) return;
    if (!force && ui.isModalOpen()) return;
    startLevel(currentLevel.id, false);
  }

  function togglePause() {
    if (mode !== 'playing' || ui.isRotateOpen()) return;
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
    if (!currentLevel) return;
    const url = new URL(window.location.href);
    url.searchParams.set('level', String(currentLevel.id));
    const text = `我在《阿吉与花海》第 ${currentLevel.id} 关「${currentLevel.name}」走了 ${rule?.steps ?? 0} 步，来试试：`;
    if (navigator.share) {
      try {
        await navigator.share({ title: '阿吉与花海', text, url: url.toString() });
        return;
      } catch (error) {
        if (error?.name === 'AbortError') return;
      }
    }
    try {
      await navigator.clipboard.writeText(`${text}${url.toString()}`);
      ui.setShareFeedback('链接已复制');
      window.setTimeout(() => ui.setShareFeedback('分享'), 1600);
    } catch {
      window.prompt('复制链接分享', url.toString());
    }
  }

  function fit() {
    const scale = Math.min(window.innerWidth / bundle.viewport[0], window.innerHeight / bundle.viewport[1]);
    const width = Math.max(1, Math.floor(bundle.viewport[0] * scale));
    const height = Math.max(1, Math.floor(bundle.viewport[1] * scale));
    renderer.resize(width, height, window.devicePixelRatio || 1);
  }

  function snapshot() {
    return {
      levelId: currentLevel?.id ?? null,
      mode,
      steps: rule?.steps ?? 0,
      finished: rule?.finished ?? false,
      g: rule?.g ?? 0,
      maxLen: rule?.maxLen ?? 0,
      trailLen: rule?.trail.length ?? 0,
      head: rule?.head ?? null,
      foods: rule ? [...rule.foods] : [],
      claimed: rule?.claimedKeys.length ?? 0,
      failed,
      paused,
      locked: inputLocked,
      menu: ui.isMenuOpen(),
      mechLevels: rule?.mechLevels ?? [],
    };
  }

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
    if (mode === 'playing' && !paused && !ui.isRotateOpen()) {
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
      startLevel: (id) => {
        startLevel(id);
        return snapshot();
      },
      menu: () => {
        showMenu();
        return snapshot();
      },
      nextLevel: () => {
        nextLevel();
        return snapshot();
      },
      restart: () => {
        restart(true);
        return snapshot();
      },
      snapshot,
      wait: (ms) => new Promise((resolve) => window.setTimeout(resolve, ms)),
    };
  }

  function loop(now) {
    renderer.draw(rule, now);
    window.requestAnimationFrame(loop);
  }
  window.requestAnimationFrame(loop);

  const requested = Number(PARAMS.get('level'));
  if (Number.isFinite(requested) && levelsById.has(requested)) {
    startLevel(requested, false);
  } else {
    showMenu();
  }
  ui.hideLoading();
}

boot().catch((error) => {
  console.error(error);
  const loading = document.getElementById('loading');
  if (loading) {
    loading.hidden = false;
    loading.textContent = '加载失败，请刷新重试';
  }
});
