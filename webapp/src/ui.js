/**
 * DOM HUD / 选关 / 面板。只改既有元素的文本与 hidden 状态，不在 JS 里搭建布局。
 */
export class UI {
  constructor(doc, callbacks = {}) {
    this.doc = doc;
    this.callbacks = callbacks;
    this.rotateDismissed = false;
    this.cards = new Map();
    this.el = {
      hud: doc.getElementById('hud'),
      length: doc.getElementById('stat-length'),
      petal: doc.getElementById('stat-petal'),
      petalBlock: doc.getElementById('stat-petal-block'),
      grow: doc.getElementById('stat-grow'),
      growBlock: doc.getElementById('stat-grow-block'),
      levelTitle: doc.getElementById('level-title'),
      btnMenu: doc.getElementById('btn-menu'),
      btnRestart: doc.getElementById('btn-restart'),
      btnPause: doc.getElementById('btn-pause'),
      btnSound: doc.getElementById('btn-sound'),
      btnFullscreen: doc.getElementById('btn-fullscreen'),
      failBanner: doc.getElementById('fail-banner'),
      levelSelect: doc.getElementById('level-select'),
      levelGrid: doc.getElementById('level-grid'),
      winPanel: doc.getElementById('win-panel'),
      winSteps: doc.getElementById('win-steps'),
      btnNext: doc.getElementById('btn-next'),
      btnReplay: doc.getElementById('btn-replay'),
      btnWinMenu: doc.getElementById('btn-win-menu'),
      btnShare: doc.getElementById('btn-share'),
      pausePanel: doc.getElementById('pause-panel'),
      btnResume: doc.getElementById('btn-resume'),
      btnPauseRestart: doc.getElementById('btn-pause-restart'),
      btnPauseMenu: doc.getElementById('btn-pause-menu'),
      btnPauseSound: doc.getElementById('btn-pause-sound'),
      rotateHint: doc.getElementById('rotate-hint'),
      btnRotateContinue: doc.getElementById('btn-rotate-continue'),
      loading: doc.getElementById('loading'),
    };
    this.bind();
    this.el.btnFullscreen.hidden = !doc.fullscreenEnabled;
    doc.addEventListener('fullscreenchange', () => {
      this.el.btnFullscreen.textContent = doc.fullscreenElement ? '退出全屏' : '全屏';
    });
    window.addEventListener('resize', () => this.maybeShowRotateHint());
    window.addEventListener('orientationchange', () => this.maybeShowRotateHint());
  }

  bind() {
    const click = (element, action) => {
      element.addEventListener('click', () => {
        this.callbacks.onButton?.();
        action();
      });
    };
    click(this.el.btnMenu, () => this.callbacks.onMenu?.());
    click(this.el.btnRestart, () => this.callbacks.onRestart?.());
    click(this.el.btnPause, () => this.callbacks.onTogglePause?.());
    click(this.el.btnSound, () => this.callbacks.onToggleMute?.());
    click(this.el.btnFullscreen, () => this.callbacks.onToggleFullscreen?.());
    click(this.el.btnNext, () => this.callbacks.onNext?.());
    click(this.el.btnReplay, () => this.callbacks.onRestart?.());
    click(this.el.btnWinMenu, () => this.callbacks.onMenu?.());
    click(this.el.btnShare, () => this.callbacks.onShare?.());
    click(this.el.btnResume, () => this.callbacks.onResume?.());
    click(this.el.btnPauseRestart, () => this.callbacks.onRestart?.());
    click(this.el.btnPauseMenu, () => this.callbacks.onMenu?.());
    click(this.el.btnPauseSound, () => this.callbacks.onToggleMute?.());
    click(this.el.btnRotateContinue, () => {
      this.rotateDismissed = true;
      this.el.rotateHint.hidden = true;
    });
  }

  setLevels(levels) {
    this.el.levelGrid.replaceChildren();
    this.cards.clear();
    for (const level of levels) {
      const card = this.doc.createElement('button');
      card.type = 'button';
      card.className = 'level-card';
      card.dataset.level = String(level.id);

      const number = this.doc.createElement('span');
      number.className = 'level-number';
      number.textContent = String(level.id);

      const name = this.doc.createElement('span');
      name.className = 'level-name';
      name.textContent = level.name;

      const best = this.doc.createElement('span');
      best.className = 'level-best';
      best.textContent = '未通关';

      card.append(number, name, best);
      card.addEventListener('click', () => {
        this.callbacks.onButton?.();
        this.callbacks.onSelectLevel?.(level.id);
      });
      this.cards.set(level.id, { card, best });
      this.el.levelGrid.append(card);
    }
  }

  updateBestSteps(bestSteps) {
    for (const [id, { best }] of this.cards) {
      const value = bestSteps?.[id] ?? bestSteps?.[String(id)];
      best.textContent = value ? `最佳 ${value} 步` : '未通关';
    }
  }

  setHudVisible(visible) {
    this.el.hud.hidden = !visible;
  }

  showLevelSelect(bestSteps) {
    this.updateBestSteps(bestSteps);
    this.setHudVisible(false);
    this.el.levelTitle.textContent = '';
    this.el.levelSelect.hidden = false;
    this.el.winPanel.hidden = true;
    this.el.pausePanel.hidden = true;
    this.hideFail();
    const first = this.cards.values().next().value;
    first?.card.focus({ preventScroll: true });
  }

  hideLevelSelect() {
    this.el.levelSelect.hidden = true;
    this.setHudVisible(true);
    this.doc.activeElement?.blur?.();
  }

  isMenuOpen() {
    return !this.el.levelSelect.hidden;
  }

  setLevelTitle(text) {
    this.el.levelTitle.textContent = text;
  }

  setStats({ length, maxLen, petalGain, petalVisible, growthCountdown, growthVisible }) {
    this.el.length.textContent = `${length}/${maxLen}`;
    this.el.petal.textContent = String(petalGain);
    this.el.grow.textContent = String(growthCountdown);
    this.el.petalBlock.hidden = !petalVisible;
    this.el.growBlock.hidden = !growthVisible;
  }

  setMuted(muted) {
    const text = muted ? '音效 关' : '音效 开';
    this.el.btnSound.textContent = text;
    this.el.btnPauseSound.textContent = text;
  }

  setShareFeedback(text) {
    this.el.btnShare.textContent = text;
  }

  showFail(text) {
    this.el.failBanner.textContent = text;
    this.el.failBanner.hidden = false;
  }

  hideFail() {
    this.el.failBanner.hidden = true;
  }

  showWin(steps, nextLevelName) {
    this.hideFail();
    this.el.winSteps.textContent = `步数 ${steps}`;
    this.el.btnNext.hidden = !nextLevelName;
    this.el.btnNext.title = nextLevelName ? `下一关：${nextLevelName}` : '';
    this.el.winPanel.hidden = false;
    (nextLevelName ? this.el.btnNext : this.el.btnReplay).focus({ preventScroll: true });
  }

  hideWin() {
    this.el.winPanel.hidden = true;
    this.doc.activeElement?.blur?.();
  }

  showPause() {
    this.el.pausePanel.hidden = false;
    this.el.btnResume.focus({ preventScroll: true });
  }

  hidePause() {
    this.el.pausePanel.hidden = true;
    this.doc.activeElement?.blur?.();
  }

  showLoading() {
    this.el.loading.hidden = false;
  }

  hideLoading() {
    this.el.loading.hidden = true;
    this.maybeShowRotateHint();
  }

  maybeShowRotateHint() {
    if (this.rotateDismissed) {
      if (!this.isPortrait()) this.el.rotateHint.hidden = true;
      return;
    }
    const coarse = window.matchMedia?.('(pointer: coarse)').matches ?? false;
    if (coarse && this.isPortrait() && this.el.loading.hidden) {
      this.el.rotateHint.hidden = false;
    } else {
      this.el.rotateHint.hidden = true;
    }
  }

  isPortrait() {
    return window.innerHeight > window.innerWidth;
  }

  isWinOpen() {
    return !this.el.winPanel.hidden;
  }

  isPauseOpen() {
    return !this.el.pausePanel.hidden;
  }

  isRotateOpen() {
    return !this.el.rotateHint.hidden;
  }

  isModalOpen() {
    return this.isMenuOpen() || this.isWinOpen() || this.isPauseOpen() || this.isRotateOpen();
  }
}
